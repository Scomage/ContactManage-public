using ContactManage.Adapters;
using ContactManage.Adapters.CardDav;
using ContactManage.Adapters.IceWarp;
using ContactManage.Core;

/// <summary>
/// Replays what the app does when it creates a merged folder, but into a scratch address book that is deleted at the end,
/// and reports which contact the server refuses. Logins come from the app's own Windows Credential Manager entries
/// (so nothing is typed or printed). Output: folder names, HTTP statuses and vCard property NAMES, never contact values.
/// </summary>
static class PutAll
{
    public static async Task<int> Run(string[] filters)
    {
        var folders = new IceWarpContactSource().ListFolders();
        // A filter is text that must appear in "account name", optionally followed by #count to tell same-named folders apart.
        var chosen = filters.Select(f =>
        {
            var parts = f.Split('#');
            int? count = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : null;
            return folders.FirstOrDefault(x => x.IsReadable && (x.AccountName + " " + x.Name).Contains(parts[0], StringComparison.OrdinalIgnoreCase)
                                               && (count is null || x.ContactCount == count));
        }).ToList();
        for (var i = 0; i < filters.Length; i++)
            Console.WriteLine($"  '{filters[i]}' -> {(chosen[i] is { } c ? $"{c.Name} ({c.AccountName}, {c.ContactCount} contacts)" : "NOT FOUND")}");
        if (chosen.Any(c => c is null)) { foreach (var f in folders.Where(x => x.IsReadable)) Console.WriteLine($"    available: {f.Name} | {f.AccountName} | {f.ContactCount}"); return 1; }

        var vault = new WindowsCredentialVault();
        using var store = new CardDavContactStore((folder, _) =>
        {
            var saved = vault.Load(SavedLogins.KeyFor(folder));
            if (saved is null) Console.WriteLine($"  No saved login for {folder.AccountName}.");
            return Task.FromResult(saved);
        });

        var all = new List<Contact>();
        var origin = new Dictionary<Contact, string>();
        foreach (var folder in chosen)
            foreach (var c in await store.ReadContactsAsync(folder!))
            {
                all.Add(c);
                origin[c] = folder!.Name;
            }
        Console.WriteLine($"  Read {all.Count} contacts.");

        var result = new MergeEngine().Merge(all);
        var toWrite = new List<(string Label, Contact Contact)>();
        foreach (var c in all) toWrite.Add(($"raw from {origin[c]}", c));
        foreach (var g in result.Groups)
            try { toWrite.Add(($"merged set of {g.Members.Count}", g.BuildMerged())); }
            catch (Exception ex) { Console.WriteLine($"  Could not build a merged contact: {ex.GetType().Name}"); }

        var first = chosen[0]!;
        var creds = vault.Load(SavedLogins.KeyFor(first));
        if (creds is null) { Console.WriteLine("  No saved MRJ login to write with."); return 1; }
        using var client = new CardDavClient(new Uri(first.ServerUrl!), creds);
        var home = await client.GetHomeAsync();
        Console.WriteLine("  Address books now in the home:");
        foreach (var b in await client.ListAddressBooksAsync(home))
        {
            var parsed = (await client.ReadAllAsync(b.Uri)).SelectMany(r => VCard.ParseAll(r.VCard)).ToList();
            Console.WriteLine($"    {b.Name}  {b.Uri.AbsolutePath}  ({parsed.Count} contacts; with a name {parsed.Count(c => c.DisplayName is not null)}, " +
                $"with an email {parsed.Count(c => c.Emails.Count > 0)}, with a phone {parsed.Count(c => c.Phones.Count > 0)})");
        }
        if (Environment.GetEnvironmentVariable("PROBE_KEEP") == "1")
        {
            // A visible test folder at a fresh address: 3 real contacts and one made-up one. Kept, so the user can look; delete it afterwards.
            var sample = all.Take(3).Append(new Contact { DisplayName = "Probe Person", GivenName = "Probe", FamilyName = "Person", Emails = { "probe.person@example.com" } }).ToList();
            var made = await store.CreateFolderAsync(first, "ZZ ContactManage test", sample);
            Console.WriteLine($"  Created folder: {made}");
            return 0;
        }
        if (Environment.GetEnvironmentVariable("PROBE_SHAPE") == "1")
        {
            // The raw vCard text of the first contact in each book, with every value replaced by its length.
            foreach (var b in await client.ListAddressBooksAsync(home))
            {
                var one = (await client.ReadAllAsync(b.Uri)).FirstOrDefault();
                if (one is null) continue;
                Console.WriteLine($"\n  --- {b.Name} (href {one.Href[(one.Href.LastIndexOf('/') + 1)..]}) ---");
                foreach (var line in one.VCard.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0))
                {
                    var colon = line.IndexOf(':');
                    Console.WriteLine(colon < 0 ? $"    (continuation, {line.Length} chars)" : $"    {line[..colon]}: <{line.Length - colon - 1} chars>");
                }
            }
        }
        if (Environment.GetEnvironmentVariable("PROBE_LIST_ONLY") == "1") return 0; // read-only: stop before any scratch write
        var book = await client.CreateAddressBookAsync(home, Environment.GetEnvironmentVariable("PROBE_BOOK") ?? "ContactManage probe");
        Console.WriteLine($"\n== Writing {toWrite.Count} versions into scratch book {book.AbsolutePath}");
        var failed = 0;
        try
        {
            for (var i = 0; i < toWrite.Count; i++)
            {
                var (label, contact) = toWrite[i];
                var text = VCard.Write(contact);
                try { await client.PutNewAsync(book, $"cm-{Guid.NewGuid():N}.vcf", text); }
                catch (CardDavException ex)
                {
                    failed++;
                    var names = text.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).Select(l => VCard.PropertyName(l)).Distinct();
                    Console.WriteLine($"  #{i + 1} ({label}): HTTP {ex.Status}; {text.Length} chars; properties: {string.Join(",", names)}");
                }
            }
        }
        finally
        {
            await client.DeleteAsync(book);
            Console.WriteLine($"  Scratch book removed. {toWrite.Count - failed} accepted, {failed} refused.");
        }

        // Now the store's own create path, exactly as the app calls it, with a name no real folder uses.
        const string storeName = "ContactManage probe store";
        Console.WriteLine($"\n== Store.CreateFolderAsync with {all.Count} contacts");
        try
        {
            var report = await store.CreateFolderAsync(first, storeName, all);
            Console.WriteLine($"  Created: {report}");
        }
        catch (Exception ex) { Console.WriteLine($"  FAILED: {ex.GetType().Name}: {ex.Message}"); }
        foreach (var b in (await client.ListAddressBooksAsync(home)).Where(b => b.Name == storeName))
        {
            await client.DeleteAsync(b.Uri);
            Console.WriteLine($"  Removed {b.Uri.AbsolutePath}");
        }
        return 0;
    }
}

