using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ContactManage.Adapters.Google;
using ContactManage.Core;
using Xunit;

namespace ContactManage.Tests;

public class GmailWriteTests
{
    /// <summary>A Google that remembers its contacts and records every change it is asked for, in order.</summary>
    private sealed class WritableGoogle : HttpMessageHandler
    {
        public List<string> Log { get; } = [];
        public List<JsonNode> Bodies { get; } = [];
        public Dictionary<string, JsonObject> People { get; } = [];
        public List<(string Resource, string Name)> Labels { get; } = [("contactGroups/aaa", "Clients"), ("contactGroups/bbb", "Scomage")];
        public bool RejectUpdates { get; set; }
        private int _next = 1;

        public WritableGoogle()
        {
            Add("people/c1", "E1", """{"names":[{"displayName":"Ann Lee","givenName":"Ann","familyName":"Lee"}],"phoneNumbers":[{"value":"+1 415 555 0101","type":"mobile"}],"memberships":[{"contactGroupMembership":{"contactGroupResourceName":"contactGroups/aaa"}}]}""");
            Add("people/c2", "E2", """{"names":[{"displayName":"Ann Lee","givenName":"Ann","familyName":"Lee"}],"emailAddresses":[{"value":"ann@x.example","type":"work"}],"phoneNumbers":[{"value":"(415) 555-0101"}],"memberships":[{"contactGroupMembership":{"contactGroupResourceName":"contactGroups/bbb"}}]}""");
            Add("people/c3", "E3", """{"names":[{"displayName":"Bob Ray","givenName":"Bob","familyName":"Ray"}],"emailAddresses":[{"value":"bob@x.example"}]}""");
        }

        private void Add(string resource, string etag, string json)
        {
            var o = JsonNode.Parse(json)!.AsObject();
            o["resourceName"] = resource;
            o["etag"] = etag;
            People[resource] = o;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!;
            if (url.Host == "oauth2.googleapis.com") return Json("""{"access_token":"AT","expires_in":3600}""");

            var path = url.AbsolutePath.Replace("/v1/", "");
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct));
            var line = $"{request.Method} {path}";

            if (request.Method == HttpMethod.Get)
            {
                if (path == "contactGroups")
                {
                    var groups = new JsonArray(new JsonObject { ["resourceName"] = "contactGroups/myContacts", ["groupType"] = "SYSTEM_CONTACT_GROUP", ["formattedName"] = "My Contacts" });
                    foreach (var l in Labels) groups.Add(new JsonObject { ["resourceName"] = l.Resource, ["groupType"] = "USER_CONTACT_GROUP", ["name"] = l.Name });
                    return Json(new JsonObject { ["contactGroups"] = groups }.ToJsonString());
                }
                return Json(new JsonObject { ["connections"] = new JsonArray(People.Values.Select(p => p.DeepClone()).ToArray()) }.ToJsonString());
            }

            Log.Add(line);
            Bodies.Add(body!);

            if (path == "contactGroups")
            {
                var resource = $"contactGroups/new{Labels.Count}";
                Labels.Add((resource, body!["contactGroup"]!["name"]!.GetValue<string>()));
                return Json($$"""{"resourceName":"{{resource}}"}""");
            }
            if (path.EndsWith("/members:modify")) return Json("{}");
            if (path == "people:batchCreateContacts")
            {
                var created = new JsonArray();
                foreach (var c in body!["contacts"]!.AsArray())
                {
                    var resource = $"people/n{_next++}";
                    var person = c!["contactPerson"]!.DeepClone().AsObject();
                    person["resourceName"] = resource;
                    person["etag"] = "NE";
                    People[resource] = person;
                    created.Add(new JsonObject { ["httpStatusCode"] = 200, ["person"] = new JsonObject { ["resourceName"] = resource } });
                }
                return Json(new JsonObject { ["createdPeople"] = created }.ToJsonString());
            }
            if (path.EndsWith(":updateContact"))
            {
                if (RejectUpdates)
                    return Json("""{"error":{"code":400,"status":"FAILED_PRECONDITION","message":"etag mismatch"}}""", HttpStatusCode.BadRequest);
                var resource = path[..path.IndexOf(':')];
                Assert.Equal(People[resource]["etag"]!.GetValue<string>(), body!["etag"]!.GetValue<string>());
                return Json("{}");
            }
            if (path == "people:batchDeleteContacts")
            {
                foreach (var r in body!["resourceNames"]!.AsArray()) People.Remove(r!.GetValue<string>());
                return Json("");
            }
            throw new InvalidOperationException("Unexpected request " + line);
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static (GooglePeopleStore Store, WritableGoogle Server, ContactFolder Folder) Setup()
    {
        var vault = new MemoryVault();
        vault.Save("google|jane@gmail.com", new ServerCredentials("jane@gmail.com", "REFRESH"));
        var server = new WritableGoogle();
        var accounts = new GoogleAccounts(vault, () => new GoogleClientConfig { ClientId = "id", ClientSecret = "secret" }, server);
        var store = new GooglePeopleStore(accounts, new GoogleAccountNames(), server, TimeSpan.Zero);
        return (store, server, store.MainFolder("jane@gmail.com"));
    }

    // Ann appears twice (c1, c2: same phone); Bob is on his own.
    private static async Task<(IReadOnlyList<Contact> Read, MergeResult Result)> Merged(GooglePeopleStore store, ContactFolder folder)
    {
        var read = await store.ReadContactsAsync(folder);
        var result = new MergeEngine().Merge(read);
        return (read, result);
    }

    [Fact]
    public async Task A_merged_set_updates_its_first_original_in_place_and_the_others_are_deleted_last()
    {
        var (store, server, folder) = Setup();
        var (_, result) = await Merged(store, folder);

        var report = await store.ReplaceContactsAsync(folder, result.BuildFinalList());

        Assert.Equal(["POST people:batchDeleteContacts"], server.Log.Where(l => l.Contains("Delete")));
        Assert.Equal(server.Log.Count - 1, server.Log.IndexOf("POST people:batchDeleteContacts"));                  // deletes come last
        Assert.Contains("PATCH people/c1:updateContact", server.Log);                                              // c1 survives, c2 is merged into it
        Assert.DoesNotContain(server.Log, l => l.Contains("c3"));                                                  // Bob was not touched
        Assert.Equal(["people/c1", "people/c3"], server.People.Keys.Order());
        Assert.Equal(new WriteReport("My Contacts", 0, 1, 1, 2, 1), report);
        Assert.True(report.Verified);

        // the survivor carries the other member's email, and the label of the deleted one is added (not replaced)
        var update = server.Bodies[server.Log.IndexOf("PATCH people/c1:updateContact")];
        Assert.Equal("E1", update["etag"]!.GetValue<string>());
        Assert.Equal("ann@x.example", update["emailAddresses"]![0]!["value"]!.GetValue<string>());
        Assert.Equal("work", update["emailAddresses"]![0]!["type"]!.GetValue<string>());
        var labelCalls = server.Log.Select((l, i) => (l, i)).Where(x => x.l.EndsWith("/members:modify")).ToList();
        Assert.Contains(labelCalls, x => x.l == "POST contactGroups/bbb/members:modify" && server.Bodies[x.i]["resourceNamesToAdd"]![0]!.GetValue<string>() == "people/c1");
        Assert.DoesNotContain(update.AsObject(), kv => kv.Key == "memberships");                                  // memberships are never overwritten
    }

    [Fact]
    public async Task Contacts_from_elsewhere_are_created_with_my_contacts_and_their_labels_and_missing_labels_are_made()
    {
        var (store, server, folder) = Setup();
        var read = await store.ReadContactsAsync(folder);
        var fromAbroad = new Contact { DisplayName = "Cy Dee", GivenName = "Cy", FamilyName = "Dee", Groups = { "Newcomers", "clients" } };
        fromAbroad.Emails.Add("cy@x.example");
        fromAbroad.Labels["cy@x.example"] = ";TYPE=INTERNET,WORK";

        var report = await store.ReplaceContactsAsync(folder, [.. read, fromAbroad]);

        Assert.Equal(["POST contactGroups", "POST people:batchCreateContacts"], server.Log);                       // existing "clients" label reused, only "Newcomers" made
        var person = server.Bodies[1]["contacts"]![0]!["contactPerson"]!;
        Assert.Equal("Cy", person["names"]![0]!["givenName"]!.GetValue<string>());
        Assert.Equal("work", person["emailAddresses"]![0]!["type"]!.GetValue<string>());
        var memberships = person["memberships"]!.AsArray().Select(m => m!["contactGroupMembership"]!["contactGroupResourceName"]!.GetValue<string>()).ToList();
        Assert.Equal(["contactGroups/myContacts", "contactGroups/new2", "contactGroups/aaa"], memberships);
        Assert.Equal(new WriteReport("My Contacts", 1, 0, 3, 4), report);                                          // nothing else changed
    }

    [Fact]
    public async Task Removed_contacts_are_deleted_and_an_empty_list_is_refused()
    {
        var (store, server, folder) = Setup();
        var (_, result) = await Merged(store, folder);
        result.Groups.Single().Decision = GroupDecision.Remove;

        var report = await store.ReplaceContactsAsync(folder, result.BuildFinalList());

        Assert.Equal(["POST people:batchDeleteContacts"], server.Log);
        Assert.Equal(["people/c3"], server.People.Keys);
        Assert.Equal(new WriteReport("My Contacts", 0, 2, 1, 1), report);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReplaceContactsAsync(folder, []));
        Assert.Single(server.Log);
    }

    [Fact]
    public async Task A_contact_changed_in_gmail_since_it_was_read_stops_the_write_before_anything_is_deleted()
    {
        var (store, server, folder) = Setup();
        var (_, result) = await Merged(store, folder);
        server.RejectUpdates = true;

        var ex = await Assert.ThrowsAsync<GoogleAuthException>(() => store.ReplaceContactsAsync(folder, result.BuildFinalList()));

        Assert.Contains("changed in Gmail", ex.Message);
        Assert.DoesNotContain(server.Log, l => l.Contains("Delete"));
        Assert.Equal(3, server.People.Count);
    }

    [Fact]
    public async Task A_backup_file_restores_into_gmail_with_its_labels_and_replaces_what_is_there()
    {
        var (store, server, folder) = Setup();
        var backup = VCard.WriteAll(await store.ReadContactsAsync(folder));                  // what the app saves before a replace
        var (_, result) = await Merged(store, folder);
        await store.ReplaceContactsAsync(folder, result.BuildFinalList());                  // the merge the user now regrets
        Assert.Equal(2, server.People.Count);
        server.Log.Clear();
        server.Bodies.Clear();

        var report = await store.ReplaceContactsAsync(folder, VCard.ParseAll(backup));       // restore: three contacts back, today's two removed

        Assert.Equal(3, server.People.Count);
        Assert.Equal(new WriteReport("My Contacts", 3, 2, 0, 3), report);
        Assert.True(report.Verified);
        var created = server.Bodies[server.Log.IndexOf("POST people:batchCreateContacts")]["contacts"]!.AsArray();
        var ann = created.Select(c => c!["contactPerson"]!).First(p => p["names"]![0]!["givenName"]?.GetValue<string>() == "Ann" && p["phoneNumbers"]![0]!["value"]!.GetValue<string>().StartsWith("+1"));
        Assert.Contains("contactGroups/aaa", ann["memberships"]!.AsArray().Select(m => m!["contactGroupMembership"]!["contactGroupResourceName"]!.GetValue<string>()));   // Clients label is back
        Assert.True(server.Log.IndexOf("POST people:batchDeleteContacts") > server.Log.IndexOf("POST people:batchCreateContacts"));
    }

    [Fact]
    public async Task New_folders_are_not_offered_and_a_read_only_kind_cannot_be_replaced()
    {
        var (store, _, folder) = Setup();

        Assert.True(folder.CanWrite);
        Assert.False(folder.CanCreateFolder);
        await Assert.ThrowsAsync<NotSupportedException>(() => store.CreateFolderAsync(folder, "x", []));
        var signIn = folder with { Kind = FolderKind.GmailSignIn };
        Assert.False(signIn.CanWrite);
        await Assert.ThrowsAsync<NotSupportedException>(() => store.ReplaceContactsAsync(signIn, [new Contact { DisplayName = "x" }]));
    }

    [Fact]
    public async Task A_label_is_read_and_written_on_its_own_members_and_the_rest_of_my_contacts_is_left_alone()
    {
        var (store, server, main) = Setup();
        // a second Ann in Clients: same phone as c1, so the label holds a duplicate; c2 is also an Ann but is only in Scomage
        server.People["people/c4"] = JsonNode.Parse("""{"resourceName":"people/c4","etag":"E4","names":[{"displayName":"Ann Lee","givenName":"Ann","familyName":"Lee"}],"phoneNumbers":[{"value":"+1 415 555 0101"}],"memberships":[{"contactGroupMembership":{"contactGroupResourceName":"contactGroups/aaa"}}]}""")!.AsObject();
        var clients = main with { Id = main.Id.Replace("/myContacts", "/label/contactGroups/aaa"), Name = "Clients", Kind = FolderKind.GmailLabel };

        var read = await store.ReadContactsAsync(clients);
        Assert.Equal(["people/c1", "people/c4"], read.Select(c => c.RemoteId).Order());
        var result = new MergeEngine().Merge(read);
        Assert.Single(result.Groups);

        var report = await store.ReplaceContactsAsync(clients, result.BuildFinalList());

        Assert.Equal(["people/c1", "people/c2", "people/c3"], server.People.Keys.Order());                       // only the label's duplicate went
        Assert.DoesNotContain(server.Log, l => l.Contains("c2") || l.Contains("c3"));
        Assert.Equal(1, report.Removed);
        Assert.True(report.Verified);
        Assert.Equal(1, report.CountAfter);                                                                         // the label now holds one contact

        // an empty merged list is refused for a label too
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReplaceContactsAsync(clients, []));
    }

    // ---- contact to person

    [Fact]
    public void A_contact_becomes_a_person_with_types_address_and_birthday_and_google_only_members_are_dropped()
    {
        var c = new Contact { GivenName = "Dee", FamilyName = "Fox", NamePrefix = "Dr.", Company = "Acme", JobTitle = "CEO", Notes = "hi" };
        c.Phones.AddRange(["555-0100", "555-0101"]);
        c.Labels["555-0100"] = ";TYPE=WORK,CELL"; c.Labels["555-0101"] = ";TYPE=HOME,FAX";
        c.Addresses.Add(";;1 Main St;Springfield;IL;62701;USA");
        c.Labels[c.Addresses[0]] = ";TYPE=HOME";
        c.Extra.AddRange(["BDAY:19800229", "URL:https://x.example/a", "PHOTO:abc",
            """X-GOOGLE-EVENTS:[{"metadata":{"primary":true},"formattedType":"Anniversary","type":"anniversary","date":{"month":5,"day":1}}]""",
            """X-GOOGLE-MORE-ORGANIZATIONS:[{"name":"Old Co","title":"Intern"}]"""]);

        var draft = GoogleContactMapper.ToPerson(c);
        var p = draft.Person;

        Assert.Equal(("Dee", "Fox", "Dr."), (p["names"]![0]!["givenName"]!.GetValue<string>(), p["names"]![0]!["familyName"]!.GetValue<string>(), p["names"]![0]!["honorificPrefix"]!.GetValue<string>()));
        Assert.Equal(["workMobile", "homeFax"], p["phoneNumbers"]!.AsArray().Select(x => x!["type"]!.GetValue<string>()));
        Assert.Equal(("Springfield", "home"), (p["addresses"]![0]!["city"]!.GetValue<string>(), p["addresses"]![0]!["type"]!.GetValue<string>()));
        Assert.Equal(("Acme", "Old Co"), (p["organizations"]![0]!["name"]!.GetValue<string>(), p["organizations"]![1]!["name"]!.GetValue<string>()));
        Assert.Equal(29, p["birthdays"]![0]!["date"]!["day"]!.GetValue<int>());
        Assert.Equal("https://x.example/a", p["urls"]![0]!["value"]!.GetValue<string>());
        Assert.Null(p["events"]![0]!["metadata"]);                                    // read-only members are not sent back
        Assert.Null(p["events"]![0]!["formattedType"]);
        Assert.Equal(1, draft.Skipped);                                               // the PHOTO line has no place
        Assert.Equal("names,emailAddresses,phoneNumbers,addresses,organizations,biographies,birthdays,urls,events", draft.UpdateFields);
    }

    [Fact]
    public void A_contact_with_only_a_display_name_is_sent_as_an_unstructured_name()
    {
        var p = GoogleContactMapper.ToPerson(new Contact { DisplayName = "Pat Green" }).Person;

        Assert.Equal("Pat Green", p["names"]![0]!["unstructuredName"]!.GetValue<string>());
        Assert.Empty(p["emailAddresses"]!.AsArray());
    }
}
