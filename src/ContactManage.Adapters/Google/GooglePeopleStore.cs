using System.Text.Json.Nodes;
using ContactManage.Core;

namespace ContactManage.Adapters.Google;

/// <summary>
/// Gmail through the People API. Each signed-in account shows as its main list ("My Contacts") with its total, and one
/// indented, unselectable row per label with that label's count. Accounts are shown by the name the user knows them by
/// (see <see cref="GoogleAccountNames"/>), with "Google (email)" as the server. The main list can be replaced; label rows are information
/// only, and a "new folder" is not offered because Gmail has no separate lists.
/// </summary>
public sealed class GooglePeopleStore(GoogleAccounts accounts, GoogleAccountNames names, HttpMessageHandler? handler = null, TimeSpan? retryUnit = null) : IContactStore
{
    private const string MyContacts = "contactGroups/myContacts";
    private const string IdPrefix = "gmail-";

    /// <summary>The Google account's email address, which is part of the folder id (the shown account name is a friendly one).</summary>
    public static string EmailOf(ContactFolder folder) => folder.AccountKey[IdPrefix.Length..];

    /// <summary>The real address is always shown as the server, so two Google accounts can never be mixed up whatever they are called.</summary>
    private static string ServerOf(string email) => $"Google ({email})";

    public ContactFolder MainFolder(string email, int? count = null) =>
        new($"{IdPrefix}{email}:/myContacts", "My Contacts", "/myContacts", names.DisplayFor(email), ServerOf(email), FolderKind.GmailApi,
            IsWritable: true, count, DisplayPath: "/My Contacts");

    private ContactFolder LabelFolder(string email, ContactGroupInfo group) =>
        new($"{IdPrefix}{email}:/label/{group.ResourceName}", group.Name, "/myContacts/" + group.Name, names.DisplayFor(email), ServerOf(email), FolderKind.GmailLabel,
            IsWritable: true, group.MemberCount, DisplayPath: "/My Contacts/" + group.Name);

    /// <summary>The main list of every saved sign-in, without counts and without any network access.</summary>
    public IReadOnlyList<ContactFolder> SavedFolders() => accounts.SavedEmails().Select(e => MainFolder(e)).ToList();

    private PeopleApiClient Client(string email) => new(ct => accounts.AccessTokenAsync(email, ct), handler, retryUnit);

    public async Task<IReadOnlyList<Contact>> ReadContactsAsync(ContactFolder folder, CancellationToken ct = default)
    {
        using var client = Client(EmailOf(folder));
        var labels = (await client.ListContactGroupsAsync(ct).ConfigureAwait(false))
            .Where(g => !g.IsSystem).ToDictionary(g => g.ResourceName, g => g.Name);
        var people = await client.ListConnectionsAsync(GoogleContactMapper.PersonFields, ct).ConfigureAwait(false);
        var all = people.Select(p => GoogleContactMapper.FromPerson(p, labels, folder.Id));
        // A label row works on its own members only; the rest of My Contacts is not read, so it cannot be changed either.
        return (folder.Kind == FolderKind.GmailLabel ? all.Where(c => c.Groups.Contains(folder.Name, StringComparer.OrdinalIgnoreCase)) : all).ToList();
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
                var groups = await client.ListContactGroupsAsync(ct).ConfigureAwait(false);
                updated.Add(MainFolder(email, groups.FirstOrDefault(g => g.ResourceName == MyContacts)?.MemberCount));
                updated.AddRange(groups.Where(g => !g.IsSystem).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Select(g => LabelFolder(email, g)));
                asked++;
            }
            catch (GoogleAuthException ex)
            {
                problems.Add($"{names.DisplayFor(email)}: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                problems.Add($"{names.DisplayFor(email)}: {ex.Message}");
            }
        }
        return new FolderRefresh(updated, problems, asked);
    }

    /// <summary>
    /// Makes the account's contact list hold exactly <paramref name="contacts"/>. Contacts read from this list and not edited are left alone.
    /// A merged contact updates one of its own originals in place (the survivor keeps its Google identity and gets the combined fields
    /// and the labels of every member) and the other originals are deleted. Anything else is created. Changes go out one request at a
    /// time, creating and updating first and deleting last, so a failure part-way loses no contact.
    /// </summary>
    public async Task<WriteReport> ReplaceContactsAsync(ContactFolder folder, IReadOnlyList<Contact> contacts, CancellationToken ct = default)
    {
        if (folder.Kind is not (FolderKind.GmailApi or FolderKind.GmailLabel)) throw new NotSupportedException("Only a Gmail contact list or label can be written to.");
        using var client = Client(EmailOf(folder));

        var everything = await client.ListContactIdsAsync(ct).ConfigureAwait(false);
        // For a label, only its current members are in play: nothing else in My Contacts may be updated or deleted.
        var existing = everything;
        if (folder.Kind == FolderKind.GmailLabel)
        {
            var members = (await ReadContactsAsync(folder, ct).ConfigureAwait(false)).Select(c => c.RemoteId).ToHashSet(StringComparer.Ordinal);
            existing = everything.Where(e => members.Contains(e.ResourceName)).ToList();
        }
        if (contacts.Count == 0 && existing.Count > 0)
            throw new InvalidOperationException("Refusing to empty a folder: the merged list has no contacts.");
        var live = existing.ToDictionary(e => e.ResourceName, e => e.Etag, StringComparer.Ordinal);

        var keep = contacts.Where(c => c.Sources.Count == 0 && c.SourceFolderId == folder.Id && c.RemoteId is not null && live.ContainsKey(c.RemoteId))
            .Select(c => c.RemoteId!).ToHashSet(StringComparer.Ordinal);
        var untouched = keep.Count;
        var toUpdate = new List<(Contact Contact, ContactSource Survivor)>();
        var toCreate = new List<Contact>();
        foreach (var c in contacts.Where(c => !(c.Sources.Count == 0 && c.RemoteId is not null && keep.Contains(c.RemoteId))))
        {
            var survivor = c.Sources.FirstOrDefault(s => s.FolderId == folder.Id && s.RemoteId is not null && s.Etag is not null
                && live.ContainsKey(s.RemoteId) && !keep.Contains(s.RemoteId));
            if (survivor is null) { toCreate.Add(c); continue; }
            keep.Add(survivor.RemoteId!);
            toUpdate.Add((c, survivor));
        }
        var toRemove = existing.Select(e => e.ResourceName).Where(r => !keep.Contains(r)).ToList();

        // Labels the contacts need, created when the account lacks them.
        var groups = await client.ListContactGroupsAsync(ct).ConfigureAwait(false);
        var labelIds = groups.Where(g => !g.IsSystem).GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().ResourceName, StringComparer.OrdinalIgnoreCase);
        foreach (var name in toCreate.Concat(toUpdate.Select(u => u.Contact)).SelectMany(c => c.Groups).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!labelIds.ContainsKey(name)) labelIds[name] = await client.CreateContactGroupAsync(name, ct).ConfigureAwait(false);

        var skipped = 0;
        var inLabel = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Remember(Contact c, string resource)
        {
            foreach (var label in c.Groups)
            {
                if (!inLabel.TryGetValue(label, out var list)) inLabel[label] = list = [];
                list.Add(resource);
            }
        }

        // 1. new contacts, already in My Contacts and in their labels
        var drafts = toCreate.Select(GoogleContactMapper.ToPerson).ToList();
        for (var i = 0; i < drafts.Count; i++)
        {
            var memberships = new JsonArray(new JsonObject { ["contactGroupMembership"] = new JsonObject { ["contactGroupResourceName"] = MyContacts } });
            foreach (var label in toCreate[i].Groups.Distinct(StringComparer.OrdinalIgnoreCase))
                memberships.Add(new JsonObject { ["contactGroupMembership"] = new JsonObject { ["contactGroupResourceName"] = labelIds[label] } });
            drafts[i].Person["memberships"] = memberships;
            skipped += drafts[i].Skipped;
        }
        var created = await client.CreateContactsAsync(drafts.Select(d => (JsonNode)d.Person).ToList(), ct).ConfigureAwait(false);

        // 2. survivors updated in place; the version tag from the read makes Google refuse the change if the contact moved on since
        foreach (var (contact, survivor) in toUpdate)
        {
            var draft = GoogleContactMapper.ToPerson(contact);
            skipped += draft.Skipped;
            await client.UpdateContactAsync(survivor.RemoteId!, survivor.Etag!, draft.Person, draft.UpdateFields, ct).ConfigureAwait(false);
            Remember(contact, survivor.RemoteId!);
        }

        // 3. labels for the survivors (created contacts got theirs above). Adding keeps stars and other labels as they were.
        foreach (var (label, people) in inLabel)
            await client.AddToGroupAsync(labelIds[label], people, ct).ConfigureAwait(false);

        // 4. only now the originals that were merged away or marked obsolete
        await client.DeleteContactsAsync(toRemove, ct).ConfigureAwait(false);

        var after = (await client.ListContactIdsAsync(ct).ConfigureAwait(false)).Count;
        var expected = everything.Count - toRemove.Count + created.Count;
        if (after != expected)
        {
            // Google's list can lag a moment behind a write; look once more before calling it a mismatch.
            await Task.Delay((retryUnit ?? TimeSpan.FromSeconds(1)) * 3, ct).ConfigureAwait(false);
            after = (await client.ListContactIdsAsync(ct).ConfigureAwait(false)).Count;
        }

        // After is the folder's own size: the whole list for My Contacts, the label's members for a label.
        var shown = folder.Kind == FolderKind.GmailLabel ? untouched + toUpdate.Count + created.Count : after;
        return new WriteReport(folder.Name, created.Count, toRemove.Count, untouched, shown, toUpdate.Count,
            skipped == 0 ? null : $"{skipped:N0} field(s) from other contact sources have no place in Gmail and were left out.");
    }

    public Task<WriteReport> CreateFolderAsync(ContactFolder accountFolder, string name, IReadOnlyList<Contact> contacts, CancellationToken ct = default) =>
        throw new NotSupportedException("A Gmail account has one contact list plus labels, so it has no separate folders to create. Replace My Contacts instead.");
}
