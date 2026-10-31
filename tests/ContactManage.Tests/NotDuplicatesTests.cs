using ContactManage.Core;
using Xunit;

namespace ContactManage.Tests;

public class NotDuplicatesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cm-nd-" + Guid.NewGuid().ToString("N"));

    public NotDuplicatesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string FilePath => Path.Combine(_dir, "not-duplicates.json");

    private static Contact C(string uid, string name, string email, string account = "a1")
    {
        var c = new Contact { DisplayName = name, Uid = uid, SourceFolderId = $"{account}:/Contacts" };
        c.Emails.Add(email);
        return c;
    }

    /// <summary>Two pairs that look like duplicates (same email), plus one unrelated contact.</summary>
    private static List<Contact> Sample() =>
    [
        C("u1", "Pat Green", "family@x.example"), C("u2", "Sam Green", "family@x.example"),
        C("u3", "Ann Lee", "ann@x.example"), C("u4", "Ann Lee", "ann@x.example"),
        C("u5", "Di Fox", "di@x.example"),
    ];

    private static MergeGroup Group(MergeResult r, string uid) => r.Groups.Single(g => g.Members.Any(m => m.Uid == uid));

    [Fact]
    public void Kept_separate_sets_are_remembered_and_not_asked_about_again()
    {
        var first = new MergeEngine().Merge(Sample());
        Group(first, "u1").Decision = GroupDecision.KeepSeparate;
        var list = new NotDuplicates(FilePath);
        list.Record(first);

        var second = new MergeEngine().Merge(Sample());
        var settled = NotDuplicates.Load(FilePath).Apply(second);

        Assert.Equal(1, settled);
        var pat = Group(second, "u1");
        Assert.Equal(GroupDecision.KeepSeparate, pat.Decision);
        Assert.True(pat.Remembered);
        Assert.False(Group(second, "u3").Remembered);                 // the Ann pair is still handled normally
        Assert.Equal(GroupDecision.Merge, Group(second, "u3").Decision);
        Assert.Equal(4, second.BuildFinalList().Count);               // Pat, Sam, merged Ann, Di
    }

    [Fact]
    public void A_remembered_set_passes_both_contacts_through_untouched()
    {
        var first = new MergeEngine().Merge(Sample());
        Group(first, "u1").Decision = GroupDecision.KeepSeparate;
        var list = new NotDuplicates(FilePath);
        list.Record(first);

        var second = new MergeEngine().Merge(Sample());
        NotDuplicates.Load(FilePath).Apply(second);

        var final = second.BuildFinalList();
        Assert.Equal(4, final.Count);                                  // Pat, Sam, merged Ann, Di
        Assert.Contains(final, c => c.Uid == "u1");
        Assert.Contains(final, c => c.Uid == "u2");
    }

    [Fact]
    public void Switching_a_remembered_set_to_merge_forgets_it()
    {
        var first = new MergeEngine().Merge(Sample());
        Group(first, "u1").Decision = GroupDecision.KeepSeparate;
        var list = new NotDuplicates(FilePath);
        list.Record(first);

        var second = new MergeEngine().Merge(Sample());
        var loaded = NotDuplicates.Load(FilePath);
        loaded.Apply(second);
        Group(second, "u1").Decision = GroupDecision.Merge;
        loaded.Record(second);

        Assert.Equal(0, NotDuplicates.Load(FilePath).Apply(new MergeEngine().Merge(Sample())));
        Assert.Equal(0, loaded.Count);
    }

    [Fact]
    public void Undecided_sets_are_not_remembered()
    {
        var result = new MergeEngine().Merge(Sample());
        var pending = Group(result, "u1");
        Assert.Equal(GroupDecision.Pending, pending.Decision);        // different names on a shared email: needs review
        var list = new NotDuplicates(FilePath);

        list.Record(result);

        Assert.Equal(0, list.Count);
    }

    [Fact]
    public void A_set_with_a_new_member_is_not_settled_until_every_pair_is_known()
    {
        var first = new MergeEngine().Merge(Sample());
        Group(first, "u1").Decision = GroupDecision.KeepSeparate;
        var list = new NotDuplicates(FilePath);
        list.Record(first);

        var contacts = Sample();
        contacts.Add(C("u6", "Pat G", "family@x.example"));           // joins the Pat/Sam set
        var second = new MergeEngine().Merge(contacts);

        Assert.Equal(0, list.Apply(second));
    }

    [Fact]
    public void Contacts_without_a_lasting_identity_cannot_be_remembered()
    {
        var a = new Contact { DisplayName = "Pat Green" };
        var b = new Contact { DisplayName = "Sam Green" };
        a.Emails.Add("family@x.example");
        b.Emails.Add("family@x.example");
        var result = new MergeEngine().Merge([a, b]);
        result.Groups[0].Decision = GroupDecision.KeepSeparate;
        var list = new NotDuplicates(FilePath);

        list.Record(result);

        Assert.Equal(0, list.Count);
    }

    [Fact]
    public void The_same_uid_in_a_different_account_is_a_different_contact()
    {
        var list = new NotDuplicates();
        var result = new MergeEngine().Merge([C("u1", "Pat Green", "f@x.example", "a1"), C("u2", "Sam Green", "f@x.example", "a1")]);
        result.Groups[0].Decision = GroupDecision.KeepSeparate;
        list.Record(result);

        var other = new MergeEngine().Merge([C("u1", "Pat Green", "f@x.example", "a2"), C("u2", "Sam Green", "f@x.example", "a2")]);

        Assert.Equal(0, list.Apply(other));
    }

    [Fact]
    public void Memory_only_list_never_touches_the_disk()
    {
        var result = new MergeEngine().Merge(Sample());
        Group(result, "u1").Decision = GroupDecision.KeepSeparate;

        new NotDuplicates().Record(result);

        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void The_file_holds_identifiers_only_no_names_or_emails()
    {
        var result = new MergeEngine().Merge(Sample());
        Group(result, "u1").Decision = GroupDecision.KeepSeparate;

        new NotDuplicates(FilePath).Record(result);

        var text = File.ReadAllText(FilePath);
        Assert.Contains("u1", text);
        Assert.DoesNotContain("Green", text);
        Assert.DoesNotContain("x.example", text);
    }

    [Fact]
    public void An_unreadable_file_is_set_aside_and_an_empty_list_is_used()
    {
        File.WriteAllText(FilePath, "{ this is not json");

        var list = NotDuplicates.Load(FilePath);

        Assert.Equal(0, list.Count);
        Assert.False(File.Exists(FilePath));
        Assert.Single(Directory.GetFiles(_dir, "not-duplicates.json.unreadable-*"));
    }

    [Fact]
    public void Missing_file_gives_an_empty_list()
    {
        Assert.Equal(0, NotDuplicates.Load(FilePath).Count);
    }
}
