using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using ContactManage.Adapters.IceWarp;

// Read-only CardDAV probe. Asks for the password itself (never pass it on the command line) and
// sends it only in the Authorization header. Prints server capabilities, address books and
// contact counts, and the vCard property NAMES of one contact, never any contact values.

var logIndex = Array.IndexOf(args, "--log");
if (logIndex >= 0 && logIndex + 1 < args.Length)
    Console.SetOut(new Tee(Console.Out, new StreamWriter(args[logIndex + 1]) { AutoFlush = true }));

var putAll = Array.IndexOf(args, "--put-all");
if (putAll >= 0)
    return await PutAll.Run(args.Skip(putAll + 1).TakeWhile(a => !a.StartsWith("--")).ToArray());

XNamespace d = "DAV:", card = "urn:ietf:params:xml:ns:carddav";

var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IceWarp Desktop Client");
var accounts = AccountReader.Read(Path.Combine(profile, "accounts.dat")).Values
    .Where(a => a.TypeName.StartsWith("CalDav", StringComparison.Ordinal)).OrderBy(a => a.AccountName).ToList();

Console.WriteLine("CardDAV accounts known to IceWarp:");
for (var i = 0; i < accounts.Count; i++)
    Console.WriteLine($"  {i + 1}. {accounts[i].AccountName}  {accounts[i].Host}");

Console.Write("Pick a number (or press Enter to type a URL): ");
var pick = Console.ReadLine();
string url = "", defaultUser = "";
if (int.TryParse(pick, out var n) && n >= 1 && n <= accounts.Count)
{
    url = accounts[n - 1].Host ?? "";
    defaultUser = accounts[n - 1].AccountName ?? "";
}
Console.Write($"Server URL [{url}]: ");
var typed = Console.ReadLine();
if (!string.IsNullOrWhiteSpace(typed)) url = typed.Trim();
Console.Write($"User name [{defaultUser}]: ");
var user = Console.ReadLine();
if (string.IsNullOrWhiteSpace(user)) user = defaultUser;
Console.Write("Password (hidden): ");
var password = ReadHidden();
Console.WriteLine();

var baseUri = new Uri(url);
using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
http.DefaultRequestHeaders.UserAgent.ParseAdd("ContactManage-spike/0.1");

async Task<(int Status, string Body, HttpResponseMessage Resp)> Send(string method, Uri uri, string? body = null, int depth = 0, string contentType = "application/xml")
{
    for (var hop = 0; hop < 5; hop++)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), uri);
        if (method == "PROPFIND" || method == "REPORT") req.Headers.Add("Depth", depth.ToString());
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, MediaTypeHeaderValue.Parse(contentType));
        var resp = await http.SendAsync(req);
        if ((int)resp.StatusCode is 301 or 302 or 307 or 308 && resp.Headers.Location is { } loc)
        {
            uri = new Uri(uri, loc);
            continue;
        }
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(), resp);
    }
    throw new InvalidOperationException("Too many redirects");
}

Console.WriteLine($"\n== OPTIONS {baseUri}");
var (status, _, resp) = await Send("OPTIONS", baseUri);
Console.WriteLine($"HTTP {status}");
foreach (var h in new[] { "DAV", "Allow" })
    if (resp.Headers.TryGetValues(h, out var v) || resp.Content.Headers.TryGetValues(h, out v))
        Console.WriteLine($"{h}: {string.Join(", ", v!)}");
if (status == 401) { Console.WriteLine("Login rejected (401). Check user name and password."); return 1; }

const string principalReq = """<d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:carddav"><d:prop><d:current-user-principal/><c:addressbook-home-set/></d:prop></d:propfind>""";
Console.WriteLine("\n== Discover address book home");
var (s2, b2, _) = await Send("PROPFIND", baseUri, principalReq, 0);
Console.WriteLine($"HTTP {s2}");
var home = XDocument.Parse(b2).Descendants(card + "addressbook-home-set").Descendants(d + "href").FirstOrDefault()?.Value;
if (home is null)
{
    var principal = XDocument.Parse(b2).Descendants(d + "current-user-principal").Descendants(d + "href").FirstOrDefault()?.Value;
    Console.WriteLine($"principal: {principal ?? "(none)"}");
    if (principal is not null)
    {
        (_, b2, _) = await Send("PROPFIND", new Uri(baseUri, principal), principalReq, 0);
        home = XDocument.Parse(b2).Descendants(card + "addressbook-home-set").Descendants(d + "href").FirstOrDefault()?.Value;
    }
}
Console.WriteLine($"addressbook-home-set: {home ?? "(not found, using the URL itself)"}");
var homeUri = home is null ? baseUri : new Uri(baseUri, home);

if (args.Contains("--write-test"))
    return await WriteTest.Run(Send, homeUri);

var restoreIndex = Array.IndexOf(args, "--restore-missing");
if (restoreIndex >= 0 && restoreIndex + 1 < args.Length)
    return await RestoreMissing.Run(Send, baseUri, homeUri, args[restoreIndex + 1]);

if (args.Contains("--key-test"))
    return await KeyTest.Run(Send, homeUri);

if (args.Contains("--identity-test"))
    return await IdentityTest.Run(Send, homeUri);

if (args.Contains("--single-contact-test"))
    return await SingleContactTest.Run(Send, homeUri);

var variantsIndex = Array.IndexOf(args, "--put-variants");
if (variantsIndex >= 0 && variantsIndex + 1 < args.Length)
    return await PutVariants.Run(Send, baseUri, homeUri, args[variantsIndex + 1]);

const string listReq = """<d:propfind xmlns:d="DAV:"><d:prop><d:displayname/><d:resourcetype/><d:getetag/></d:prop></d:propfind>""";
Console.WriteLine("\n== Collections");
var (s3, b3, _) = await Send("PROPFIND", homeUri, listReq, 1);
Console.WriteLine($"HTTP {s3}");
var books = new List<(string Href, string Name)>();
foreach (var r in XDocument.Parse(b3).Descendants(d + "response"))
{
    var isBook = r.Descendants(card + "addressbook").Any();
    var href = r.Element(d + "href")!.Value;
    var name = r.Descendants(d + "displayname").FirstOrDefault()?.Value ?? "";
    Console.WriteLine($"  {(isBook ? "[address book]" : "[other]       ")} {name}  {href}");
    if (isBook) books.Add((href, name));
}

// --book <text> samples the first folder whose name contains the text, instead of the first folder with items.
var bookIndex = Array.IndexOf(args, "--book");
var bookFilter = bookIndex >= 0 && bookIndex + 1 < args.Length ? args[bookIndex + 1] : null;
string? sampleHref = null;
foreach (var (href, name) in books)
{
    var (s4, b4, _) = await Send("PROPFIND", new Uri(baseUri, href), listReq, 1);
    var items = XDocument.Parse(b4).Descendants(d + "response")
        .Select(r => r.Element(d + "href")!.Value)
        .Where(h => !h.TrimEnd('/').Equals(href.TrimEnd('/'), StringComparison.Ordinal)).ToList();
    Console.WriteLine($"  {name,-24} HTTP {s4}, {items.Count} items");
    if (bookFilter is null || name.Contains(bookFilter, StringComparison.OrdinalIgnoreCase))
        sampleHref ??= items.FirstOrDefault();
}

if (sampleHref is not null)
{
    Console.WriteLine("\n== vCard properties present in one sample contact (names only)");
    var (s5, b5, r5) = await Send("GET", new Uri(baseUri, sampleHref));
    Console.WriteLine($"HTTP {s5}, {r5.Content.Headers.ContentType}");
    var names = b5.Replace("\r\n ", "").Split('\n')
        .Select(l => l.Trim()).Where(l => l.Contains(':'))
        .Select(l => l.Split(':')[0]).Distinct();
    Console.WriteLine(string.Join(", ", names));

    // Which fields our own parser fills from that contact (yes/no only, never the values).
    var parsed = ContactManage.Core.VCard.ParseAll(b5);
    Console.WriteLine($"\nOur parser found {parsed.Count} contact(s) in it");
    if (parsed.Count > 0)
    {
        var p = parsed[0];
        Console.WriteLine($"  display name: {(p.DisplayName is null ? "no" : "yes")}, given: {(p.GivenName is null ? "no" : "yes")}, family: {(p.FamilyName is null ? "no" : "yes")}, " +
            $"company: {(p.Company is null ? "no" : "yes")}, emails: {p.Emails.Count}, phones: {p.Phones.Count}, extra lines: {p.Extra.Count}");
    }

    // The same through the bulk REPORT the app uses.
    var bookHref = sampleHref[..(sampleHref.TrimEnd('/').LastIndexOf('/') + 1)];
    const string query = """<c:addressbook-query xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:carddav"><d:prop><d:getetag/><c:address-data/></d:prop></c:addressbook-query>""";
    var (s6, b6, _) = await Send("REPORT", new Uri(baseUri, bookHref), query, 1);
    Console.WriteLine($"\nREPORT on {bookHref}: HTTP {s6}");
    var hrefs = XDocument.Parse(b6).Descendants(d + "response").Select(r => r.Element(d + "href")!.Value).Take(3).ToList();
    var variants = new Dictionary<string, string>
    {
        ["allprop"] = """<c:addressbook-query xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:carddav"><d:prop><d:getetag/><c:address-data><c:allprop/></c:address-data></d:prop></c:addressbook-query>""",
        ["multiget"] = $"""<c:addressbook-multiget xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:carddav"><d:prop><d:getetag/><c:address-data/></d:prop>{string.Concat(hrefs.Select(h => $"<d:href>{h}</d:href>"))}</c:addressbook-multiget>""",
    };
    foreach (var (label, q) in variants)
    {
        var (sv, bv, _) = await Send("REPORT", new Uri(baseUri, bookHref), q, 1);
        Console.WriteLine($"\nREPORT variant {label}: HTTP {sv}");
        if (sv != 207) continue;
        foreach (var data in XDocument.Parse(bv).Descendants(card + "address-data").Take(3).Select(x => x.Value))
        {
            var one = ContactManage.Core.VCard.ParseAll(data);
            Console.WriteLine($"  address-data {data.Length} chars, emails: {(one.Count == 0 ? 0 : one[0].Emails.Count)}, phones: {(one.Count == 0 ? 0 : one[0].Phones.Count)}");
        }
    }
    foreach (var data in XDocument.Parse(b6).Descendants(card + "address-data").Take(3).Select(x => x.Value))
    {
        var one = ContactManage.Core.VCard.ParseAll(data);
        Console.WriteLine($"  address-data {data.Length} chars, {one.Count} contact(s), " +
            (one.Count == 0 ? "" : $"display name: {(one[0].DisplayName is null ? "no" : "yes")}, emails: {one[0].Emails.Count}, phones: {one[0].Phones.Count}, extra lines: {one[0].Extra.Count}"));
    }
}

Console.WriteLine("\nRead-only probe finished. Nothing was changed on the server.");
return 0;

static string ReadHidden()
{
    var sb = new StringBuilder();
    while (Console.ReadKey(intercept: true) is var k && k.Key != ConsoleKey.Enter)
    {
        if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
        else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
    }
    return sb.ToString();
}
