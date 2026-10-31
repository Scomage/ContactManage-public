using System.Xml.Linq;

/// <summary>
/// Write test for servers that cannot create an address book (AOL has only one). Adds one clearly fake contact to the
/// account's first address book, reads it back, updates it, deletes it, and checks the book is back to its old size.
/// It never touches an existing contact; the fake contact is removed even when a check fails.
/// </summary>
static class SingleContactTest
{
    private static readonly XNamespace D = "DAV:", Card = "urn:ietf:params:xml:ns:carddav";
    private static int _failures;

    private static void Check(string what, bool ok, string detail = "")
    {
        if (!ok) _failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail.Length > 0 ? " - " + detail : "")}");
    }

    private const string TestCard = """
        BEGIN:VCARD
        VERSION:3.0
        UID:cm-single-test
        FN:ContactManage Test - delete me
        N:Test - delete me;ContactManage;;;
        ORG:Test Company
        EMAIL;TYPE=WORK:cm-test@example.com
        TEL;TYPE=CELL:+12125550100
        NOTE:Created by a ContactManage test. Safe to delete.
        END:VCARD
        """;

    private const string ListReq = """<d:propfind xmlns:d="DAV:"><d:prop><d:displayname/><d:resourcetype/><d:getetag/></d:prop></d:propfind>""";

    internal static async Task<List<string>> Items(WriteTest.SendFn send, Uri book)
    {
        var (s, b, _) = await send("PROPFIND", book, ListReq, 1);
        if (s != 207) return [];
        var self = book.AbsolutePath.TrimEnd('/');
        return XDocument.Parse(b).Descendants(D + "response").Select(r => r.Element(D + "href")!.Value)
            .Where(h => !h.EndsWith('/') && !Uri.UnescapeDataString(new Uri(book, h).AbsolutePath).TrimEnd('/').Equals(self, StringComparison.Ordinal))
            .ToList();
    }

    public static async Task<int> Run(WriteTest.SendFn send, Uri home)
    {
        var (hs, hb, _) = await send("PROPFIND", home, ListReq, 1);
        var book = hs != 207 ? null : XDocument.Parse(hb).Descendants(D + "response")
            .Where(r => r.Descendants(Card + "addressbook").Any()).Select(r => r.Element(D + "href")!.Value).FirstOrDefault();
        if (book is null) { Console.WriteLine("No address book found."); return 1; }
        var bookUri = new Uri(home, book);

        Console.WriteLine($"\n== Single-contact write test in the existing book {bookUri.AbsolutePath}");
        var before = await Items(send, bookUri);
        Console.WriteLine($"  Contacts before: {before.Count}");

        var file = new Uri(bookUri, $"cm-test-{Guid.NewGuid().ToString("N")[..8]}.vcf");
        var put = false;
        try
        {
            var (ps, pb, _) = await send("PUT", file, Crlf(TestCard), contentType: "text/vcard; charset=utf-8");
            put = ps is 200 or 201 or 204;
            Check("PUT one new contact", put, $"HTTP {ps}{(ps >= 400 ? " " + Shorten(pb) : "")}");
            if (!put) return 1;

            var after = await Items(send, bookUri);
            Check("The book grew by exactly one", after.Count == before.Count + 1, $"{after.Count} found");

            var (gs, gb, _) = await send("GET", file);
            if (gs != 200) Check("GET it back", false, $"HTTP {gs}");
            else
            {
                var sent = Properties(TestCard);
                var got = Properties(gb);
                var lost = sent.Except(got).ToList();
                Check("Round trip: every property comes back unchanged", lost.Count == 0);
                // The contact is fake, so its values are safe to print.
                foreach (var l in lost) Console.WriteLine($"      sent but not returned as-is: {l.Key}:{l.Value}");
                foreach (var a in got.Except(sent)) Console.WriteLine($"      server added or changed: {a.Key}:{a.Value}");
            }

            var (us, ub, _) = await send("PUT", file, Crlf(TestCard.Replace("delete me;", "delete me (updated);").Replace("FN:ContactManage Test - delete me", "FN:ContactManage Test - delete me (updated)")),
                contentType: "text/vcard; charset=utf-8");
            Check("Update the same contact", us is 200 or 204, $"HTTP {us}{(us >= 400 ? " " + Shorten(ub) : "")}");
            var (_, again, _) = await send("GET", file);
            Check("The update shows", again.Contains("(updated)"));
            Check("Still only one new contact", (await Items(send, bookUri)).Count == before.Count + 1);
        }
        finally
        {
            if (put)
            {
                var (ds, _, _) = await send("DELETE", file);
                var final = await Items(send, bookUri);
                var restored = ds is 200 or 204 && final.Count == before.Count && final.Order().SequenceEqual(before.Order());
                Check("Test contact deleted, book back to its old contents", restored, $"DELETE HTTP {ds}, {final.Count} contacts");
                if (!restored) Console.WriteLine($"  Please delete {file.AbsolutePath} by hand; it is the fake test contact.");
            }
        }

        Console.WriteLine(_failures == 0 ? "\nAll checks passed." : $"\n{_failures} check(s) failed.");
        return _failures == 0 ? 0 : 1;
    }

    private static string Crlf(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n";
    private static string Shorten(string s) => s.Length > 300 ? s[..300] + "..." : s;

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
