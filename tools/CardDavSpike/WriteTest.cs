using System.Xml.Linq;

/// <summary>
/// Write test against a scratch address book created inside the user's own address-book home.
/// Everything it creates is fake and lives only under that book, which is deleted at the end.
/// It never touches an existing folder or contact.
/// </summary>
static class WriteTest
{
    public delegate Task<(int Status, string Body, HttpResponseMessage Resp)> SendFn(
        string method, Uri uri, string? body = null, int depth = 0, string contentType = "application/xml");

    private static readonly XNamespace D = "DAV:", Card = "urn:ietf:params:xml:ns:carddav";
    private static int _failures;

    private static void Check(string what, bool ok, string detail = "")
    {
        if (!ok) _failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail.Length > 0 ? " - " + detail : "")}");
    }

    private const string RichVCard = """
        BEGIN:VCARD
        VERSION:3.0
        UID:cm-test-rich
        FN:José Müller-Łukasz
        N:Müller-Łukasz;José;;Dr.;Jr.
        ORG:Acme\, Inc.;Purchasing
        TITLE:Senior Buyer
        EMAIL;TYPE=INTERNET,WORK:jose@acme.example
        EMAIL;TYPE=INTERNET,HOME:jose@home.example
        TEL;TYPE=CELL:+14155550101
        TEL;TYPE=WORK,VOICE:+14155550102
        TEL;TYPE=HOME,FAX:+14155550103
        ADR;TYPE=WORK:;;1 Main St\, Suite 4;Springfield;IL;62701;USA
        URL:https://acme.example
        BDAY:1980-02-29
        CATEGORIES:Friends,Work
        NOTE:Line one\nLine two; with semicolon\, comma and \\ backslash
        END:VCARD
        """;

    private const string SimpleVCard = """
        BEGIN:VCARD
        VERSION:3.0
        UID:cm-test-simple
        FN:Test Person
        N:Person;Test;;;
        EMAIL:test.person@example.com
        END:VCARD
        """;

    private const string ThirdVCard = """
        BEGIN:VCARD
        VERSION:3.0
        UID:cm-test-third
        FN:Third Contact
        N:Contact;Third;;;
        TEL:+12125550100
        END:VCARD
        """;

    private const string VCardType = "text/vcard; charset=utf-8";
    private const string PropfindType = """<d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getetag/></d:prop></d:propfind>""";

    public static async Task<int> Run(SendFn send, Uri home)
    {
        var bookUri = new Uri(home, $"ContactManage-test-{Guid.NewGuid().ToString("N")[..8]}/");
        Console.WriteLine($"\n== Write test in scratch address book {bookUri.AbsolutePath}");
        var created = false;
        try
        {
            const string mkcol = """
                <d:mkcol xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:carddav"><d:set><d:prop>
                <d:resourcetype><d:collection/><c:addressbook/></d:resourcetype>
                <d:displayname>ContactManage test</d:displayname></d:prop></d:set></d:mkcol>
                """;
            var (s, body, _) = await send("MKCOL", bookUri, mkcol);
            created = s is 201 or 200 or 207;
            Check("Create address book (extended MKCOL)", created, $"HTTP {s}");
            if (!created)
            {
                Console.WriteLine($"  Server said: {Shorten(body)}");
                return 1;
            }

            var (s2, b2, _) = await send("PROPFIND", home, PropfindType, 1);
            var listed = XDocument.Parse(b2).Descendants(D + "response").Any(r =>
                r.Element(D + "href")!.Value.TrimEnd('/') == bookUri.AbsolutePath.TrimEnd('/') && r.Descendants(Card + "addressbook").Any());
            Check("New book is listed as an address book", s2 == 207 && listed);

            var items = new[] { ("rich.vcf", RichVCard), ("simple.vcf", SimpleVCard), ("third.vcf", ThirdVCard) };
            foreach (var (name, text) in items)
            {
                var (ps, pb, _) = await send("PUT", new Uri(bookUri, name), Crlf(text), contentType: VCardType);
                Check($"PUT {name}", ps is 201 or 204 or 200, $"HTTP {ps}{(ps >= 400 ? " " + Shorten(pb) : "")}");
            }

            // Field fidelity: compare what we sent with what the server returns.
            foreach (var (name, text) in items)
            {
                var (gs, gb, _) = await send("GET", new Uri(bookUri, name));
                if (gs != 200) { Check($"GET {name}", false, $"HTTP {gs}"); continue; }
                var sent = Properties(text);
                var got = Properties(gb);
                var lost = sent.Except(got).ToList();
                Check($"Round trip {name}: every property comes back unchanged", lost.Count == 0);
                foreach (var l in lost) Console.WriteLine($"      sent but not returned as-is: {l.Key}:{l.Value}");
                // The contacts here are fake, so the returned values are safe to print.
                foreach (var a in got.Except(sent))
                    Console.WriteLine($"      server returned: {a.Key}:{a.Value}");
                if (name == "rich.vcf")
                    Console.WriteLine($"      returned vCard version: {got.FirstOrDefault(p => p.Key == "VERSION").Value}");
            }

            // Bulk read, the way the real reader will fetch thousands of contacts.
            const string query = """
                <c:addressbook-query xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:carddav"><d:prop><d:getetag/><c:address-data/></d:prop></c:addressbook-query>
                """;
            var (qs, qb, _) = await send("REPORT", bookUri, query, 1);
            var n = qs == 207 ? XDocument.Parse(qb).Descendants(Card + "address-data").Count() : -1;
            Check("Bulk read with addressbook-query returns all 3 contacts", n == 3, $"HTTP {qs}, {n} vCards");

            var changed = SimpleVCard.Replace("FN:Test Person", "FN:Test Person Updated");
            var (us, ub, _) = await send("PUT", new Uri(bookUri, "simple.vcf"), Crlf(changed), contentType: VCardType);
            Check("Overwrite an existing contact", us is 200 or 204, $"HTTP {us}{(us >= 400 ? " " + Shorten(ub) : "")}");
            var (_, after, _) = await send("GET", new Uri(bookUri, "simple.vcf"));
            Check("Overwritten contact shows the new name", after.Contains("Test Person Updated"));

            var (ds, _, _) = await send("DELETE", new Uri(bookUri, "third.vcf"));
            Check("Delete one contact", ds is 200 or 204, $"HTTP {ds}");
            var (cs, cb, _) = await send("PROPFIND", bookUri, PropfindType, 1);
            var count = XDocument.Parse(cb).Descendants(D + "response").Count() - 1;
            Check("Two contacts remain", cs == 207 && count == 2, $"{count} found");
        }
        finally
        {
            if (created)
            {
                var (xs, _, _) = await send("DELETE", bookUri);
                var (gone, _, _) = await send("PROPFIND", bookUri, PropfindType, 0);
                var removed = xs is 200 or 204 && gone == 404;
                Check("Scratch address book deleted", removed, $"DELETE HTTP {xs}, afterwards HTTP {gone}");
                if (!removed) Console.WriteLine($"  Please delete {bookUri.AbsolutePath} by hand; it holds only fake contacts.");
            }
        }

        Console.WriteLine(_failures == 0 ? "\nAll checks passed." : $"\n{_failures} check(s) failed.");
        return _failures == 0 ? 0 : 1;
    }

    private static string Crlf(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n";
    private static string Shorten(string s) => s.Length > 300 ? s[..300] + "..." : s;

    /// <summary>Unfolded vCard lines as (name with sorted parameters, value). Volatile server fields are skipped.</summary>
    private static HashSet<(string Key, string Value)> Properties(string vcard)
    {
        var set = new HashSet<(string, string)>();
        foreach (var line in vcard.Replace("\r\n", "\n").Replace("\n ", "").Split('\n'))
        {
            var i = line.IndexOf(':');
            if (i <= 0) continue;
            var parts = line[..i].Split(';');
            var name = parts[0].ToUpperInvariant();
            if (name is "PRODID" or "REV" or "DTSTAMP" or "CREATED" or "BEGIN" or "END") continue;
            var prms = parts.Skip(1).SelectMany(p => p.ToUpperInvariant().Replace("TYPE=", "").Split(',')).OrderBy(x => x).ToList();
            set.Add(($"{name}{(prms.Count > 0 ? ";" + string.Join(",", prms) : "")}", line[(i + 1)..].Trim()));
        }
        return set;
    }
}
