using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using ContactManage.Core;

namespace ContactManage.Adapters.CardDav;

public class CardDavException(string message, int? status = null) : Exception(message)
{
    public int? Status { get; } = status;
}

/// <summary>The server rejected the user name or password.</summary>
public sealed class CardDavAuthException(string message, int status) : CardDavException(message, status);

public sealed record RemoteContact(string Href, string? ETag, string VCard);

public sealed record AddressBookInfo(Uri Uri, string Name);

/// <summary>
/// A small CardDAV (RFC 6352) client using HTTP Basic over HTTPS. The password is sent only to the server
/// it was entered for: redirects to another host are refused rather than followed with credentials.
/// </summary>
public sealed class CardDavClient : IDisposable
{
    private static readonly XNamespace D = "DAV:", C = "urn:ietf:params:xml:ns:carddav";

    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly string _authorization;

    public CardDavClient(Uri baseUri, ServerCredentials credentials, HttpMessageHandler? handler = null)
    {
        if (baseUri.Scheme != Uri.UriSchemeHttps)
            throw new CardDavException($"{baseUri.Host} is not an https address. A password is never sent over an unencrypted connection.");
        _base = baseUri;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ContactManage/0.1");
        _authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.UserName}:{credentials.Password}"));
    }

    public void Dispose() => _http.Dispose();

    private async Task<(int Status, string Body, HttpResponseHeaders Headers)> SendAsync(
        string method, Uri uri, string? body, int depth, string contentType, CancellationToken ct,
        Action<HttpRequestMessage>? configure = null)
    {
        for (var hop = 0; hop < 5; hop++)
        {
            if (!uri.Host.Equals(_base.Host, StringComparison.OrdinalIgnoreCase) || uri.Scheme != Uri.UriSchemeHttps)
                throw new CardDavException($"The server redirected to {uri.Host}, which was refused to keep the password safe.");

            using var req = new HttpRequestMessage(new HttpMethod(method), uri);
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", _authorization);
            if (method is "PROPFIND" or "REPORT") req.Headers.Add("Depth", depth.ToString());
            if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, MediaTypeHeaderValue.Parse(contentType));
            configure?.Invoke(req);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect && resp.Headers.Location is { } location)
            {
                uri = new Uri(uri, location);
                continue;
            }

            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // Only 401 means a wrong login. 403 is a permission refusal for a request that was understood, so it falls
            // through to the caller, which reports it with what was being attempted.
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new CardDavAuthException("The server rejected the user name or password.", (int)resp.StatusCode);
            return ((int)resp.StatusCode, text, resp.Headers);
        }
        throw new CardDavException("The server redirected too many times.");
    }

    private Task<(int Status, string Body, HttpResponseHeaders Headers)> Dav(string method, Uri uri, string body, int depth, CancellationToken ct) =>
        SendAsync(method, uri, body, depth, "application/xml", ct);

    private static void Expect(int status, string what, params int[] ok) => Expect(status, what, "", ok);

    private static void Expect(int status, string what, string body, params int[] ok)
    {
        if (!ok.Contains(status)) throw new CardDavException($"{what} failed (HTTP {status}).{Reason(body)}", status);
    }

    /// <summary>Finds the user's address book home. Also confirms the login works.</summary>
    public async Task<Uri> GetHomeAsync(CancellationToken ct = default)
    {
        const string request = """<d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:carddav"><d:prop><d:current-user-principal/><c:addressbook-home-set/></d:prop></d:propfind>""";
        var (status, body, _) = await Dav("PROPFIND", _base, request, 0, ct).ConfigureAwait(false);
        Expect(status, "Contacting the server", 207, 200);

        var doc = XDocument.Parse(body);
        var home = doc.Descendants(C + "addressbook-home-set").Descendants(D + "href").FirstOrDefault()?.Value;
        if (home is null)
        {
            var principal = doc.Descendants(D + "current-user-principal").Descendants(D + "href").FirstOrDefault()?.Value;
            if (principal is not null)
            {
                (status, body, _) = await Dav("PROPFIND", new Uri(_base, principal), request, 0, ct).ConfigureAwait(false);
                Expect(status, "Finding the address book home", 207, 200);
                home = XDocument.Parse(body).Descendants(C + "addressbook-home-set").Descendants(D + "href").FirstOrDefault()?.Value;
            }
        }
        return home is null ? _base : AsCollection(new Uri(_base, home));
    }

    /// <summary>
    /// Whether the server lists MKCOL (creating an address book) among its allowed methods; null when it does not say.
    /// AOL lists no MKCOL although its DAV header mentions extended-mkcol, and answers an attempt with 404.
    /// </summary>
    public async Task<bool?> AllowsNewAddressBooksAsync(CancellationToken ct = default)
    {
        try
        {
            // Sent directly because .NET files Allow under the content headers, which SendAsync does not return. The address is
            // the one the login just succeeded against, so the password goes nowhere new.
            var uri = _base;
            for (var hop = 0; hop < 4; hop++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Options, uri);
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", _authorization);
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if ((int)resp.StatusCode is 301 or 302 or 307 or 308 && resp.Headers.Location is { } location)
                {
                    uri = new Uri(uri, location);
                    if (!uri.Host.Equals(_base.Host, StringComparison.OrdinalIgnoreCase) || uri.Scheme != Uri.UriSchemeHttps) return null;
                    continue;
                }
                if (!resp.Content.Headers.TryGetValues("Allow", out var values) && !resp.Headers.TryGetValues("Allow", out values)) return null;
                return values.SelectMany(v => v.Split(',')).Any(m => m.Trim().Equals("MKCOL", StringComparison.OrdinalIgnoreCase));
            }
            return null;
        }
        catch (Exception ex) when (ex is CardDavException or HttpRequestException)
        {
            return null;
        }
    }

    private const string ListRequest ="""<d:propfind xmlns:d="DAV:"><d:prop><d:displayname/><d:resourcetype/><d:getetag/></d:prop></d:propfind>""";

    public async Task<IReadOnlyList<AddressBookInfo>> ListAddressBooksAsync(Uri collection, CancellationToken ct = default)
    {
        var (status, body, _) = await Dav("PROPFIND", collection, ListRequest, 1, ct).ConfigureAwait(false);
        if (status == 404) return [];
        Expect(status, "Listing address books", 207);
        var books = new List<AddressBookInfo>();
        foreach (var r in XDocument.Parse(body).Descendants(D + "response"))
        {
            if (!r.Descendants(C + "addressbook").Any()) continue;
            var uri = AsCollection(new Uri(_base, r.Element(D + "href")!.Value));
            books.Add(new AddressBookInfo(uri, r.Descendants(D + "displayname").FirstOrDefault()?.Value ?? ""));
        }
        return books;
    }

    public async Task<bool> IsAddressBookAsync(Uri collection, CancellationToken ct = default)
    {
        var (status, body, _) = await Dav("PROPFIND", collection, ListRequest, 0, ct).ConfigureAwait(false);
        if (status == 404) return false;
        Expect(status, "Checking the folder", 207);
        return XDocument.Parse(body).Descendants(C + "addressbook").Any();
    }

    /// <summary>The number of contacts in a book, or null if the server's answer could not be read.</summary>
    public async Task<int?> CountAsync(Uri book, CancellationToken ct = default)
    {
        var (status, body, _) = await Dav("PROPFIND", book, ListRequest, 1, ct).ConfigureAwait(false);
        if (status != 207) return null;
        var self = book.AbsolutePath.TrimEnd('/');
        return XDocument.Parse(body).Descendants(D + "response")
            .Count(r => !Uri.UnescapeDataString(new Uri(_base, r.Element(D + "href")!.Value).AbsolutePath).TrimEnd('/')
                .Equals(Uri.UnescapeDataString(self), StringComparison.Ordinal));
    }

    private const int MultigetBatch = 50;

    /// <summary>How many requests run at once. Modest, so a server is never hammered.</summary>
    public const int Parallelism = 4;

    /// <summary>
    /// The address of every contact in a book, without their data: one cheap request. IceWarp answers an addressbook-query
    /// (even asking for all properties) with an empty stub per contact, so the query is only good for listing.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListHrefsAsync(Uri book, CancellationToken ct = default)
    {
        const string query = """<c:addressbook-query xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:carddav"><d:prop><d:getetag/></d:prop></c:addressbook-query>""";
        var (status, body, _) = await Dav("REPORT", book, query, 1, ct).ConfigureAwait(false);
        // AOL refuses a query without a <filter> (HTTP 400). A plain listing of the folder gives the same addresses.
        if (status is 400 or 403 or 405 or 501)
            (status, body, _) = await Dav("PROPFIND", book, ListRequest, 1, ct).ConfigureAwait(false);
        Expect(status, "Listing the contacts", 207);

        var self = Uri.UnescapeDataString(book.AbsolutePath).TrimEnd('/');
        return XDocument.Parse(body).Descendants(D + "response")
            .Select(r => r.Element(D + "href")?.Value)
            .Where(h => !string.IsNullOrEmpty(h) && !h.EndsWith('/')
                && !Uri.UnescapeDataString(new Uri(_base, h).AbsolutePath).TrimEnd('/').Equals(self, StringComparison.Ordinal))
            .Select(h => h!).ToList();
    }

    /// <summary>All vCards in a book: the listing, then the real data from addressbook-multiget in batches, a few at a time.</summary>
    public async Task<IReadOnlyList<RemoteContact>> ReadAllAsync(Uri book, CancellationToken ct = default)
    {
        var hrefs = await ListHrefsAsync(book, ct).ConfigureAwait(false);
        var batches = hrefs.Chunk(MultigetBatch).ToArray();
        var fetched = new List<RemoteContact>[batches.Length]; // by batch number, so the order is stable

        await Parallel.ForEachAsync(Enumerable.Range(0, batches.Length),
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (i, token) =>
            {
                var multiget = "<c:addressbook-multiget xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:carddav\"><d:prop><d:getetag/><c:address-data/></d:prop>"
                    + string.Concat(batches[i].Select(h => "<d:href>" + System.Security.SecurityElement.Escape(h) + "</d:href>"))
                    + "</c:addressbook-multiget>";
                var (status, body, _) = await Dav("REPORT", book, multiget, 0, token).ConfigureAwait(false);
                Expect(status, "Reading the contacts", 207);

                var batch = new List<RemoteContact>(batches[i].Length);
                foreach (var r in XDocument.Parse(body).Descendants(D + "response"))
                {
                    var data = r.Descendants(C + "address-data").FirstOrDefault()?.Value;
                    if (string.IsNullOrWhiteSpace(data)) continue;
                    batch.Add(new RemoteContact(r.Element(D + "href")!.Value, r.Descendants(D + "getetag").FirstOrDefault()?.Value, data));
                }
                fetched[i] = batch;
            }).ConfigureAwait(false);

        var result = fetched.SelectMany(b => b).ToList();
        if (result.Count != hrefs.Count)
            throw new CardDavException($"The server listed {hrefs.Count} contacts but returned the data of only {result.Count}. Nothing was changed.");
        return result;
    }

    /// <summary>Overwrites an existing contact in place, keeping its address.</summary>
    public async Task PutAsync(Uri resource, string vcard, CancellationToken ct = default)
    {
        var (status, body, _) = await SendAsync("PUT", resource, vcard, 0, "text/vcard; charset=utf-8", ct).ConfigureAwait(false);
        Expect(status, "Updating a contact", body, 200, 201, 204);
    }

    /// <summary>Creates a contact. Refuses to overwrite an existing file of the same name.</summary>
    public async Task PutNewAsync(Uri book, string fileName, string vcard, CancellationToken ct = default)
    {
        var (status, body, _) = await SendAsync("PUT", new Uri(book, Uri.EscapeDataString(fileName)), vcard, 0,
            "text/vcard; charset=utf-8", ct, req => req.Headers.TryAddWithoutValidation("If-None-Match", "*")).ConfigureAwait(false);
        Expect(status, $"Writing {fileName}", body, 200, 201, 204);
    }

    /// <summary>
    /// The server's stated reason from an error body: the names of the XML elements, such as "no-uid-conflict", or a short
    /// plain-text start. Never the request, so contact data is not repeated into messages.
    /// </summary>
    internal static string Reason(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            var names = XDocument.Parse(body).Descendants().Where(e => !e.HasElements).Select(e => e.Name.LocalName).Distinct().Take(4);
            var text = string.Join(", ", names);
            return text.Length > 0 ? $" Server reason: {text}." : "";
        }
        catch (System.Xml.XmlException)
        {
            var plain = System.Text.RegularExpressions.Regex.Replace(body, @"\s+", " ").Trim();
            return $" Server reason: {(plain.Length > 120 ? plain[..120] + "..." : plain)}.";
        }
    }

    public async Task DeleteAsync(Uri resource, CancellationToken ct = default)
    {
        var (status, _, _) = await SendAsync("DELETE", resource, null, 0, "application/xml", ct).ConfigureAwait(false);
        Expect(status, "Deleting a contact", 200, 204, 404);
    }

    /// <summary>Creates a new address book in <paramref name="home"/> and returns its address.</summary>
    public async Task<Uri> CreateAddressBookAsync(Uri home, string displayName, CancellationToken ct = default)
    {
        var slug = new string(displayName.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        if (slug.Length == 0) slug = "contacts";
        var body = $"""
            <d:mkcol xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:carddav"><d:set><d:prop>
            <d:resourcetype><d:collection/><c:addressbook/></d:resourcetype>
            <d:displayname>{System.Security.SecurityElement.Escape(displayName)}</d:displayname></d:prop></d:set></d:mkcol>
            """;
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            // A random suffix, so the address is never one a deleted folder used before: clients such as IceWarp's
            // desktop client keep sync state per address and can show a recreated folder as empty.
            var uri = new Uri(home, $"{slug}-{Guid.NewGuid().ToString("N")[..6]}/");
            // Look first: some servers answer MKCOL on an existing name with 403 rather than 405.
            if ((await Dav("PROPFIND", uri, ListRequest, 0, ct).ConfigureAwait(false)).Status != 404) continue;
            var (status, _, _) = await Dav("MKCOL", uri, body, 0, ct).ConfigureAwait(false);
            if (status is 200 or 201 or 207) return uri;
            if (status is 403 or 404 or 501)
                throw new CardDavException($"Creating the address book \"{displayName}\" failed (HTTP {status}). This server probably does not allow " +
                    "creating extra address books (AOL has only one). Replace the folder instead, or save a vCard file.", status);
            if (status != 405 && status != 409) Expect(status, $"Creating the address book \"{displayName}\"", 201);
            // 405 or 409: a collection of that name already exists, so try the next name.
        }
        throw new CardDavException("Could not find a free name for the new address book.");
    }

    private static Uri AsCollection(Uri uri) =>
        uri.AbsolutePath.EndsWith('/') ? uri : new UriBuilder(uri) { Path = uri.AbsolutePath + "/" }.Uri;
}
