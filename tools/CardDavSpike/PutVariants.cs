using System.Xml.Linq;
using ContactManage.Core;

/// <summary>
/// Finds out which form of a real contact the server accepts. Takes one existing contact from the folder named by --book,
/// writes several versions of it into a scratch address book (deleted at the end), and prints only the HTTP status of each.
/// The contact's values are sent back to the same server and are never printed. Nothing existing is changed.
/// </summary>
static class PutVariants
{
    private static readonly XNamespace D = "DAV:", Card = "urn:ietf:params:xml:ns:carddav";
    private const string VCardType = "text/vcard; charset=utf-8";
    private static readonly string[] ServerManaged = ["PRODID", "CREATED", "REV", "DTSTAMP", "SORT-STRING"];

    public static async Task<int> Run(WriteTest.SendFn send, Uri baseUri, Uri home, string bookFilter)
    {
        const string list = """<d:propfind xmlns:d="DAV:"><d:prop><d:displayname/><d:resourcetype/></d:prop></d:propfind>""";
        var (_, hb, _) = await send("PROPFIND", home, list, 1);
        var source = XDocument.Parse(hb).Descendants(D + "response")
            .Where(r => r.Descendants(Card + "addressbook").Any() &&
                        (r.Descendants(D + "displayname").FirstOrDefault()?.Value ?? "").Contains(bookFilter, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Element(D + "href")!.Value).FirstOrDefault();
        if (source is null) { Console.WriteLine($"No address book matching '{bookFilter}'."); return 1; }

        var (_, ib, _) = await send("PROPFIND", new Uri(baseUri, source), list, 1);
        var item = XDocument.Parse(ib).Descendants(D + "response").Select(r => r.Element(D + "href")!.Value)
            .FirstOrDefault(h => !h.EndsWith('/'));
        if (item is null) { Console.WriteLine("That folder has no contacts."); return 1; }
        var (gs, original, _) = await send("GET", new Uri(baseUri, item));
        if (gs != 200) { Console.WriteLine($"Could not read the sample contact (HTTP {gs})."); return 1; }

        var raw = original.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        string Fresh(IEnumerable<string> lines) =>
            string.Join("\r\n", lines.Select(l => VCard.PropertyName(l) == "UID" ? "UID:cm-probe-" + Guid.NewGuid().ToString("N") : l)) + "\r\n";
        string Plain(IEnumerable<string> lines) => string.Join("\r\n", lines) + "\r\n";
        var stripped = raw.Where(l => !ServerManaged.Contains(VCard.PropertyName(l))).ToList();
        var ours = VCard.Write(VCard.ParseAll(original)[0]).Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToList();

        var variants = new (string Label, string Body)[]
        {
            ("A  exactly as the server gave it (same UID, server-managed lines)", Plain(raw)),
            ("B  without server-managed lines, same UID", Plain(stripped)),
            ("C  as given, new UID", Fresh(raw)),
            ("D  without server-managed lines, new UID", Fresh(stripped)),
            ("G  what ContactManage writes today, same UID", Plain(ours)),
            ("H  what ContactManage writes today, new UID", Fresh(ours)),
        };

        var book = new Uri(home, $"ContactManage-probe-{Guid.NewGuid().ToString("N")[..8]}/");
        Console.WriteLine($"\n== PUT variants in scratch address book {book.AbsolutePath}");
        var created = false;
        try
        {
            const string mkcol = """<d:mkcol xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:carddav"><d:set><d:prop><d:resourcetype><d:collection/><c:addressbook/></d:resourcetype><d:displayname>ContactManage probe</d:displayname></d:prop></d:set></d:mkcol>""";
            var (ms, _, _) = await send("MKCOL", book, mkcol);
            created = ms is 200 or 201 or 207;
            Console.WriteLine($"  MKCOL: HTTP {ms}");
            if (!created) return 1;

            foreach (var (label, body) in variants)
            {
                var (ps, pb, _) = await send("PUT", new Uri(book, $"p-{Guid.NewGuid():N}.vcf"), body, contentType: VCardType);
                Console.WriteLine($"  {label}: HTTP {ps}{(ps >= 400 && pb.Length > 0 ? $", body {pb.Length} chars" : "")}");
            }
        }
        finally
        {
            if (created)
            {
                var (ds, _, _) = await send("DELETE", book);
                Console.WriteLine($"  Scratch book removed: HTTP {ds}");
            }
        }
        return 0;
    }
}
