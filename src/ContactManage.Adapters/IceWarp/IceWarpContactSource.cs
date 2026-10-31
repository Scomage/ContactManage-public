using ContactManage.Core;
using Microsoft.Win32;

namespace ContactManage.Adapters.IceWarp;

public sealed class IceWarpContactSource : IContactSource
{
    private const string MailClientKey = @"SOFTWARE\Clients\Mail\IceWarp Desktop Client";
    private const string LocalFoldersDir = "Local Folders";

    private readonly string _profileDir;

    public IceWarpContactSource(string? profileDir = null) =>
        _profileDir = profileDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IceWarp Desktop Client");

    public ClientInfo Detect()
    {
        var exe = ReadInstallPath();
        var hasProfile = File.Exists(Path.Combine(_profileDir, "accounts.dat"));
        if (exe is null && !hasProfile)
            return new ClientInfo("icewarp", "IceWarp Desktop Client", null, false, "Not installed");
        if (!hasProfile)
            return new ClientInfo("icewarp", "IceWarp Desktop Client", exe, false, "No profile found; start the client once and add an account");
        return new ClientInfo("icewarp", "IceWarp Desktop Client", exe, true);
    }

    public IReadOnlyList<ContactFolder> ListFolders()
    {
        var accounts = AccountReader.Read(Path.Combine(_profileDir, "accounts.dat"));
        var result = new List<ContactFolder>();

        foreach (var file in Directory.EnumerateFiles(_profileDir, "folders.dat", SearchOption.AllDirectories))
        {
            var dir = Path.GetDirectoryName(file)!;
            var id = Path.GetFileName(dir);
            string accountName, serverName;
            string? serverUrl = null;
            FolderKind kind;
            bool writable;

            if (id.Equals(LocalFoldersDir, StringComparison.OrdinalIgnoreCase))
            {
                (accountName, serverName, kind, writable) = (LocalFoldersDir, "Local", FolderKind.Local, true);
            }
            else if (accounts.TryGetValue(id, out var rec) && rec.TypeName.StartsWith("CalDav", StringComparison.Ordinal))
            {
                (accountName, serverName, kind, writable) = (rec.AccountName ?? id, ServerNames.FromHost(rec.Host), FolderKind.CardDav, true);
                serverUrl = rec.Host;
            }
            else if (rec is not null && rec.TypeName.StartsWith("Gdata", StringComparison.Ordinal))
            {
                (accountName, serverName, kind, writable) = (rec.AccountName ?? id, "Google", FolderKind.Google, false);
            }
            else
            {
                continue; // mail, archive and search folder caches
            }

            var indexFile = Path.Combine(dir, "contact_index.dat");
            var folders = FolderCacheReader.ReadContactFolders(file);
            var (counts, labelCounts) = File.Exists(indexFile)
                ? FolderCacheReader.ReadAllCounts(indexFile, withLabels: folders.Any(f => f.CategoryName is not null))
                : (null, null);

            foreach (var f in folders)
            {
                FolderCounts? c = counts is null ? null
                    : f.CategoryName is not null ? (labelCounts?.GetValueOrDefault(f.CategoryName) ?? default)
                    : counts.GetValueOrDefault(f.Id);
                result.Add(new ContactFolder($"{id}:{f.Path}", f.ShownName, f.Path, accountName, serverName, kind, writable,
                    c?.Contacts, c?.Lists ?? 0, f.ShownPath, serverUrl));
            }
        }

        return result
            .OrderBy(f => f.AccountName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ReadInstallPath()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var cmd = hklm.OpenSubKey(MailClientKey + @"\shell\open\command");
            if (cmd?.GetValue(null) is string s && !string.IsNullOrWhiteSpace(s))
                return s.Trim('"');
        }
        return null;
    }
}
