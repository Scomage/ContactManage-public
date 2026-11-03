using ContactManage.Adapters.MicrosoftGraph;
using ContactManage.Core;

namespace ContactManage.Tests;

public class MicrosoftStoreTests
{
    private const string Email = "jane@outlook.example";

    private static ContactFolder Folder(string id = "F1", string name = "Contacts") =>
        new($"ms-{Email}:/{id}", name, "/" + id, Email, $"Microsoft ({Email})", FolderKind.Microsoft, IsWritable: true);

    // Ann is in the folder twice (same phone), Bob once. Ann's second card carries things the first lacks.
    private static (MicrosoftGraphStore Store, FakeGraph Graph) Setup()
    {
        var graph = new FakeGraph();
        graph.Folders["F1"] = "Contacts";
        graph.Folders["F2"] = "Clients";
        graph.AddContact("F1", """
            {"id":"C1","changeKey":"K1","displayName":"Ann Lee","givenName":"Ann","surname":"Lee",
             "emailAddresses":[{"address":"ann@x.example"}],"mobilePhone":"+1 415 555 0101","categories":["Blue"]}
            """);
        graph.AddContact("F1", """
            {"id":"C2","changeKey":"K2","displayName":"Ann Lee","givenName":"Ann","surname":"Lee",
             "emailAddresses":[{"address":"ann.lee@work.example"}],"businessPhones":["(415) 555-0101"],"nickName":"Annie","jobTitle":"Buyer"}
            """);
        graph.AddContact("F1", """{"id":"C3","changeKey":"K3","displayName":"Bob Ray","givenName":"Bob","surname":"Ray","emailAddresses":[{"address":"bob@x.example"}]}""");
        graph.AddContact("F2", """{"id":"C4","changeKey":"K4","displayName":"Cy Dee","emailAddresses":[{"address":"cy@x.example"}]}""");

        var vault = new MemoryVault();
        vault.Save("microsoft|" + Email, new ServerCredentials(Email, "RT1"));
        var accounts = new MicrosoftAccounts(vault, () => new MicrosoftClientConfig { ClientId = "11112222-bbbb-3333-cccc-4444dddd5555" }, graph);
        return (new MicrosoftGraphStore(accounts, graph, TimeSpan.Zero), graph);
    }

    [Fact]
    public async Task Refresh_lists_the_folders_of_every_signed_in_account_with_their_counts()
    {
        var (store, _) = Setup();

        var refresh = await store.RefreshFoldersAsync([], _ => false);

        Assert.Empty(refresh.Problems);
        Assert.Equal(1, refresh.AccountsAsked);
        Assert.Equal(["Clients", "Contacts"], refresh.Folders.Select(f => f.Name));
        var contacts = refresh.Folders.Single(f => f.Name == "Contacts");
        Assert.Equal(3, contacts.ContactCount);
        Assert.Equal(FolderKind.Microsoft, contacts.Kind);
        Assert.Equal(Email, contacts.AccountName);
        Assert.Equal($"Microsoft ({Email})", contacts.ServerName);
        Assert.True(contacts.CanWrite && contacts.CanCreateFolder && contacts.IsReadable);
        Assert.Equal($"ms-{Email}", contacts.AccountKey);
    }

    [Fact]
    public async Task The_built_in_contacts_folder_is_found_even_when_the_folder_list_leaves_it_out()
    {
        var (store, graph) = Setup();
        graph.UnlistedDefaultFolder = "F1";   // what a brand-new Outlook.com account showed: an empty list and no error

        var refresh = await store.RefreshFoldersAsync([], _ => false);

        Assert.Empty(refresh.Problems);
        Assert.Equal(["Clients", "Contacts"], refresh.Folders.Select(f => f.Name));
        Assert.Equal(3, refresh.Folders.Single(f => f.Name == "Contacts").ContactCount);
    }

    [Fact]
    public async Task A_folder_list_that_leaves_out_the_default_folder_and_a_server_that_will_not_name_it_still_lists_the_rest()
    {
        var (store, graph) = Setup();
        graph.UnlistedDefaultFolder = "NONE";   // the well-known name answers 404

        var refresh = await store.RefreshFoldersAsync([], _ => false);

        Assert.Empty(refresh.Problems);
        Assert.Equal(["Clients", "Contacts"], refresh.Folders.Select(f => f.Name));
    }

    [Fact]
    public async Task A_sign_in_that_no_longer_works_is_reported_as_a_problem_not_thrown()
    {
        var (store, graph) = Setup();
        graph.RefuseRefresh = true;

        var refresh = await store.RefreshFoldersAsync([], _ => false);

        Assert.Empty(refresh.Folders);
        Assert.Contains(refresh.Problems, p => p.Contains(Email) && p.Contains("Add Microsoft account"));
    }

    [Fact]
    public async Task Reading_follows_the_paging_links_and_maps_every_contact()
    {
        var (store, graph) = Setup();
        graph.PageSize = 2;

        var contacts = await store.ReadContactsAsync(Folder());

        Assert.Equal(["Ann Lee", "Ann Lee", "Bob Ray"], contacts.Select(c => c.DisplayName));
        Assert.All(contacts, c => Assert.Equal($"ms-{Email}:/F1", c.SourceFolderId));
        Assert.Equal(2, graph.Log.Count(l => l.EndsWith("/me/contactFolders/F1/contacts")));
    }

    [Fact]
    public async Task Merged_duplicates_update_one_original_in_place_and_delete_the_other_last()
    {
        var (store, graph) = Setup();
        var folder = Folder();
        var final = new MergeEngine().Merge(await store.ReadContactsAsync(folder)).BuildFinalList();
        graph.Log.Clear();

        var report = await store.ReplaceContactsAsync(folder, final);

        Assert.Equal(2, graph.In("F1"));
        Assert.Equal(new WriteReport("Contacts", 0, 1, 1, 2, 1), report);
        Assert.True(report.Verified);
        Assert.Equal(1, graph.Contacts.Keys.Count(k => k is "C1" or "C2"));
        Assert.Contains("C3", graph.Contacts.Keys);                                  // Bob untouched
        Assert.DoesNotContain(graph.Log, l => l.Contains("C3"));
        Assert.Equal(1, graph.In("F2"));                                             // the other folder is left alone

        var survivor = graph.Contacts.Single(c => c.Key is "C1" or "C2").Value.Body;
        Assert.Equal(2, survivor["emailAddresses"]!.AsArray().Count);                // the union of both cards' addresses
        Assert.Equal("Annie", (string?)survivor["nickName"]);                        // a field only one card had is kept
        Assert.Equal("Buyer", (string?)survivor["jobTitle"]);
        Assert.Equal("Blue", (string?)survivor["categories"]![0]);

        var ops = graph.Log.Where(l => l.StartsWith("  op ")).ToList();
        Assert.True(ops.FindLastIndex(o => o.Contains("PATCH")) < ops.FindIndex(o => o.Contains("DELETE")), "the delete must come after the update");
    }

    [Fact]
    public async Task New_contacts_are_created_in_the_folder_and_counted()
    {
        var (store, graph) = Setup();
        var folder = Folder();
        var read = await store.ReadContactsAsync(folder);
        var added = new Contact { DisplayName = "Di Fox", GivenName = "Di", FamilyName = "Fox", Emails = { "di@x.example" } };

        var report = await store.ReplaceContactsAsync(folder, [.. read, added]);

        Assert.Equal(new WriteReport("Contacts", 1, 0, 3, 4), report);
        Assert.Equal(4, graph.In("F1"));
        Assert.Contains(graph.Contacts.Values, c => c.Folder == "F1" && (string?)c.Body["displayName"] == "Di Fox");
        Assert.DoesNotContain(graph.Log, l => l.StartsWith("  op PATCH") || l.StartsWith("  op DELETE"));
    }

    [Fact]
    public async Task A_throttled_operation_in_a_batch_is_sent_again_and_nothing_is_done_twice()
    {
        var (store, graph) = Setup();
        graph.ThrottleNextBatchItem = true;
        var folder = Folder();
        var read = await store.ReadContactsAsync(folder);

        var report = await store.ReplaceContactsAsync(folder, [.. read, new Contact { DisplayName = "Di Fox", Emails = { "di@x.example" } }]);

        Assert.Equal(4, graph.In("F1"));
        Assert.Equal(1, graph.Contacts.Values.Count(c => (string?)c.Body["displayName"] == "Di Fox"));
        Assert.True(report.Verified);
    }

    [Fact]
    public async Task Replace_refuses_to_empty_a_folder_and_changes_nothing()
    {
        var (store, graph) = Setup();
        graph.Log.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReplaceContactsAsync(Folder(), []));

        Assert.Equal(3, graph.In("F1"));
        Assert.DoesNotContain(graph.Log, l => l.Contains("$batch"));
    }

    [Fact]
    public async Task A_refused_change_stops_the_replace_before_any_delete()
    {
        var (store, graph) = Setup();
        var folder = Folder();
        var read = await store.ReadContactsAsync(folder);
        var final = new MergeEngine().Merge(read).BuildFinalList();
        graph.RefusePatch = true;   // Outlook refuses the in-place update

        var ex = await Assert.ThrowsAsync<MicrosoftAuthException>(() => store.ReplaceContactsAsync(folder, final));

        Assert.Contains("ErrorAccessDenied", ex.Message);
        Assert.Equal(3, graph.In("F1"));                                             // nothing was deleted
        Assert.DoesNotContain(graph.Log, l => l.StartsWith("  op DELETE"));
    }

    [Fact]
    public async Task A_new_folder_is_created_and_filled()
    {
        var (store, graph) = Setup();
        var account = Folder();

        var report = await store.CreateFolderAsync(account, "Merged", [new Contact { DisplayName = "Di Fox", Emails = { "di@x.example" } }]);

        Assert.Equal(new WriteReport("Merged", 1, 0, 0, 1), report);
        var id = graph.Folders.Single(f => f.Value == "Merged").Key;
        Assert.Equal(1, graph.In(id));
        Assert.True(((IContactStore)store).CanCreateFolder(account));
    }

    [Fact]
    public async Task A_new_folder_whose_contacts_could_not_be_written_is_removed_when_still_empty()
    {
        var (_, graph) = Setup();
        var broken = new MicrosoftGraphStore(new MicrosoftAccounts(new MemoryVault { Items = { ["microsoft|" + Email] = new ServerCredentials(Email, "RT1") } },
            () => new MicrosoftClientConfig { ClientId = "11112222-bbbb-3333-cccc-4444dddd5555" }, graph), new FailingCreates(graph), TimeSpan.Zero);

        await Assert.ThrowsAsync<MicrosoftAuthException>(() => broken.CreateFolderAsync(Folder(), "Merged", [new Contact { DisplayName = "Di Fox" }]));

        Assert.DoesNotContain("Merged", graph.Folders.Values);
    }

    [Fact]
    public async Task A_gateway_error_after_a_batch_of_creates_does_not_send_the_batch_again()
    {
        var (store, graph) = Setup();
        graph.GatewayErrorAfterNextBatch = true;   // Microsoft did the work, then answered 504

        await Assert.ThrowsAsync<MicrosoftAuthException>(() => store.CreateFolderAsync(Folder(), "Merged",
            [new Contact { DisplayName = "Di Fox", Emails = { "di@x.example" } }, new Contact { DisplayName = "Ed Gray", Emails = { "ed@x.example" } }]));

        Assert.Equal(1, graph.Log.Count(l => l.EndsWith("/$batch", StringComparison.Ordinal)));
        var id = graph.Folders.Single(f => f.Value == "Merged").Key;
        Assert.Equal(2, graph.In(id));   // each contact once; resending would have made them twice
    }

    /// <summary>Passes everything to the fake except the batch that creates contacts, which it refuses.</summary>
    private sealed class FailingCreates(FakeGraph inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/$batch"))
                return new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden) { Content = new StringContent("""{"error":{"code":"ErrorAccessDenied","message":"no"}}""") };
            return await base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task A_link_to_another_host_in_a_paging_answer_is_not_followed_with_the_token()
    {
        var (_, graph) = Setup();
        var evil = new EvilLink(graph);
        var vault = new MemoryVault { Items = { ["microsoft|" + Email] = new ServerCredentials(Email, "RT1") } };
        var accounts = new MicrosoftAccounts(vault, () => new MicrosoftClientConfig { ClientId = "11112222-bbbb-3333-cccc-4444dddd5555" }, graph);
        var careful = new MicrosoftGraphStore(accounts, evil, TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<MicrosoftAuthException>(() => careful.ReadContactsAsync(Folder()));

        Assert.Contains("another address", ex.Message);
        Assert.DoesNotContain(evil.Hosts, h => h == "evil.example");
    }

    private sealed class EvilLink(FakeGraph inner) : DelegatingHandler(inner)
    {
        public List<string> Hosts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Hosts.Add(request.RequestUri!.Host);
            if (request.RequestUri.AbsolutePath.EndsWith("/contacts"))
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"value":[],"@odata.nextLink":"https://evil.example/steal"}""", System.Text.Encoding.UTF8, "application/json"),
                };
            return await base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task The_composite_store_sends_microsoft_folders_to_the_microsoft_store()
    {
        var (store, _) = Setup();
        var composite = new CompositeContactStore(new NoStore(), new NoStore(), null, store);

        var contacts = await composite.ReadContactsAsync(Folder());
        var refresh = await composite.RefreshFoldersAsync([], _ => false);

        Assert.Equal(3, contacts.Count);
        Assert.Equal(2, refresh.Folders.Count);
        Assert.True(composite.CanCreateFolder(Folder()));
    }

    private sealed class NoStore : IContactStore
    {
        public Task<IReadOnlyList<Contact>> ReadContactsAsync(ContactFolder folder, CancellationToken ct = default) => throw new InvalidOperationException("wrong store");
        public Task<WriteReport> ReplaceContactsAsync(ContactFolder folder, IReadOnlyList<Contact> contacts, CancellationToken ct = default) => throw new InvalidOperationException("wrong store");
        public Task<FolderRefresh> RefreshFoldersAsync(IReadOnlyList<ContactFolder> folders, Func<ContactFolder, bool> mayContact, CancellationToken ct = default) =>
            Task.FromResult(new FolderRefresh([], [], 0));
        public Task<WriteReport> CreateFolderAsync(ContactFolder accountFolder, string name, IReadOnlyList<Contact> contacts, CancellationToken ct = default) => throw new InvalidOperationException("wrong store");
        public bool CanCreateFolder(ContactFolder account) => false;
    }
}
