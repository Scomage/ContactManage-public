using ContactManage.Core;
using Xunit;

namespace ContactManage.Tests;

public class RestoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cm-restore-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static Contact C(string name, params string[] labels)
    {
        var c = new Contact { DisplayName = name, GivenName = name.Split(' ')[0], FamilyName = name.Split(' ')[^1] };
        c.Emails.Add($"{name.Replace(' ', '.').ToLowerInvariant()}@x.example");
        c.Groups.AddRange(labels);
        return c;
    }

    private static ContactFolder Folder(string account, string name) =>
        new($"{account}:/{name}", name, "/" + name, account, "srv", FolderKind.CardDav, true, 1);

    [Fact]
    public void Labels_survive_a_backup_file_round_trip_and_are_not_left_in_the_raw_lines()
    {
        var written = VCard.WriteAll([C("Ann Lee", "Clients", "Family, close")]);

        var back = Assert.Single(VCard.ParseAll(written));

        Assert.Equal(["Clients", "Family, close"], back.Groups);              // a comma inside a label name is escaped, not split
        Assert.DoesNotContain(back.Extra, l => l.StartsWith("CATEGORIES"));
    }

    [Fact]
    public void Backups_are_listed_newest_first_with_their_contact_counts_and_strangers_are_skipped()
    {
        var older = VCardFiles.Backup(_root, "Work Two - Contacts", [C("A One"), C("B Two")], new DateTime(2026, 3, 1, 9, 0, 0));
        var newer = VCardFiles.Backup(_root, "Home Gmail - My Contacts", [C("C Three")], new DateTime(2026, 4, 2, 10, 30, 0));
        Directory.CreateDirectory(Path.Combine(_root, "not-a-backup"));
        File.WriteAllText(Path.Combine(_root, "not-a-backup", "x.vcf"), "BEGIN:VCARD\r\nEND:VCARD\r\n");
        Directory.CreateDirectory(Path.Combine(_root, "20260501-000000"));                      // a backup folder with no files in it

        var sets = BackupCatalog.List(_root);

        Assert.Equal([new DateTime(2026, 4, 2, 10, 30, 0), new DateTime(2026, 3, 1, 9, 0, 0)], sets.Select(s => s.Taken));
        Assert.Equal(("Home Gmail - My Contacts", 1, newer), (sets[0].Files[0].Label, sets[0].Files[0].ContactCount, sets[0].Files[0].Path));
        Assert.Equal(("Work Two - Contacts", 2, older), (sets[1].Files[0].Label, sets[1].Files[0].ContactCount, sets[1].Files[0].Path));
    }

    [Fact]
    public void A_missing_backup_root_means_no_backups()
    {
        Assert.Empty(BackupCatalog.List(Path.Combine(_root, "never-made")));
    }

    [Fact]
    public void A_backup_is_matched_to_the_folder_with_that_account_and_name_even_when_the_name_had_forbidden_characters()
    {
        var file = new BackupFile("x.vcf", VCardFiles.BackupLabel("Work Two", "A/B: Team"), 3);
        var folders = new[] { Folder("Work Two", "Contacts"), Folder("Work Two", "A/B: Team"), Folder("Home AOL", "A/B: Team") };

        var target = BackupCatalog.FindTarget(file, folders);

        Assert.Equal("Work Two", target!.AccountName);
        Assert.Equal("A/B: Team", target.Name);
        Assert.Null(BackupCatalog.FindTarget(new BackupFile("y.vcf", "Gone - Folder", 1), folders));    // no guess when nothing matches
    }
}
