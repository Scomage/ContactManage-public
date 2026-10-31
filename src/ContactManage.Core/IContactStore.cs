using System.Text;

namespace ContactManage.Core;

/// <summary>Credentials for one server account. Held in memory for the session only.</summary>
public sealed record ServerCredentials(string UserName, string Password);

/// <param name="Added">Contacts written to the folder.</param>
/// <param name="Removed">Contacts deleted from the folder.</param>
/// <param name="Unchanged">Contacts already in the folder and left alone.</param>
/// <param name="CountAfter">Contacts the server reports in the folder after the write; null when it could not be checked.</param>
/// <param name="Updated">Contacts changed in place (Gmail: the surviving contact of a merged set). They do not change the count.</param>
/// <param name="Note">Anything the user should know about what could not be stored.</param>
public sealed record WriteReport(string FolderName, int Added, int Removed, int Unchanged, int? CountAfter, int Updated = 0, string? Note = null)
{
    public int Expected => Added + Unchanged + Updated;
    public bool Verified => CountAfter is null || CountAfter == Expected;
}

/// <summary>Reads and writes the contents of contact folders.</summary>
public interface IContactStore
{
    Task<IReadOnlyList<Contact>> ReadContactsAsync(ContactFolder folder, CancellationToken ct = default);

    /// <summary>
    /// Makes the folder hold exactly <paramref name="contacts"/>. New contacts are written first and old ones deleted last,
    /// so a failure part-way never loses contacts. Contacts read from this folder and not edited are left untouched.
    /// </summary>
    Task<WriteReport> ReplaceContactsAsync(ContactFolder folder, IReadOnlyList<Contact> contacts, CancellationToken ct = default);

    /// <summary>
    /// Asks each account's server which folders it has and how many contacts are in them. Only accounts already signed in to,
    /// or for which <paramref name="mayContact"/> says a saved login exists, are asked, so a refresh never opens a sign-in box
    /// for an account the user has not used. A server that cannot be reached is reported in the result, not thrown.
    /// </summary>
    Task<FolderRefresh> RefreshFoldersAsync(IReadOnlyList<ContactFolder> folders, Func<ContactFolder, bool> mayContact, CancellationToken ct = default);

    /// <summary>Creates a new folder in the same account as <paramref name="accountFolder"/> and fills it.</summary>
    Task<WriteReport> CreateFolderAsync(ContactFolder accountFolder, string name, IReadOnlyList<Contact> contacts, CancellationToken ct = default);

    /// <summary>Whether a new folder can be offered for this account. A server that states it has no such operation (AOL) says no.</summary>
    bool CanCreateFolder(ContactFolder account) => account.CanCreateFolder;
}

/// <summary>Plain vCard files: backups and the "save to a local file" destination.</summary>
public static class VCardFiles
{
    public static void Save(string path, IEnumerable<Contact> contacts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, VCard.WriteAll(contacts), new UTF8Encoding(false));
    }

    public static IReadOnlyList<Contact> Load(string path) => VCard.ParseAll(File.ReadAllText(path));

    /// <summary>The file name a backup of this folder gets (without extension): "Account - Folder" with characters Windows forbids replaced.</summary>
    public static string BackupLabel(string accountName, string folderName) =>
        string.Concat($"{accountName} - {folderName}".Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    /// <summary>Saves a timestamped backup of a folder's contacts and returns the file path.</summary>
    public static string Backup(string backupRoot, string label, IEnumerable<Contact> contacts, DateTime? now = null)
    {
        var safe = string.Concat(label.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var path = Path.Combine(backupRoot, $"{now ?? DateTime.Now:yyyyMMdd-HHmmss}", safe + ".vcf");
        Save(path, contacts);
        return path;
    }
}

/// <param name="Label">"Account - Folder", from the file name.</param>
public sealed record BackupFile(string Path, string Label, int ContactCount);

/// <param name="Taken">When the backup was made, from its folder name.</param>
public sealed record BackupSet(DateTime Taken, string Directory, IReadOnlyList<BackupFile> Files);

/// <summary>The backups the app has saved (<see cref="VCardFiles.Backup"/>), and which folder each one belongs to.</summary>
public static class BackupCatalog
{
    /// <summary>Every backup under <paramref name="root"/>, newest first. Folders and files that are not ours are skipped, never an error.</summary>
    public static IReadOnlyList<BackupSet> List(string root)
    {
        var sets = new List<BackupSet>();
        if (!Directory.Exists(root)) return sets;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (!DateTime.TryParseExact(System.IO.Path.GetFileName(dir), "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var taken)) continue;
            var files = new List<BackupFile>();
            foreach (var file in Directory.EnumerateFiles(dir, "*.vcf").Order(StringComparer.OrdinalIgnoreCase))
            {
                try { files.Add(new BackupFile(file, System.IO.Path.GetFileNameWithoutExtension(file), VCardFiles.Load(file).Count)); }
                catch (IOException) { /* unreadable: leave it out of the list */ }
            }
            if (files.Count > 0) sets.Add(new BackupSet(taken, dir, files));
        }
        return sets.OrderByDescending(s => s.Taken).ToList();
    }

    /// <summary>The folder a backup was taken from, or null when none of the current folders has that account and name.</summary>
    public static ContactFolder? FindTarget(BackupFile file, IEnumerable<ContactFolder> folders) =>
        folders.FirstOrDefault(f => string.Equals(VCardFiles.BackupLabel(f.AccountName, f.Name), file.Label, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Sends each folder to the store that can handle its kind (CardDAV servers, Gmail), and combines their answers.</summary>
public sealed class CompositeContactStore(IContactStore cardDav, IContactStore gmail, IContactStore? local = null) : IContactStore
{
    private IContactStore For(ContactFolder folder) =>
        folder.Kind is FolderKind.GmailApi or FolderKind.GmailLabel ? gmail
        : folder.Kind == FolderKind.Local && local is not null ? local
        : cardDav;

    public Task<IReadOnlyList<Contact>> ReadContactsAsync(ContactFolder folder, CancellationToken ct = default) => For(folder).ReadContactsAsync(folder, ct);

    public Task<WriteReport> ReplaceContactsAsync(ContactFolder folder, IReadOnlyList<Contact> contacts, CancellationToken ct = default) =>
        For(folder).ReplaceContactsAsync(folder, contacts, ct);

    public Task<WriteReport> CreateFolderAsync(ContactFolder accountFolder, string name, IReadOnlyList<Contact> contacts, CancellationToken ct = default) =>
        For(accountFolder).CreateFolderAsync(accountFolder, name, contacts, ct);

    public bool CanCreateFolder(ContactFolder account) => For(account).CanCreateFolder(account);

    public async Task<FolderRefresh> RefreshFoldersAsync(IReadOnlyList<ContactFolder> folders, Func<ContactFolder, bool> mayContact, CancellationToken ct = default)
    {
        // The Gmail store lists its own accounts (from the saved sign-ins), whatever the list holds; the CardDAV store gets its folders.
        static bool IsGmail(ContactFolder f) => f.Kind is FolderKind.GmailApi or FolderKind.GmailLabel;
        var dav = await cardDav.RefreshFoldersAsync(folders.Where(f => !IsGmail(f)).ToList(), mayContact, ct).ConfigureAwait(false);
        var mail = await gmail.RefreshFoldersAsync(folders.Where(IsGmail).ToList(), mayContact, ct).ConfigureAwait(false);
        return new FolderRefresh(dav.Folders.Concat(mail.Folders).ToList(), dav.Problems.Concat(mail.Problems).ToList(), dav.AccountsAsked + mail.AccountsAsked);
    }
}
