using System.Xml.Linq;
using ContactManage.Core;

/// <summary>
/// Adds back the contacts of a backup file that the first address book no longer holds. It never deletes or changes an
/// existing contact. A contact counts as present when the book already has one with the same name and email (which is how
/// AOL tells contacts apart). Prints counts only, never names or addresses, and asks for YES before it writes.
/// </summary>
static class RestoreMissing
{
    private static readonly XNamespace D = "DAV:", Card = "urn:ietf:params:xml:ns:carddav";

    private static string Key(string? name, string? email) => (name ?? "").Trim().ToLowerInvariant() + "|" + (email ?? "").Trim().ToLowerInvariant();

    private static IEnumerable<string> Keys(Contact c) => c.Emails.Count == 0 ? [Key(c.DisplayName, null)] : c.Emails.Select(e => Key(c.DisplayName, e));

    public static async Task<int> Run(WriteTest.SendFn send, Uri baseUri, Uri home, string backupPath)
    {
        if (!File.Exists(backupPath)) { Console.WriteLine("The backup file was not found."); return 1; }
        var text = File.ReadAllText(backupPath);
        var blocks = text.Replace("\r\n", "\n").Split("END:VCARD", StringSplitOptions.RemoveEmptyEntries)
            .Select(b => b.Trim()).Where(b => b.Contains("BEGIN:VCARD")).Select(b => b + "\nEND:VCARD").ToList();
        var parsed = blocks.Select(b => (Raw: b, Contact: VCard.ParseAll(b).FirstOrDefault())).Where(p => p.Contact is not null).ToList();
        Console.WriteLine($"\n== Restore missing contacts. Backup holds {parsed.Count} cards.");

        var (hs, hb, _) = await send("PROPFIND", home, """<d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/></d:prop></d:propfind>""", 1);
        var book = hs != 207 ? null : XDocument.Parse(hb).Descendants(D + "response")
            .Where(r => r.Descendants(Card + "addressbook").Any()).Select(r => r.Element(D + "href")!.Value).FirstOrDefault();
        if (book is null) { Console.WriteLine("No address book found."); return 1; }
        var bookUri = new Uri(home, book);

        var hrefs = await SingleContactTest.Items(send, bookUri);
        var present = new HashSet<string>(StringComparer.Ordinal);
        var presentCount = 0;
        foreach (var batch in hrefs.Chunk(50))
        {
            var multiget = "<c:addressbook-multiget xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:carddav\"><d:prop><d:getetag/><c:address-data/></d:prop>"
                + string.Concat(batch.Select(h => "<d:href>" + System.Security.SecurityElement.Escape(h) + "</d:href>")) + "</c:addressbook-multiget>";
            var (s, body, _) = await send("REPORT", bookUri, multiget);
            if (s != 207) { Console.WriteLine($"Reading the book failed (HTTP {s}). Nothing was changed."); return 1; }
            foreach (var data in XDocument.Parse(body).Descendants(Card + "address-data").Select(x => x.Value))
                foreach (var c in VCard.ParseAll(data)) { presentCount++; foreach (var k in Keys(c)) present.Add(k); }
        }

        // One entry per distinct name+email in the backup: a server that keys on them cannot hold two of the same anyway.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<string>();
        var alreadyThere = 0;
        var sameInBackup = 0;
        foreach (var (raw, c) in parsed)
        {
            var keys = Keys(c!).ToList();
            if (keys.Any(present.Contains)) { alreadyThere++; continue; }
            if (keys.Any(seen.Contains)) { sameInBackup++; continue; }
            foreach (var k in keys) seen.Add(k);
            missing.Add(raw);
        }

        Console.WriteLine($"  Contacts in the book now:                         {presentCount}");
        Console.WriteLine($"  Backup cards already in the book (same name+email): {alreadyThere}");
        Console.WriteLine($"  Backup cards that repeat another card's name+email: {sameInBackup}   (the book cannot hold these twice through CardDAV)");
        Console.WriteLine($"  Backup cards missing from the book:                {missing.Count}");
        Console.WriteLine($"  The book would hold {presentCount + missing.Count} contacts afterwards. Nothing is deleted or changed.");
        if (missing.Count == 0) { Console.WriteLine("\nNothing to add."); return 0; }

        Console.Write($"\nType YES to add the {missing.Count} missing contacts: ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "YES", StringComparison.Ordinal)) { Console.WriteLine("Cancelled. Nothing was written."); return 0; }

        var made = new List<string>();
        var folded = 0;
        for (var i = 0; i < missing.Count; i++)
        {
            var file = $"cm-restore-{i + 1}-{Guid.NewGuid().ToString("N")[..8]}.vcf";
            var (s, _, _) = await send("PUT", new Uri(bookUri, file), missing[i].Replace("\n", "\r\n") + "\r\n", contentType: "text/vcard; charset=utf-8");
            if (s == 201) made.Add(file);
            else if (s is 200 or 204) folded++;
            else Console.WriteLine($"  Card {i + 1}: HTTP {s} (not written)");
            await Task.Delay(250);   // one at a time, gently
        }

        await Task.Delay(TimeSpan.FromSeconds(8));
        var after = await SingleContactTest.Items(send, bookUri);
        Console.WriteLine($"\n  Written as new: {made.Count}, answered as an update of an existing contact: {folded}");
        Console.WriteLine($"  The book now holds {after.Count} contacts (expected {presentCount + missing.Count}).");
        return 0;
    }
}
