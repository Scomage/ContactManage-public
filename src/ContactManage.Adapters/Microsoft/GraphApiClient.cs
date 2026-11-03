using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContactManage.Adapters.MicrosoftGraph;

/// <param name="Id">Graph's long opaque folder id.</param>
public sealed record GraphFolder(string Id, string Name);

/// <summary>
/// Microsoft Graph, the parts that deal with contacts. The access token comes from <paramref name="accessToken"/> on every call, so the
/// caller can refresh it. Requests go out one at a time (Outlook throttles a mailbox that is hit in parallel); writes use $batch, which
/// carries up to 20 operations in one request.
/// </summary>
public sealed class GraphApiClient(Func<CancellationToken, Task<string>> accessToken, HttpMessageHandler? handler = null, TimeSpan? retryUnit = null) : IDisposable
{
    private static readonly Uri Base = new("https://graph.microsoft.com/v1.0/");
    private const int MaxAttempts = 6, BatchSize = 20, PageSize = 100;

    private readonly HttpClient _http = new(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(60) };

    public void Dispose() => _http.Dispose();

    // ---- one request

    private async Task<JsonElement> SendAsync(HttpMethod method, string relativeOrAbsolute, JsonNode? body, CancellationToken ct)
    {
        var uri = relativeOrAbsolute.StartsWith("https://", StringComparison.Ordinal) ? new Uri(relativeOrAbsolute) : new Uri(Base, relativeOrAbsolute);
        // A paging link comes from the server; the token must not follow it anywhere else.
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals(Base.Host, StringComparison.OrdinalIgnoreCase))
            throw new MicrosoftAuthException("Microsoft Graph sent a link to another address, which was not followed.");

        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, uri);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(ct).ConfigureAwait(false));
            if (body is not null) req.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
            {
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(text)) return default;
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.Clone();
            }

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new MicrosoftAuthException("Microsoft no longer accepts the saved sign-in.", signInAgain: true);

            if (IsRetryable(method, (int)resp.StatusCode) && attempt < MaxAttempts)
            {
                await Task.Delay(Wait(resp.Headers.RetryAfter?.Delta, attempt), ct).ConfigureAwait(false);
                continue;
            }
            throw new MicrosoftAuthException($"Microsoft's contacts service answered HTTP {(int)resp.StatusCode}.{ErrorReason(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false))}");
        }
    }

    /// <summary>
    /// 429 means "not done, try later", so any request may be sent again. A gateway error (502, 503, 504) can come after the work was
    /// done, so only a read is repeated: re-sending a $batch of creates would make every contact twice.
    /// </summary>
    private static bool IsRetryable(HttpMethod method, int status) => status == 429 || (status is 502 or 503 or 504 && method == HttpMethod.Get);

    /// <summary>Microsoft's own wait when it gave one ("Retry-After"), otherwise a doubling pause.</summary>
    private TimeSpan Wait(TimeSpan? asked, int attempt) =>
        asked is { } a && a > TimeSpan.Zero ? a : (retryUnit ?? TimeSpan.FromSeconds(1)) * Math.Pow(2, attempt);

    /// <summary>Graph's short error code and message ("ErrorItemNotFound: The specified object was not found ...") without request data.</summary>
    private static string ErrorReason(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var e = doc.RootElement.GetProperty("error");
            var code = e.TryGetProperty("code", out var c) ? c.GetString() : null;
            var message = e.TryGetProperty("message", out var m) ? m.GetString() : null;
            return $" {code}{(message is null ? "" : ": " + (message.Length > 200 ? message[..200] + "..." : message))}";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return ""; }
    }

    // ---- reading

    private async Task<List<JsonElement>> PagedAsync(string first, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        string? next = first;
        while (next is not null)
        {
            var root = await SendAsync(HttpMethod.Get, next, null, ct).ConfigureAwait(false);
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var list)) items.AddRange(list.EnumerateArray());
            next = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("@odata.nextLink", out var n) ? n.GetString() : null;
        }
        return items;
    }

    private static string FolderPath(string folderId) => $"me/contactFolders/{Uri.EscapeDataString(folderId)}";

    public async Task<IReadOnlyList<GraphFolder>> ListFoldersAsync(CancellationToken ct = default) =>
        (await PagedAsync($"me/contactFolders?$top={PageSize}", ct).ConfigureAwait(false))
            .Select(f => new GraphFolder(f.GetProperty("id").GetString()!, f.TryGetProperty("displayName", out var n) ? n.GetString() ?? "" : ""))
            .ToList();

    /// <summary>
    /// The built-in Contacts folder, asked for by its well-known name. The folder list does not always include it (a new Outlook.com
    /// account showed nothing), so it is looked up on its own. Null when Graph will not say.
    /// </summary>
    public async Task<GraphFolder?> GetDefaultFolderAsync(CancellationToken ct = default)
    {
        try
        {
            var root = await SendAsync(HttpMethod.Get, "me/contactFolders/contacts?$select=id,displayName", null, ct).ConfigureAwait(false);
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var id) || id.GetString() is not { Length: > 0 } folderId) return null;
            var name = root.TryGetProperty("displayName", out var n) ? n.GetString() : null;
            return new GraphFolder(folderId, string.IsNullOrWhiteSpace(name) ? "Contacts" : name);
        }
        catch (MicrosoftAuthException ex) when (!ex.SignInAgain)
        {
            return null;
        }
    }

    /// <summary>How many contacts a folder holds: Graph's own count when it gives one, otherwise by listing the ids.</summary>
    public async Task<int> CountAsync(string folderId, CancellationToken ct = default)
    {
        var root = await SendAsync(HttpMethod.Get, $"{FolderPath(folderId)}/contacts?$count=true&$top=1&$select=id", null, ct).ConfigureAwait(false);
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("@odata.count", out var c) && c.ValueKind == JsonValueKind.Number) return c.GetInt32();
        return (await PagedAsync($"{FolderPath(folderId)}/contacts?$top=999&$select=id", ct).ConfigureAwait(false)).Count;
    }

    /// <summary>Every contact of a folder as Graph's JSON.</summary>
    public Task<List<JsonElement>> ListContactsAsync(string folderId, CancellationToken ct = default) =>
        PagedAsync($"{FolderPath(folderId)}/contacts?$top={PageSize}", ct);

    /// <summary>The id and version tag of every contact in a folder, with nothing else.</summary>
    public async Task<IReadOnlyList<(string Id, string? ChangeKey)>> ListContactIdsAsync(string folderId, CancellationToken ct = default) =>
        (await PagedAsync($"{FolderPath(folderId)}/contacts?$top=999&$select=id,changeKey", ct).ConfigureAwait(false))
            .Select(c => (c.GetProperty("id").GetString()!, c.TryGetProperty("changeKey", out var k) ? k.GetString() : null)).ToList();

    // ---- writing

    public async Task<string> CreateFolderAsync(string name, CancellationToken ct = default)
    {
        var root = await SendAsync(HttpMethod.Post, "me/contactFolders", new JsonObject { ["displayName"] = name }, ct).ConfigureAwait(false);
        return root.GetProperty("id").GetString()!;
    }

    public Task DeleteFolderAsync(string folderId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, FolderPath(folderId), null, ct);

    /// <summary>Creates contacts in a folder and returns their new ids in the order given.</summary>
    public async Task<IReadOnlyList<string>> CreateContactsAsync(string folderId, IReadOnlyList<JsonObject> contacts, CancellationToken ct = default)
    {
        var url = $"/{FolderPath(folderId)}/contacts";
        var answers = await BatchAsync(contacts.Select(c => ("POST", url, (JsonNode?)c)).ToList(), ct).ConfigureAwait(false);
        return answers.Select(a => a.GetProperty("body").GetProperty("id").GetString()!).ToList();
    }

    /// <summary>Changes the given properties of existing contacts. Properties not in the body stay as they are.</summary>
    public Task UpdateContactsAsync(IReadOnlyList<(string Id, JsonObject Changes)> updates, CancellationToken ct = default) =>
        BatchAsync(updates.Select(u => ("PATCH", $"/me/contacts/{Uri.EscapeDataString(u.Id)}", (JsonNode?)u.Changes)).ToList(), ct);

    public Task DeleteContactsAsync(IReadOnlyList<string> ids, CancellationToken ct = default) =>
        BatchAsync(ids.Select(id => ("DELETE", $"/me/contacts/{Uri.EscapeDataString(id)}", (JsonNode?)null)).ToList(), ct);

    /// <summary>
    /// Sends operations 20 at a time through $batch. An operation that Graph throttles (429, 503) is sent again after the wait it asked
    /// for; any other failure stops everything with Graph's reason. Returns each operation's answer in the order given.
    /// </summary>
    private async Task<IReadOnlyList<JsonElement>> BatchAsync(IReadOnlyList<(string Method, string Url, JsonNode? Body)> operations, CancellationToken ct)
    {
        var answers = new JsonElement[operations.Count];
        for (var start = 0; start < operations.Count; start += BatchSize)
        {
            var pending = Enumerable.Range(start, Math.Min(BatchSize, operations.Count - start)).ToList();
            for (var attempt = 1; pending.Count > 0; attempt++)
            {
                var requests = new JsonArray(pending.Select(i =>
                {
                    var r = new JsonObject { ["id"] = i.ToString(), ["method"] = operations[i].Method, ["url"] = operations[i].Url };
                    if (operations[i].Body is { } b)
                    {
                        r["headers"] = new JsonObject { ["Content-Type"] = "application/json" };
                        r["body"] = b.DeepClone();
                    }
                    return (JsonNode)r;
                }).ToArray());
                var root = await SendAsync(HttpMethod.Post, "$batch", new JsonObject { ["requests"] = requests }, ct).ConfigureAwait(false);
                var responses = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("responses", out var list) ? list.EnumerateArray().ToList() : [];

                var again = new List<int>();
                TimeSpan? asked = null;
                foreach (var response in responses)
                {
                    var i = int.Parse(response.GetProperty("id").GetString()!);
                    var status = response.GetProperty("status").GetInt32();
                    if (status is >= 200 and < 300) { answers[i] = response; continue; }
                    if (status is 429 or 503 && attempt < MaxAttempts)
                    {
                        again.Add(i);
                        if (response.TryGetProperty("headers", out var h) && h.TryGetProperty("Retry-After", out var ra) && int.TryParse(ra.GetString(), out var secs))
                            asked = TimeSpan.FromSeconds(Math.Max(asked?.TotalSeconds ?? 0, secs));
                        continue;
                    }
                    throw new MicrosoftAuthException($"Microsoft refused a change (HTTP {status}).{(response.TryGetProperty("body", out var b) ? ErrorReason(b.GetRawText()) : "")}");
                }
                // An operation with no answer at all is a failure too: never assume it happened.
                var answered = responses.Select(r => int.Parse(r.GetProperty("id").GetString()!)).ToHashSet();
                if (pending.Any(i => !answered.Contains(i)))
                    throw new MicrosoftAuthException("Microsoft did not answer every change in a batch. Check the folder before trying again.");

                pending = again;
                if (pending.Count > 0) await Task.Delay(Wait(asked, attempt), ct).ConfigureAwait(false);
            }
        }
        return answers;
    }
}
