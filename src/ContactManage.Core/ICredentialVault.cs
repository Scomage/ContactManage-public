namespace ContactManage.Core;

/// <summary>Remembers logins between sessions, one entry per account on one server.</summary>
public interface ICredentialVault
{
    ServerCredentials? Load(string key);
    void Save(string key, ServerCredentials credentials);
    void Delete(string key);

    /// <summary>Removes every login this app saved. Returns how many there were.</summary>
    int DeleteAll();

    /// <summary>The keys of the saved logins that start with <paramref name="prefix"/>, without the app's own entry prefix.</summary>
    IReadOnlyList<string> Keys(string prefix);
}
