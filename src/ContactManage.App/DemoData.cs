using ContactManage.Core;

namespace ContactManage.App;

/// <summary>
/// Made-up folders and contacts so the whole flow can be tried without touching a real account.
/// The demo store keeps its changes in memory only.
/// </summary>
public static class DemoData
{
    private const string Work = "demoA:/0/Contacts", Team = "demoA:/0/Shared/Team contacts", Personal = "demoB:/Contacts";

    public static IReadOnlyList<ContactFolder> Folders() =>
    [
        Folder(Work, "Contacts", "/0/Contacts", "Demo Work", "work.example.com"),
        Folder(Team, "Team contacts", "/0/Shared/Team contacts", "Demo Work", "work.example.com"),
        Folder(Personal, "Contacts", "/Contacts", "Demo Personal", "mail.example.org"),
    ];

    private static ContactFolder Folder(string id, string name, string path, string account, string server) =>
        new(id, name, path, account, server, FolderKind.CardDav, true, Contacts().Count(c => c.SourceFolderId == id), 0, null,
            $"https://{server}/dav/");

    /// <summary>A fresh copy each call, so the demo can be restarted from the same state.</summary>
    public static List<Contact> Contacts()
    {
        _counter = 0;
        return
        [
            C(Work, "Alice Brown", "Acme", "Buyer", emails: ["alice@acme.example"], phones: ["+1 415 555 0101"]),
            C(Personal, "Alice Brown", "Acme Corp", "Purchasing Lead", emails: ["ALICE@acme.example", "abrown@home.example"]),
            C(Team, "Bob Carter", emails: ["bob@carter.example"], notes: "Met at the 2024 expo"),
            C(Work, "Bob Carter", emails: ["bob@carter.example"], notes: "Prefers phone calls"),
            C(Work, "Dana Lee", phones: ["415-555-0199"]),
            C(Personal, "Dana Lee", phones: ["(415) 555-0199"]),
            C(Work, "Pat Green", phones: ["212 555 0100"]),
            C(Personal, "Sam Green", phones: ["212 555 0100"]),
            C(Work, "Chris Young", emails: ["chris@one.example"]),
            C(Team, "Chris Young", emails: ["cyoung@two.example"]),
            C(Work, "Erin Walsh", emails: ["erin@walsh.example"]),
            C(Work, "Erin Walsh", company: "Walsh Design", emails: ["erin@walsh.example"], phones: ["303 555 0142"]),
            C(Team, "Frank Osei", emails: ["frank@osei.example"], phones: ["+44 20 7946 0958"]),
            C(Personal, "Grace Kim", emails: ["grace@kim.example"]),
        ];
    }

    // Stable ids, so a "not duplicates" decision made in one demo run is recognised in the next.
    private static int _counter;

    private static Contact C(string folder, string name, string? company = null, string? title = null, string? notes = null,
        string[]? emails = null, string[]? phones = null)
    {
        var c = new Contact { SourceFolderId = folder, DisplayName = name, Company = company, JobTitle = title, Notes = notes };
        c.Uid = "demo-" + ++_counter;
        c.RemoteId = c.Uid;
        c.Emails.AddRange(emails ?? []);
        c.Phones.AddRange(phones ?? []);
        return c;
    }
}

public sealed class DemoContactSource : IContactSource
{
    public ClientInfo Detect() => new("demo", "Demo sample data", null, true);
    public IReadOnlyList<ContactFolder> ListFolders() => DemoData.Folders();
}

/// <summary>An in-memory stand-in for a server, behaving like the real store (untouched contacts are left alone).</summary>
public sealed class DemoContactStore : IContactStore
{
    private readonly Dictionary<string, List<Contact>> _folders;
    private readonly Dictionary<string, ContactFolder> _created = new();

    public DemoContactStore()
    {
        _folders = DemoData.Folders().ToDictionary(f => f.Id, _ => new List<Contact>());
        foreach (var c in DemoData.Contacts()) _folders[c.SourceFolderId!].Add(c);
    }

    public Task<IReadOnlyList<Contact>> ReadContactsAsync(ContactFolder folder, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Contact>>(_folders[folder.Id].ToList());

    public Task<WriteReport> ReplaceContactsAsync(ContactFolder folder, IReadOnlyList<Contact> contacts, CancellationToken ct = default)
    {
        var existing = _folders[folder.Id];
        var existingIds = existing.Select(c => c.RemoteId).ToHashSet();
        bool Untouched(Contact c) => c.SourceFolderId == folder.Id && existingIds.Contains(c.RemoteId);

        var kept = contacts.Where(Untouched).ToList();
        var added = contacts.Where(c => !Untouched(c)).ToList();
        var removed = existing.Count - kept.Count;

        _folders[folder.Id] = kept.Concat(added.Select(c => Stored(c, folder))).ToList();
        return Task.FromResult(new WriteReport(folder.Name, added.Count, removed, kept.Count, _folders[folder.Id].Count));
    }

    public Task<WriteReport> CreateFolderAsync(ContactFolder accountFolder, string name, IReadOnlyList<Contact> contacts, CancellationToken ct = default)
    {
        var id = $"{accountFolder.AccountKey}:/{name}";
        var folder = accountFolder with { Id = id, Name = name, Path = "/" + name };
        _folders[id] = contacts.Select(c => Stored(c, folder)).ToList();
        _created[id] = folder;
        return Task.FromResult(new WriteReport(name, contacts.Count, 0, 0, contacts.Count));
    }

    /// <summary>Like a server: the current counts of every folder, including ones created during this demo.</summary>
    public Task<FolderRefresh> RefreshFoldersAsync(IReadOnlyList<ContactFolder> folders, Func<ContactFolder, bool> mayContact, CancellationToken ct = default)
    {
        var current = folders.Where(f => _folders.ContainsKey(f.Id)).Select(f => f with { ContactCount = _folders[f.Id].Count })
            .Concat(_created.Values.Where(f => folders.All(k => k.Id != f.Id)).Select(f => f with { ContactCount = _folders[f.Id].Count }));
        return Task.FromResult(new FolderRefresh(current.ToList(), [], 1));
    }

    private static Contact Stored(Contact c, ContactFolder folder)
    {
        c.SourceFolderId = folder.Id;
        c.RemoteId = "demo-" + c.Id;
        return c;
    }
}
