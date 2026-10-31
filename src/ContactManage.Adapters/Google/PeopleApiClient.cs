using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContactManage.Adapters.Google;

/// <param name="ResourceName">For example "contactGroups/abc123", or "contactGroups/myContacts" for the main list.</param>
/// <param name="IsSystem">Built-in groups (myContacts, starred, family ...) as opposed to labels the user made.</param>
public sealed record ContactGroupInfo(string ResourceName, string Name, bool IsSystem, int MemberCount);

/// <summary>
/// Google People API, reading only for now. The access token comes from <paramref name="accessToken"/> on every call,
/// so the caller can refresh it. Requests are sent one at a time: Google asks that a user's calls are not made in parallel.
/// </summary>
public sealed class PeopleApiClient(Func<CancellationToken, Task<string>> accessToken, HttpMessageHandler? handler = null, TimeSpan? retryUnit = null) : IDisposable
{
    private static readonly Uri Base = new("https://people.googleapis.com/v1/");
    private const int MaxAttempts = 6;

    private readonly HttpClient _http = new(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(60) };

    public void Dispose() => _http.Dispose();

    private Task<JsonElement> GetAsync(string relative, CancellationToken ct) => SendAsync(HttpMethod.Get, relative, null, ct);

    private async Task<JsonElement> SendAsync(HttpMethod method, string relative, string? json, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            // "./" because a first segment with a colon ("people:batchDeleteContacts") would otherwise be read as a URI scheme.
            using var req = new HttpRequestMessage(method, new Uri(Base, "./" + relative));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(ct).ConfigureAwait(false));
            if (json is not null) req.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
            {
                // Deletes answer with an empty body.
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(text)) return default;
                return JsonDocument.Parse(text).RootElement.Clone();
            }

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new GoogleAuthException("Google no longer accepts the saved sign-in.", signInAgain: true);

            // Rate limits and brief server trouble: wait and try again, a few times.
            if (resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway && attempt < MaxAttempts)
            {
                await Task.Delay((retryUnit ?? TimeSpan.FromSeconds(1)) * Math.Pow(2, attempt), ct).ConfigureAwait(false);
                continue;
            }

            // 400 FAILED_PRECONDITION on an update means the contact changed in Google since it was read.
            if (resp.StatusCode == HttpStatusCode.BadRequest && method != HttpMethod.Get)
            {
                var reason = ErrorReason(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                throw new GoogleAuthException($"Google refused a change.{reason}" +
                    (reason.Contains("FAILED_PRECONDITION") ? " A contact was changed in Gmail after it was read; read the folder again and retry." : ""));
            }

            throw new GoogleAuthException($"Google's contacts service answered HTTP {(int)resp.StatusCode}.{ErrorReason(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false))}");
        }
    }

    /// <summary>Google's short reason ("PERMISSION_DENIED", "People API has not been used in project ...") without any request data.</summary>
    private static string ErrorReason(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var e = doc.RootElement.GetProperty("error");
            var status = e.TryGetProperty("status", out var s) ? s.GetString() : null;
            var message = e.TryGetProperty("message", out var m) ? m.GetString() : null;
            return $" {status}{(message is null ? "" : ": " + (message.Length > 200 ? message[..200] + "..." : message))}";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return ""; }
    }

    /// <summary>Every label and built-in group of the account, with how many contacts each holds.</summary>
    public async Task<IReadOnlyList<ContactGroupInfo>> ListContactGroupsAsync(CancellationToken ct = default)
    {
        var groups = new List<ContactGroupInfo>();
        string? page = null;
        do
        {
            var root = await GetAsync("contactGroups?pageSize=200&groupFields=name,groupType,memberCount" +
                (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page)), ct).ConfigureAwait(false);
            if (root.TryGetProperty("contactGroups", out var list))
                foreach (var g in list.EnumerateArray())
                    groups.Add(new ContactGroupInfo(
                        g.GetProperty("resourceName").GetString()!,
                        g.TryGetProperty("formattedName", out var fn) ? fn.GetString()! : g.TryGetProperty("name", out var n) ? n.GetString()! : "",
                        g.TryGetProperty("groupType", out var t) && t.GetString() == "SYSTEM_CONTACT_GROUP",
                        g.TryGetProperty("memberCount", out var c) ? c.GetInt32() : 0));
            page = root.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        } while (page is not null);
        return groups;
    }

    // ---- writing. Google asks that one user's changes are sent one after another, so nothing here runs in parallel.

    private const int CreateBatch = 200, DeleteBatch = 500;

    /// <summary>Creates a label and returns its resource name.</summary>
    public async Task<string> CreateContactGroupAsync(string name, CancellationToken ct = default)
    {
        var root = await SendAsync(HttpMethod.Post, "contactGroups", JsonSerializer.Serialize(new { contactGroup = new { name } }), ct).ConfigureAwait(false);
        return root.GetProperty("resourceName").GetString()!;
    }

    /// <summary>Adds contacts to a label without touching their other memberships (stars, other labels).</summary>
    public async Task AddToGroupAsync(string groupResourceName, IReadOnlyList<string> people, CancellationToken ct = default)
    {
        for (var i = 0; i < people.Count; i += 1000)
            await SendAsync(HttpMethod.Post, $"{groupResourceName}/members:modify",
                JsonSerializer.Serialize(new { resourceNamesToAdd = people.Skip(i).Take(1000) }), ct).ConfigureAwait(false);
    }

    /// <summary>Creates contacts, up to 200 per request. Returns the new resource names in the order given.</summary>
    public async Task<IReadOnlyList<string>> CreateContactsAsync(IReadOnlyList<JsonNode> people, CancellationToken ct = default)
    {
        var created = new List<string>();
        for (var i = 0; i < people.Count; i += CreateBatch)
        {
            var body = new JsonObject
            {
                ["contacts"] = new JsonArray(people.Skip(i).Take(CreateBatch).Select(p => (JsonNode)new JsonObject { ["contactPerson"] = p.DeepClone() }).ToArray()),
                ["readMask"] = "names",
            };
            var root = await SendAsync(HttpMethod.Post, "people:batchCreateContacts", body.ToJsonString(), ct).ConfigureAwait(false);
            var batch = root.TryGetProperty("createdPeople", out var list) ? list.EnumerateArray().ToList() : [];
            foreach (var r in batch)
                created.Add(r.GetProperty("person").GetProperty("resourceName").GetString()!);
            if (batch.Count != Math.Min(CreateBatch, people.Count - i))
                throw new GoogleAuthException($"Google created {batch.Count} of {Math.Min(CreateBatch, people.Count - i)} contacts in one request.");
        }
        return created;
    }

    /// <summary>Changes only <paramref name="fields"/> of one contact. <paramref name="etag"/> is the version it was read at.</summary>
    public async Task UpdateContactAsync(string resourceName, string etag, JsonNode person, string fields, CancellationToken ct = default)
    {
        var body = person.DeepClone().AsObject();
        body["etag"] = etag;
        await SendAsync(HttpMethod.Patch, $"{resourceName}:updateContact?updatePersonFields={Uri.EscapeDataString(fields)}&personFields=names",
            body.ToJsonString(), ct).ConfigureAwait(false);
    }

    public async Task DeleteContactsAsync(IReadOnlyList<string> resourceNames, CancellationToken ct = default)
    {
        for (var i = 0; i < resourceNames.Count; i += DeleteBatch)
            await SendAsync(HttpMethod.Post, "people:batchDeleteContacts",
                JsonSerializer.Serialize(new { resourceNames = resourceNames.Skip(i).Take(DeleteBatch) }), ct).ConfigureAwait(false);
    }

    /// <summary>Resource name and version tag of every contact, with no other data.</summary>
    public async Task<IReadOnlyList<(string ResourceName, string? Etag)>> ListContactIdsAsync(CancellationToken ct = default) =>
        (await ListConnectionsAsync("names", ct).ConfigureAwait(false))
            .Select(p => (p.GetProperty("resourceName").GetString()!, p.TryGetProperty("etag", out var e) ? e.GetString() : null)).ToList();

    /// <summary>All of the account's own contacts (not "other contacts"), as Google's JSON, one page of 1,000 at a time.</summary>
    public async Task<IReadOnlyList<JsonElement>> ListConnectionsAsync(string personFields, CancellationToken ct = default)
    {
        var people = new List<JsonElement>();
        string? page = null;
        do
        {
            var root = await GetAsync($"people/me/connections?pageSize=1000&personFields={Uri.EscapeDataString(personFields)}" +
                (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page)), ct).ConfigureAwait(false);
            if (root.TryGetProperty("connections", out var list)) people.AddRange(list.EnumerateArray());
            page = root.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        } while (page is not null);
        return people;
    }
}
