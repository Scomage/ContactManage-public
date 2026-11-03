using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace ContactManage.Adapters.MicrosoftGraph;

/// <param name="RefreshToken">Long-lived, and replaced by a new one every time it is used; this is what may be saved, in Credential Manager.</param>
/// <param name="Email">The Microsoft account that signed in, from the id token.</param>
/// <param name="GrantedScope">The permissions the user actually approved.</param>
public sealed record MicrosoftTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string? Email, string? GrantedScope = null)
{
    // Tokens are secrets: never let a log or a message show them.
    public override string ToString() => $"MicrosoftTokens {{ Email = {Email}, ExpiresAt = {ExpiresAt:u} }}";
}

/// <summary>
/// Microsoft sign-in for a desktop app: the system browser shows Microsoft's own page (the password is typed there, never in this app),
/// and the answer comes back to a listener on this PC's loopback address. PKCE and a state value guard the exchange. The app is a
/// public client, so no secret is sent.
/// </summary>
public sealed class MicrosoftAuth(MicrosoftClientConfig config, HttpMessageHandler? handler = null) : IDisposable
{
    public const string ContactsScope = "https://graph.microsoft.com/Contacts.ReadWrite";

    /// <summary>Contacts only (read and write), the account's address for naming the sign-in, and a lasting sign-in. No mail access.</summary>
    public const string Scopes = "openid email offline_access " + ContactsScope;

    private readonly HttpClient _http = new(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(30) };

    public void Dispose() => _http.Dispose();

    /// <summary>Opens Microsoft's sign-in page through <paramref name="openBrowser"/> and waits for the user to finish there.</summary>
    public async Task<MicrosoftTokens> SignInAsync(Func<Uri, Task> openBrowser, string? loginHint = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var verifier = OAuthLoopback.RandomUrlSafe(48);
        var state = OAuthLoopback.RandomUrlSafe(24);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        // Microsoft ignores the port of a registered http://localhost address, so the random port needs no registration of its own.
        var redirect = $"http://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}/";

        var query = new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirect,
            ["response_mode"] = "query",
            ["scope"] = Scopes,
            ["state"] = state,
            ["code_challenge"] = OAuthLoopback.Challenge(verifier),
            ["code_challenge_method"] = "S256",
            ["prompt"] = "select_account",   // so a second account can be added even while the browser is signed in to the first
        };
        if (!string.IsNullOrWhiteSpace(loginHint)) query["login_hint"] = loginHint;
        var authUrl = new UriBuilder(config.AuthUri) { Query = string.Join("&", query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}")) }.Uri;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
        // Listen while the browser is being opened, so neither waits for the other. If opening fails, stop waiting.
        var opening = openBrowser(authUrl);
        _ = opening.ContinueWith(_ => cts.Cancel(), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        string code;
        try
        {
            code = await OAuthLoopback.WaitForCodeAsync(listener, state, error => new MicrosoftAuthException($"Microsoft sign-in was cancelled or refused ({error})."), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (opening.IsFaulted) await opening.ConfigureAwait(false); // the real reason: the browser could not be opened
            throw new MicrosoftAuthException("Microsoft sign-in was not finished in time. Try again.");
        }
        await opening.ConfigureAwait(false);

        return await PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["scope"] = Scopes,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirect,
            ["grant_type"] = "authorization_code",
        }, existingRefresh: null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A fresh access token from a saved refresh token. The answer holds a new refresh token, which must replace the saved one.
    /// Throws with <see cref="MicrosoftAuthException.SignInAgain"/> when Microsoft no longer accepts it.
    /// </summary>
    public Task<MicrosoftTokens> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
        PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["scope"] = Scopes,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        }, existingRefresh: refreshToken, ct);

    private async Task<MicrosoftTokens> PostTokenAsync(Dictionary<string, string> form, string? existingRefresh, CancellationToken ct)
    {
        using var resp = await _http.PostAsync(config.TokenUri, new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        JsonElement root;
        try { using var doc = JsonDocument.Parse(body); root = doc.RootElement.Clone(); }
        catch (JsonException) { throw new MicrosoftAuthException($"Microsoft's sign-in service gave an unreadable answer (HTTP {(int)resp.StatusCode})."); }

        if (!resp.IsSuccessStatusCode)
        {
            // The error code is a short word; the description names an AADSTS code and carries trace ids, never tokens. Only the first line is shown.
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            var detail = root.TryGetProperty("error_description", out var d) ? d.GetString()?.Split('\n')[0].Trim() : null;
            throw new MicrosoftAuthException($"Microsoft refused the sign-in: {error}{(detail is null ? "" : " - " + detail)}",
                signInAgain: error is "invalid_grant" or "interaction_required");
        }

        var access = root.GetProperty("access_token").GetString()!;
        var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : existingRefresh;
        var expires = root.TryGetProperty("expires_in", out var x) ? ExpiresIn(x) : 3600;
        // A personal account may have no "email" claim; its sign-in name is then the address.
        var email = root.TryGetProperty("id_token", out var id) ? OAuthLoopback.ClaimFromIdToken(id.GetString(), "email", "preferred_username") : null;
        var scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() : null;
        return new MicrosoftTokens(access, refresh, DateTimeOffset.UtcNow.AddSeconds(expires - 60), email, scope);
    }

    // Microsoft sends expires_in as a number; some proxies and libraries turn it into a string.
    private static int ExpiresIn(JsonElement x) =>
        x.ValueKind == JsonValueKind.Number ? x.GetInt32() : int.TryParse(x.GetString(), out var n) ? n : 3600;
}
