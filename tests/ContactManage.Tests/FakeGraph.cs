using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace ContactManage.Tests;

/// <summary>
/// A small in-memory stand-in for Microsoft's sign-in service and Microsoft Graph's contact endpoints: folders, contacts with paging,
/// $count, $batch (create, update, delete) and the throttling answer. Enough to exercise the Microsoft store without the network.
/// </summary>
public sealed class FakeGraph : HttpMessageHandler
{
    public Dictionary<string, string> Folders { get; } = new();                         // id -> display name
    public Dictionary<string, (string Folder, JsonObject Body)> Contacts { get; } = new();
    public List<string> Log { get; } = [];                                             // "METHOD path" and, inside a batch, "  op METHOD url"
    public List<string> TokenForms { get; } = [];
    public int PageSize { get; set; } = 100;
    public bool ThrottleNextBatchItem { get; set; }
    public bool RefuseRefresh { get; set; }
    public bool RefusePatch { get; set; }
    public string? ScopeOverride { get; set; }
    public bool GatewayErrorAfterNextBatch { get; set; }                               // the next $batch is carried out, then answered with HTTP 504
    public string? UnlistedDefaultFolder { get; set; }                                 // id of the built-in folder; left out of the folder list, found by its well-known name
    private int _tokens, _created;

    public static string IdToken(string email)
    {
        string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{\"alg\":\"none\"}")}.{B64($"{{\"preferred_username\":\"{email}\"}}")}.sig";
    }

    public void AddContact(string folder, string json)
    {
        var body = JsonNode.Parse(json)!.AsObject();
        Contacts[body["id"]!.GetValue<string>()] = (folder, body);
    }

    public int In(string folder) => Contacts.Values.Count(c => c.Folder == folder);

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Log) Log.Add($"{request.Method} {url.Host}{url.AbsolutePath}");

        if (url.Host == "login.microsoftonline.com")
        {
            lock (TokenForms) TokenForms.Add(body);
            if (RefuseRefresh) return Json("""{"error":"invalid_grant","error_description":"AADSTS70000: The refresh token has expired.\r\nTrace ID: x"}""", HttpStatusCode.BadRequest);
            var n = ++_tokens;
            var scope = ScopeOverride ?? "openid email https://graph.microsoft.com/Contacts.ReadWrite";
            return Json($$"""{"access_token":"AT{{n}}","refresh_token":"RT{{n + 1}}","expires_in":3600,"scope":"{{scope}}","id_token":"{{IdToken("jane@outlook.example")}}"}""");
        }

        if (request.Headers.Authorization?.Parameter is not { } token || !token.StartsWith("AT", StringComparison.Ordinal))
            return Json("""{"error":{"code":"InvalidAuthenticationToken","message":"no token"}}""", HttpStatusCode.Unauthorized);

        var path = Uri.UnescapeDataString(url.AbsolutePath).Replace("/v1.0", "");
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);

        if (path == "/$batch")
        {
            var answer = Batch(JsonNode.Parse(body)!.AsObject());
            if (!GatewayErrorAfterNextBatch) return answer;
            GatewayErrorAfterNextBatch = false;
            return Json("""{"error":{"code":"GatewayTimeout","message":"the work was done but the answer was lost"}}""", HttpStatusCode.GatewayTimeout);
        }
        return Handle(request.Method, path, query, body);
    }

    private HttpResponseMessage Handle(HttpMethod method, string path, System.Collections.Specialized.NameValueCollection query, string body)
    {
        if (method == HttpMethod.Get && path == "/me/contactFolders")
            return Json(new JsonObject { ["value"] = new JsonArray(Folders.Where(f => f.Key != UnlistedDefaultFolder)
                .Select(f => (JsonNode)new JsonObject { ["id"] = f.Key, ["displayName"] = f.Value }).ToArray()) }.ToJsonString());

        if (method == HttpMethod.Get && path == "/me/contactFolders/contacts")
            return UnlistedDefaultFolder is { } d && Folders.TryGetValue(d, out var dn)
                ? Json(new JsonObject { ["id"] = d, ["displayName"] = dn }.ToJsonString())
                : Json("""{"error":{"code":"ErrorItemNotFound","message":"not found"}}""", HttpStatusCode.NotFound);

        if (method == HttpMethod.Post && path == "/me/contactFolders")
        {
            var id = "F" + (Folders.Count + 100);
            Folders[id] = JsonNode.Parse(body)!["displayName"]!.GetValue<string>();
            return Json(new JsonObject { ["id"] = id, ["displayName"] = Folders[id] }.ToJsonString(), HttpStatusCode.Created);
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries); // me, contactFolders, F1, contacts
        if (parts is ["me", "contactFolders", var folder] && method == HttpMethod.Delete)
            return Folders.Remove(folder) ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json("{}", HttpStatusCode.NotFound);

        if (parts is ["me", "contactFolders", var f, "contacts"] && method == HttpMethod.Get)
        {
            if (!Folders.ContainsKey(f)) return Json("""{"error":{"code":"ErrorInvalidIdMalformed","message":"unknown folder"}}""", HttpStatusCode.NotFound);
            var all = Contacts.Values.Where(c => c.Folder == f).Select(c => c.Body).OrderBy(b => b["id"]!.GetValue<string>(), StringComparer.Ordinal).ToList();
            var top = int.TryParse(query["$top"], out var t) ? Math.Min(t, PageSize) : PageSize;
            var skip = int.TryParse(query["$skip"], out var s) ? s : 0;
            var page = all.Skip(skip).Take(top).Select(b => Project(b, query["$select"])).ToArray();
            var result = new JsonObject { ["value"] = new JsonArray(page) };
            if (query["$count"] == "true") result["@odata.count"] = all.Count;
            if (skip + top < all.Count)
                result["@odata.nextLink"] = $"https://graph.microsoft.com/v1.0/me/contactFolders/{f}/contacts?$top={top}&$skip={skip + top}" +
                    (query["$select"] is { } sel ? "&$select=" + sel : "");
            return Json(result.ToJsonString());
        }
        return Json("""{"error":{"code":"NotImplemented","message":"fake"}}""", HttpStatusCode.NotImplemented);
    }

    private static JsonNode Project(JsonObject full, string? select)
    {
        if (select is null) return full.DeepClone();
        var o = new JsonObject();
        foreach (var name in select.Split(',')) if (full[name] is { } v) o[name] = v.DeepClone();
        return o;
    }

    private HttpResponseMessage Batch(JsonObject request)
    {
        var responses = new JsonArray();
        foreach (var op in request["requests"]!.AsArray())
        {
            var id = op!["id"]!.GetValue<string>();
            var method = op["method"]!.GetValue<string>();
            var url = op["url"]!.GetValue<string>();
            lock (Log) Log.Add($"  op {method} {Uri.UnescapeDataString(url)}");
            if (ThrottleNextBatchItem)
            {
                ThrottleNextBatchItem = false;
                responses.Add(new JsonObject { ["id"] = id, ["status"] = 429, ["headers"] = new JsonObject { ["Retry-After"] = "0" },
                    ["body"] = new JsonObject { ["error"] = new JsonObject { ["code"] = "TooManyRequests" } } });
                continue;
            }
            responses.Add(Operation(id, method, Uri.UnescapeDataString(url).Split('/', StringSplitOptions.RemoveEmptyEntries), op["body"]?.AsObject()));
        }
        return Json(new JsonObject { ["responses"] = responses }.ToJsonString());
    }

    private JsonObject Operation(string id, string method, string[] parts, JsonObject? body)
    {
        JsonObject Answer(int status, JsonObject? b = null) => new() { ["id"] = id, ["status"] = status, ["body"] = b };
        switch (method, parts)
        {
            case ("POST", ["me", "contactFolders", var folder, "contacts"]):
                if (!Folders.ContainsKey(folder)) return Answer(404, new JsonObject { ["error"] = new JsonObject { ["code"] = "ErrorItemNotFound", ["message"] = "folder" } });
                var made = body!.DeepClone().AsObject();
                made["id"] = "N" + (++_created);
                made["changeKey"] = "K0";
                Contacts[made["id"]!.GetValue<string>()] = (folder, made);
                return Answer(201, made.DeepClone().AsObject());
            case ("PATCH", ["me", "contacts", var contact]):
                if (RefusePatch) return Answer(403, new JsonObject { ["error"] = new JsonObject { ["code"] = "ErrorAccessDenied", ["message"] = "read only" } });
                if (!Contacts.TryGetValue(contact, out var existing)) return Answer(404, new JsonObject { ["error"] = new JsonObject { ["code"] = "ErrorItemNotFound", ["message"] = "contact" } });
                foreach (var (key, value) in body!)
                {
                    if (value is null) existing.Body.Remove(key);
                    else existing.Body[key] = value.DeepClone();
                }
                return Answer(200, existing.Body.DeepClone().AsObject());
            case ("DELETE", ["me", "contacts", var gone]):
                return Contacts.Remove(gone) ? Answer(204) : Answer(404, new JsonObject { ["error"] = new JsonObject { ["code"] = "ErrorItemNotFound", ["message"] = "contact" } });
            default:
                return Answer(400, new JsonObject { ["error"] = new JsonObject { ["code"] = "BadRequest", ["message"] = "fake: " + method } });
        }
    }
}
