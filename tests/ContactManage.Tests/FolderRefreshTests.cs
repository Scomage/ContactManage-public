using ContactManage.Adapters.CardDav;
using ContactManage.Core;

namespace ContactManage.Tests;

public class FolderRefreshTests
{
    private const string Url = "https://dav.example.com/dav/";

    private static ContactFolder Cached(string account, string path, string name, int? count) =>
        new($"{account}:{path}", name, path, "Jane", "dav.example.com", FolderKind.CardDav, true, count, ServerUrl: Url);

    private static string Card(string uid, string name) =>
        $"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:{uid}\r\nFN:{name}\r\nN:;{name};;;\r\nEND:VCARD\r\n";

    [Fact]
    public void Splice_replaces_known_folders_and_puts_new_ones_after_their_account()
    {
        var original = new[] { Cached("a1", "/0/Contacts", "Contacts", 5), Cached("b1", "/0/Contacts", "Contacts", 9), Cached("a1", "/0/Team", "Team", 2) };
        var fromServer = new[] { Cached("a1", "/0/Contacts", "Contacts", 7), Cached("a1", "/0/Merged", "Merged", 27) };

        var result = FolderList.Splice(original, fromServer);

        Assert.Equal(["a1:/0/Contacts", "b1:/0/Contacts", "a1:/0/Team", "a1:/0/Merged"], result.Select(f => f.Id));
        Assert.Equal(7, result[0].ContactCount);          // fresh count
        Assert.Equal(9, result[1].ContactCount);          // account not asked: untouched
        Assert.Equal(2, result[2].ContactCount);          // known folder the server did not return: untouched
    }

    [Fact]
    public void A_folder_of_a_new_account_is_placed_by_account_name_not_at_the_end()
    {
        ContactFolder F(string account, string key) => new($"{key}:/Contacts", "Contacts", "/Contacts", account, "srv", FolderKind.CardDav, true, 1);
        var original = new[] { F("Home AOL", "k1"), F("Home Work", "k2"), F("Pat Smith", "k3") };

        var result = FolderList.Splice(original, [F("jane.doe@gmail.com", "k4")]);

        Assert.Equal(["Home AOL", "Home Work", "jane.doe@gmail.com", "Pat Smith"], result.Select(f => f.AccountName));
        Assert.Equal("zed@gmail.com", FolderList.Splice(original, [F("zed@gmail.com", "k5")]).Last().AccountName);
    }

    private static ContactFolder Gmail(string email, string path, FolderKind kind) =>
        new($"gmail-{email}:{path}", path, path, email, "Gmail", kind, true, 5);

    [Theory]
    [InlineData(FolderKind.GmailApi, FolderKind.GmailLabel)]
    [InlineData(FolderKind.GmailLabel, FolderKind.GmailLabel)]
    public void Two_rows_of_one_google_account_are_refused(FolderKind first, FolderKind second)
    {
        var problem = FolderList.SelectionProblem([Gmail("me@gmail.com", "/a", first), Gmail("me@gmail.com", "/b", second)]);

        Assert.NotNull(problem);
        Assert.Contains("me@gmail.com", problem);
    }

    [Fact]
    public void One_row_per_google_account_and_any_number_of_carddav_folders_are_fine()
    {
        Assert.Null(FolderList.SelectionProblem([Gmail("me@gmail.com", "/a", FolderKind.GmailLabel), Gmail("you@gmail.com", "/a", FolderKind.GmailApi)]));
        Assert.Null(FolderList.SelectionProblem([Cached("a1", "/0/Contacts", "Contacts", 5), Cached("a1", "/0/Team", "Team", 2), Gmail("me@gmail.com", "/a", FolderKind.GmailApi)]));
        Assert.Null(FolderList.SelectionProblem([]));
    }

    [Fact]
    public async Task A_folder_the_client_has_not_synced_yet_appears_with_its_count()
    {
        var server = new FakeDavServer();
        server.Add(FakeDavServer.Home + "Contacts/", "a.vcf", Card("u1", "Ann"));
        server.Books[FakeDavServer.Home + "Merged-3f9a2c/"] = "Merged contacts";
        server.Add(FakeDavServer.Home + "Merged-3f9a2c/", "x.vcf", Card("u2", "Bob"));
        server.Add(FakeDavServer.Home + "Merged-3f9a2c/", "y.vcf", Card("u3", "Cy"));
        using var store = new CardDavContactStore((_, _) => Task.FromResult<ServerCredentials?>(new ServerCredentials(FakeDavServer.User, FakeDavServer.Password)), server);

        var refresh = await store.RefreshFoldersAsync([Cached("acct1", "/0/Contacts", "Contacts", 0)], _ => true);

        Assert.Empty(refresh.Problems);
        Assert.Equal(1, refresh.AccountsAsked);
        Assert.Equal(1, refresh.Folders.Single(f => f.Name == "Contacts").ContactCount);       // the cache said 0
        var merged = refresh.Folders.Single(f => f.Name == "Merged contacts");
        Assert.Equal(2, merged.ContactCount);
        Assert.Equal("acct1:/0/Merged-3f9a2c", merged.Id);
        Assert.True(merged.CanWrite);

        // And the new folder can be read through the normal path.
        Assert.Equal(["Bob", "Cy"], (await store.ReadContactsAsync(merged)).Select(c => c.DisplayName).Order());
    }

    [Fact]
    public async Task An_account_without_a_session_or_saved_login_is_skipped_without_asking_for_a_password()
    {
        var asked = 0;
        using var store = new CardDavContactStore((_, _) => { asked++; return Task.FromResult<ServerCredentials?>(null); }, new FakeDavServer());

        var refresh = await store.RefreshFoldersAsync([Cached("acct1", "/0/Contacts", "Contacts", 3)], _ => false);

        Assert.Equal(0, asked);
        Assert.Equal(0, refresh.AccountsAsked);
        Assert.Empty(refresh.Folders);
    }

    [Fact]
    public async Task A_server_that_refuses_the_login_is_reported_not_thrown()
    {
        var server = new FakeDavServer();
        using var store = new CardDavContactStore((_, _) => Task.FromResult<ServerCredentials?>(new ServerCredentials(FakeDavServer.User, "wrong")), server);

        var refresh = await store.RefreshFoldersAsync([Cached("acct1", "/0/Contacts", "Contacts", 3)], _ => true);

        Assert.Equal(0, refresh.AccountsAsked);
        Assert.Contains("Jane", Assert.Single(refresh.Problems));
    }

    [Fact]
    public async Task A_cached_folder_that_cannot_be_matched_is_not_duplicated_by_a_book_of_the_same_name()
    {
        var server = new FakeDavServer();
        using var store = new CardDavContactStore((_, _) => Task.FromResult<ServerCredentials?>(new ServerCredentials(FakeDavServer.User, FakeDavServer.Password)), server);

        // The cache path does not exist on the server, but exactly one book is called "Team" (nested, so not listed at the top).
        var refresh = await store.RefreshFoldersAsync([Cached("acct1", "/0/Elsewhere/Team", "Team", 4)], _ => true);

        Assert.DoesNotContain(refresh.Folders, f => f.Name == "Team" && f.Id != "acct1:/0/Elsewhere/Team");
    }
}
