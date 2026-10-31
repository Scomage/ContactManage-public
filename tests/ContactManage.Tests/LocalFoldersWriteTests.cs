using ContactManage.Adapters.IceWarp;
using ContactManage.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ContactManage.Tests;

/// <summary>
/// Writes to a stand-in for IceWarp's Local Folders databases. The stand-in has the real tables, the real full-text search
/// tables and the real triggers (copied from a client's schema), so these tests show what IceWarp's own machinery does with the rows.
/// </summary>
public class LocalFoldersWriteTests : IDisposable
{
    private const string FolderId = "Local Folders:/Contacts";
    private const string Prefix = "xyz:abcd:";
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "cm-tests-" + Guid.NewGuid().ToString("N"));
    private string LocalDir => Path.Combine(_profile, "Local Folders");
    private string Index => Path.Combine(LocalDir, "contact_index.dat");
    private string Data => Path.Combine(LocalDir, "contact_data.dat");
    private string BackupRoot => Path.Combine(_profile, "Backups");

    private static readonly ContactFolder Folder = new(FolderId, "Contacts", "/Contacts", "Local Folders", "Local", FolderKind.Local, true, 3);

    public LocalFoldersWriteTests()
    {
        Directory.CreateDirectory(LocalDir);
        CreateFolders();
        CreateIndex();
        CreateData();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_profile, recursive: true);
    }

    // ---- the stand-in

    private static void Exec(string file, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={file};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static List<object?[]> Rows(string file, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={file};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<object?[]>();
        while (r.Read())
        {
            var row = new object?[r.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            list.Add(row);
        }
        return list;
    }

    private static long Count(string file, string sql) => Convert.ToInt64(Rows(file, sql)[0][0]);

    private void CreateFolders()
    {
        var file = Path.Combine(LocalDir, "folders.dat");
        Exec(file, """
            create table Folders (id integer primary key, name text, path text, folderFlags integer, parentFolderId integer);
            create table FolderAttributes (id integer primary key, parentId integer, name text, value);
            insert into Folders (id, name, path, folderFlags) values (9, 'Contacts', '/Contacts', 0), (12, 'RecipientHistory', '/RecipientHistory', 520);
            insert into FolderAttributes (parentId, name, value) values (9, 'type', 4), (12, 'type', 4);
            """);
    }

    private void CreateIndex()
    {
        Exec(Index, """
            pragma journal_mode = wal;
            pragma user_version = 17;
            create table "ContactItems" ("id" INTEGER NOT NULL PRIMARY KEY, "uniqueId", "versionId", "syncFlags" INTEGER, "folder" INTEGER, "syncFolder" INTEGER,
                "type" INTEGER, "flags" INTEGER, "gender" INTEGER, "birthday" TIMESTAMP, "salutation" TEXT, "firstName" TEXT, "middleName" TEXT, "lastName" TEXT,
                "suffix" TEXT, "displayName" TEXT, "nickName" TEXT, "initials" TEXT, "fileAs" TEXT, "listSubscribe" TEXT, "listUnsubscribe" TEXT, "listHelp" TEXT,
                "comment" TEXT, "description" TEXT, "organization" TEXT, "title" TEXT, "prefix" TEXT, "assistantsName" TEXT, "assistantsPhone" TEXT, "managersName" TEXT,
                "managersPhone" TEXT, "referredBy" TEXT, "spouse" TEXT, "children" TEXT, "anniversary" TIMESTAMP, "hobbies" TEXT, "department" TEXT, "created" TIMESTAMP,
                "updated" TIMESTAMP, "profession" TEXT, "office" TEXT, "bankAccount" TEXT, "class" TEXT, "componentUid" TEXT, "avatarHash" TEXT, "birthdayDay" INTEGER,
                "anniversaryDay" INTEGER, "sendCount" INTEGER, "lastSend" TIMESTAMP, userPreferenceMessage TEXT, userPreferenceCall TEXT, userPreferenceVideo TEXT, userPreferenceMail TEXT);
            create table "ContactEmails" ("id" INTEGER NOT NULL PRIMARY KEY, "parentId" INTEGER NOT NULL, "email" TEXT, "displayName" TEXT, "purpose" TEXT, "description" TEXT, "isPrimary" INTEGER);
            create table "ContactHomepages" ("id" INTEGER NOT NULL PRIMARY KEY, "parentId" INTEGER NOT NULL, "url" TEXT, "purpose" TEXT, "description" TEXT);
            create table "ContactIM" ("id" INTEGER NOT NULL PRIMARY KEY, "parentId" INTEGER NOT NULL, "displayName" TEXT, "imType" INTEGER, "uin" TEXT, "uri" TEXT,
                "specificUin" TEXT, "purpose" TEXT, "description" TEXT, "group" TEXT, contactType INTEGER, lastMessage TEXT, lastRead TEXT, subscription INTEGER DEFAULT 0,
                unreadCount INTEGER DEFAULT 0, timeZone TEXT DEFAULT NULL, creator TEXT DEFAULT NULL, visibility INTEGER DEFAULT 0, flags INTEGER DEFAULT 0);
            create table "ContactTelephones" ("id" INTEGER NOT NULL PRIMARY KEY, "parentId" INTEGER NOT NULL, "prefix" TEXT, "telephone" TEXT, "suffix" TEXT, "purpose" TEXT, "description" TEXT, "isPrimary" INTEGER);
            create table "ContactAddresses" ("id" INTEGER NOT NULL PRIMARY KEY, "parentId" INTEGER NOT NULL, "mailingName" TEXT, "street" TEXT, "city" TEXT, "county" TEXT,
                "country" TEXT, "zip" TEXT, "purpose" TEXT, "description" TEXT, "geo" TEXT, "isPrimary" INTEGER);
            create table "ContactLanguages" ("id" INTEGER NOT NULL PRIMARY KEY, "parentId" INTEGER NOT NULL, "languageName" TEXT);
            create table "ContactCategoryNames" ("categoryId" INTEGER NOT NULL PRIMARY KEY, "id" INTEGER NOT NULL, "categoryName" TEXT NOT NULL);
            create unique index "idx_ContactCategoryNames_CategoryName" ON "ContactCategoryNames" ("categoryName", "id");
            create table "ContactKeys" ("id" INTEGER NOT NULL PRIMARY KEY, "parentId" INTEGER NOT NULL, "purpose" TEXT, "preference" INTEGER, "mediaType" TEXT, "address" TEXT, "content" BLOB);
            create table "NonUploadedAndMarkedForDeleteContactItems" ("id" INTEGER PRIMARY KEY NOT NULL);
            create virtual table "ContactAddressIndex" using fts5 ("mailingName","street","zip","city","county","country", tokenize=unicode61, content="ContactAddresses", content_rowid="id");
            create virtual table "ContactEmailIndex" using fts5 ("email","displayName", tokenize=unicode61, content="ContactEmails", content_rowid="id");
            create virtual table "ContactIMIndex" using fts5 ("displayName", tokenize=unicode61, content="ContactIM", content_rowid="id");
            create trigger "trig_ContactAddresses_Delete" after delete on "ContactAddresses" begin
                insert into "ContactAddressIndex"("ContactAddressIndex", rowid, "mailingName", "street", "zip", "city", "county", "country")
                values('delete', old.id, old."mailingName", old."street", old."zip", old."city", old."county", old."country"); end;
            create trigger "trig_ContactAddresses_Insert" after insert on "ContactAddresses" begin
                insert into "ContactAddressIndex"(rowid, "mailingName", "street", "zip", "city", "county", "country")
                values (new.id, new.mailingName, new.street, new.zip, new.city, new.county, new.country); end;
            create trigger "trig_ContactAddresses_Update" after update on "ContactAddresses" begin
                insert into "ContactAddressIndex"("ContactAddressIndex", rowid, "mailingName", "street", "zip", "city", "county", "country")
                values('delete', old.id, old."mailingName", old."street", old."zip", old."city", old."county", old."country");
                insert into "ContactAddressIndex"(rowid, "mailingName", "street", "zip", "city", "county", "country")
                values (new.id, new.mailingName, new.street, new.zip, new.city, new.county, new.country); end;
            create trigger "trig_ContactEmails_Delete" after delete on "ContactEmails" begin
                insert into "ContactEmailIndex"("ContactEmailIndex", rowid, email, displayName) values('delete', old.id, old.email, old.displayName); end;
            create trigger "trig_ContactEmails_Insert" after insert on "ContactEmails" begin
                insert into "ContactEmailIndex"(rowid, email, displayName) values (new.id, new.email, new.displayName); end;
            create trigger "trig_ContactEmails_Update" after update on "ContactEmails" begin
                insert into "ContactEmailIndex"("ContactEmailIndex", rowid, email, displayName) values('delete', old.id, old.email, old.displayName);
                insert into "ContactEmailIndex"(rowid, email, displayName) values (new.id, new.email, new.displayName); end;
            create trigger "trig_ContactIMIndex_Delete" after delete on "ContactIM" begin
                insert into "ContactIMIndex"("ContactIMIndex", rowid, displayName) values('delete', old.id, old.displayName); end;
            create trigger "trig_ContactIMIndex_Insert" after insert on "ContactIM" begin
                insert into "ContactIMIndex"(rowid, displayName) values (new.id, new.displayName); end;
            create trigger "trig_ContactIMIndex_Update" after update on "ContactIM" begin
                insert into "ContactIMIndex"("ContactIMIndex", rowid, displayName) values('delete', old.id, old.displayName);
                insert into "ContactIMIndex"(rowid, displayName) values (new.id,  new.displayName); end;
            """);

        Exec(Index, $"""
            insert into ContactItems (id, syncFlags, folder, syncFolder, type, flags, gender, birthday, anniversary, sendCount, lastSend, created, updated,
                firstName, lastName, displayName, fileAs, organization, comment, componentUid, avatarHash, nickName, salutation, initials, "class") values
              (1, 0, 9, 0, 0, 0, 2, 0, 0, 0, 0, 100, 100, 'Ann', 'Lee', 'Ann Lee', 'Ann Lee', 'Acme', 'likes tea', '{Prefix}11111111-1111-1111-1111-111111111111', 'hashA', 'Annie', '', '', ''),
              (2, 0, 9, 0, 0, 0, 2, 0, 0, 0, 0, 100, 100, 'Ann', 'Lee', 'Ann Lee', 'Ann Lee', '', '', '{Prefix}22222222-2222-2222-2222-222222222222', 'hashB', '', '', '', ''),
              (3, 0, 9, 0, 0, 0, 2, 0, 0, 0, 0, 100, 100, 'Bob', 'Ray', 'Bob Ray', 'Bob Ray', '', '', '{Prefix}33333333-3333-3333-3333-333333333333', 'hashC', '', '', '', ''),
              (4, 0, 12, 0, 0, 0, 2, 0, 0, 0, 0, 100, 100, '', '', 'History Person', 'History Person', '', '', '{Prefix}44444444-4444-4444-4444-444444444444', null, '', '', '', '');
            insert into ContactEmails (parentId, email, displayName, purpose, description, isPrimary) values
              (1, 'ann@work.example', '', 'WORK_1', '', 1), (1, 'ann@home.example', '', 'HOME_1', '', 100), (2, 'ann2@work.example', '', 'WORK_1', '', 1),
              (3, 'bob@x.example', '', 'EMAIL_1', '', 1), (4, 'history@x.example', '', 'EMAIL_1', '', 1);
            insert into ContactTelephones (parentId, prefix, telephone, suffix, purpose, description, isPrimary) values
              (1, '', '+1 555 0100', '', 'CELL_1', '', 1), (2, '', '+1 555 0100', '', 'CELL_1', '', 1), (3, '', '+1 555 0300', '', 'Personal_1', '', 1);
            insert into ContactAddresses (parentId, mailingName, street, city, county, country, zip, purpose, description, geo, isPrimary) values
              (1, '', '1 Main St', 'Town', 'ST', 'US', '12345', 'WORK_1', '', '', 1);
            insert into ContactHomepages (parentId, url, purpose, description) values (1, 'https://ann.example', 'HOMEPAGE_1', '');
            insert into ContactIM (parentId, displayName, imType, uin, specificUin, contactType) values (1, 'ann-im', 9, 'ann-im', 'ann-im', 1);
            insert into ContactCategoryNames (id, categoryName) values (1, 'My Contacts'), (1, 'Clients'), (2, 'My Contacts'), (3, 'My Contacts'), (3, 'Friends');
            """);
    }

    private void CreateData()
    {
        Exec(Data, """
            pragma journal_mode = wal;
            create table "ContactItemContents" ("id" INTEGER NOT NULL, "propertyName" TEXT, "propertyId" TEXT, "properties", PRIMARY KEY ("id", "propertyName", "propertyId"));
            create virtual table "CommentIndex" using fts5 ("plainTextComment", tokenize=unicode61);
            create virtual table "NameIndex" using fts5 ("firstName","middleName","lastName","fileAs","displayName","nickName","prefix","title","organization", tokenize=unicode61);
            insert into NameIndex(rowid, firstName, middleName, lastName, fileAs, displayName, nickName, prefix, title, organization) values
              (1, 'Ann', '', 'Lee', 'Ann Lee', 'Ann Lee', 'Annie', '', '', 'Acme'), (2, 'Ann', '', 'Lee', 'Ann Lee', 'Ann Lee', '', '', '', ''),
              (3, 'Bob', '', 'Ray', 'Bob Ray', 'Bob Ray', '', '', '', ''), (4, '', '', '', 'History Person', 'History Person', '', '', '', '');
            insert into CommentIndex(rowid, plainTextComment) values (1, 'likes tea'), (2, ''), (3, ''), (4, '');
            insert into ContactItemContents (id, propertyName, propertyId, properties) values
              (1, 'card', '', x'424547494E3A56434152440D0A454E443A56434152440D0A'), (2, 'card', '', x'424547494E3A56434152440D0A454E443A56434152440D0A'),
              (3, 'card', '', x'424547494E3A56434152440D0A454E443A56434152440D0A'),
              (1, 'avatar', '', x'89504E47'), (2, 'avatar', '', x'89504E47'), (3, 'avatar', '', x'89504E47');
            """);
    }

    private IReadOnlyList<Contact> ReadAll() => LocalContactReader.Read(Index, 9, FolderId);

    private WriteReport Replace(IReadOnlyList<Contact> contacts) =>
        new LocalFoldersContactStore(_profile, BackupRoot).ReplaceContactsAsync(Folder, contacts).GetAwaiter().GetResult();

    /// <summary>Every ContactItems row has exactly one name row and one comment row, and the email search finds what the rows hold.</summary>
    private void AssertConsistent()
    {
        var items = Rows(Index, "select id from ContactItems order by id").Select(r => Convert.ToInt64(r[0])).ToList();
        Assert.Equal(items, Rows(Data, "select rowid from NameIndex order by rowid").Select(r => Convert.ToInt64(r[0])).ToList());
        Assert.Equal(items, Rows(Data, "select rowid from CommentIndex order by rowid").Select(r => Convert.ToInt64(r[0])).ToList());
        Assert.Empty(Rows(Data, "select id from ContactItemContents").Select(r => Convert.ToInt64(r[0])).Except(items));
        foreach (var fts in new[] { "ContactEmailIndex", "ContactAddressIndex", "ContactIMIndex" })
            Exec(Index, $"insert into {fts}({fts}, rank) values('integrity-check', 1)");
    }

    // ---- tests

    [Fact]
    public void Nothing_is_changed_for_contacts_that_were_not_touched()
    {
        var before = Rows(Index, "select * from ContactItems order by id").Select(r => string.Join("|", r)).ToList();

        var report = Replace(ReadAll());

        Assert.Equal((0, 0, 3, 0), (report.Added, report.Removed, report.Unchanged, report.Updated));
        Assert.Equal(3, report.CountAfter);
        Assert.Equal(before, Rows(Index, "select * from ContactItems order by id").Select(r => string.Join("|", r)).ToList());
        AssertConsistent();
    }

    [Fact]
    public void A_removed_contact_leaves_no_row_behind_in_any_table_and_other_folders_are_not_touched()
    {
        var keep = ReadAll().Where(c => c.DisplayName != "Bob Ray").ToList();

        var report = Replace(keep);

        Assert.Equal((0, 1, 2), (report.Added, report.Removed, report.Unchanged));
        Assert.Equal(0, Count(Index, "select count(*) from ContactItems where id = 3"));
        foreach (var t in new[] { "ContactEmails", "ContactTelephones" })
            Assert.Equal(0, Count(Index, $"select count(*) from {t} where parentId = 3"));
        Assert.Equal(0, Count(Index, "select count(*) from ContactCategoryNames where id = 3"));
        Assert.Equal(0, Count(Data, "select count(*) from NameIndex where rowid = 3"));
        Assert.Equal(0, Count(Data, "select count(*) from ContactItemContents where id = 3"));
        Assert.Equal(0, Count(Index, "select count(*) from ContactEmailIndex where ContactEmailIndex match 'bob'"));
        Assert.Equal(1, Count(Index, "select count(*) from ContactItems where id = 4")); // recipient history stays
        AssertConsistent();
    }

    [Fact]
    public void A_merged_contact_is_written_over_one_of_its_originals_and_keeps_its_picture_and_card()
    {
        var all = ReadAll();
        var ann1 = all[0];
        var ann2 = all[1];
        var merged = new Contact { DisplayName = "Ann Q. Lee", GivenName = "Ann", MiddleName = "Q", FamilyName = "Lee", Company = "Acme" };
        merged.Emails.AddRange(ann1.Emails.Concat(ann2.Emails));
        merged.Phones.AddRange(ann1.Phones);
        merged.Addresses.AddRange(ann1.Addresses);
        foreach (var kv in ann1.Labels.Concat(ann2.Labels)) merged.Labels[kv.Key] = kv.Value;
        merged.Groups.AddRange(["Clients", "Newsletter"]);
        merged.Extra.AddRange(ann1.Extra);
        merged.Sources.Add(new ContactSource(FolderId, ann1.RemoteId, null, ann1.Uid));
        merged.Sources.Add(new ContactSource(FolderId, ann2.RemoteId, null, ann2.Uid));

        var report = Replace([merged, all[2]]);

        Assert.Equal((0, 1, 1, 1), (report.Added, report.Removed, report.Unchanged, report.Updated));
        Assert.Equal(2, report.CountAfter);
        var row = Rows(Index, "select middleName, displayName, fileAs, componentUid, avatarHash, updated from ContactItems where id = 1")[0];
        Assert.Equal("Q", row[0]);
        Assert.Equal("Ann Q. Lee", row[1]);
        Assert.Equal("Ann Q. Lee", row[2]);
        Assert.Equal(Prefix + "11111111-1111-1111-1111-111111111111", row[3]); // identity kept
        Assert.Equal("hashA", row[4]);                                          // picture kept
        Assert.NotEqual(100L, Convert.ToInt64(row[5]));
        Assert.Equal(1, Count(Data, "select count(*) from ContactItemContents where id = 1 and propertyName = 'avatar'"));
        Assert.Equal(1, Count(Data, "select count(*) from NameIndex where NameIndex match 'Q'"));
        Assert.Equal(0, Count(Index, "select count(*) from ContactItems where id = 2"));

        Assert.Equal(["ann@work.example", "ann@home.example", "ann2@work.example"],
            Rows(Index, "select email from ContactEmails where parentId = 1 order by id").Select(r => (string)r[0]!));
        Assert.Equal(1, Count(Index, "select count(*) from ContactEmailIndex where ContactEmailIndex match 'ann2'")); // triggers kept the search right
        Assert.Equal(1, Count(Index, "select count(*) from ContactTelephones where parentId = 1"));
        Assert.Equal(1, Count(Index, "select count(*) from ContactEmails where parentId = 1 and isPrimary = 1"));
        var labels = Rows(Index, "select categoryName from ContactCategoryNames where id = 1").Select(r => (string)r[0]!).Order().ToList();
        Assert.Equal(["Clients", "My Contacts", "Newsletter"], labels); // built-in label kept, new one added
        Assert.Equal(1, Count(Index, "select count(*) from ContactIM where parentId = 1")); // untouched value kinds survive
        Assert.Equal(1, Count(Index, "select count(*) from ContactHomepages where parentId = 1"));
        AssertConsistent();
    }

    [Fact]
    public void A_new_contact_gets_a_row_a_card_a_name_row_and_values_in_icewarps_own_conventions()
    {
        var fresh = new Contact { DisplayName = "Zed Zimmer", GivenName = "Zed", FamilyName = "Zimmer", Company = "Zeta", Uid = "people/c123" };
        fresh.Emails.AddRange(["zed@work.example", "zed@home.example"]);
        fresh.Labels["zed@work.example"] = ";TYPE=WORK";
        fresh.Labels["zed@home.example"] = ";TYPE=HOME";
        fresh.Phones.Add("+1 555 0999");
        fresh.Labels["+1 555 0999"] = ";TYPE=WORK,FAX";
        fresh.Addresses.Add(@";;2 Side\; Rd;City;ST;99999;US");
        fresh.Groups.Add("Clients");
        fresh.Extra.AddRange(["NICKNAME:Zedd", "URL;TYPE=WORK:https://zed.example", "BDAY:19800101", "X-GOOGLE-EVENTS:[]"]);

        var report = Replace([.. ReadAll(), fresh]);

        Assert.Equal((1, 0, 3), (report.Added, report.Removed, report.Unchanged));
        Assert.Contains("BDAY", report.Note);
        var id = Convert.ToInt64(Rows(Index, "select id from ContactItems where displayName = 'Zed Zimmer'")[0][0]);
        Assert.True(id > 4);
        var item = Rows(Index, "select folder, type, componentUid, avatarHash, nickName, fileAs, organization, uniqueId, salutation, created from ContactItems where id = " + id)[0];
        Assert.Equal((9L, 0L), (Convert.ToInt64(item[0]), Convert.ToInt64(item[1])));
        Assert.StartsWith(Prefix, (string)item[2]!);
        Assert.Equal(Prefix.Length + 36, ((string)item[2]!).Length);
        Assert.Null(item[3]);                          // no picture to describe
        Assert.Equal(("Zedd", "Zed Zimmer", "Zeta", null, ""), ((string)item[4]!, (string)item[5]!, (string)item[6]!, item[7], (string)item[8]!));
        Assert.NotEqual(0L, Convert.ToInt64(item[9]));

        Assert.Equal([("WORK_1", 1L), ("HOME_1", 100L)],
            Rows(Index, $"select purpose, isPrimary from ContactEmails where parentId = {id} order by id").Select(r => ((string)r[0]!, Convert.ToInt64(r[1]))));
        Assert.Equal([("FAXWORK_1", 1L)], Rows(Index, $"select purpose, isPrimary from ContactTelephones where parentId = {id}").Select(r => ((string)r[0]!, Convert.ToInt64(r[1]))));
        var adr = Rows(Index, $"select street, city, county, zip, country, purpose from ContactAddresses where parentId = {id}")[0];
        Assert.Equal(["2 Side; Rd", "City", "ST", "99999", "US", "OTHER_1"], adr.Select(v => (string)v!));
        Assert.Equal("WORK_1", Rows(Index, $"select purpose from ContactHomepages where parentId = {id}")[0][0]);
        Assert.Equal(["Clients", "My Contacts"], Rows(Index, $"select categoryName from ContactCategoryNames where id = {id}").Select(r => (string)r[0]!).Order());

        Assert.Equal(1, Count(Data, $"select count(*) from ContactItemContents where id = {id} and propertyName = 'card'"));
        Assert.Equal(0, Count(Data, $"select count(*) from ContactItemContents where id = {id} and propertyName = 'avatar'"));
        Assert.Equal(1, Count(Data, "select count(*) from NameIndex where NameIndex match 'Zimmer'"));
        Assert.Equal(2, Count(Index, "select count(*) from ContactEmailIndex where ContactEmailIndex match 'zed'")); // both new addresses are searchable
        AssertConsistent();
    }

    [Fact]
    public void A_contact_read_back_from_a_backup_updates_the_row_it_came_from_instead_of_duplicating_it()
    {
        var restored = ReadAll()[2];
        restored.SourceFolderId = null; // as read from a vcf: no source, but the old UID
        restored.RemoteId = null;
        restored.GivenName = "Robert";
        restored.DisplayName = "Robert Ray";

        var report = Replace([ReadAll()[0], ReadAll()[1], restored]);

        Assert.Equal((0, 0, 2, 1), (report.Added, report.Removed, report.Unchanged, report.Updated));
        Assert.Equal("Robert Ray", Rows(Index, "select displayName from ContactItems where id = 3")[0][0]);
        AssertConsistent();
    }

    [Fact]
    public void A_contact_deleted_earlier_comes_back_under_its_own_uid_with_a_new_row()
    {
        var bob = ReadAll()[2];
        Replace(ReadAll().Where(c => c.DisplayName != "Bob Ray").ToList());
        var fromBackup = VCard.ParseAll(VCard.Write(bob))[0]; // what a backup file gives back

        var report = Replace([.. ReadAll(), fromBackup]);

        Assert.Equal(1, report.Added);
        Assert.Equal(bob.Uid, Rows(Index, "select componentUid from ContactItems where displayName = 'Bob Ray'")[0][0]);
        AssertConsistent();
    }

    [Fact]
    public void An_empty_list_is_refused_and_nothing_changes()
    {
        var before = Rows(Index, "select count(*) from ContactItems")[0][0];

        var ex = Record.Exception(() => Replace([]));

        Assert.Contains("Refusing to empty", ex!.GetBaseException().Message);
        Assert.Equal(before, Rows(Index, "select count(*) from ContactItems")[0][0]);
        AssertConsistent();
    }

    [Fact]
    public void A_database_layout_it_does_not_know_is_refused_before_anything_is_backed_up()
    {
        Exec(Index, "pragma user_version = 18");

        var ex = Record.Exception(() => Replace(ReadAll()));

        Assert.Contains("does not know", ex!.GetBaseException().Message);
        Assert.False(Directory.Exists(BackupRoot));
    }

    [Fact]
    public void A_backup_of_both_database_files_is_made_first_and_holds_the_old_contacts()
    {
        Replace(ReadAll().Where(c => c.DisplayName != "Bob Ray").ToList());

        var dir = Assert.Single(Directory.GetDirectories(BackupRoot, "LocalFolders-files-*"));
        Assert.Equal(3, Count(Path.Combine(dir, "contact_index.dat"), "select count(*) from ContactItems where folder = 9"));
        Assert.Equal(4, Count(Path.Combine(dir, "contact_data.dat"), "select count(*) from NameIndex"));
        // The vCard backup list must not mistake this folder for a vCard backup.
        Assert.Empty(BackupCatalog.List(BackupRoot));
    }

    [Fact]
    public void Writing_works_while_another_connection_has_the_database_open_for_reading()
    {
        using var client = new SqliteConnection($"Data Source={Index};Pooling=False");
        client.Open();
        using var read = client.CreateCommand();
        read.CommandText = "select count(*) from ContactItems";
        Assert.Equal(4L, read.ExecuteScalar());

        var report = Replace(ReadAll().Where(c => c.DisplayName != "Bob Ray").ToList());

        Assert.Equal(1, report.Removed);
        Assert.Equal(3L, read.ExecuteScalar()); // the open connection sees the change on its next read
    }

    [Fact]
    public async Task Without_a_backup_place_the_store_refuses_to_write()
    {
        var store = new LocalFoldersContactStore(_profile);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReplaceContactsAsync(Folder, ReadAll()));
    }

    [Fact]
    public async Task What_was_written_reads_back_the_same_way()
    {
        var fresh = new Contact { DisplayName = "Zed Zimmer", GivenName = "Zed", FamilyName = "Zimmer" };
        fresh.Emails.Add("zed@work.example");
        fresh.Labels["zed@work.example"] = ";TYPE=WORK";
        Replace([.. ReadAll(), fresh]);

        var back = await new LocalFoldersContactStore(_profile).ReadContactsAsync(Folder);

        Assert.Equal(4, back.Count);
        var zed = back.Single(c => c.DisplayName == "Zed Zimmer");
        Assert.Equal(["zed@work.example"], zed.Emails);
        Assert.Equal(";TYPE=WORK", zed.Labels["zed@work.example"]);
        Assert.Empty(zed.Groups); // the built-in "My Contacts" label is stored but never shown
    }
}
