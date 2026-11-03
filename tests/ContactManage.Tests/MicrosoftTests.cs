using System.Text.Json;
using System.Text.Json.Nodes;
using ContactManage.Adapters.MicrosoftGraph;
using ContactManage.Core;

namespace ContactManage.Tests;

public class MicrosoftTests
{
    private const string ClientId = "11112222-bbbb-3333-cccc-4444dddd5555";
    private const string Email = "jane@outlook.example";

    private static MicrosoftClientConfig Config() => new() { ClientId = ClientId };

    // ---- client file

    private static string TempFile(string content)
    {
        var file = Path.Combine(Path.GetTempPath(), $"mc-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, content);
        return file;
    }

    [Fact]
    public void The_client_file_needs_only_the_application_id_and_defaults_to_the_common_tenant()
    {
        var file = TempFile($$"""{"client_id":"{{ClientId}}"}""");
        try
        {
            var c = MicrosoftClientConfig.Load(file);
            Assert.Equal(ClientId, c.ClientId);
            Assert.Equal("common", c.Tenant);
            Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/token", c.TokenUri.AbsoluteUri);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData("""{"client_id":"not-a-guid"}""", "client_id")]
    [InlineData("""{"tenant":"common"}""", "client_id")]
    [InlineData("""{"client_id":"11112222-bbbb-3333-cccc-4444dddd5555","tenant":"evil.example/x"}""", "tenant")]
    [InlineData("not json", "could not be read")]
    public void A_bad_client_file_gets_a_plain_explanation(string content, string expected)
    {
        var file = TempFile(content);
        try { Assert.Contains(expected, Assert.Throws<MicrosoftAuthException>(() => MicrosoftClientConfig.Load(file)).Message); }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_missing_file_and_a_folder_are_explained()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Contains("not found", Assert.Throws<MicrosoftAuthException>(() => MicrosoftClientConfig.Load(Path.Combine(dir, "nope.json"))).Message);
            Assert.Contains("is a folder", Assert.Throws<MicrosoftAuthException>(() => MicrosoftClientConfig.Load(dir)).Message);
        }
        finally { Directory.Delete(dir); }
    }

    // ---- sign-in

    private static async Task<MicrosoftTokens> SignInAsync(MicrosoftAuth auth, Action<Uri>? seen = null) =>
        await auth.SignInAsync(async url =>
        {
            seen?.Invoke(url);
            // Play the browser: Microsoft would send the user back to redirect_uri with a code and the same state.
            var q = System.Web.HttpUtility.ParseQueryString(url.Query);
            using var browser = new HttpClient();
            var page = await browser.GetStringAsync($"{q["redirect_uri"]}?code=CODE123&state={Uri.EscapeDataString(q["state"]!)}");
            Assert.Contains("signed in", page);
        });

    [Fact]
    public async Task Sign_in_uses_pkce_a_localhost_redirect_the_contacts_scope_and_no_secret()
    {
        var graph = new FakeGraph();
        using var auth = new MicrosoftAuth(Config(), graph);
        Uri? opened = null;

        var tokens = await SignInAsync(auth, url => opened = url);

        Assert.Equal("AT1", tokens.AccessToken);
        Assert.Equal("RT2", tokens.RefreshToken);
        Assert.Equal(Email, tokens.Email);           // from preferred_username: a personal account may have no email claim
        Assert.Equal("login.microsoftonline.com", opened!.Host);
        var q = System.Web.HttpUtility.ParseQueryString(opened.Query);
        Assert.StartsWith("http://localhost:", q["redirect_uri"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal(ClientId, q["client_id"]);
        Assert.Contains("https://graph.microsoft.com/Contacts.ReadWrite", q["scope"]);
        Assert.Contains("offline_access", q["scope"]);
        Assert.DoesNotContain("Mail", q["scope"]);

        var form = System.Web.HttpUtility.ParseQueryString(Assert.Single(graph.TokenForms));
        Assert.Equal("CODE123", form["code"]);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.NotNull(form["code_verifier"]);
        Assert.Null(form["client_secret"]);          // a desktop app is a public client
        Assert.DoesNotContain("AT1", tokens.ToString());
    }

    [Fact]
    public async Task A_refused_sign_in_page_is_reported_without_waiting_for_the_timeout()
    {
        using var auth = new MicrosoftAuth(Config(), new FakeGraph());
        var ex = await Assert.ThrowsAsync<MicrosoftAuthException>(() => auth.SignInAsync(async url =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(url.Query);
            using var browser = new HttpClient();
            await browser.GetStringAsync($"{q["redirect_uri"]}?error=access_denied&state={Uri.EscapeDataString(q["state"]!)}");
        }));
        Assert.Contains("access_denied", ex.Message);
    }

    [Fact]
    public async Task A_stray_request_to_the_sign_in_port_cannot_cancel_the_sign_in()
    {
        using var auth = new MicrosoftAuth(Config(), new FakeGraph());

        var tokens = await auth.SignInAsync(async url =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(url.Query);
            using var browser = new HttpClient();
            // Another program on the PC knocks with an error and without the state of this sign-in: it must be ignored.
            using var stray = await browser.GetAsync($"{q["redirect_uri"]}?error=access_denied");
            Assert.False(stray.IsSuccessStatusCode);
            await browser.GetStringAsync($"{q["redirect_uri"]}?code=CODE123&state={Uri.EscapeDataString(q["state"]!)}");
        });

        Assert.Equal("AT1", tokens.AccessToken);
    }

    [Fact]
    public async Task A_rejected_refresh_token_asks_for_a_new_sign_in_and_shows_one_line_of_the_reason()
    {
        using var auth = new MicrosoftAuth(Config(), new FakeGraph { RefuseRefresh = true });
        var ex = await Assert.ThrowsAsync<MicrosoftAuthException>(() => auth.RefreshAsync("RT-old"));
        Assert.True(ex.SignInAgain);
        Assert.Contains("AADSTS70000", ex.Message);
        Assert.DoesNotContain("Trace ID", ex.Message);
    }

    // ---- saved accounts

    private static (MicrosoftAccounts Accounts, MemoryVault Vault, FakeGraph Graph) Setup(bool signedIn = true)
    {
        var vault = new MemoryVault();
        if (signedIn) vault.Save("microsoft|" + Email, new ServerCredentials(Email, "RT1"));
        var graph = new FakeGraph();
        return (new MicrosoftAccounts(vault, Config, graph), vault, graph);
    }

    [Fact]
    public async Task Signing_in_saves_only_the_refresh_token_under_the_account_address()
    {
        var (accounts, vault, graph) = Setup(signedIn: false);
        var email = await accounts.SignInAsync(async url =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(url.Query);
            using var browser = new HttpClient();
            await browser.GetStringAsync($"{q["redirect_uri"]}?code=C&state={Uri.EscapeDataString(q["state"]!)}");
        });

        Assert.Equal(Email, email);
        Assert.Equal([Email], accounts.SavedEmails());
        Assert.Equal("RT2", vault.Load("microsoft|" + Email)!.Password);
        Assert.Single(graph.TokenForms);
    }

    [Fact]
    public async Task A_sign_in_without_the_contacts_permission_is_refused_and_saves_nothing()
    {
        var (accounts, vault, graph) = Setup(signedIn: false);
        graph.ScopeOverride = "openid email";
        var ex = await Assert.ThrowsAsync<MicrosoftAuthException>(() => accounts.SignInAsync(async url =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(url.Query);
            using var browser = new HttpClient();
            await browser.GetStringAsync($"{q["redirect_uri"]}?code=C&state={Uri.EscapeDataString(q["state"]!)}");
        }));
        Assert.Contains("contacts permission", ex.Message);
        Assert.Empty(vault.Items);
    }

    [Fact]
    public async Task Refreshing_replaces_the_saved_refresh_token_with_the_new_one_and_reuses_the_access_token_until_it_expires()
    {
        var (accounts, vault, graph) = Setup();

        Assert.Equal("AT1", await accounts.AccessTokenAsync(Email));
        Assert.Equal("AT1", await accounts.AccessTokenAsync(Email));

        Assert.Single(graph.TokenForms);
        Assert.Equal("RT2", vault.Load("microsoft|" + Email)!.Password);
    }

    [Fact]
    public async Task A_sign_in_that_expired_is_forgotten_and_the_user_is_told_to_sign_in_again()
    {
        var (accounts, vault, graph) = Setup();
        graph.RefuseRefresh = true;

        var ex = await Assert.ThrowsAsync<MicrosoftAuthException>(() => accounts.AccessTokenAsync(Email));

        Assert.True(ex.SignInAgain);
        Assert.Contains("Add Microsoft account", ex.Message);
        Assert.Empty(vault.Items);
    }

    // ---- slots

    private static Contact With(Action<Contact> set) { var c = new Contact { DisplayName = "Pat" }; set(c); return c; }

    [Fact]
    public void Emails_beyond_the_third_do_not_fit()
    {
        var s = MicrosoftSlots.Of(With(c => c.Emails.AddRange(["a@x.example", "b@x.example", "c@x.example", "d@x.example"])));
        Assert.Equal(["a@x.example", "b@x.example", "c@x.example"], s.Emails);
        Assert.Equal(1, s.Overflow);
    }

    [Fact]
    public void Phones_go_where_their_type_says_and_spill_into_free_places_before_anything_is_lost()
    {
        var c = With(c =>
        {
            c.Phones.AddRange(["+1 201 555 0001", "+1 201 555 0002", "+1 201 555 0003", "+1 201 555 0004"]);
            foreach (var p in c.Phones) c.Labels[p] = ";TYPE=HOME";       // four home numbers: two fit at home, two take the free places
        });

        var s = MicrosoftSlots.Of(c);

        Assert.Equal(2, s.HomePhones.Count);
        Assert.NotNull(s.MobilePhone);
        Assert.Single(s.BusinessPhones);
        Assert.Equal(0, s.Overflow);
        c.Phones.AddRange(["+1 201 555 0005", "+1 201 555 0006"]);
        Assert.Equal(1, MicrosoftSlots.Of(c).Overflow);                 // five places in all
    }

    [Fact]
    public void Addresses_use_their_own_place_and_otherwise_the_first_free_one()
    {
        var c = With(c =>
        {
            c.Addresses.AddRange([";;1 Main St;Town;;;", ";;2 Oak St;Town;;;", ";;3 Elm St;Town;;;", ";;4 Fir St;Town;;;"]);
            c.Labels[c.Addresses[0]] = ";TYPE=HOME";
            c.Labels[c.Addresses[1]] = ";TYPE=HOME";
        });

        var s = MicrosoftSlots.Of(c);

        Assert.Equal(";;1 Main St;Town;;;", s.HomeAddress);
        Assert.NotNull(s.BusinessAddress);
        Assert.NotNull(s.OtherAddress);
        Assert.Equal(1, s.Overflow);
    }

    [Fact]
    public void The_warning_appears_only_for_a_microsoft_target_and_only_when_something_does_not_fit()
    {
        var big = With(c => c.Emails.AddRange(["a@x.example", "b@x.example", "c@x.example", "d@x.example", "e@x.example"]));
        var microsoft = new ContactFolder("ms-a:/F1", "Contacts", "/F1", "a", "srv", FolderKind.Microsoft, true);
        var dav = microsoft with { Kind = FolderKind.CardDav };

        Assert.Contains("2 values do not fit", MicrosoftSlots.Warning([microsoft], [big]));
        Assert.Equal("", MicrosoftSlots.Warning([dav], [big]));
        Assert.Equal("", MicrosoftSlots.Warning([microsoft], [With(c => c.Emails.Add("a@x.example"))]));
    }

    // ---- mapper

    [Fact]
    public void A_graph_contact_becomes_a_contact_with_labels_and_keeps_the_rest_as_raw_lines()
    {
        var item = JsonDocument.Parse("""
            {"id":"C1","changeKey":"K1","displayName":"Dr. Ann Lee","givenName":"Ann","surname":"Lee","middleName":"B","title":"Dr.","generation":"Jr.",
             "companyName":"Acme","jobTitle":"Buyer","department":"Purchasing","personalNotes":"Likes tea",
             "emailAddresses":[{"name":"Ann","address":"Ann@X.example"},{"address":"ann@x.example"}],
             "businessPhones":["+1 415 555 0101"],"homePhones":["+1 415 555 0102"],"mobilePhone":"+1 415 555 0103",
             "homeAddress":{"street":"1 Main St","city":"Springfield","state":"IL","postalCode":"62701","countryOrRegion":"USA"},
             "businessAddress":{},"categories":["Clients","Blue"],
             "nickName":"Annie","birthday":"1980-02-29T00:00:00Z","spouseName":"","children":[],"imAddresses":["ann@im.example"]}
            """).RootElement;

        var c = MicrosoftContactMapper.FromGraph(item, "ms-a:/F1");

        Assert.Equal("C1", c.RemoteId);
        Assert.Equal("K1", c.RemoteEtag);
        Assert.Equal("ms-a:/F1", c.SourceFolderId);
        Assert.Equal(("Ann", "Lee", "B", "Dr.", "Jr."), (c.GivenName, c.FamilyName, c.MiddleName, c.NamePrefix, c.NameSuffix));
        Assert.Equal(("Acme", "Buyer", "Purchasing", "Likes tea"), (c.Company, c.JobTitle, c.Department, c.Notes));
        Assert.Equal(["Ann@X.example"], c.Emails);                                   // the case-insensitive repeat is one address
        Assert.Equal(["+1 415 555 0101", "+1 415 555 0102", "+1 415 555 0103"], c.Phones);
        Assert.Equal(";TYPE=HOME", c.Labels["+1 415 555 0102"]);
        Assert.Equal(";TYPE=CELL", c.Labels["+1 415 555 0103"]);
        Assert.Equal([";;1 Main St;Springfield;IL;62701;USA"], c.Addresses);          // the empty business address is not an address
        Assert.Equal(["Clients", "Blue"], c.Groups);
        Assert.Contains("X-MS-NICKNAME:\"Annie\"", c.Extra);
        Assert.Contains("X-MS-BIRTHDAY:\"1980-02-29T00:00:00Z\"", c.Extra);
        Assert.Contains("X-MS-IMADDRESSES:[\"ann@im.example\"]", c.Extra);
        Assert.DoesNotContain(c.Extra, l => l.StartsWith("X-MS-SPOUSENAME") || l.StartsWith("X-MS-CHILDREN"));   // empty values are not worth keeping
    }

    [Fact]
    public void An_outlook_contact_with_no_birthday_does_not_get_a_year_one_line()
    {
        var item = JsonDocument.Parse("""{"id":"C1","displayName":"A","birthday":"0001-01-01T00:00:00Z"}""").RootElement;
        Assert.Empty(MicrosoftContactMapper.FromGraph(item, "f").Extra);
    }

    private static Contact Merged() => With(c =>
    {
        c.DisplayName = "Ann Lee"; c.GivenName = "Ann"; c.FamilyName = "Lee"; c.Company = "Acme";
        c.Emails.AddRange(["ann@x.example", "ann2@x.example"]);
        c.Phones.AddRange(["+1 415 555 0101", "+1 415 555 0102"]);
        c.Labels["+1 415 555 0102"] = ";TYPE=CELL";
        c.Addresses.Add(";;1 Main St;Springfield;IL;62701;USA");
        c.Labels[c.Addresses[0]] = ";TYPE=HOME";
        c.Groups.Add("Clients");
        c.Extra.AddRange(["X-MS-NICKNAME:\"Annie\"", "BDAY:19800229", "URL:https://ann.example", "X-GOOGLE-EVENTS:[]"]);
    });

    [Fact]
    public void A_new_contact_carries_only_what_it_has()
    {
        var draft = MicrosoftContactMapper.ToGraph(Merged(), forUpdate: false);
        var b = draft.Body;

        Assert.Equal("Ann Lee", (string?)b["displayName"]);
        Assert.Equal("Lee", (string?)b["surname"]);
        Assert.Equal("ann@x.example", (string?)b["emailAddresses"]![0]!["address"]);
        Assert.Equal("+1 415 555 0101", (string?)b["businessPhones"]![0]);
        Assert.Equal("+1 415 555 0102", (string?)b["mobilePhone"]);
        Assert.Equal("1 Main St", (string?)b["homeAddress"]!["street"]);
        Assert.Equal("IL", (string?)b["homeAddress"]!["state"]);
        Assert.Equal("Clients", (string?)b["categories"]![0]);
        Assert.Equal("Annie", (string?)b["nickName"]);
        Assert.Equal("1980-02-29T00:00:00Z", (string?)b["birthday"]);
        Assert.Equal("https://ann.example", (string?)b["businessHomePage"]);
        Assert.Null(b["jobTitle"]);
        Assert.False(b.ContainsKey("jobTitle"));
        Assert.Equal(1, draft.Skipped);                                              // the Google-only line has no place here
    }

    [Fact]
    public void An_update_names_every_managed_property_so_cleared_values_are_cleared()
    {
        var c = With(c => { c.DisplayName = "Ann"; c.Emails.Add("ann@x.example"); });

        var b = MicrosoftContactMapper.ToGraph(c, forUpdate: true).Body;

        Assert.True(b.ContainsKey("jobTitle") && b["jobTitle"] is null);
        Assert.True(b.ContainsKey("homeAddress") && b["homeAddress"] is null);
        Assert.True(b.ContainsKey("mobilePhone") && b["mobilePhone"] is null);
        Assert.Empty(b["businessPhones"]!.AsArray());
        Assert.Empty(b["categories"]!.AsArray());
        Assert.Single(b["emailAddresses"]!.AsArray());
    }

    [Fact]
    public void A_contact_survives_a_round_trip_through_graph_json()
    {
        var original = Merged();
        original.Extra.Remove("X-GOOGLE-EVENTS:[]");
        var json = MicrosoftContactMapper.ToGraph(original, forUpdate: false).Body;
        json["id"] = "C9";

        var back = MicrosoftContactMapper.FromGraph(JsonDocument.Parse(json.ToJsonString()).RootElement, "f");

        Assert.Equal(original.Emails, back.Emails);
        Assert.Equal(original.Phones.Order(), back.Phones.Order());
        Assert.Equal(original.Addresses, back.Addresses);
        Assert.Equal(original.Groups, back.Groups);
        Assert.Equal(("Ann", "Lee", "Acme"), (back.GivenName, back.FamilyName, back.Company));
        Assert.Contains("X-MS-NICKNAME:\"Annie\"", back.Extra);
    }
}
