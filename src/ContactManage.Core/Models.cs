namespace ContactManage.Core;

public sealed record ClientInfo(string Id, string Name, string? InstallPath, bool IsSupported, string? Note = null);

public enum FolderKind
{
    CardDav,
    /// <summary>A Gmail account read through Google's People API (OAuth), not through IceWarp's cache.</summary>
    GmailApi,
    /// <summary>A Gmail label shown under its account's My Contacts for information; it is not selectable (its contacts are part of My Contacts).</summary>
    GmailLabel,
    /// <summary>A placeholder for an IceWarp Google account the user has not signed in to yet; it only points to Add Google account.</summary>
    GmailSignIn,
    Google,
    Local,
    /// <summary>A contact folder of a Microsoft account (Outlook.com, Hotmail, Microsoft 365) read through Microsoft Graph.</summary>
    Microsoft,
}

/// <summary>A contact folder as shown in the selection list.</summary>
public sealed record ContactFolder(
    string Id,
    string Name,
    string Path,
    string AccountName,
    string ServerName,
    FolderKind Kind,
    bool IsWritable,
    int? ContactCount = null,
    int ListCount = 0,
    string? DisplayPath = null,
    string? ServerUrl = null)
{
    /// <summary>The mail-client account the folder belongs to (the part of Id before the first colon).</summary>
    public string AccountKey => AccountKeyOf(Id);

    /// <summary>The account part of a folder id ("account:/0/Contacts"); the whole text when there is no colon.</summary>
    public static string AccountKeyOf(string folderId) => folderId.Contains(':') ? folderId[..folderId.IndexOf(':')] : folderId;

    /// <summary>CardDAV folders, Gmail through its API and IceWarp's Local Folders can be read; IceWarp's cached copy of a Google account cannot.</summary>
    public bool IsReadable => Kind is FolderKind.CardDav or FolderKind.GmailApi or FolderKind.GmailLabel or FolderKind.Local or FolderKind.Microsoft;

    public bool CanWrite => Kind is FolderKind.CardDav or FolderKind.GmailApi or FolderKind.GmailLabel or FolderKind.Local or FolderKind.Microsoft && IsWritable;

    /// <summary>A Gmail account has one list plus labels, so a "new folder" is only offered for CardDAV and Microsoft accounts.</summary>
    public bool CanCreateFolder => Kind is FolderKind.CardDav or FolderKind.Microsoft && IsWritable;

    /// <summary>Labels are indented under their account's main list; ticking one works on just that label's contacts. Only the sign-in row has no box.</summary>
    public bool HasCheckbox => Kind != FolderKind.GmailSignIn;

    /// <summary>The name as the list shows it: a label is indented under its account's main list.</summary>
    public string ListName => Kind == FolderKind.GmailLabel ? "      ↳ " + Name : Name;

    /// <summary>Path for the screen. <see cref="Path"/> stays the technical path used to find the folder.</summary>
    public string ShownPath => DisplayPath ?? Path;

    /// <summary>Contacts, plus distribution lists when there are any. Blank when the count is unknown.</summary>
    public string CountText => ContactCount is null
        ? ""
        : ListCount == 0
            ? ContactCount.Value.ToString("N0")
            : $"{ContactCount.Value:N0} + {ListCount:N0} {(ListCount == 1 ? "list" : "lists")}";
}

/// <summary>
/// Folders as the servers report them right now. <see cref="Folders"/> holds, for every account that was asked, its known
/// folders with fresh counts plus any folder the client's cache does not know yet.
/// </summary>
public sealed record FolderRefresh(IReadOnlyList<ContactFolder> Folders, IReadOnlyList<string> Problems, int AccountsAsked);

public static class FolderList
{
    /// <summary>
    /// Puts the server's view into the list: a folder the server returned replaces the one with the same Id (fresh count),
    /// and folders that are new are placed after the last folder of their account. Folders of accounts not asked stay as they were.
    /// </summary>
    public static IReadOnlyList<ContactFolder> Splice(IReadOnlyList<ContactFolder> original, IReadOnlyList<ContactFolder> fromServer)
    {
        var fresh = fromServer.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var result = original.Select(f => fresh.GetValueOrDefault(f.Id, f)).ToList();
        var known = original.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var added in fromServer.Where(f => !known.Contains(f.Id)))
        {
            var at = result.FindLastIndex(f => f.AccountKey == added.AccountKey);
            if (at >= 0) { result.Insert(at + 1, added); continue; }

            // A new account goes where its name falls among the others (the client's list is sorted by account name).
            var before = result.FindIndex(f => string.Compare(f.AccountName, added.AccountName, StringComparison.OrdinalIgnoreCase) > 0);
            result.Insert(before < 0 ? result.Count : before, added);
        }
        return result;
    }

    /// <summary>
    /// Why this selection can't be processed, or null when it can. A Gmail contact can sit in My Contacts and in several labels at
    /// once, so ticking two rows of one Google account would read (and later replace) the same contacts twice.
    /// </summary>
    public static string? SelectionProblem(IReadOnlyList<ContactFolder> selected)
    {
        var twice = selected.Where(f => f.Kind is FolderKind.GmailApi or FolderKind.GmailLabel)
            .GroupBy(f => f.AccountKey).FirstOrDefault(g => g.Count() > 1);
        return twice is null ? null : $"Tick only one list or label of the Google account {twice.First().AccountName} at a time: its contacts can be in several of them.";
    }
}

/// <summary>Which rows of the folder list are shown: a Gmail label row hides when its account's main list is collapsed.</summary>
public static class FolderTree
{
    public static bool IsVisible(ContactFolder folder, IReadOnlySet<string> collapsedAccounts) =>
        folder.Kind != FolderKind.GmailLabel || !collapsedAccounts.Contains(folder.AccountKey);
}
