/// <summary>
/// Finds out what makes a server treat a new vCard as a contact it already has: identical content under a new UID, the same
/// name and email with an extra phone, or the same name with another email. Fake contacts only; only the four fake files are deleted.
/// </summary>
static class KeyTest
{
    private static string Card(string uid, string email, string? phone = null) =>
        $"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:{uid}\r\nFN:CM Key Test\r\nN:Test;CM Key;;;\r\nEMAIL:{email}\r\n{(phone is null ? "" : $"TEL:{phone}\r\n")}END:VCARD\r\n";

    public static async Task<int> Run(WriteTest.SendFn send, Uri home)
    {
        var (hs, hb, _) = await send("PROPFIND", home, """<d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/></d:prop></d:propfind>""", 1);
        var book = hs != 207 ? null : System.Xml.Linq.XDocument.Parse(hb).Descendants(System.Xml.Linq.XName.Get("response", "DAV:"))
            .Where(r => r.Descendants(System.Xml.Linq.XName.Get("addressbook", "urn:ietf:params:xml:ns:carddav")).Any())
            .Select(r => r.Element(System.Xml.Linq.XName.Get("href", "DAV:"))!.Value).FirstOrDefault();
        if (book is null) { Console.WriteLine("No address book found."); return 1; }
        var bookUri = new Uri(home, book);

        var before = await SingleContactTest.Items(send, bookUri);
        Console.WriteLine($"\n== Key test. Contacts in the book right now: {before.Count}");

        var files = new (string Name, string Text, string What)[]
        {
            ("cm-key-1.vcf", Card("cm-key-1", "key@example.com"), "original"),
            ("cm-key-2.vcf", Card("cm-key-2", "key@example.com"), "same name and email, only the UID differs"),
            ("cm-key-3.vcf", Card("cm-key-3", "key@example.com", "+12125550111"), "same name and email, plus a phone"),
            ("cm-key-4.vcf", Card("cm-key-4", "key2@example.com"), "same name, another email"),
        };
        var made = new List<string>();
        try
        {
            foreach (var f in files)
            {
                var (s, body, _) = await send("PUT", new Uri(bookUri, f.Name), f.Text, contentType: "text/vcard; charset=utf-8");
                if (s is 200 or 201 or 204) made.Add(f.Name);
                var now = await SingleContactTest.Items(send, bookUri);
                Console.WriteLine($"  PUT {f.Name} ({f.What}): HTTP {s}{(s >= 400 ? " " + body[..Math.Min(150, body.Length)] : "")}; book now {now.Count} (+{now.Count - before.Count})");
            }

            Console.WriteLine("\n  -- which of the four files can be read back");
            foreach (var f in files)
            {
                var (g, _, _) = await send("GET", new Uri(bookUri, f.Name));
                Console.WriteLine($"      {f.Name}: GET {g}");
            }

            Console.WriteLine("\n  -- delete cm-key-1.vcf and look at the others");
            var (ds, _, _) = await send("DELETE", new Uri(bookUri, "cm-key-1.vcf"));
            var after = await SingleContactTest.Items(send, bookUri);
            Console.WriteLine($"  DELETE cm-key-1.vcf: HTTP {ds}; book now {after.Count} ({after.Count - before.Count:+#;-#;0} compared with the start)");
            foreach (var f in files.Skip(1))
            {
                var (g, _, _) = await send("GET", new Uri(bookUri, f.Name));
                Console.WriteLine($"      {f.Name}: GET {g}");
            }

            await Task.Delay(TimeSpan.FromSeconds(10));
            var later = await SingleContactTest.Items(send, bookUri);
            Console.WriteLine($"\n  Ten seconds later the book holds {later.Count} ({later.Count - before.Count:+#;-#;0} compared with the start)");
        }
        finally
        {
            foreach (var f in files) await send("DELETE", new Uri(bookUri, f.Name));
            var final = await SingleContactTest.Items(send, bookUri);
            var leftovers = final.Count(h => h.Contains("cm-key-"));
            Console.WriteLine($"\n  Cleanup: book has {final.Count} contacts (started with {before.Count}); fake entries left: {leftovers}");
            if (leftovers > 0) Console.WriteLine("  Please delete the 'CM Key Test' contacts by hand.");
        }
        return 0;
    }
}
