using ContactManage.Core;
using Xunit;

namespace ContactManage.Tests;

public class ChangeLogTests
{
    private static Contact C(string id, string name, string folder, string? email = null, string? company = null)
    {
        var c = new Contact { Id = id, DisplayName = name, Company = company, SourceFolderId = folder };
        if (email is not null) c.Emails.Add(email);
        return c;
    }

    // ann: merges by itself. bo: same email, different company. cy: same name only. di: no duplicate.
    private static MergeResult Sample() => new MergeEngine().Merge(
    [
        C("a1", "Ann Lee", "acct:/A", "ann@x.example"), C("a2", "Ann Lee", "acct:/B", "ann@x.example"),
        C("b1", "Bo Ray", "acct:/A", "bo@x.example", "One"), C("b2", "Bo Ray", "acct:/A", "bo@x.example", "Two"),
        C("c1", "Cy Dee", "acct:/A", "cy@one.example"), C("c2", "Cy Dee", "acct:/B", "cy@two.example"),
        C("d1", "Di Fox", "acct:/A", "di@x.example"),
    ]);

    private static ChangeLogContext Ctx(params string[] replaced) => new(new DateTime(2026, 5, 6, 7, 8, 9), "Replaced the contents of Work - A",
        ["Work - A", "Work - B"], id => id is null ? "" : "Work - " + id[(id.IndexOf(':') + 2)..], replaced.ToHashSet());

    [Fact]
    public void Every_kind_of_outcome_is_listed_with_its_contacts()
    {
        var result = Sample();
        result.Groups.Single(g => g.Members.Any(m => m.Id == "b1")).Conflicts.Single().Blank();
        result.Groups.Single(g => g.Members.Any(m => m.Id == "c1")).Decision = GroupDecision.Remove;

        var log = ChangeLog.Build(result, Ctx("acct:/A"));

        Assert.Contains("When:         2026-05-06 07:08:09", log);
        Assert.Contains("Folders read: Work - A; Work - B", log);
        Assert.Contains("== MERGED: 2 ==", log);
        Assert.Contains("Ann Lee: 2 contacts merged into one", log);
        Assert.Contains("- Ann Lee | ann@x.example | in Work - A", log);
        Assert.Contains("- Ann Lee | ann@x.example | in Work - B", log);
        Assert.Contains("Company differed (\"One\" / \"Two\"): left blank", log);
        Assert.Contains("== REMOVED (you marked these obsolete): 1 ==", log);
        Assert.Contains("Cy Dee: all 2 contacts removed", log);
        Assert.Contains("== KEPT SEPARATE (you decided these are different people): 0 ==", log);
        Assert.Contains("== LEFT UNDECIDED (unchanged): 0 ==", log);
        Assert.Contains("== RETAINED (no duplicates found): 1 ==", log);
        Assert.Contains("- Di Fox | di@x.example | in Work - A", log);
    }

    [Fact]
    public void Contacts_in_folders_that_were_not_replaced_are_said_to_be_still_there()
    {
        var result = Sample();
        result.Groups.Single(g => g.Members.Any(m => m.Id == "c1")).Decision = GroupDecision.Remove;

        var log = ChangeLog.Build(result, Ctx("acct:/A"));

        Assert.Contains("1 of 2 originals (deleted) were in replaced folders; the rest are in folders that were not replaced and are still there.", log);
        Assert.Contains("Only folders that were replaced were changed", log);
    }

    [Fact]
    public void Saving_to_a_file_says_the_folders_were_not_changed()
    {
        var log = ChangeLog.Build(Sample(), Ctx());

        Assert.Contains("The folders that were read were not changed.", log);
        Assert.Contains("The originals are still in their folders.", log);
    }

    [Fact]
    public void Sets_nobody_decided_are_listed_as_left_alone_and_typed_or_chosen_values_are_shown()
    {
        var result = Sample();
        var bo = result.Groups.Single(g => g.Members.Any(m => m.Id == "b1"));

        var undecided = ChangeLog.Build(result, Ctx());
        Assert.Contains("Bo Ray: 2 contacts left as they were (marked to merge, but fields that differ were not settled)", undecided);
        Assert.Contains("Cy Dee: 2 contacts left as they were (no decision)", undecided);

        bo.Conflicts.Single().Custom("Three");
        Assert.Contains("Company differed (\"One\" / \"Two\"): you typed \"Three\"", ChangeLog.Build(result, Ctx()));
        bo.Conflicts.Single().Choose(1);
        Assert.Contains("kept \"Two\"", ChangeLog.Build(result, Ctx()));
        bo.Conflicts.Single().KeepBoth();
        Assert.Contains("kept both", ChangeLog.Build(result, Ctx()));
    }
}
