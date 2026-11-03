using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContactManage.Adapters;

/// <summary>
/// The parts of a desktop OAuth sign-in that do not depend on the provider: PKCE, the loopback listener that catches the browser's
/// redirect, and reading the account's address from an id token. Google and Microsoft sign-ins both use them.
/// </summary>
internal static class OAuthLoopback
{
    /// <summary>PKCE challenge: base64url of the SHA-256 of the verifier (RFC 7636).</summary>
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string RandomUrlSafe(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Waits for the provider's redirect and returns the authorization code. Stray requests (favicon, probes) are answered and ignored.
    /// </summary>
    /// <param name="refused">Makes the exception to throw when the provider sends back an error (the user said no); gets the error code.</param>
    public static async Task<string> WaitForCodeAsync(TcpListener listener, string state, Func<string, Exception> refused, CancellationToken ct)
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
                if (!Uri.TryCreate("http://127.0.0.1" + parts[1], UriKind.Absolute, out var target)) { await Respond(stream, 400, "Bad request."); continue; }
                var query = System.Web.HttpUtility.ParseQueryString(target.Query);

                // Only the answer to this sign-in counts, error or not: another program on this PC must not be able to cancel it.
                if (query["state"] != state) { await Respond(stream, query["code"] is null && query["error"] is null ? 404 : 400, "This sign-in answer does not belong to this session."); continue; }
                if (query["error"] is { } error)
                {
                    await Respond(stream, 200, "Sign-in was cancelled. You can close this tab and return to ContactManage.");
                    throw refused(error);
                }
                if (query["code"] is not { Length: > 0 } code) { await Respond(stream, 404, "Not found."); continue; }

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

    /// <summary>
    /// The first of the named claims found in an id token. Not verified: the token came straight from the provider over https and only
    /// names the sign-in.
    /// </summary>
    public static string? ClaimFromIdToken(string? idToken, params string[] claims)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: 3 }) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            foreach (var claim in claims)
                if (doc.RootElement.TryGetProperty(claim, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                    return v.GetString();
            return null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }
}
