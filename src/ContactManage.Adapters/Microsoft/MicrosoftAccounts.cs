using ContactManage.Core;

namespace ContactManage.Adapters.MicrosoftGraph;

/// <summary>
/// The Microsoft accounts the user has signed in to. The long-lived refresh token is kept in the credential vault
/// (Windows Credential Manager) under "microsoft|&lt;email&gt;"; short-lived access tokens stay in memory. Microsoft replaces the
/// refresh token every time it is used, so each refresh saves the new one. A token Microsoft no longer accepts is deleted and the
/// user is told to sign in again.
/// </summary>
public sealed class MicrosoftAccounts(ICredentialVault vault, Func<MicrosoftClientConfig> loadConfig, HttpMessageHandler? tokenHandler = null)
{
    private const string KeyPrefix = "microsoft|";

    private readonly Dictionary<string, MicrosoftTokens> _access = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The Microsoft accounts with a saved sign-in. No network access.</summary>
    public IReadOnlyList<string> SavedEmails() => vault.Keys(KeyPrefix).Select(k => k[KeyPrefix.Length..]).Order().ToList();

    /// <summary>Opens Microsoft's sign-in page through <paramref name="openBrowser"/>, saves the sign-in, and returns the account's email address.</summary>
    public async Task<string> SignInAsync(Func<Uri, Task> openBrowser, string? loginHint = null, CancellationToken ct = default)
    {
        using var auth = new MicrosoftAuth(loadConfig(), tokenHandler);
        var tokens = await auth.SignInAsync(openBrowser, loginHint, ct: ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(tokens.Email) || tokens.RefreshToken is null)
            throw new MicrosoftAuthException("Microsoft did not return the account's email address or a lasting sign-in. Try again.");

        // Without the contacts permission every later request would fail with a bare 403, so say what happened now and save nothing.
        if (tokens.GrantedScope is { } granted &&
            !granted.Split(' ').Any(s => s.EndsWith("Contacts.ReadWrite", StringComparison.OrdinalIgnoreCase)))
            throw new MicrosoftAuthException("Microsoft signed you in, but the contacts permission was not granted. Use Add Microsoft account again and accept the request to read and change your contacts.");

        var email = tokens.Email.Trim().ToLowerInvariant();
        vault.Save(KeyPrefix + email, new ServerCredentials(email, tokens.RefreshToken));
        _access[email] = tokens;
        return email;
    }

    /// <summary>A valid access token for the account, refreshed from the saved sign-in when the last one ran out.</summary>
    public async Task<string> AccessTokenAsync(string email, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_access.TryGetValue(email, out var current) && current.ExpiresAt > DateTimeOffset.UtcNow) return current.AccessToken;

            var key = KeyPrefix + email.ToLowerInvariant();
            var saved = vault.Load(key)
                ?? throw new MicrosoftAuthException($"{email} is not signed in. Use Add Microsoft account.", signInAgain: true);
            using var auth = new MicrosoftAuth(loadConfig(), tokenHandler);
            try
            {
                var fresh = await auth.RefreshAsync(saved.Password, ct).ConfigureAwait(false);
                // The old refresh token is finished with; keep the one that came back, or the next start would have to sign in again.
                if (fresh.RefreshToken is { } rotated && rotated != saved.Password) vault.Save(key, new ServerCredentials(saved.UserName, rotated));
                _access[email] = fresh;
                return fresh.AccessToken;
            }
            catch (MicrosoftAuthException ex) when (ex.SignInAgain)
            {
                vault.Delete(key);
                _access.Remove(email);
                throw new MicrosoftAuthException($"The Microsoft sign-in for {email} expired or was revoked. Use Add Microsoft account to sign in again.", signInAgain: true);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Removes the saved sign-in of one account.</summary>
    public void Forget(string email)
    {
        vault.Delete(KeyPrefix + email.ToLowerInvariant());
        _access.Remove(email);
    }
}
