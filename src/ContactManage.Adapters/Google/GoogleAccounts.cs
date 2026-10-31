using ContactManage.Core;

namespace ContactManage.Adapters.Google;

/// <summary>
/// The Google accounts the user has signed in to. The long-lived refresh token is kept in the credential vault
/// (Windows Credential Manager) under "google|&lt;email&gt;"; short-lived access tokens stay in memory.
/// A refresh token Google no longer accepts (revoked, or expired after 7 days while the app is in Testing) is deleted,
/// and the user is told to sign in again.
/// </summary>
public sealed class GoogleAccounts(ICredentialVault vault, Func<GoogleClientConfig> loadConfig, HttpMessageHandler? tokenHandler = null)
{
    private const string KeyPrefix = "google|";

    private readonly Dictionary<string, GoogleTokens> _access = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The Google accounts with a saved sign-in. No network access.</summary>
    public IReadOnlyList<string> SavedEmails() => vault.Keys(KeyPrefix).Select(k => k[KeyPrefix.Length..]).Order().ToList();

    /// <summary>Opens Google's sign-in page through <paramref name="openBrowser"/>, saves the sign-in, and returns the account's email address.</summary>
    public async Task<string> SignInAsync(Func<Uri, Task> openBrowser, string? loginHint = null, CancellationToken ct = default)
    {
        using var auth = new GoogleAuth(loadConfig(), tokenHandler);
        var tokens = await auth.SignInAsync(openBrowser, loginHint, ct: ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(tokens.Email) || tokens.RefreshToken is null)
            throw new GoogleAuthException("Google did not return the account's email address or a lasting sign-in. Try again.");

        // Without the contacts permission every later request would fail with a bare 403, so say what happened now and save nothing.
        if (tokens.GrantedScope is { } granted && !granted.Split(' ').Contains("https://www.googleapis.com/auth/contacts"))
            throw new GoogleAuthException("Google signed you in, but the contacts permission was not ticked on its consent page. " +
                "Use Add Google account again and keep every box ticked, including the one about contacts.");

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

            var saved = vault.Load(KeyPrefix + email.ToLowerInvariant())
                ?? throw new GoogleAuthException($"{email} is not signed in. Use Add Google account.", signInAgain: true);
            using var auth = new GoogleAuth(loadConfig(), tokenHandler);
            try
            {
                var fresh = await auth.RefreshAsync(saved.Password, ct).ConfigureAwait(false);
                _access[email] = fresh;
                return fresh.AccessToken;
            }
            catch (GoogleAuthException ex) when (ex.SignInAgain)
            {
                vault.Delete(KeyPrefix + email.ToLowerInvariant());
                _access.Remove(email);
                throw new GoogleAuthException($"The Gmail sign-in for {email} expired or was revoked. Use Add Google account to sign in again.", signInAgain: true);
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
