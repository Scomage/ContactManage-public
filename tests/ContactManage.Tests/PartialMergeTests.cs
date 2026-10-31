using ContactManage.Core;
using Xunit;

namespace ContactManage.Tests;

public class PartialMergeTests
{
    private static Contact C(string id, string name, string? email = null, string? phone = null, string? company = null)
    {
        var c = new Contact { Id = id, DisplayName = name, Company = company };
        if (email is not null) c.Emails.Add(email);
        if (phone is not null) c.Phones.Add(phone);
        return c;
    }

    /// <summary>
    /// "ok" pair: merges by itself. "conflict" pair: same email, different company. "possible" pair: same name only.
    /// "other": a contact with no duplicate.
    /// </summary>
    private static MergeResult Sample() => new MergeEngine().Merge(
    [
        C("ok1", "Ann Lee", email: "ann@x.example"), C("ok2", "Ann Lee", email: "ann@x.example"),
        C("cf1", "Bo Ray", email: "bo@x.example", company: "One"), C("cf2", "Bo Ray", email: "bo@x.example", company: "Two"),
        C("pn1", "Cy Dee", email: "cy@one.example"), C("pn2", "Cy Dee", email: "cy@two.example"),
        C("other", "Di Fox", email: "di@x.example"),
    ]);

    [Fact]
    public void Partial_list_applies_finished_decisions_and_leaves_the_rest_untouched()
    {
        var result = Sample();

        var partial = result.BuildPartialList();

        // ann merged (2 -> 1); bo and cy pairs untouched (2 each); di untouched.
        Assert.Equal(6, partial.Count);
        Assert.Contains(partial, c => c.SourceIds.SequenceEqual(["ok1", "ok2"]));
        Assert.Equal(["cf1", "cf2", "pn1", "pn2", "other"], partial.Where(c => c.SourceIds.Count == 0).Select(c => c.Id));
        Assert.False(result.IsComplete);
        Assert.Throws<InvalidOperationException>(() => result.BuildFinalList());
    }

    [Fact]
    public void Summary_counts_merged_kept_and_deferred_sets()
    {
        var result = Sample();
        var possible = result.Groups.Single(g => g.NeedsReview);
        possible.Decision = GroupDecision.KeepSeparate;

        var summary = result.Summary;

        Assert.Equal(new MergeSummary(1, 1, 1), summary);   // ann merged, cy kept separate, bo waiting on conflicts
        Assert.Contains("1 left undecided and unchanged", summary.Text);
    }

    [Fact]
    public void A_conflict_resolved_by_the_user_is_applied_in_the_partial_list()
    {
        var result = Sample();
        var conflicted = result.Groups.Single(g => g.Conflicts.Count > 0);
        conflicted.Conflicts[0].Choose(1);

        var partial = result.BuildPartialList();

        var merged = Assert.Single(partial, c => c.SourceIds.SequenceEqual(["cf1", "cf2"]));
        Assert.Equal("Two", merged.Company);
        Assert.Equal(new MergeSummary(2, 0, 1), result.Summary);
    }

    [Fact]
    public void A_conflict_can_be_settled_by_leaving_the_field_blank()
    {
        var result = Sample();
        var conflicted = result.Groups.Single(g => g.Conflicts.Count > 0);
        conflicted.Conflicts[0].Blank();

        Assert.True(conflicted.Conflicts[0].IsResolved);
        var merged = Assert.Single(result.BuildPartialList(), c => c.SourceIds.SequenceEqual(["cf1", "cf2"]));
        Assert.Null(merged.Company);
    }

    [Fact]
    public void A_possible_duplicate_the_user_chooses_to_merge_is_applied()
    {
        var result = Sample();
        var possible = result.Groups.Single(g => g.NeedsReview);
        possible.Decision = GroupDecision.Merge;

        var partial = result.BuildPartialList();

        Assert.Contains(partial, c => c.SourceIds.SequenceEqual(["pn1", "pn2"]));   // no field conflict, so it merges
        Assert.Equal(5, partial.Count);                                              // ann, cy merged; bo pair and di untouched
    }

    [Fact]
    public void A_complete_result_gives_the_same_list_either_way()
    {
        var result = new MergeEngine().Merge([C("a", "Ann Lee", email: "ann@x.example"), C("b", "Ann Lee", email: "ann@x.example")]);

        Assert.Equal(result.BuildFinalList().Count, result.BuildPartialList().Count);
        Assert.Equal(new MergeSummary(1, 0, 0), result.Summary);
        Assert.DoesNotContain("undecided", result.Summary.Text);
    }

    [Fact]
    public void Removing_a_set_drops_every_one_of_its_contacts_and_keeps_the_rest()
    {
        var result = Sample();
        var ok = result.Groups.Single(g => g.Members.Any(m => m.Id == "ok1"));
        ok.Decision = GroupDecision.Remove;                                         // Ann Lee is obsolete: both entries go
        foreach (var g in result.Groups.Where(g => g != ok)) g.Decision = GroupDecision.KeepSeparate;

        var final = result.BuildFinalList();

        Assert.Equal(["cf1", "cf2", "pn1", "pn2", "other"], final.Select(c => c.Id));
        Assert.DoesNotContain(final, c => c.DisplayName == "Ann Lee");
        Assert.Equal(new MergeSummary(0, 2, 0, 1, 2), result.Summary);
        Assert.Contains("1 removed (2 contacts)", result.Summary.Text);
    }

    [Fact]
    public void A_removed_set_needs_no_conflict_resolution_and_counts_as_decided()
    {
        var result = Sample();
        var conflict = result.Groups.Single(g => g.Conflicts.Count > 0);
        conflict.Decision = GroupDecision.Remove;                                   // would have needed a company choice if merged

        Assert.False(conflict.HasUnresolvedConflicts);
        Assert.DoesNotContain(conflict, result.GroupsWithConflicts);
        Assert.DoesNotContain(conflict, result.PendingGroups);
    }

    [Fact]
    public void The_partial_list_also_honors_a_removal_while_other_sets_stay_undecided()
    {
        var result = Sample();
        result.Groups.Single(g => g.Members.Any(m => m.Id == "pn1")).Decision = GroupDecision.Remove;   // the possible duplicates: both obsolete

        var partial = result.BuildPartialList();

        Assert.DoesNotContain(partial, c => c.Id is "pn1" or "pn2");
        Assert.Contains(partial, c => c.Id == "cf1");                               // still undecided: untouched
        Assert.Equal(1, result.Summary.Removed);
        Assert.Equal(2, result.Summary.RemovedContacts);
    }

    [Fact]
    public void Removing_a_single_contact_set_of_one_member_is_worded_in_the_singular()
    {
        Assert.Contains("1 removed (1 contact)", new MergeSummary(0, 0, 0, 1, 1).Text);
        Assert.DoesNotContain("removed", new MergeSummary(1, 0, 0).Text);
    }
}
