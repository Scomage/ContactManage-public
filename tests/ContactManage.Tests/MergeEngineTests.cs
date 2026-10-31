using ContactManage.Core;
using Xunit;

namespace ContactManage.Tests;

public class MergeEngineTests
{
    private static Contact C(string id, string? given = null, string? family = null, string? email = null, string? phone = null,
        string? company = null, string? title = null, string? notes = null, string? display = null)
    {
        var c = new Contact { Id = id, GivenName = given, FamilyName = family, Company = company, JobTitle = title, Notes = notes, DisplayName = display };
        if (email is not null) c.Emails.Add(email);
        if (phone is not null) c.Phones.Add(phone);
        return c;
    }

    private static MergeResult Run(params Contact[] contacts) => new MergeEngine().Merge(contacts);

    [Fact]
    public void Shared_email_merges_automatically()
    {
        var r = Run(C("a", "John", "Smith", email: "john@x.com"), C("b", "John", "Smith", email: "JOHN@x.com"));

        var g = Assert.Single(r.Groups);
        Assert.False(g.NeedsReview);
        Assert.Equal(GroupDecision.Merge, g.Decision);
        Assert.True(r.IsComplete);
        Assert.Single(r.BuildFinalList());
    }

    [Fact]
    public void Shared_phone_in_different_formats_merges()
    {
        var r = Run(C("a", "Ann", "Lee", phone: "(201) 555-0123"), C("b", "Ann", "Lee", phone: "+1 201-555-0123"));

        var g = Assert.Single(r.Groups);
        Assert.Equal(MatchKind.Phone, g.Reasons[0].Kind);
        Assert.Single(r.BuildFinalList());
    }

    [Fact]
    public void Links_are_transitive()
    {
        var a = C("a", "Bob", "Ray", email: "bob@x.com");
        var b = C("b", "Bob", "Ray", email: "bob@x.com", phone: "201-555-0100");
        var c = C("c", "Bob", "Ray", phone: "2015550100");

        var g = Assert.Single(Run(a, b, c).Groups);
        Assert.Equal(3, g.Members.Count);
    }

    [Fact]
    public void Same_name_alone_needs_confirmation_and_is_not_merged_by_default()
    {
        var r = Run(C("a", "Mary", "Jones", email: "mary@home.com"), C("b", "Mary", "Jones", email: "mjones@work.com"));

        var g = Assert.Single(r.Groups);
        Assert.True(g.NeedsReview);
        Assert.Equal(GroupDecision.Pending, g.Decision);
        Assert.False(r.IsComplete);
        Assert.Throws<InvalidOperationException>(() => r.BuildFinalList());
    }

    [Fact]
    public void Confirming_a_possible_duplicate_merges_it()
    {
        var r = Run(C("a", "Mary", "Jones", email: "mary@home.com"), C("b", "Mary", "Jones", email: "mjones@work.com"));
        r.Groups[0].Decision = GroupDecision.Merge;

        var final = Assert.Single(r.BuildFinalList());
        Assert.Equal(["mary@home.com", "mjones@work.com"], final.Emails);
    }

    [Fact]
    public void Rejecting_a_possible_duplicate_keeps_both_contacts()
    {
        var r = Run(C("a", "Mary", "Jones", email: "mary@home.com"), C("b", "Mary", "Jones", email: "mjones@work.com"));
        r.Groups[0].Decision = GroupDecision.KeepSeparate;

        Assert.Equal(2, r.BuildFinalList().Count);
    }

    [Fact]
    public void Single_word_names_do_not_link()
    {
        var r = Run(C("a", display: "Mom", email: "a@x.com"), C("b", display: "Mom", email: "b@x.com"));

        Assert.Empty(r.Groups);
        Assert.Equal(2, r.BuildFinalList().Count);
    }

    [Fact]
    public void Shared_phone_with_different_names_needs_review()
    {
        var r = Run(C("a", "John", "Smith", phone: "2015550100"), C("b", "Jane", "Doe", phone: "2015550100"));

        var g = Assert.Single(r.Groups);
        Assert.True(g.NeedsReview);
        Assert.Equal(GroupDecision.Pending, g.Decision);
    }

    [Fact]
    public void Compatible_names_with_shared_email_do_not_need_review()
    {
        var g = Assert.Single(Run(C("a", "John", "Smith", email: "j@x.com"), C("b", display: "Smith, John A.", email: "j@x.com")).Groups);
        Assert.False(g.NeedsReview);
    }

    [Fact]
    public void Chained_different_people_are_flagged()
    {
        // A and C share nothing, but both link to B through different channels.
        var r = Run(
            C("a", "John", "Smith", email: "j@x.com"),
            C("b", "John", "Smith", email: "j@x.com", phone: "2015550100"),
            C("c", "Jane", "Doe", phone: "2015550100"));

        Assert.True(Assert.Single(r.Groups).NeedsReview);
    }

    [Fact]
    public void Identical_values_are_not_conflicts_and_blanks_are_filled()
    {
        var r = Run(
            C("a", "Ann", "Lee", email: "a@x.com", company: "Acme Inc.", title: null),
            C("b", "Ann", "Lee", email: "a@x.com", company: "ACME Inc", title: "CEO"));

        var g = Assert.Single(r.Groups);
        Assert.Empty(g.Conflicts);
        var merged = Assert.Single(r.BuildFinalList());
        Assert.Equal("Acme Inc.", merged.Company);
        Assert.Equal("CEO", merged.JobTitle);
    }

    [Fact]
    public void Different_values_create_a_conflict_that_blocks_the_final_list()
    {
        var r = Run(
            C("a", "Ann", "Lee", email: "a@x.com", title: "CEO"),
            C("b", "Ann", "Lee", email: "a@x.com", title: "Founder"));

        var conflict = Assert.Single(Assert.Single(r.Groups).Conflicts);
        Assert.Equal(ContactField.JobTitle, conflict.Field);
        Assert.Equal(["CEO", "Founder"], conflict.Options.Select(o => o.Value));
        Assert.False(r.IsComplete);
        Assert.Throws<InvalidOperationException>(() => r.BuildFinalList());
    }

    [Fact]
    public void Conflict_can_be_resolved_by_choice_keep_both_or_custom()
    {
        FieldConflict Fresh(out MergeResult r)
        {
            r = Run(C("a", "Ann", "Lee", email: "a@x.com", title: "CEO"), C("b", "Ann", "Lee", email: "a@x.com", title: "Founder"));
            return r.Groups[0].Conflicts[0];
        }

        var c1 = Fresh(out var r1); c1.Choose(1);
        Assert.Equal("Founder", Assert.Single(r1.BuildFinalList()).JobTitle);

        var c2 = Fresh(out var r2); c2.KeepBoth();
        Assert.Equal("CEO; Founder", Assert.Single(r2.BuildFinalList()).JobTitle);

        var c3 = Fresh(out var r3); c3.Custom("  Chair ");
        Assert.Equal("Chair", Assert.Single(r3.BuildFinalList()).JobTitle);
    }

    [Fact]
    public void Notes_keep_both_uses_a_blank_line()
    {
        var r = Run(C("a", "Ann", "Lee", email: "a@x.com", notes: "met at expo"), C("b", "Ann", "Lee", email: "a@x.com", notes: "owes lunch"));
        r.Groups[0].Conflicts[0].KeepBoth();

        Assert.Equal("met at expo\n\nowes lunch", Assert.Single(r.BuildFinalList()).Notes);
    }

    [Fact]
    public void Multi_value_fields_are_unioned_without_duplicates_and_keep_original_text()
    {
        var a = C("a", "Ann", "Lee", email: "A@x.com", phone: "(201) 555-0123");
        var b = C("b", "Ann", "Lee", email: "a@x.com", phone: "201-555-0123");
        b.Emails.Add("ann@home.com");
        b.Phones.Add("212-555-0199");
        a.Addresses.Add("1 Main St, Newark NJ");
        b.Addresses.Add("1 main st newark nj");

        var merged = Assert.Single(Run(a, b).BuildFinalList());

        Assert.Equal(["A@x.com", "ann@home.com"], merged.Emails);
        Assert.Equal(["(201) 555-0123", "212-555-0199"], merged.Phones);
        Assert.Single(merged.Addresses);
        Assert.Equal(["a", "b"], merged.SourceIds);
    }

    [Fact]
    public void Unparseable_values_are_kept_not_dropped()
    {
        var a = C("a", "Ann", "Lee", email: "a@x.com");
        var b = C("b", "Ann", "Lee", email: "a@x.com");
        b.Phones.Add("ext 5");

        Assert.Equal(["ext 5"], Assert.Single(Run(a, b).BuildFinalList()).Phones);
    }

    [Fact]
    public void Unmatched_contacts_pass_through_and_input_order_is_kept()
    {
        var x = C("x", "Zed", "One", email: "z@x.com");
        var a = C("a", "Ann", "Lee", email: "a@x.com");
        var b = C("b", "Ann", "Lee", email: "a@x.com");
        var y = C("y", "Yan", "Two", email: "y@x.com");

        var final = Run(x, a, b, y).BuildFinalList();

        Assert.Equal(3, final.Count);
        Assert.Same(x, final[0]);
        Assert.Equal(["a", "b"], final[1].SourceIds);
        Assert.Same(y, final[2]);
    }

    [Fact]
    public void Finds_duplicates_inside_a_single_folder()
    {
        var a = new Contact { SourceFolderId = "f1", DisplayName = "Erin Walsh" };
        a.Emails.Add("erin@walsh.example");
        var b = new Contact { SourceFolderId = "f1", DisplayName = "Erin Walsh", Company = "Walsh Design" };
        b.Emails.Add("ERIN@walsh.example");
        var c = new Contact { SourceFolderId = "f1", DisplayName = "Someone Else" };

        var result = new MergeEngine().Merge([a, b, c]);

        var group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Members.Count);
        Assert.Equal(2, result.BuildFinalList().Count);
    }

    [Fact]
    public void Empty_input_gives_an_empty_complete_result()
    {
        var r = Run();
        Assert.True(r.IsComplete);
        Assert.Empty(r.BuildFinalList());
    }

    [Fact]
    public void A_large_input_with_many_duplicate_sets_is_grouped_correctly()
    {
        // 1,500 sets of 4 contacts sharing an email, plus 3,000 contacts with nothing in common: sets must not be mixed up.
        var contacts = new List<Contact>();
        for (var s = 0; s < 1500; s++)
            for (var k = 0; k < 4; k++)
                contacts.Add(C($"d{s}-{k}", "Person", $"Set{s}", email: $"set{s}@x.example"));
        for (var i = 0; i < 3000; i++)
            contacts.Add(C($"u{i}", "Unique", $"N{i}", email: $"u{i}@x.example"));

        var r = Run(contacts.ToArray());

        Assert.Equal(1500, r.Groups.Count);
        Assert.All(r.Groups, g => Assert.Equal(4, g.Members.Count));
        Assert.All(r.Groups, g => Assert.Single(g.Reasons));
        Assert.Equal(4500, r.BuildFinalList().Count);
    }
}
