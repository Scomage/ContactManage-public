using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContactManage.Adapters.Google;

public class GoogleAuthException(string message, bool signInAgain = false) : Exception(message)
{
    /// <summary>True when the saved sign-in no longer works (revoked, or expired after 7 days while the app is in Testing) and the user must sign in again.</summary>
    public bool SignInAgain { get; } = signInAgain;
}

/// <param name="RefreshToken">Long-lived; only present on the first sign-in. This is what may be saved, in Credential Manager.</param>
/// <param name="Email">The Google account that signed in, from the id token.</param>
/// <param name="GrantedScope">The permissions the user actually approved; Google lets them untick individual ones on its consent page.</param>
public sealed record GoogleTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string? Email, string? GrantedScope = null)
{
    // Tokens are secrets: never let a log or a message show them.
    public override string ToString() => $"GoogleTokens {{ Email = {Email}, ExpiresAt = {ExpiresAt:u} }}";
}

/// <summary>
/// Google sign-in for a desktop app: the system browser shows Google's own page (the user's password is typed there, never in this app),
/// and the answer comes back to a listener on this PC's loopback address. PKCE and a state value guard the exchange.
/// </summary>
public sealed class GoogleAuth(GoogleClientConfig config, HttpMessageHandler? handler = null) : IDisposable
{
    /// <summary>Contacts only (read and write), plus the account's email address so the sign-in can be named. No mail access.</summary>
    public const string Scopes = "openid email https://www.googleapis.com/auth/contacts";

    private readonly HttpClient _http = new(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(30) };

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Opens Google's sign-in page through <paramref name="openBrowser"/> and waits for the user to finish there.
    /// </summary>
    /// <param name="loginHint">Pre-fills the account on Google's page.</param>
    public async Task<GoogleTokens> SignInAsync(Func<Uri, Task> openBrowser, string? loginHint = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var verifier = RandomUrlSafe(48);
        var state = RandomUrlSafe(24);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";

        var query = new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["redirect_uri"] = redirect,
            ["response_type"] = "code",
            ["scope"] = Scopes,
            ["state"] = state,
            ["code_challenge"] = Challenge(verifier),
            ["code_challenge_method"] = "S256",
            ["access_type"] = "offline",   // asks for a refresh token
            ["prompt"] = "consent",        // makes Google issue it again even if the app was approved before
        };
        if (!string.IsNullOrWhiteSpace(loginHint)) query["login_hint"] = loginHint;
        var authUrl = new UriBuilder(config.AuthUri) { Query = string.Join("&", query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}")) }.Uri;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
        // Listen while the browser is being opened, so neither waits for the other. If opening fails, stop waiting.
        var opening = openBrowser(authUrl);
        _ = opening.ContinueWith(_ => cts.Cancel(), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        string code;
        try { code = await WaitForCodeAsync(listener, state, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (opening.IsFaulted) await opening.ConfigureAwait(false); // the real reason: the browser could not be opened
            throw new GoogleAuthException("Google sign-in was not finished in time. Try again.");
        }
        await opening.ConfigureAwait(false);

        return await PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["client_secret"] = config.ClientSecret,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirect,
            ["grant_type"] = "authorization_code",
        }, existingRefresh: null, ct).ConfigureAwait(false);
    }

    /// <summary>A fresh access token from a saved refresh token. Throws with <see cref="GoogleAuthException.SignInAgain"/> when Google no longer accepts it.</summary>
    public Task<GoogleTokens> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
        PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["client_secret"] = config.ClientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        }, existingRefresh: refreshToken, ct);

    private async Task<GoogleTokens> PostTokenAsync(Dictionary<string, string> form, string? existingRefresh, CancellationToken ct)
    {
        using var resp = await _http.PostAsync(config.TokenUri, new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        JsonElement root;
        try { root = JsonDocument.Parse(body).RootElement; }
        catch (JsonException) { throw new GoogleAuthException($"Google's sign-in service gave an unreadable answer (HTTP {(int)resp.StatusCode})."); }

        if (!resp.IsSuccessStatusCode)
        {
            // Google's error fields are short codes and sentences, never tokens.
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            var detail = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            throw new GoogleAuthException($"Google refused the sign-in: {error}{(detail is null ? "" : " - " + detail)}", signInAgain: error == "invalid_grant");
        }

        var access = root.GetProperty("access_token").GetString()!;
        var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : existingRefresh;
        var expires = root.TryGetProperty("expires_in", out var x) ? x.GetInt32() : 3600;
        var email = root.TryGetProperty("id_token", out var id) ? EmailFromIdToken(id.GetString()) : null;
        var scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() : null;
        return new GoogleTokens(access, refresh, DateTimeOffset.UtcNow.AddSeconds(expires - 60), email, scope);
    }

    /// <summary>Waits for Google's redirect and returns the authorization code. Stray requests (favicon, probes) are answered and ignored.</summary>
    private static async Task<string> WaitForCodeAsync(TcpListener listener, string state, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            using var perConnection = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perConnection.CancelAfter(TimeSpan.FromSeconds(10)); // a browser may open a connection and send nothing
            try
            {
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                var requestLine = await reader.ReadLineAsync(perConnection.Token).ConfigureAwait(false);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(perConnection.Token).ConfigureAwait(false))) { } // skip headers

                var parts = requestLine?.Split(' ');
                if (parts is not { Length: >= 2 } || parts[0] != "GET") { await Respond(stream, 400, "Bad request."); continue; }
                var query = System.Web.HttpUtility.ParseQueryString(new Uri("http://127.0.0.1" + parts[1]).Query);

                if (query["error"] is { } error)
                {
                    await Respond(stream, 200, "Sign-in was cancelled. You can close this tab and return to ContactManage.");
                    throw new GoogleAuthException($"Google sign-in was cancelled or refused ({error}).");
                }
                if (query["code"] is not { Length: > 0 } code) { await Respond(stream, 404, "Not found."); continue; }
                if (query["state"] != state) { await Respond(stream, 400, "This sign-in answer does not belong to this session."); continue; }

                await Respond(stream, 200, "You are signed in. You can close this tab and return to ContactManage.");
                return code;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                // a connection that stalled or dropped; wait for the real one
            }
        }
    }

    private static async Task Respond(NetworkStream stream, int status, string message)
    {
        var html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>ContactManage</title></head><body style=\"font-family:sans-serif;margin:3em\"><h2>ContactManage</h2><p>{WebUtility.HtmlEncode(message)}</p></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    /// <summary>PKCE challenge: base64url of the SHA-256 of the verifier (RFC 7636).</summary>
    internal static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string RandomUrlSafe(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The email claim of an id token. Not verified: the token came straight from Google over https, and only names the sign-in.</summary>
    internal static string? EmailFromIdToken(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: 3 }) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }
}
