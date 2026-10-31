/// <summary>
/// Finds out how a server decides that two vCards are the same contact. Creates four fake contacts in the first address
/// book (one reusing another's UID, one reusing another's email, one without a UID), watches the count and whether the
/// earlier ones survive, then deletes one and watches the others. Only the four fake files are ever deleted.
/// </summary>
static class IdentityTest
{
    private static string Card(string? uid, string name, string email) =>
        "BEGIN:VCARD\r\nVERSION:3.0\r\n" + (uid is null ? "" : $"UID:{uid}\r\n") + $"FN:{name}\r\nN:{name};;;;\r\nEMAIL:{email}\r\nEND:VCARD\r\n";

    public static async Task<int> Run(WriteTest.SendFn send, Uri home)
    {
        var (hs, hb, _) = await send("PROPFIND", home, """<d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/></d:prop></d:propfind>""", 1);
        var book = hs != 207 ? null : System.Xml.Linq.XDocument.Parse(hb).Descendants(System.Xml.Linq.XName.Get("response", "DAV:"))
            .Where(r => r.Descendants(System.Xml.Linq.XName.Get("addressbook", "urn:ietf:params:xml:ns:carddav")).Any())
            .Select(r => r.Element(System.Xml.Linq.XName.Get("href", "DAV:"))!.Value).FirstOrDefault();
        if (book is null) { Console.WriteLine("No address book found."); return 1; }
        var bookUri = new Uri(home, book);

        var before = await Items(send, bookUri);
        Console.WriteLine($"\n== Identity test. Contacts in the book right now: {before.Count}");

        var files = new (string Name, string? Uid, string Display, string Email)[]
        {
            ("cm-id-a.vcf", "cm-id-a", "CM ID Test A", "cm-id-a@example.com"),
            ("cm-id-b.vcf", "cm-id-a", "CM ID Test B", "cm-id-b@example.com"),   // same UID as A
            ("cm-id-c.vcf", "cm-id-c", "CM ID Test C", "cm-id-a@example.com"),   // same email as A
            ("cm-id-d.vcf", null, "CM ID Test D", "cm-id-d@example.com"),         // no UID
        };
        var made = new List<string>();
        try
        {
            foreach (var f in files)
            {
                var (s, body, _) = await send("PUT", new Uri(bookUri, f.Name), Card(f.Uid, f.Display, f.Email), contentType: "text/vcard; charset=utf-8");
                if (s is 200 or 201 or 204) made.Add(f.Name);
                var now = await Items(send, bookUri);
                Console.WriteLine($"  PUT {f.Name} (uid {f.Uid ?? "none"}, email {f.Email}): HTTP {s}{(s >= 400 ? " " + Shorten(body) : "")}; book now {now.Count} (+{now.Count - before.Count})");
                foreach (var earlier in made.Where(m => m != f.Name))
                {
                    var (g, _, _) = await send("GET", new Uri(bookUri, earlier));
                    Console.WriteLine($"      earlier {earlier}: GET {g}");
                }
            }

            Console.WriteLine("\n  -- delete cm-id-a.vcf and look at the others");
            var (ds, _, _) = await send("DELETE", new Uri(bookUri, "cm-id-a.vcf"));
            var afterDelete = await Items(send, bookUri);
            Console.WriteLine($"  DELETE cm-id-a.vcf: HTTP {ds}; book now {afterDelete.Count} (+{afterDelete.Count - before.Count} compared with the start)");
            foreach (var other in new[] { "cm-id-b.vcf", "cm-id-c.vcf", "cm-id-d.vcf" })
            {
                var (g, _, _) = await send("GET", new Uri(bookUri, other));
                Console.WriteLine($"      {other}: GET {g}");
            }
        }
        finally
        {
            foreach (var f in files)
                await send("DELETE", new Uri(bookUri, f.Name));
            var final = await Items(send, bookUri);
            var leftovers = final.Count(h => h.Contains("cm-id-"));
            Console.WriteLine($"\n  Cleanup: book has {final.Count} contacts (started with {before.Count}); fake entries left: {leftovers}");
            if (leftovers > 0) Console.WriteLine("  Please delete the 'CM ID Test' contacts by hand.");
        }
        return 0;
    }

    private static Task<List<string>> Items(WriteTest.SendFn send, Uri book) => SingleContactTest.Items(send, book);
    private static string Shorten(string s) => s.Length > 200 ? s[..200] + "..." : s;
}
