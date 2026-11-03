using System.Text.Json;
using System.Text.Json.Nodes;
using ContactManage.Core;

namespace ContactManage.Adapters.MicrosoftGraph;

/// <summary>
/// Microsoft accounts (Outlook.com, Hotmail, Microsoft 365) through Microsoft Graph. Each signed-in account shows its contact folders,
/// each with its own count, like a CardDAV account does; any folder can be read, replaced, or given a sibling ("new folder"). Folders
/// are shown under the account's email address, with "Microsoft (email)" as the server.
/// </summary>
public sealed class MicrosoftGraphStore(MicrosoftAccounts accounts, HttpMessageHandler? handler = null, TimeSpan? retryUnit = null) : IContactStore
{
    private const string IdPrefix = "ms-";

    /// <summary>The Microsoft account's email address, which is part of the folder id.</summary>
    public static string EmailOf(ContactFolder folder) => folder.AccountKey[IdPrefix.Length..];

    /// <summary>Graph's folder id, kept as the folder's path.</summary>
    private static string GraphIdOf(ContactFolder folder) => folder.Path.TrimStart('/');

    private static string ServerOf(string email) => $"Microsoft ({email})";

    private static ContactFolder FolderFor(string email, GraphFolder f, int? count) =>
        new($"{IdPrefix}{email}:/{f.Id}", f.Name, "/" + f.Id, email, ServerOf(email), FolderKind.Microsoft, IsWritable: true, count, DisplayPath: "/" + f.Name);

    private GraphApiClient Client(string email) => new(ct => accounts.AccessTokenAsync(email, ct), handler, retryUnit);

    public async Task<IReadOnlyList<Contact>> ReadContactsAsync(ContactFolder folder, CancellationToken ct = default)
    {
        using var client = Client(EmailOf(folder));
        var items = await client.ListContactsAsync(GraphIdOf(folder), ct).ConfigureAwait(false);
        return items.Select(i => MicrosoftContactMapper.FromGraph(i, folder.Id)).ToList();
    }

    public async Task<FolderRefresh> RefreshFoldersAsync(IReadOnlyList<ContactFolder> folders, Func<ContactFolder, bool> mayContact, CancellationToken ct = default)
    {
        var updated = new List<ContactFolder>();
        var problems = new List<string>();
        var asked = 0;
        foreach (var email in accounts.SavedEmails())
        {
            try
            {
                using var client = Client(email);
                var mine = new List<ContactFolder>();
                var listed = (await client.ListFoldersAsync(ct).ConfigureAwait(false)).ToList();
                if (await client.GetDefaultFolderAsync(ct).ConfigureAwait(false) is { } main && listed.All(f => f.Id != main.Id)) listed.Add(main);
                foreach (var f in listed)
                    mine.Add(FolderFor(email, f, await client.CountAsync(f.Id, ct).ConfigureAwait(false)));
                updated.AddRange(mine.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase));
                asked++;
            }
            catch (MicrosoftAuthException ex)
            {
                problems.Add($"{email}: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                problems.Add($"{email}: {ex.Message}");
            }
            catch (Exception ex) when (ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                problems.Add($"{email}: Microsoft did not answer in time.");   // the HttpClient timeout, not the user
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                problems.Add($"{email}: Microsoft's answer could not be read.");
            }
        }
        return new FolderRefresh(updated, problems, asked);
    }

    /// <summary>
    /// Makes the folder hold exactly <paramref name="contacts"/>. Contacts read from this folder and not edited are left alone. A merged
    /// contact updates one of its own originals in place (it keeps its Outlook identity and photo) and the other originals are deleted.
    /// Anything else is created. Creating and updating come first and deleting last, so a failure part-way loses no contact.
    /// </summary>
    public async Task<WriteReport> ReplaceContactsAsync(ContactFolder folder, IReadOnlyList<Contact> contacts, CancellationToken ct = default)
    {
        if (folder.Kind != FolderKind.Microsoft) throw new NotSupportedException("Only a Microsoft contact folder can be written to.");
        using var client = Client(EmailOf(folder));
        var folderId = GraphIdOf(folder);

        var existing = await client.ListContactIdsAsync(folderId, ct).ConfigureAwait(false);
        if (contacts.Count == 0 && existing.Count > 0)
            throw new InvalidOperationException("Refusing to empty a folder: the merged list has no contacts.");
        var live = existing.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

        var keep = contacts.Where(c => c.Sources.Count == 0 && c.SourceFolderId == folder.Id && c.RemoteId is not null && live.Contains(c.RemoteId))
            .Select(c => c.RemoteId!).ToHashSet(StringComparer.Ordinal);
        var untouched = keep.Count;
        var toUpdate = new List<(Contact Contact, ContactSource Survivor)>();
        var toCreate = new List<Contact>();
        foreach (var c in contacts.Where(c => !(c.Sources.Count == 0 && c.RemoteId is not null && keep.Contains(c.RemoteId))))
        {
            var survivor = c.Sources.FirstOrDefault(s => s.FolderId == folder.Id && s.RemoteId is not null && live.Contains(s.RemoteId) && !keep.Contains(s.RemoteId));
            if (survivor is null) { toCreate.Add(c); continue; }
            keep.Add(survivor.RemoteId!);
            toUpdate.Add((c, survivor));
        }
        var toRemove = existing.Select(e => e.Id).Where(id => !keep.Contains(id)).ToList();

        var skipped = 0;
        var drafts = toCreate.Select(c => MicrosoftContactMapper.ToGraph(c, forUpdate: false)).ToList();
        skipped += drafts.Sum(d => d.Skipped);
        var updates = toUpdate.Select(u => (Id: u.Survivor.RemoteId!, Draft: MicrosoftContactMapper.ToGraph(u.Contact, forUpdate: true))).ToList();
        skipped += updates.Sum(u => u.Draft.Skipped);

        // 1. new contacts, 2. merged contacts over their originals, 3. only now the originals that were merged away or marked obsolete
        var created = await client.CreateContactsAsync(folderId, drafts.Select(d => d.Body).ToList(), ct).ConfigureAwait(false);
        await client.UpdateContactsAsync(updates.Select(u => (u.Id, u.Draft.Body)).ToList(), ct).ConfigureAwait(false);
        await client.DeleteContactsAsync(toRemove, ct).ConfigureAwait(false);

        var after = await CountAfterWriteAsync(client, folderId, existing.Count - toRemove.Count + created.Count, ct).ConfigureAwait(false);
        return new WriteReport(folder.Name, created.Count, toRemove.Count, untouched, after, toUpdate.Count, Note(skipped));
    }

    public async Task<WriteReport> CreateFolderAsync(ContactFolder accountFolder, string name, IReadOnlyList<Contact> contacts, CancellationToken ct = default)
    {
        using var client = Client(EmailOf(accountFolder));
        var id = await client.CreateFolderAsync(name, ct).ConfigureAwait(false);
        var drafts = contacts.Select(c => MicrosoftContactMapper.ToGraph(c, forUpdate: false)).ToList();
        try
        {
            await client.CreateContactsAsync(id, drafts.Select(d => d.Body).ToList(), ct).ConfigureAwait(false);
        }
        catch
        {
            // Remove the folder we just made only if it is still empty: never delete contacts that did get written.
            try
            {
                if (await client.CountAsync(id, CancellationToken.None).ConfigureAwait(false) == 0)
                    await client.DeleteFolderAsync(id, CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* the original error matters more */ }
            throw;
        }
        var after = await CountAfterWriteAsync(client, id, contacts.Count, ct).ConfigureAwait(false);
        return new WriteReport(name, contacts.Count, 0, 0, after, Note: Note(drafts.Sum(d => d.Skipped)));
    }

    /// <summary>The folder's count after a write. A count can lag a moment behind, so a figure that is not the expected one is looked at once more.</summary>
    private async Task<int> CountAfterWriteAsync(GraphApiClient client, string folderId, int expected, CancellationToken ct)
    {
        var after = await client.CountAsync(folderId, ct).ConfigureAwait(false);
        if (after == expected) return after;
        await Task.Delay((retryUnit ?? TimeSpan.FromSeconds(1)) * 3, ct).ConfigureAwait(false);
        return await client.CountAsync(folderId, ct).ConfigureAwait(false);
    }

    private static string? Note(int skipped) =>
        skipped == 0 ? null : $"{skipped:N0} value(s) have no place in an Outlook contact (more than it can hold, or fields from other contact sources) and were left out.";
}
