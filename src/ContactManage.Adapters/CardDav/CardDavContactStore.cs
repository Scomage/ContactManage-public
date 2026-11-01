using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using ContactManage.Core;

namespace ContactManage.Adapters.CardDav;

/// <summary>
/// Reads and writes contact folders over CardDAV. Passwords come from <paramref name="credentials"/>, which is asked
/// once per account and again, with an error message, when the server rejects the login.
/// <paramref name="signedIn"/> is called once the server has accepted a login, so a caller can save it only then.
/// </summary>
public sealed class CardDavContactStore(
    Func<ContactFolder, string?, Task<ServerCredentials?>> credentials,
    HttpMessageHandler? handler = null,
    Action<ContactFolder>? signedIn = null) : IContactStore, IDisposable
{
    private const int MaxLoginAttempts = 3;

    private sealed record Session(CardDavClient Client, Uri Home, bool CanCreateBooks);

    private readonly Dictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, Uri> _books = new(); // filled from parallel lookups

    /// <summary>False once a signed-in server has said it cannot create address books; unknown (not signed in yet) counts as yes.</summary>
    public bool CanCreateFolder(ContactFolder account) =>
        account.CanCreateFolder && (!_sessions.TryGetValue(account.AccountKey, out var session) || session.CanCreateBooks);

    public void Dispose()
    {
        foreach (var s in _sessions.Values) s.Client.Dispose();
        _sessions.Clear();
    }

    private async Task<Session> SessionFor(ContactFolder folder, CancellationToken ct)
    {
        if (_sessions.TryGetValue(folder.AccountKey, out var existing)) return existing;
        if (string.IsNullOrWhiteSpace(folder.ServerUrl))
            throw new CardDavException($"The server address of {folder.AccountName} is not known.");

        string? error = null;
        for (var attempt = 0; attempt < MaxLoginAttempts; attempt++)
        {
            var creds = await credentials(folder, error).ConfigureAwait(false)
                ?? throw new OperationCanceledException("Sign-in was cancelled.");
            var client = new CardDavClient(new Uri(folder.ServerUrl), creds, handler);
            try
            {
                var home = await client.GetHomeAsync(ct).ConfigureAwait(false);
                var session = new Session(client, home, await client.AllowsNewAddressBooksAsync(ct).ConfigureAwait(false) != false);
                _sessions[folder.AccountKey] = session;
                signedIn?.Invoke(folder);
                return session;
            }
            catch (CardDavAuthException ex)
            {
                client.Dispose();
                error = ex.Message;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
        throw new CardDavAuthException($"Could not sign in to {folder.AccountName}.", 401);
    }

    /// <summary>
    /// Maps a folder from IceWarp's cache to its address book on the server. The cache path
    /// ("/0/Shared/Team") is relative to the account's home once the leading account number is removed.
    /// If nothing is there, an address book with the same display name is used when exactly one matches.
    /// </summary>
    private async Task<(Session Session, Uri Book)> ResolveAsync(ContactFolder folder, CancellationToken ct)
    {
        var session = await SessionFor(folder, ct).ConfigureAwait(false);
        if (_books.TryGetValue(folder.Id, out var cached)) return (session, cached);

        var relative = Regex.Replace(folder.Path.Trim('/'), @"^\d+(/|$)", "");
        if (relative.Length > 0)
        {
            var candidate = new Uri(session.Home, string.Join("/", relative.Split('/').Select(Uri.EscapeDataString)) + "/");
            if (await session.Client.IsAddressBookAsync(candidate, ct).ConfigureAwait(false))
                return (session, _books[folder.Id] = candidate);
        }

        var byName = (await session.Client.ListAddressBooksAsync(session.Home, ct).ConfigureAwait(false))
            .Where(b => b.Name.Equals(folder.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1) return (session, _books[folder.Id] = byName[0].Uri);

        throw new CardDavException($"Could not find the folder \"{folder.Name}\" on {folder.ServerName}.");
    }

    public async Task<FolderRefresh> RefreshFoldersAsync(IReadOnlyList<ContactFolder> folders, Func<ContactFolder, bool> mayContact, CancellationToken ct = default)
    {
        var updated = new List<ContactFolder>();
        var problems = new List<string>();
        var asked = 0;

        foreach (var account in folders.Where(f => f.Kind == FolderKind.CardDav && !string.IsNullOrWhiteSpace(f.ServerUrl)).GroupBy(f => f.AccountKey))
        {
            var template = account.First();
            if (!_sessions.ContainsKey(account.Key) && !mayContact(template)) continue;
            try
            {
                var session = await SessionFor(template, ct).ConfigureAwait(false);
                asked++;
                var found = new HashSet<Uri>();
                var unmatchedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Counting lists a whole folder, so the folders of an account are counted a few at a time.
                var cached = account.ToArray();
                var counted = new (Uri? Book, int? Count)[cached.Length];
                await Parallel.ForEachAsync(Enumerable.Range(0, cached.Length),
                    new ParallelOptions { MaxDegreeOfParallelism = CardDavClient.Parallelism, CancellationToken = ct },
                    async (i, token) =>
                    {
                        try
                        {
                            var (_, book) = await ResolveAsync(cached[i], token).ConfigureAwait(false);
                            counted[i] = (book, await session.Client.CountAsync(book, token).ConfigureAwait(false));
                        }
                        catch (CardDavException ex) when (ex is not CardDavAuthException)
                        {
                            counted[i] = (null, null); // the cached entry stays as it is
                        }
                    }).ConfigureAwait(false);

                for (var i = 0; i < cached.Length; i++)
                {
                    if (counted[i].Book is not { } book) { unmatchedNames.Add(cached[i].Name); continue; }
                    found.Add(book);
                    updated.Add(cached[i] with { ContactCount = counted[i].Count ?? cached[i].ContactCount });
                }

                foreach (var b in await session.Client.ListAddressBooksAsync(session.Home, ct).ConfigureAwait(false))
                {
                    // A book that matches a cached folder we could not resolve by path is that folder, not a new one.
                    if (found.Contains(b.Uri) || unmatchedNames.Contains(b.Name)) continue;
                    var relative = Uri.UnescapeDataString(session.Home.MakeRelativeUri(b.Uri).ToString()).Trim('/');
                    updated.Add(new ContactFolder($"{template.AccountKey}:/0/{relative}", b.Name, "/0/" + relative, template.AccountName,
                        template.ServerName, FolderKind.CardDav, true,
                        await session.Client.CountAsync(b.Uri, ct).ConfigureAwait(false), ServerUrl: template.ServerUrl));
                }
            }
            catch (CardDavException ex)
            {
                problems.Add($"{template.AccountName}: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                problems.Add($"{template.AccountName}: {ex.Message}");
            }
        }
        return new FolderRefresh(updated, problems, asked);
    }

    public async Task<IReadOnlyList<Contact>> ReadContactsAsync(ContactFolder folder, CancellationToken ct = default)
    {
        var (session, book) = await ResolveAsync(folder, ct).ConfigureAwait(false);
        var remote = await session.Client.ReadAllAsync(book, ct).ConfigureAwait(false);
        var contacts = new List<Contact>(remote.Count);
        foreach (var r in remote)
            foreach (var c in VCard.ParseAll(r.VCard))
            {
                c.SourceFolderId = folder.Id;
                c.RemoteId = r.Href;
                c.Uid ??= Path.GetFileNameWithoutExtension(r.Href.TrimEnd('/'));
                contacts.Add(c);
            }
        return contacts;
    }

    public async Task<WriteReport> ReplaceContactsAsync(ContactFolder folder, IReadOnlyList<Contact> contacts, CancellationToken ct = default)
    {
        var (session, book) = await ResolveAsync(folder, ct).ConfigureAwait(false);
        var existing = await session.Client.ListHrefsAsync(book, ct).ConfigureAwait(false); // addresses only, no need for the data

        if (contacts.Count == 0 && existing.Count > 0)
            throw new InvalidOperationException("Refusing to empty a folder: the merged list has no contacts.");

        var existingSet = existing.ToHashSet(StringComparer.Ordinal);
        bool IsUntouched(Contact c) => c.Sources.Count == 0 && c.SourceFolderId == folder.Id && c.RemoteId is not null && existingSet.Contains(c.RemoteId);

        var keep = contacts.Where(IsUntouched).Select(c => c.RemoteId!).ToHashSet(StringComparer.Ordinal);
        var untouched = keep.Count;

        // A merged contact is written over one of its own originals instead of next to them. Some servers (AOL) treat a new
        // entry with the same name and email as the entry they already have, so writing it under a new address and then
        // deleting the originals would delete it too.
        var toUpdate = new List<(Contact Contact, ContactSource Survivor)>();
        var toAdd = new List<Contact>();
        foreach (var c in contacts.Where(c => !IsUntouched(c)))
        {
            var survivor = c.Sources.FirstOrDefault(s => s.FolderId == folder.Id && s.RemoteId is not null && existingSet.Contains(s.RemoteId) && !keep.Contains(s.RemoteId));
            if (survivor is null) { toAdd.Add(c); continue; }
            keep.Add(survivor.RemoteId!);
            toUpdate.Add((c, survivor));
        }

        // 1. new contacts, 2. check they really are there, 3. merged contacts over their originals, 4. only now the deletions:
        // if anything fails or looks wrong before step 4, no contact has been lost.
        var written = await PutAllAsync(session.Client, book, toAdd, ct).ConfigureAwait(false);
        var folded = 0;
        var foldedInto = 0;
        if (written.Count > 0)
        {
            var listed = (await session.Client.ListHrefsAsync(book, ct).ConfigureAwait(false)).Select(PathOf).ToHashSet(StringComparer.Ordinal);
            var missing = written.Where(w => !listed.Contains(PathOf(w.Uri.AbsolutePath))).Select(w => w.Contact).ToList();
            if (missing.Count > 0)
            {
                // The server (AOL) took these as updates of contacts it already had, which is what a restore of a backup into the
                // folder it came from does. Those contacts now hold the new data, so they stay; but only when every one of them can
                // be matched to a contact that was there, otherwise something else happened and nothing is deleted.
                var matches = await MatchFoldedAsync(session.Client, book, missing, existing, ct).ConfigureAwait(false)
                    ?? throw new CardDavException($"The server accepted {missing.Count:N0} of the {written.Count:N0} new contacts but did not store them separately, " +
                        "and they could not be matched to contacts the folder already had. Nothing was deleted.");
                folded = missing.Count;
                foldedInto = matches.Count(keep.Add);
            }
        }
        var toRemove = existing.Where(h => !keep.Contains(h)).ToList();
        await ForEachAsync(toUpdate, (u, token) => session.Client.PutAsync(new Uri(book, u.Survivor.RemoteId!), VCard.Write(u.Contact, u.Survivor.Uid), token), ct).ConfigureAwait(false);
        await ForEachAsync(toRemove, (href, token) => session.Client.DeleteAsync(new Uri(book, href), token), ct).ConfigureAwait(false);

        var after = await session.Client.CountAsync(book, ct).ConfigureAwait(false);
        var note = folded == 0 ? null
            : $"{folded:N0} {(folded == 1 ? "contact has" : "contacts have")} the same name and email as {(folded == 1 ? "one" : "ones")} this server already had. " +
              "The server identifies a contact by name and email, so those were updated instead of added.";
        return new WriteReport(folder.Name, toAdd.Count - folded, toRemove.Count, untouched, after, toUpdate.Count + foldedInto, note);
    }

    /// <summary>
    /// For contacts the server folded into existing ones, the address of the existing contact each went into (a contact with
    /// the same name and a shared email). Null when any of them has no such contact.
    /// </summary>
    private static async Task<List<string>?> MatchFoldedAsync(CardDavClient client, Uri book, IReadOnlyList<Contact> folded, IReadOnlyList<string> existing, CancellationToken ct)
    {
        var normalizer = new Normalizer();
        var existingSet = existing.ToHashSet(StringComparer.Ordinal);
        var now = new List<(string Href, string Name, HashSet<string?> Emails)>();
        foreach (var r in await client.ReadAllAsync(book, ct).ConfigureAwait(false))
        {
            if (!existingSet.Contains(r.Href)) continue;
            foreach (var c in VCard.ParseAll(r.VCard)) now.Add((r.Href, normalizer.NameKey(c), c.Emails.Select(normalizer.Email).ToHashSet()));
        }

        var hrefs = new List<string>();
        foreach (var c in folded)
        {
            var name = normalizer.NameKey(c);
            var emails = c.Emails.Select(normalizer.Email).Where(e => e is not null).ToList();
            var hit = now.FirstOrDefault(n => n.Name == name && emails.Any(n.Emails.Contains));
            if (hit.Href is null) return null;
            hrefs.Add(hit.Href);
        }
        return hrefs;
    }

    /// <summary>An address as comparable text: without host, percent-escapes undone, no trailing slash.</summary>
    private static string PathOf(string href) =>
        Uri.UnescapeDataString(href.StartsWith('/') || !href.Contains("://") ? href : new Uri(href).AbsolutePath).TrimEnd('/');

    /// <summary>Runs <paramref name="action"/> for every item, a few at a time. The first failure stops the rest and is rethrown.</summary>
    private static Task ForEachAsync<T>(IEnumerable<T> items, Func<T, CancellationToken, Task> action, CancellationToken ct) =>
        Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = CardDavClient.Parallelism, CancellationToken = ct },
            async (item, token) => await action(item, token).ConfigureAwait(false));

    /// <summary>Writes the contacts as new entries and returns the address of each.</summary>
    private static async Task<IReadOnlyList<(Contact Contact, Uri Uri)>> PutAllAsync(CardDavClient client, Uri book, IEnumerable<Contact> contacts, CancellationToken ct)
    {
        var made = new ConcurrentBag<(Contact, Uri)>();
        await ForEachAsync(contacts, async (c, token) => made.Add((c, await PutContactAsync(client, book, c, token).ConfigureAwait(false))), ct).ConfigureAwait(false);
        return made.ToList();
    }

    /// <summary>
    /// Writes one contact. When the server refuses it, the message says which kind of contact it was (names of its vCard
    /// properties and its size, never the values), so a refusal can be traced without exposing anyone's data.
    /// </summary>
    private static async Task<Uri> PutContactAsync(CardDavClient client, Uri book, Contact c, CancellationToken ct)
    {
        var text = VCard.Write(c);
        var file = $"cm-{Guid.NewGuid():N}.vcf";
        try
        {
            await client.PutNewAsync(book, file, text, ct).ConfigureAwait(false);
            return new Uri(book, file);
        }
        catch (CardDavException ex) when (ex is not CardDavAuthException)
        {
            var names = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Select(VCard.PropertyName).Distinct();
            throw new CardDavException($"{ex.Message} The contact was {text.Length} characters with these properties: {string.Join(", ", names)}.", ex.Status);
        }
    }

    public async Task<WriteReport> CreateFolderAsync(ContactFolder accountFolder, string name, IReadOnlyList<Contact> contacts, CancellationToken ct = default)
    {
        var session = await SessionFor(accountFolder, ct).ConfigureAwait(false);
        var book = await session.Client.CreateAddressBookAsync(session.Home, name, ct).ConfigureAwait(false);
        try
        {
            await PutAllAsync(session.Client, book, contacts, ct).ConfigureAwait(false);
        }
        catch
        {
            // Remove the folder we just made only if it is still empty: never delete contacts that did get written.
            try
            {
                if ((await session.Client.CountAsync(book, CancellationToken.None).ConfigureAwait(false)) == 0)
                    await session.Client.DeleteAsync(book, CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* the original error matters more */ }
            throw;
        }
        var after = await session.Client.CountAsync(book, ct).ConfigureAwait(false);
        return new WriteReport(name, contacts.Count, 0, 0, after);
    }
}
