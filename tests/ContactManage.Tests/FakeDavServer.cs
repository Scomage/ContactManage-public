using System.Net;
using System.Text;
using System.Xml.Linq;

namespace ContactManage.Tests;

/// <summary>A minimal in-memory CardDAV server, enough for the client and store to run against.</summary>
public sealed class FakeDavServer : HttpMessageHandler
{
    private static readonly XNamespace D = "DAV:", C = "urn:ietf:params:xml:ns:carddav";

    public const string Root = "/dav/";
    public const string Home = "/dav/jane@example.com/";
    public const string User = "jane@example.com";
    public const string Password = "correct horse";

    public Dictionary<string, string> Resources { get; } = new();   // path -> vCard text
    public Dictionary<string, string> Books { get; } = new();       // collection path -> display name
    public List<string> Log { get; } = [];                          // "METHOD path"
    public int UnauthorizedCount { get; private set; }
    public bool FailPuts { get; set; }
    public bool ForbidMkcol { get; set; }
    public bool RejectQueries { get; set; }
    public bool FoldSameNameAndEmail { get; set; }                   // like AOL: a new entry with an existing name and email updates that entry
    public bool OmitMkcolFromAllow { get; set; }                   // like AOL, which lists no MKCOL

    public FakeDavServer()
    {
        Books[Home + "Contacts/"] = "Contacts";
        Books[Home + "Shared/Team/"] = "Team";
    }

    public IEnumerable<string> In(string book) => Resources.Keys.Where(k => k.StartsWith(book, StringComparison.Ordinal));

    public void Add(string book, string name, string vcard) => Resources[book + name] = vcard;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
        var body = request.Content is null ? "" : request.Content.ReadAsStringAsync(cancellationToken).Result;
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));

        // The client sends requests in parallel; the in-memory dictionaries are not thread-safe, so one request at a time.
        lock (_gate)
        {
            Log.Add($"{request.Method} {path}");
            if (request.Headers.Authorization?.Parameter != expected)
            {
                UnauthorizedCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }
            return Task.FromResult(Handle(request, path, body));
        }
    }

    private readonly object _gate = new();

    private HttpResponseMessage Handle(HttpRequestMessage request, string path, string body)
    {
        switch (request.Method.Method)
        {
            case "OPTIONS":
                var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
                ok.Content.Headers.Allow.Add("PROPFIND");
                ok.Content.Headers.Allow.Add("REPORT");
                ok.Content.Headers.Allow.Add("PUT");
                if (!OmitMkcolFromAllow) ok.Content.Headers.Allow.Add("MKCOL");
                return ok;

            case "PROPFIND":
                if (path == Root)
                    return Multistatus(Response(Root, extra: new XElement(D + "current-user-principal", new XElement(D + "href", Root)),
                        home: Home));
                if (Books.ContainsKey(path))
                {
                    var depth = request.Headers.GetValues("Depth").First();
                    var list = new List<XElement> { Response(path, book: true, name: Books[path]) };
                    if (depth == "1")
                        list.AddRange(In(path).Select(k => Response(k)));
                    return Multistatus(list.ToArray());
                }
                if (path == Home)
                {
                    var list = new List<XElement> { Response(Home) };
                    list.AddRange(Books.Keys.Where(k => k.StartsWith(Home) && k.Count(ch => ch == '/') == Home.Count(ch => ch == '/') + 1)
                        .Select(k => Response(k, book: true, name: Books[k])));
                    return Multistatus(list.ToArray());
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            case "REPORT":
                if (!Books.ContainsKey(path)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (body.Contains("addressbook-multiget"))
                {
                    var wanted = XDocument.Parse(body).Descendants(D + "href").Select(h => Uri.UnescapeDataString(h.Value)).ToList();
                    return Multistatus(wanted.Where(Resources.ContainsKey).Select(k => Response(k, data: Resources[k])).ToArray());
                }
                if (RejectQueries) return new HttpResponseMessage(HttpStatusCode.BadRequest);   // like AOL: a query needs a filter
                // Like IceWarp, a plain query lists the contacts but returns only a stub for each, not the real data.
                return Multistatus(In(path).Select(k => Response(k, data: "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:\r\nN:;;;;\r\nEND:VCARD\r\n")).ToArray());

            case "PUT":
                if (FailPuts)
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("<d:error xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:carddav\"><c:no-uid-conflict/></d:error>", Encoding.UTF8, "application/xml"),
                    };
                var exists = Resources.ContainsKey(path);
                if (FoldSameNameAndEmail && !exists)
                {
                    // Like AOL: an entry with the name and email of an existing one is that entry, updated; nothing new is stored.
                    var same = Resources.Keys.FirstOrDefault(k => k[..(k.LastIndexOf('/') + 1)] == path[..(path.LastIndexOf('/') + 1)] && NameAndEmail(Resources[k]) == NameAndEmail(body));
                    if (same is not null) { Resources[same] = body; return new HttpResponseMessage(HttpStatusCode.NoContent); }
                }
                if (request.Headers.TryGetValues("If-None-Match", out var inm) && inm.Contains("*") && exists)
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                Resources[path] = body;
                return new HttpResponseMessage(exists ? HttpStatusCode.NoContent : HttpStatusCode.Created);

            case "DELETE":
                if (Books.Remove(path)) { foreach (var k in In(path).ToList()) Resources.Remove(k); return new HttpResponseMessage(HttpStatusCode.NoContent); }
                return new HttpResponseMessage(Resources.Remove(path) ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);

            case "MKCOL":
                if (ForbidMkcol) return new HttpResponseMessage(HttpStatusCode.Forbidden);
                if (Books.ContainsKey(path)) return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
                Books[path] = XDocument.Parse(body).Descendants(D + "displayname").First().Value;
                return new HttpResponseMessage(HttpStatusCode.Created);

            default:
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }
    }

    private static string NameAndEmail(string vcard)
    {
        string Line(string prefix) => vcard.Split("\r\n").FirstOrDefault(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ?? "";
        return Line("FN:") + "|" + Line("EMAIL").Split(':').Last().ToLowerInvariant();
    }

    private static XElement Response(string href, bool book = false, string? name = null, string? data = null,
        XElement? extra = null, string? home = null)
    {
        var prop = new XElement(D + "prop");
        if (name is not null) prop.Add(new XElement(D + "displayname", name));
        prop.Add(new XElement(D + "resourcetype", book ? [new XElement(D + "collection"), new XElement(C + "addressbook")] : []));
        prop.Add(new XElement(D + "getetag", "\"" + href.GetHashCode() + "\""));
        if (data is not null) prop.Add(new XElement(C + "address-data", data));
        if (extra is not null) prop.Add(extra);
        if (home is not null) prop.Add(new XElement(C + "addressbook-home-set", new XElement(D + "href", home)));
        return new XElement(D + "response", new XElement(D + "href", string.Join("/", href.Split('/').Select(s => Uri.EscapeDataString(s).Replace("%40", "@")))),
            new XElement(D + "propstat", prop, new XElement(D + "status", "HTTP/1.1 200 OK")));
    }

    private static HttpResponseMessage Multistatus(params XElement[] responses) =>
        new((HttpStatusCode)207)
        {
            Content = new StringContent(new XDocument(new XElement(D + "multistatus", responses)).ToString(), Encoding.UTF8, "application/xml"),
        };
}
