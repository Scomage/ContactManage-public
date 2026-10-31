using Microsoft.Data.Sqlite;

namespace ContactManage.Adapters.IceWarp;

/// <summary>
/// The client keeps its databases open in WAL mode. We never open them in place:
/// the .dat file and its -wal/-shm companions are copied to a temp folder and read there.
/// </summary>
internal sealed class SqliteCopy : IDisposable
{
    private readonly string _dir;
    public string Path { get; }

    public SqliteCopy(string sourceFile)
    {
        _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ContactManage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Path = System.IO.Path.Combine(_dir, System.IO.Path.GetFileName(sourceFile));
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var src = sourceFile + suffix;
            if (!File.Exists(src)) continue;
            using var from = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var to = File.Create(Path + suffix);
            from.CopyTo(to);
        }
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate, // on the private copy, so WAL recovery can run
            Pooling = false,
        }.ToString());
        conn.Open();
        return conn;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
