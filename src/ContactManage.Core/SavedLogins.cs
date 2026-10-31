namespace ContactManage.Core;

/// <summary>
/// Decides when a login is reused, saved or forgotten. A login is saved only after the server accepted it, and only
/// when the user ticked "remember"; one the server rejects is removed so it is never offered again.
/// Each entry is tied to the server address as well as the account, so it is never sent anywhere else.
/// </summary>
public sealed class SavedLogins(ICredentialVault vault)
{
    private readonly Dictionary<string, ServerCredentials> _toSave = new();

    public static string KeyFor(ContactFolder folder)
    {
        var host = Uri.TryCreate(folder.ServerUrl, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : "";
        return $"{host}|{folder.AccountKey}";
    }

    /// <summary>The saved login for this account, or null if there is none.</summary>
    public ServerCredentials? Find(ContactFolder folder) => vault.Load(KeyFor(folder));

    /// <summary>The user typed this login and asked to remember it: keep it until the server accepts it.</summary>
    public void RememberAfterSignIn(ContactFolder folder, ServerCredentials credentials) =>
        _toSave[KeyFor(folder)] = credentials;

    /// <summary>The server accepted the login: save it now if the user asked for that.</summary>
    public void SignedIn(ContactFolder folder)
    {
        if (_toSave.Remove(KeyFor(folder), out var credentials)) vault.Save(KeyFor(folder), credentials);
    }

    /// <summary>The server refused the login: forget it, saved or waiting.</summary>
    public void Rejected(ContactFolder folder)
    {
        _toSave.Remove(KeyFor(folder));
        vault.Delete(KeyFor(folder));
    }

    public int ForgetAll()
    {
        _toSave.Clear();
        return vault.DeleteAll();
    }
}
