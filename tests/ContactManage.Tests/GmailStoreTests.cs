using System.Net;
using System.Text;
using System.Text.Json;
using ContactManage.Adapters;
using ContactManage.Adapters.Google;
using ContactManage.Core;

namespace ContactManage.Tests;

public class GmailStoreTests
{
    private sealed class FakeGoogle : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        public string? PeopleBodyOverride { get; set; }
        public bool RevokeRefresh { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            lock (Calls) Calls.Add($"{request.Method} {url.Host}{url.AbsolutePath}");

            if (url.Host == "oauth2.googleapis.com")
            {
                if (RevokeRefresh) return Task.FromResult(Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest));
                return Task.FromResult(Json("""{"access_token":"AT","expires_in":3600}"""));
            }

            Assert.Equal("Bearer AT", request.Headers.Authorization!.ToString());
            if (url.AbsolutePath.EndsWith("contactGroups"))
                return Task.FromResult(Json("""
                    {"contactGroups":[
                      {"resourceName":"contactGroups/myContacts","groupType":"SYSTEM_CONTACT_GROUP","formattedName":"My Contacts","memberCount":3},
                      {"resourceName":"contactGroups/starred","groupType":"SYSTEM_CONTACT_GROUP","formattedName":"Starred","memberCount":1},
                      {"resourceName":"contactGroups/aaa","groupType":"USER_CONTACT_GROUP","name":"Clients","memberCount":2},
                      {"resourceName":"contactGroups/bbb","groupType":"USER_CONTACT_GROUP","name":"Scomage","memberCount":2}]}
                    """));
            return Task.FromResult(Json(PeopleBodyOverride ?? People));
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // Ann appears twice (same phone) with different labels; Bob has fields the app does not model.
    private const string People = """
        {"connections":[
          {"resourceName":"people/c1","etag":"E1",
           "names":[{"metadata":{"primary":true},"displayName":"Ann Lee","givenName":"Ann","familyName":"Lee","honorificPrefix":"Dr."}],
           "organizations":[{"name":"Acme","title":"Buyer","department":"Purchasing"}],
           "emailAddresses":[{"value":"ann@x.example","type":"work"},{"value":"ANN@x.example"}],
           "phoneNumbers":[{"value":"+1 415 555 0101","type":"mobile"}],
           "addresses":[{"streetAddress":"1 Main St","city":"Springfield","region":"IL","postalCode":"62701","country":"USA","type":"home"}],
           "biographies":[{"value":"Likes tea"}],
           "memberships":[{"contactGroupMembership":{"contactGroupResourceName":"contactGroups/myContacts"}},
                          {"contactGroupMembership":{"contactGroupResourceName":"contactGroups/aaa"}},
                          {"contactGroupMembership":{"contactGroupResourceName":"contactGroups/starred"}}]},
          {"resourceName":"people/c2","etag":"E2",
           "names":[{"displayName":"Ann Lee","givenName":"Ann","familyName":"Lee"}],
           "phoneNumbers":[{"value":"(415) 555-0101"}],
           "memberships":[{"contactGroupMembership":{"contactGroupResourceName":"contactGroups/bbb"}}]},
          {"resourceName":"people/c3","etag":"E3",
           "names":[{"displayName":"Bob Ray"},{"displayName":"Robert Ray"}],
           "birthdays":[{"date":{"year":1980,"month":2,"day":29}}],
           "urls":[{"value":"https://bob.example","type":"work"}],
           "photos":[{"url":"https://photo.example/x"}]}
        ]}
        """;

    private static (GoogleAccounts Accounts, MemoryVault Vault, FakeGoogle Server) Setup(bool signedIn = true)
    {
        var vault = new MemoryVault();
        if (signedIn) vault.Save("google|jane@gmail.com", new ServerCredentials("jane@gmail.com", "REFRESH"));
        var server = new FakeGoogle();
        var accounts = new GoogleAccounts(vault, () => new GoogleClientConfig { ClientId = "id", ClientSecret = "secret" }, server);
        return (accounts, vault, server);
    }

    // Jane's account is shown the way IceWarp names it, with "Google" as the server.
    private static GooglePeopleStore Store(GoogleAccounts accounts, HttpMessageHandler server)
    {
        var names = new GoogleAccountNames();
        names.Set("jane@gmail.com", "Jane GMail");
        return new GooglePeopleStore(accounts, names, server);
    }

    private static ContactFolder Folder() => Store(Setup().Accounts, new FakeGoogle()).MainFolder("jane@gmail.com");

    // ---- mapping

    [Fact]
    public async Task Gmail_contacts_are_read_with_their_fields_and_only_the_users_own_labels()
    {
        var (accounts, _, server) = Setup();
        var store = Store(accounts, server);

        var contacts = await store.ReadContactsAsync(Folder());

        Assert.Equal(3, contacts.Count);
        var ann = contacts[0];
        Assert.Equal(("Ann Lee", "Ann", "Lee", "Dr."), (ann.DisplayName, ann.GivenName, ann.FamilyName, ann.NamePrefix));
        Assert.Equal(("Acme", "Buyer", "Purchasing"), (ann.Company, ann.JobTitle, ann.Department));
        Assert.Equal(["ann@x.example"], ann.Emails);                       // the same address in another case is one email
        Assert.Equal(";TYPE=WORK", ann.Labels["ann@x.example"]);
        Assert.Equal(";TYPE=CELL", ann.Labels["+1 415 555 0101"]);
        Assert.Equal([";;1 Main St;Springfield;IL;62701;USA"], ann.Addresses);
        Assert.Equal("Likes tea", ann.Notes);
        Assert.Equal(["Clients"], ann.Groups);                              // not My Contacts or Starred
        Assert.Equal(("people/c1", "people/c1", "E1"), (ann.Uid, ann.RemoteId, ann.RemoteEtag));
        Assert.Equal("gmail-jane@gmail.com:/myContacts", ann.SourceFolderId);
        Assert.Equal(["Scomage"], contacts[1].Groups);
    }

    [Fact]
    public async Task Fields_the_app_does_not_model_are_kept_as_raw_lines()
    {
        var (accounts, _, server) = Setup();
        var bob = (await Store(accounts, server).ReadContactsAsync(Folder()))[2];

        Assert.Equal("Bob Ray", bob.DisplayName);
        Assert.Contains(bob.Extra, l => l.StartsWith("X-GOOGLE-BIRTHDAYS:") && l.Contains("\"day\":29"));
        Assert.Contains(bob.Extra, l => l.StartsWith("X-GOOGLE-URLS:") && l.Contains("bob.example"));
        Assert.Contains(bob.Extra, l => l.StartsWith("X-GOOGLE-MORE-NAMES:") && l.Contains("Robert Ray")); // a second name is not dropped
        Assert.DoesNotContain(bob.Extra, l => l.Contains("photo"));                                       // photos are Google's own
    }

    // ---- merging labels

    [Fact]
    public async Task Duplicates_merge_into_one_contact_that_carries_every_label_and_the_file_has_them()
    {
        var (accounts, _, server) = Setup();
        var contacts = await Store(accounts, server).ReadContactsAsync(Folder());

        var result = new MergeEngine().Merge(contacts);
        var group = Assert.Single(result.Groups);                            // the two Ann Lees share a phone number
        Assert.Equal(2, group.Members.Count);
        var merged = Assert.Single(result.BuildFinalList(), c => c.DisplayName == "Ann Lee");

        Assert.Equal(["Clients", "Scomage"], merged.Groups.Order());
        var line = VCard.Write(merged).Split("\r\n").Single(l => l.StartsWith("CATEGORIES:"));   // saved to a vCard file as categories
        Assert.Equal(["Clients", "Scomage"], line["CATEGORIES:".Length..].Split(',').Order());
    }

    [Fact]
    public void Labels_that_differ_only_in_case_count_once_when_merging()
    {
        var a = new Contact { DisplayName = "Cy Dee", Emails = { "cy@x.example" }, Groups = { "Clients" } };
        var b = new Contact { DisplayName = "Cy Dee", Emails = { "cy@x.example" }, Groups = { "clients", "Family" } };

        var merged = Assert.Single(new MergeEngine().Merge([a, b]).BuildFinalList());

        Assert.Equal(["Clients", "Family"], merged.Groups);
    }

    // ---- accounts and tokens

    [Fact]
    public async Task A_saved_sign_in_gives_an_access_token_without_opening_a_browser()
    {
        var (accounts, _, server) = Setup();

        Assert.Equal(["jane@gmail.com"], accounts.SavedEmails());
        Assert.Equal("AT", await accounts.AccessTokenAsync("jane@gmail.com"));
        Assert.Equal("AT", await accounts.AccessTokenAsync("jane@gmail.com"));          // cached
        Assert.Single(server.Calls, c => c.Contains("oauth2.googleapis.com"));
    }

    [Fact]
    public async Task A_revoked_sign_in_is_deleted_and_the_user_is_told_to_sign_in_again()
    {
        var (accounts, vault, server) = Setup();
        server.RevokeRefresh = true;

        var ex = await Assert.ThrowsAsync<GoogleAuthException>(() => accounts.AccessTokenAsync("jane@gmail.com"));

        Assert.True(ex.SignInAgain);
        Assert.Contains("Add Google account", ex.Message);
        Assert.Empty(vault.Items);
        Assert.Empty(accounts.SavedEmails());
    }

    [Fact]
    public async Task Signing_in_saves_only_the_refresh_token_under_the_accounts_email()
    {
        var vault = new MemoryVault();
        // FakeGoogle's token endpoint gives no refresh token, so the sign-in uses a handler that does.
        var signIn = new GoogleAccounts(vault, () => new GoogleClientConfig { ClientId = "id", ClientSecret = "secret" },
            new SignInHandler());

        var email = await signIn.SignInAsync(async url =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(url.Query);
            using var browser = new HttpClient();
            await browser.GetStringAsync($"{q["redirect_uri"]}?code=C&state={Uri.EscapeDataString(q["state"]!)}");
        });

        Assert.Equal("jane@gmail.com", email);
        var saved = Assert.Single(vault.Items);
        Assert.Equal("google|jane@gmail.com", saved.Key);
        Assert.Equal("REFRESH-TOKEN", saved.Value.Password);
    }

    [Fact]
    public async Task A_sign_in_without_the_contacts_permission_is_refused_and_saves_nothing()
    {
        var vault = new MemoryVault();
        var signIn = new GoogleAccounts(vault, () => new GoogleClientConfig { ClientId = "id", ClientSecret = "secret" },
            new SignInHandler("openid https://www.googleapis.com/auth/userinfo.email"));

        var ex = await Assert.ThrowsAsync<GoogleAuthException>(() => signIn.SignInAsync(async url =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(url.Query);
            using var browser = new HttpClient();
            await browser.GetStringAsync($"{q["redirect_uri"]}?code=C&state={Uri.EscapeDataString(q["state"]!)}");
        }));

        Assert.Contains("contacts permission", ex.Message);
        Assert.Empty(vault.Items);
    }

    private sealed class SignInHandler(string? scope = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var idToken = $"{B64("{}")}.{B64("{\"email\":\"Jane@Gmail.com\"}")}.s";
            var granted = scope is null ? "" : $",\"scope\":\"{scope}\"";
            return Task.FromResult(Json($$"""{"access_token":"AT","refresh_token":"REFRESH-TOKEN","expires_in":3600,"id_token":"{{idToken}}"{{granted}}}"""));
        }
    }

    // ---- store: folders, refresh, routing

    [Fact]
    public async Task Refresh_lists_each_saved_account_with_the_count_of_its_main_list_and_reports_problems()
    {
        var (accounts, vault, server) = Setup();
        vault.Save("google|gone@gmail.com", new ServerCredentials("gone@gmail.com", "R2"));
        var store = Store(accounts, server);

        var refresh = await store.RefreshFoldersAsync([], _ => true);

        Assert.Equal(2, refresh.AccountsAsked);              // the fake answers any account's token request
        var main = Assert.Single(refresh.Folders, f => f.Kind == FolderKind.GmailApi && f.AccountName == "Jane GMail");
        Assert.Equal(("My Contacts", "Google (jane@gmail.com)", 3), (main.Name, main.ServerName, main.ContactCount));
        Assert.True(main.IsReadable && main.HasCheckbox && main.CanWrite && !main.CanCreateFolder);
        Assert.Equal("jane@gmail.com", GooglePeopleStore.EmailOf(main));                    // the email is still known, whatever name is shown
        Assert.Contains(refresh.Folders, f => f.Kind == FolderKind.GmailApi && f.AccountName == "gone@gmail.com"); // no friendly name yet: the email
    }

    [Fact]
    public async Task Labels_are_listed_under_the_main_list_as_selectable_subfolders_with_their_counts()
    {
        var (accounts, _, server) = Setup();
        var store = Store(accounts, server);

        var folders = (await store.RefreshFoldersAsync([], _ => true)).Folders;

        Assert.Equal(["My Contacts", "Clients", "Scomage"], folders.Select(f => f.Name));    // built-in groups such as Starred are not shown
        var clients = folders[1];
        Assert.Equal(FolderKind.GmailLabel, clients.Kind);
        Assert.Equal(2, clients.ContactCount);
        Assert.True(clients.HasCheckbox);
        Assert.True(clients.IsReadable);
        Assert.StartsWith("      \u21B3 ", clients.ListName);                              // indented under My Contacts
        Assert.Equal("/My Contacts/Clients", clients.ShownPath);
        Assert.All(folders, f => Assert.Equal(("Jane GMail", "Google (jane@gmail.com)"), (f.AccountName, f.ServerName)));
        Assert.Equal("My Contacts", folders[0].ListName);
    }

    [Fact]
    public void Saved_accounts_show_at_once_without_a_count_and_by_their_friendly_name()
    {
        var (accounts, _, server) = Setup();

        var saved = Store(accounts, server).SavedFolders();

        var only = Assert.Single(saved);
        Assert.Equal(("Jane GMail", "My Contacts", null), (only.AccountName, only.Name, only.ContactCount));
    }

    [Fact]
    public void Account_names_are_kept_in_a_file_that_holds_no_tokens_and_a_damaged_file_is_survivable()
    {
        var file = Path.Combine(Path.GetTempPath(), $"names-{Guid.NewGuid():N}.json");
        try
        {
            var names = new GoogleAccountNames(file);
            Assert.Equal("a@gmail.com", names.DisplayFor("a@gmail.com"));        // no name yet: the email
            names.Set("A@Gmail.com", "Home Gmail");

            var again = new GoogleAccountNames(file);
            Assert.Equal("Home Gmail", again.DisplayFor("a@gmail.com"));
            Assert.Contains("Home Gmail", again.Names);
            Assert.DoesNotContain("token", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);

            File.WriteAllText(file, "{ not json");
            Assert.Equal("a@gmail.com", new GoogleAccountNames(file).DisplayFor("a@gmail.com"));

            names.Clear();
            Assert.False(File.Exists(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Label_rows_slot_in_under_their_account_and_stale_ones_can_be_removed()
    {
        ContactFolder F(string key, string name, FolderKind kind, int? n = null) => new($"{key}:/{name}", name, "/" + name, "Jane GMail", "Google", kind, false, n);
        var main = F("gmail-j", "My Contacts", FolderKind.GmailApi);
        var other = new ContactFolder("z:/Contacts", "Contacts", "/Contacts", "Zed", "srv", FolderKind.CardDav, true, 1);

        var withLabels = FolderList.Splice([main, other], [F("gmail-j", "My Contacts", FolderKind.GmailApi, 516), F("gmail-j", "Clients", FolderKind.GmailLabel, 2), F("gmail-j", "Scomage", FolderKind.GmailLabel, 3)]);

        Assert.Equal(["My Contacts", "Clients", "Scomage", "Contacts"], withLabels.Select(f => f.Name));
        Assert.Equal(516, withLabels[0].ContactCount);
    }

    [Fact]
    public async Task The_composite_store_sends_each_folder_to_its_own_store_and_combines_refresh_results()
    {
        var (accounts, _, server) = Setup();
        var gmail = Store(accounts, server);
        var dav = new FakeStore();
        var composite = new CompositeContactStore(dav, gmail);
        var davFolder = new ContactFolder("a1:/0/Contacts", "Contacts", "/0/Contacts", "Jane", "srv", FolderKind.CardDav, true, 5);

        await composite.ReadContactsAsync(davFolder);
        var fromGmail = await composite.ReadContactsAsync(Folder());
        var refresh = await composite.RefreshFoldersAsync([davFolder, Folder()], _ => true);

        Assert.Equal(["read a1:/0/Contacts", "refresh 1"], dav.Log);
        Assert.Equal(3, fromGmail.Count);
        Assert.Contains(refresh.Folders, f => f.Kind == FolderKind.GmailApi && f.ContactCount == 3);
        Assert.Contains(refresh.Folders, f => f.Id == "a1:/0/Contacts" && f.ContactCount == 99);
    }

    private sealed class FakeStore : IContactStore
    {
        public List<string> Log { get; } = [];
        public Task<IReadOnlyList<Contact>> ReadContactsAsync(ContactFolder folder, CancellationToken ct = default) { Log.Add("read " + folder.Id); return Task.FromResult<IReadOnlyList<Contact>>([]); }
        public Task<WriteReport> ReplaceContactsAsync(ContactFolder folder, IReadOnlyList<Contact> contacts, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WriteReport> CreateFolderAsync(ContactFolder accountFolder, string name, IReadOnlyList<Contact> contacts, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<FolderRefresh> RefreshFoldersAsync(IReadOnlyList<ContactFolder> folders, Func<ContactFolder, bool> mayContact, CancellationToken ct = default)
        {
            Log.Add("refresh " + folders.Count);
            return Task.FromResult(new FolderRefresh(folders.Select(f => f with { ContactCount = 99 }).ToList(), [], 1));
        }
    }

    // ---- vault listing

    [Fact]
    public void The_credential_vault_lists_saved_keys_by_prefix()
    {
        var prefix = "gtest-" + Guid.NewGuid().ToString("N") + "|";
        var vault = new WindowsCredentialVault();
        try
        {
            vault.Save(prefix + "a@gmail.com", new ServerCredentials("a@gmail.com", "x"));
            vault.Save(prefix + "b@gmail.com", new ServerCredentials("b@gmail.com", "y"));

            Assert.Equal([prefix + "a@gmail.com", prefix + "b@gmail.com"], vault.Keys(prefix).Order());
            Assert.Empty(vault.Keys("nothing-like-" + Guid.NewGuid().ToString("N")));
        }
        finally
        {
            vault.Delete(prefix + "a@gmail.com");
            vault.Delete(prefix + "b@gmail.com");
        }
    }
}
