using ContactManage.Adapters.IceWarp;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ContactManage.Tests;

public class ServerNamesTests
{
    [Theory]
    [InlineData("https://webmail.example.com/webdav/", "webmail.example.com")]
    [InlineData("https://carddav.aol.com/.well-known/carddav", "carddav.aol.com")]
    [InlineData("mail.example.com", "mail.example.com")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void FromHost_returns_hostname(string? host, string expected) =>
        Assert.Equal(expected, ServerNames.FromHost(host));
}

public class FolderCacheReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cm-tests-" + Guid.NewGuid().ToString("N"));

    public FolderCacheReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private string CreateFoldersDat(params (string name, string path, long flags, int type)[] rows)
    {
        var file = Path.Combine(_dir, "folders.dat");
        using (var conn = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            conn.Open();
            Exec(conn, "create table Folders (id integer primary key, name text, path text, folderFlags integer, parentFolderId integer)");
            Exec(conn, "create table FolderAttributes (id integer primary key, parentId integer, name text, value)");
            var id = 1;
            foreach (var r in rows)
            {
                Exec(conn, $"insert into Folders (id, name, path, folderFlags) values ({id}, '{r.name}', '{r.path}', {r.flags})");
                Exec(conn, $"insert into FolderAttributes (parentId, name, value) values ({id}, 'type', {r.type})");
                id++;
            }
        }
        return file;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Returns_only_contact_folders_and_skips_system_folders()
    {
        var file = CreateFoldersDat(
            ("Contacts", "/0/Contacts", 0, 4),
            ("Calendar", "/0/Calendar", 0, 8),
            ("inbox", "/inbox", 13704, 24),
            ("RecipientHistory", "/RecipientHistory", 520, 4),
            ("Team Contacts", "/0/Shared/Team Contacts", 0, 4));

        var folders = FolderCacheReader.ReadContactFolders(file);

        Assert.Equal(["Contacts", "Team Contacts"], folders.Select(f => f.Name));
    }

    [Fact]
    public void ReadCounts_counts_contacts_and_lists_per_folder_and_skips_items_marked_for_delete()
    {
        var file = Path.Combine(_dir, "contact_index.dat");
        using (var conn = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            conn.Open();
            Exec(conn, "create table ContactItems (id integer primary key, folder integer, type integer)");
            Exec(conn, "create table NonUploadedAndMarkedForDeleteContactItems (id integer primary key)");
            Exec(conn, "insert into ContactItems (id, folder, type) values (1,10,0),(2,10,0),(3,10,0),(4,10,1),(5,10,1),(6,20,0)");
            Exec(conn, "insert into NonUploadedAndMarkedForDeleteContactItems (id) values (3)");
        }

        var counts = FolderCacheReader.ReadCounts(file);

        Assert.Equal(new FolderCounts(2, 2), counts[10]);
        Assert.Equal(new FolderCounts(1, 0), counts[20]);
        Assert.False(counts.ContainsKey(30));
    }

    [Fact]
    public void Display_name_replaces_opaque_ids_in_name_and_path()
    {
        var file = CreateFoldersDat(
            ("contactGroups%2F2dc9", "/Contacts/contactGroups%2F2dc9", 0, 4),
            ("Contacts", "/0/Contacts", 0, 4));
        using (var conn = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            conn.Open();
            Exec(conn, "insert into FolderAttributes (parentId, name, value) values (1, 'displayName', 'Scomage')");
        }

        var folders = FolderCacheReader.ReadContactFolders(file);

        Assert.Equal("Scomage", folders[0].ShownName);
        Assert.Equal("/Contacts/Scomage", folders[0].ShownPath);
        Assert.Equal("/Contacts/contactGroups%2F2dc9", folders[0].Path);
        Assert.Equal("Contacts", folders[1].ShownName);
        Assert.Equal("/0/Contacts", folders[1].ShownPath);
    }

    [Fact]
    public void ReadCategoryCounts_counts_each_contact_once_per_label_and_skips_items_marked_for_delete()
    {
        var file = Path.Combine(_dir, "contact_index.dat");
        using (var conn = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            conn.Open();
            Exec(conn, "create table ContactItems (id integer primary key, folder integer, type integer)");
            Exec(conn, "create table NonUploadedAndMarkedForDeleteContactItems (id integer primary key)");
            Exec(conn, "create table ContactCategoryNames (categoryId integer primary key, id integer, categoryName text)");
            Exec(conn, "insert into ContactItems (id, folder, type) values (1,2,0),(2,2,0),(3,2,0),(4,2,1)");
            Exec(conn, "insert into ContactCategoryNames (id, categoryName) values (1,'Personal'),(2,'Personal'),(3,'Personal'),(1,'Scomage'),(4,'Personal'),(2,'Personal')");
            Exec(conn, "insert into NonUploadedAndMarkedForDeleteContactItems (id) values (3)");
        }

        var counts = FolderCacheReader.ReadCategoryCounts(file);

        Assert.Equal(new FolderCounts(2, 1), counts["Personal"]);
        Assert.Equal(new FolderCounts(1, 0), counts["Scomage"]);
        Assert.False(counts.ContainsKey("Unfiled"));
    }

    [Fact]
    public void Does_not_modify_the_source_file()
    {
        var file = CreateFoldersDat(("Contacts", "/Contacts", 0, 4));
        var before = File.ReadAllBytes(file);

        FolderCacheReader.ReadContactFolders(file);

        Assert.Equal(before, File.ReadAllBytes(file));
    }
}
