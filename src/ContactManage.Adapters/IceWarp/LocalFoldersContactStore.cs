using ContactManage.Core;

namespace ContactManage.Adapters.IceWarp;

/// <summary>
/// IceWarp's Local Folders: contacts that live in the client's profile, not on a server. Reading uses a copy of
/// <c>contact_index.dat</c>. Writing (<see cref="ReplaceContactsAsync"/>) changes the client's own databases in place, in one
/// transaction, with IceWarp Desktop Client allowed to stay open (see <see cref="LocalContactWriter"/>); before that, both
/// databases are backed up. There is no server copy of these contacts, so that backup is the only way back.
/// </summary>
public sealed class LocalFoldersContactStore : IContactStore
{
    private const string LocalFoldersDir = "Local Folders";

    private readonly string _dir;
    private readonly string? _backupRoot;

    /// <param name="backupRoot">Where the copy of IceWarp's database files goes before a write. Without one, writing is refused.</param>
    public LocalFoldersContactStore(string? profileDir = null, string? backupRoot = null)
    {
        _dir = Path.Combine(profileDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IceWarp Desktop Client"), LocalFoldersDir);
        _backupRoot = backupRoot;
    }

    private string IndexPath => Path.Combine(_dir, "contact_index.dat");
    private string DataPath => Path.Combine(_dir, "contact_data.dat");

    private CachedFolder FindFolder(ContactFolder folder) =>
        FolderCacheReader.ReadContactFolders(Path.Combine(_dir, "folders.dat"))
            .FirstOrDefault(f => string.Equals(f.Path, folder.Path, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"IceWarp's Local Folders no longer have a folder {folder.Path}. Refresh the list.");

    public Task<IReadOnlyList<Contact>> ReadContactsAsync(ContactFolder folder, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<Contact>>(() => LocalContactReader.Read(IndexPath, FindFolder(folder).Id, folder.Id), ct);

    public Task<WriteReport> ReplaceContactsAsync(ContactFolder folder, IReadOnlyList<Contact> contacts, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (_backupRoot is null) throw new InvalidOperationException("No place for a backup of IceWarp's database files was given, so nothing was written.");
            var row = FindFolder(folder);
            LocalContactWriter.Preflight(IndexPath, DataPath);

            var backupDir = Path.Combine(_backupRoot, $"LocalFolders-files-{DateTime.Now:yyyyMMdd-HHmmss}");
            LocalContactWriter.Backup(IndexPath, DataPath, backupDir);

            try
            {
                return LocalContactWriter.Replace(IndexPath, DataPath, row.Id, folder.Id, folder.Name, contacts);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                // The change is one transaction, so a failure here left IceWarp's files as they were.
                throw new InvalidOperationException($"{ex.Message}\n\nIceWarp's Local Folders were not changed. A copy of its database files is in {backupDir}.", ex);
            }
        }, ct);

    public Task<WriteReport> CreateFolderAsync(ContactFolder accountFolder, string name, IReadOnlyList<Contact> contacts, CancellationToken ct = default) =>
        throw new NotSupportedException("A new folder cannot be created inside IceWarp's Local Folders. Replace the Contacts folder, or write to an account or a vCard file.");

    // The list already comes from the client's cache; there is no server to ask.
    public Task<FolderRefresh> RefreshFoldersAsync(IReadOnlyList<ContactFolder> folders, Func<ContactFolder, bool> mayContact, CancellationToken ct = default) =>
        Task.FromResult(new FolderRefresh([], [], 0));

    public bool CanCreateFolder(ContactFolder account) => false;
}
