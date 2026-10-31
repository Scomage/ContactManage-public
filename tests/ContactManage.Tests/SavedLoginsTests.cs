using ContactManage.Adapters;
using ContactManage.Core;

namespace ContactManage.Tests;

public class SavedLoginsTests
{
    private static ContactFolder Folder(string account = "acct1", string url = "https://Mail.Example.com/webdav/") =>
        new($"{account}:/0/Contacts", "Contacts", "/0/Contacts", "Jane", "mail.example.com", FolderKind.CardDav, true, ServerUrl: url);

    private static readonly ServerCredentials Jane = new("jane@example.com", "secret");

    [Fact]
    public void A_login_is_saved_only_after_the_server_accepted_it()
    {
        var vault = new MemoryVault();
        var logins = new SavedLogins(vault);

        logins.RememberAfterSignIn(Folder(), Jane);
        Assert.Empty(vault.Items);

        logins.SignedIn(Folder());
        Assert.Equal(Jane, logins.Find(Folder()));
    }

    [Fact]
    public void A_login_the_user_did_not_ask_to_remember_is_not_saved()
    {
        var vault = new MemoryVault();
        new SavedLogins(vault).SignedIn(Folder());
        Assert.Empty(vault.Items);
    }

    [Fact]
    public void A_rejected_login_is_forgotten_whether_saved_or_waiting()
    {
        var vault = new MemoryVault();
        var logins = new SavedLogins(vault);
        vault.Save(SavedLogins.KeyFor(Folder()), Jane);
        logins.RememberAfterSignIn(Folder(), new ServerCredentials("jane@example.com", "typo"));

        logins.Rejected(Folder());
        logins.SignedIn(Folder());

        Assert.Empty(vault.Items);
    }

    [Fact]
    public void A_login_belongs_to_one_account_on_one_server()
    {
        var vault = new MemoryVault();
        var logins = new SavedLogins(vault);
        logins.RememberAfterSignIn(Folder(), Jane);
        logins.SignedIn(Folder());

        Assert.Null(logins.Find(Folder("acct2")));
        Assert.Null(logins.Find(Folder(url: "https://other.example.net/webdav/")));
        Assert.Equal(Jane, logins.Find(Folder(url: "https://mail.example.com/other/path/"))); // host only, case-insensitive
    }

    [Fact]
    public void Forgetting_everything_reports_how_many_were_removed()
    {
        var vault = new MemoryVault();
        var logins = new SavedLogins(vault);
        vault.Save("a", Jane);
        vault.Save("b", Jane);
        logins.RememberAfterSignIn(Folder(), Jane);

        Assert.Equal(2, logins.ForgetAll());
        logins.SignedIn(Folder());
        Assert.Empty(vault.Items);
    }

    [Fact]
    public void Windows_Credential_Manager_round_trips_a_login_and_deletes_it()
    {
        var key = "test|" + Guid.NewGuid().ToString("N"); // never touches the user's real entries
        var vault = new WindowsCredentialVault();
        try
        {
            Assert.Null(vault.Load(key));
            vault.Save(key, new ServerCredentials("jane@example.com", "pässword with spaces"));

            var loaded = vault.Load(key);
            Assert.Equal(new ServerCredentials("jane@example.com", "pässword with spaces"), loaded);

            vault.Save(key, new ServerCredentials("jane@example.com", "changed"));
            Assert.Equal("changed", vault.Load(key)!.Password);
        }
        finally { vault.Delete(key); }

        Assert.Null(vault.Load(key));
        vault.Delete(key); // deleting what is not there is not an error
    }
}
