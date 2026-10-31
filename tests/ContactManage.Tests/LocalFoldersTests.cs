using ContactManage.Adapters.IceWarp;
using ContactManage.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ContactManage.Tests;

public class LocalFoldersTests : IDisposable
{
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "cm-tests-" + Guid.NewGuid().ToString("N"));
    private string LocalDir => Path.Combine(_profile, "Local Folders");

    public LocalFoldersTests() => Directory.CreateDirectory(LocalDir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_profile, recursive: true);
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>A contact_index.dat with the real table layout (only the columns the reader uses) and a small set of contacts.</summary>
    private string CreateIndex()
    {
        var file = Path.Combine(LocalDir, "contact_index.dat");
        using var conn = new SqliteConnection($"Data Source={file};Pooling=False");
        conn.Open();
        Exec(conn, """
            create table ContactItems (id integer primary key, componentUid text, folder integer, type integer, salutation text, firstName text,
                middleName text, lastName text, suffix text, displayName text, nickName text, comment text, description text,
                organization text, title text, prefix text, department text);
            create table ContactEmails (id integer primary key, parentId integer, email text, purpose text);
            create table ContactTelephones (id integer primary key, parentId integer, telephone text, purpose text);
            create table ContactAddresses (id integer primary key, parentId integer, street text, city text, county text, zip text, country text, purpose text);
            create table ContactHomepages (id integer primary key, parentId integer, url text, purpose text);
            create table ContactIM (id integer primary key, parentId integer, uin text, imType integer);
            create table ContactCategoryNames (categoryId integer primary key, id integer, categoryName text);
            create table NonUploadedAndMarkedForDeleteContactItems (id integer primary key);
            """);
        Exec(conn, """
            insert into ContactItems (id, componentUid, folder, type, firstName, middleName, lastName, suffix, displayName, nickName, comment, organization, title, prefix, department)
            values (1, 'uid-ann', 9, 0, 'Ann', 'Q', 'Lee', 'Jr', 'Ann Lee', 'Annie', 'likes tea', 'Acme', 'CEO', 'Dr', 'Sales'),
                   (2, 'uid-bob', 9, 0, 'Bob', null, 'Ray', null, '', null, null, null, null, null, null),
                   (3, 'uid-gone', 9, 0, 'Deleted', null, 'One', null, 'Deleted One', null, null, null, null, null, null),
                   (4, 'uid-list', 9, 1, null, null, null, null, 'A distribution list', null, null, null, null, null, null),
                   (5, 'uid-other', 12, 0, 'Other', null, 'Folder', null, 'Other Folder', null, null, null, null, null, null);
            insert into NonUploadedAndMarkedForDeleteContactItems (id) values (3);
            insert into ContactEmails (parentId, email, purpose) values (1, 'ann@work.example', 'WORK_1'), (1, 'ann@home.example', 'HOME_1'),
                (1, 'ann@work.example', 'WORK_2'), (2, 'bob@x.example', 'EMAIL_1');
            insert into ContactTelephones (parentId, telephone, purpose) values (1, '+1 555 0100', 'CELL_1'), (1, '+1 555 0101', 'FAXWORK_1'),
                (1, '+1 555 0102', 'Personal_1');
            insert into ContactAddresses (parentId, street, city, county, zip, country, purpose) values (1, '1 Main; Suite 2', 'Town', 'ST', '12345', 'US', 'WORK_1'),
                (1, null, null, null, null, null, 'HOME_1');
            insert into ContactHomepages (parentId, url, purpose) values (1, 'https://ann.example', 'WORK_1');
            insert into ContactIM (parentId, uin, imType) values (1, 'ann-im', 9);
            insert into ContactCategoryNames (id, categoryName) values (1, 'Clients'), (1, 'My Contacts'), (1, 'starred'), (1, 'clients'), (2, 'My Contacts');
            """);
        return file;
    }

    private string CreateFolders()
    {
        var file = Path.Combine(LocalDir, "folders.dat");
        using var conn = new SqliteConnection($"Data Source={file};Pooling=False");
        conn.Open();
        Exec(conn, "create table Folders (id integer primary key, name text, path text, folderFlags integer, parentFolderId integer)");
        Exec(conn, "create table FolderAttributes (id integer primary key, parentId integer, name text, value)");
        Exec(conn, "insert into Folders (id, name, path, folderFlags) values (9, 'Contacts', '/Contacts', 0), (12, 'RecipientHistory', '/RecipientHistory', 520)");
        Exec(conn, "insert into FolderAttributes (parentId, name, value) values (9, 'type', 4), (12, 'type', 4)");
        return file;
    }

    [Fact]
    public void Reads_the_contacts_of_one_folder_and_skips_lists_deleted_items_and_other_folders()
    {
        var contacts = LocalContactReader.Read(CreateIndex(), 9, "Local Folders:/Contacts");

        Assert.Equal(["Ann Lee", "Bob Ray"], contacts.Select(c => c.DisplayName));
        Assert.All(contacts, c => Assert.Equal("Local Folders:/Contacts", c.SourceFolderId));
    }

    [Fact]
    public void Maps_name_company_notes_and_the_ids_that_remembered_decisions_rely_on()
    {
        var ann = LocalContactReader.Read(CreateIndex(), 9, "f")[0];

        Assert.Equal(("Ann", "Q", "Lee", "Jr", "Dr"), (ann.GivenName, ann.MiddleName, ann.FamilyName, ann.NameSuffix, ann.NamePrefix));
        Assert.Equal(("Acme", "CEO", "Sales", "likes tea"), (ann.Company, ann.JobTitle, ann.Department, ann.Notes));
        Assert.Equal("uid-ann", ann.Uid);
        Assert.Equal("1", ann.RemoteId);
        Assert.Contains("NICKNAME:Annie", ann.Extra);
    }

    [Fact]
    public void A_contact_without_a_display_name_gets_one_from_its_name_parts()
    {
        var bob = LocalContactReader.Read(CreateIndex(), 9, "f")[1];

        Assert.Equal("Bob Ray", bob.DisplayName);
    }

    [Fact]
    public void Maps_values_with_their_types_and_drops_repeats_and_empty_addresses()
    {
        var ann = LocalContactReader.Read(CreateIndex(), 9, "f")[0];

        Assert.Equal(["ann@work.example", "ann@home.example"], ann.Emails);
        Assert.Equal(";TYPE=WORK", ann.Labels["ann@work.example"]);
        Assert.Equal(";TYPE=HOME", ann.Labels["ann@home.example"]);
        Assert.Equal(["+1 555 0100", "+1 555 0101", "+1 555 0102"], ann.Phones);
        Assert.Equal(";TYPE=CELL", ann.Labels["+1 555 0100"]);
        Assert.Equal(";TYPE=WORK,FAX", ann.Labels["+1 555 0101"]);
        Assert.False(ann.Labels.ContainsKey("+1 555 0102")); // free-text purpose: no vCard type
        var adr = Assert.Single(ann.Addresses);
        Assert.Equal(@";;1 Main\; Suite 2;Town;ST;12345;US", adr);
        Assert.Equal(";TYPE=WORK", ann.Labels[adr]);
    }

    [Fact]
    public void Keeps_homepages_and_instant_messaging_as_extra_lines_so_a_write_back_loses_nothing()
    {
        var ann = LocalContactReader.Read(CreateIndex(), 9, "f")[0];

        Assert.Contains("URL;TYPE=WORK:https://ann.example", ann.Extra);
        Assert.Contains("X-ICEWARP-IM;TYPE=9:ann-im", ann.Extra);
    }

    [Fact]
    public void Labels_come_through_once_and_the_built_in_google_ones_are_left_out()
    {
        var contacts = LocalContactReader.Read(CreateIndex(), 9, "f");

        Assert.Equal(["Clients"], contacts[0].Groups);
        Assert.Empty(contacts[1].Groups);
    }

    [Fact]
    public void Reading_does_not_modify_the_source_file()
    {
        var file = CreateIndex();
        var before = File.ReadAllBytes(file);

        LocalContactReader.Read(file, 9, "f");

        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task The_store_finds_the_folder_by_its_path_and_reads_it()
    {
        CreateIndex();
        CreateFolders();
        var store = new LocalFoldersContactStore(_profile);
        var folder = new ContactFolder("Local Folders:/Contacts", "Contacts", "/Contacts", "Local Folders", "Local", FolderKind.Local, true, 2);

        var contacts = await store.ReadContactsAsync(folder);

        Assert.Equal(2, contacts.Count);
    }

    [Fact]
    public async Task The_store_says_so_when_the_folder_is_gone_and_refuses_a_new_folder()
    {
        CreateIndex();
        CreateFolders();
        var store = new LocalFoldersContactStore(_profile);
        var gone = new ContactFolder("Local Folders:/Nope", "Nope", "/Nope", "Local Folders", "Local", FolderKind.Local, true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadContactsAsync(gone));
        await Assert.ThrowsAsync<NotSupportedException>(() => store.CreateFolderAsync(gone, "New", []));
        Assert.False(store.CanCreateFolder(gone));
    }

    private sealed class Recording(string name, List<string> log) : IContactStore
    {
        public Task<IReadOnlyList<Contact>> ReadContactsAsync(ContactFolder folder, CancellationToken ct = default)
        { log.Add($"{name}:read"); return Task.FromResult<IReadOnlyList<Contact>>([]); }
        public Task<WriteReport> ReplaceContactsAsync(ContactFolder folder, IReadOnlyList<Contact> contacts, CancellationToken ct = default)
        { log.Add($"{name}:replace"); return Task.FromResult(new WriteReport(folder.Name, 0, 0, 0, null)); }
        public Task<WriteReport> CreateFolderAsync(ContactFolder accountFolder, string name2, IReadOnlyList<Contact> contacts, CancellationToken ct = default)
        { log.Add($"{name}:create"); return Task.FromResult(new WriteReport(name2, contacts.Count, 0, 0, null)); }
        public Task<FolderRefresh> RefreshFoldersAsync(IReadOnlyList<ContactFolder> folders, Func<ContactFolder, bool> mayContact, CancellationToken ct = default) =>
            Task.FromResult(new FolderRefresh([], [], 0));
    }

    [Fact]
    public async Task Local_contacts_are_read_locally_and_can_be_put_in_a_new_folder_of_a_server_account()
    {
        var log = new List<string>();
        var store = new CompositeContactStore(new Recording("dav", log), new Recording("gmail", log), new Recording("local", log));
        var local = new ContactFolder("Local Folders:/Contacts", "Contacts", "/Contacts", "Local Folders", "Local", FolderKind.Local, true, 2);
        var account = new ContactFolder("acct:/0/Contacts", "Contacts", "/0/Contacts", "Work", "mail.example.com", FolderKind.CardDav, true, 5);

        await store.ReadContactsAsync(local);
        await store.CreateFolderAsync(account, "Merged", []);

        Assert.Equal(["local:read", "dav:create"], log);
        Assert.True(store.CanCreateFolder(account));
    }

    [Fact]
    public void A_local_folder_can_be_ticked_read_and_replaced_but_gets_no_new_folder()
    {
        var folder = new ContactFolder("Local Folders:/Contacts", "Contacts", "/Contacts", "Local Folders", "Local", FolderKind.Local, true, 2);

        Assert.True(folder.IsReadable);
        Assert.True(folder.CanWrite);          // its contents can be replaced (LocalFoldersWriteTests) ...
        Assert.False(folder.CanCreateFolder);  // ... but no new folder can be made inside it
    }
}
