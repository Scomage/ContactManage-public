using Microsoft.Data.Sqlite;

namespace ContactManage.Adapters.IceWarp;

/// <param name="Name">Internal name; for Google groups this is an opaque id such as contactGroups%2F1b87...</param>
/// <param name="DisplayName">The name the client shows (FolderAttributes displayName), or null when absent.</param>
/// <param name="CategoryName">Set for Google label folders: the folder holds no contacts itself, its members are the contacts tagged with this label.</param>
public sealed record CachedFolder(long Id, string Name, string Path, long Flags, string? DisplayName = null, string? CategoryName = null)
{
    public string ShownName => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;

    /// <summary>Path with the last segment replaced by the display name, so ids never reach the screen.</summary>
    public string ShownPath =>
        ShownName != Name && Path.EndsWith("/" + Name, StringComparison.Ordinal)
            ? Path[..^Name.Length] + ShownName
            : Path;
}

/// <summary>Items in one folder: contacts (ContactItems.type 0) and distribution lists (type 1).</summary>
public readonly record struct FolderCounts(int Contacts, int Lists);

/// <summary>
/// Reads one folders.dat. A contact folder is a Folders row with a FolderAttributes row type = 4.
/// Counts come from the sibling contact_index.dat and reflect the client's last sync.
/// </summary>
public static class FolderCacheReader
{
    /// <summary>
    /// Members per label name, from ContactCategoryNames (one row per contact per label).
    /// This is how Google labels such as "Personal" are stored; their folders have no ContactItems rows.
    /// </summary>
    public static IReadOnlyDictionary<string, FolderCounts> ReadCategoryCounts(string contactIndexPath)
    {
        using var copy = new SqliteCopy(contactIndexPath);
        using var conn = copy.Open();
        return CountsPerLabel(conn);
    }

    /// <summary>Folder counts, and label counts when asked, from a single copy of the index (which can be large).</summary>
    public static (IReadOnlyDictionary<long, FolderCounts> Folders, IReadOnlyDictionary<string, FolderCounts>? Labels) ReadAllCounts(
        string contactIndexPath, bool withLabels)
    {
        using var copy = new SqliteCopy(contactIndexPath);
        using var conn = copy.Open();
        return (CountsPerFolder(conn), withLabels ? CountsPerLabel(conn) : null);
    }

    private static Dictionary<string, FolderCounts> CountsPerLabel(SqliteConnection conn)
    {
        var counts = new Dictionary<string, FolderCounts>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select cn.categoryName, i.type, count(distinct cn.id)
            from ContactCategoryNames cn
            join ContactItems i on i.id = cn.id
            where cn.id not in (select id from NonUploadedAndMarkedForDeleteContactItems)
            group by cn.categoryName, i.type
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            var n = reader.GetInt32(2);
            counts.TryGetValue(name, out var c);
            counts[name] = reader.GetInt64(1) == 1 ? c with { Lists = c.Lists + n } : c with { Contacts = c.Contacts + n };
        }
        return counts;
    }

    /// <summary>Item counts by Folders.id. Items marked for deletion are not counted.</summary>
    public static IReadOnlyDictionary<long, FolderCounts> ReadCounts(string contactIndexPath)
    {
        using var copy = new SqliteCopy(contactIndexPath);
        using var conn = copy.Open();
        return CountsPerFolder(conn);
    }

    private static Dictionary<long, FolderCounts> CountsPerFolder(SqliteConnection conn)
    {
        var counts = new Dictionary<long, FolderCounts>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select i.folder, i.type, count(*)
            from ContactItems i
            where i.id not in (select id from NonUploadedAndMarkedForDeleteContactItems)
            group by i.folder, i.type
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var folder = reader.GetInt64(0);
            var n = reader.GetInt32(2);
            counts.TryGetValue(folder, out var c);
            counts[folder] = reader.GetInt64(1) == 1 ? c with { Lists = c.Lists + n } : c with { Contacts = c.Contacts + n };
        }
        return counts;
    }

    /// <summary>Internal folders (RecipientHistory, UnsafeContentWhitelist) carry this bit; assumption from the dev PC, to be verified.</summary>
    public const long SystemFolderFlag = 0x200;

    public static IReadOnlyList<CachedFolder> ReadContactFolders(string foldersDatPath)
    {
        var folders = new List<CachedFolder>();
        using var copy = new SqliteCopy(foldersDatPath);
        using var conn = copy.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select f.id, f.name, f.path, f.folderFlags,
                   (select cast(d.value as text) from FolderAttributes d where d.parentId = f.id and d.name = 'displayName' limit 1),
                   (select cast(d.value as text) from FolderAttributes d where d.parentId = f.id and d.name = 'categoryName' limit 1)
            from Folders f
            join FolderAttributes a on a.parentId = f.id
            where a.name = 'type' and cast(a.value as text) = '4'
            order by f.id
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var flags = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
            if ((flags & SystemFolderFlag) != 0) continue;
            folders.Add(new CachedFolder(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2), flags,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return folders;
    }
}
