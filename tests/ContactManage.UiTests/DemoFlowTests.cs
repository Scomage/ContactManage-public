using Xunit;
using static ContactManage.UiTests.DemoApp;

namespace ContactManage.UiTests;

/// <summary>
/// End-to-end walk through the real windows using the demo data.
/// Demo folders: Work "Contacts" (7 contacts, one duplicate pair), "Team contacts" (3), Personal "Contacts" (4).
/// All three together: 14 contacts, 6 duplicate sets (2 merge cleanly, 2 merge with conflicts, 2 need a decision).
/// </summary>
[Collection("UI")]
[Trait("Category", "UI")]
public class DemoFlowTests
{
    private static void Tick(DemoApp app, params int[] folders)
    {
        var boxes = FolderChecks(app.Main);
        foreach (var i in folders) boxes[i].IsChecked = true;
    }

    [Fact]
    public void Demo_mode_lists_the_sample_folders_and_next_needs_a_selection()
    {
        using var app = DemoApp.Start();
        var main = app.Main;

        Assert.Equal(3, FolderChecks(main).Count);
        WaitForText(ById(main, "StatusText"), t => t.StartsWith("0 of 3 selected"), "the folder count");
        Assert.False(ButtonById(main, "NextButton").IsEnabled);
        Assert.True(DemoApp.RadioById(main, "DemoModeRadio").IsChecked);
        Assert.False(ButtonById(main, "RestoreBackupButton").IsEnabled);   // demo makes no backups

        Tick(app, 0);

        WaitForText(ById(main, "StatusText"), t => t.StartsWith("1 of 3 selected, 7 contacts"), "the selection count");
        Assert.True(ButtonById(main, "NextButton").IsEnabled);
    }

    [Fact]
    public void One_folder_can_be_cleaned_of_its_own_duplicates_and_replaced()
    {
        using var app = DemoApp.Start();
        var main = app.Main;
        Tick(app, 0);

        ButtonById(main, "NextButton").Invoke();
        var review = Dialog(main, "Review duplicates");

        Assert.Contains("(demo data)", review.Title);
        Assert.Equal(["Erin Walsh (Will merge)"], GroupNames(review));

        ButtonById(review, "FinishButton").Invoke();
        var destination = Dialog(main, "Save the merged contacts");
        ButtonById(destination, "SaveButton").Invoke();

        var confirm = MessageBoxContaining(main, "Replace the contents of these folders");
        Assert.Contains("Demo mode: nothing real is changed.", MessageText(confirm));
        ButtonNamed(confirm, "Yes").Invoke();

        var done = MessageBoxContaining(main, "added");
        var text = MessageText(done);
        Assert.Contains("Contacts: added 1, removed 2, left alone 5. The server now has 6.", text);
        Assert.Contains("1 duplicate set merged, 0 kept separate.", text);
    }

    [Fact]
    public void A_set_of_obsolete_contacts_can_be_removed_entirely()
    {
        using var app = DemoApp.Start();
        var main = app.Main;
        Tick(app, 0);

        ButtonById(main, "NextButton").Invoke();
        var review = Dialog(main, "Review duplicates");
        SelectGroup(review, "Erin Walsh");
        RadioById(review, "RemoveRadio").IsChecked = true;
        WaitForText(ById(review, "ReviewStatus"), t => t.StartsWith("1 duplicate sets reviewed"), "the removal counting as decided");
        Assert.Equal(["Erin Walsh (Will remove all)"], GroupNames(review));

        ButtonById(review, "FinishButton").Invoke();
        ButtonById(Dialog(main, "Save the merged contacts"), "SaveButton").Invoke();

        var confirm = MessageBoxContaining(main, "Replace the contents of these folders");
        Assert.Contains("You marked 2 contacts as obsolete", MessageText(confirm));
        ButtonNamed(confirm, "Yes").Invoke();

        var text = MessageText(MessageBoxContaining(main, "removed"));
        Assert.Contains("Contacts: added 0, removed 2, left alone 5. The server now has 5.", text);
        Assert.Contains("1 removed (2 contacts)", text);
    }

    [Fact]
    public void Resolving_every_set_across_three_folders_creates_a_merged_folder()
    {
        using var app = DemoApp.Start();
        var main = app.Main;
        Tick(app, 0, 1, 2);
        ButtonById(main, "NextButton").Invoke();
        var review = Dialog(main, "Review duplicates");

        Assert.Equal(
        [
            "Alice Brown (Resolve conflicts)", "Bob Carter (Resolve conflicts)", "Dana Lee (Will merge)",
            "Pat Green (Needs decision)", "Chris Young (Needs decision)", "Erin Walsh (Will merge)",
        ], GroupNames(review));
        WaitForText(ById(review, "ReviewStatus"), t => t.StartsWith("2 awaiting a decision, 2 with conflicts to resolve"), "the starting status");

        // Alice differs in company and job title; Bob in notes. Keep both values of each.
        foreach (var name in new[] { "Alice Brown", "Bob Carter" })
        {
            SelectGroup(review, name);
            foreach (var both in Wait(() => FindAll(review, "Keep both"), "the Keep both options"))
                both.IsChecked = true;
        }

        // Pat and Sam share a family phone, and the two Chris Youngs have different emails: different people.
        foreach (var name in new[] { "Pat Green", "Chris Young" })
        {
            SelectGroup(review, name);
            RadioById(review, "KeepSeparateRadio").IsChecked = true;
        }

        WaitForText(ById(review, "ReviewStatus"), t => t.StartsWith("6 duplicate sets reviewed. Ready to finish."), "all sets settled");
        ButtonById(review, "FinishButton").Invoke();

        var destination = Dialog(main, "Save the merged contacts");
        RadioById(destination, "NewRadio").IsChecked = true;
        ButtonById(destination, "SaveButton").Invoke();

        ButtonNamed(MessageBoxContaining(main, "Create a new folder"), "Yes").Invoke();

        var text = MessageText(MessageBoxContaining(main, "added"));
        Assert.Contains("Merged contacts: added 10, removed 0, left alone 0. The server now has 10.", text);
        Assert.Contains("4 duplicate sets merged, 2 kept separate.", text);
    }

    [Fact]
    public void Finishing_early_asks_first_and_leaves_undecided_sets_alone()
    {
        using var app = DemoApp.Start();
        var main = app.Main;
        Tick(app, 0, 1, 2);
        ButtonById(main, "NextButton").Invoke();
        var review = Dialog(main, "Review duplicates");

        ButtonById(review, "FinishButton").Invoke();
        var ask = MessageBoxContaining(review, "still need a decision");
        Assert.Contains("4 duplicate sets still need a decision", MessageText(ask));

        // No: stay in the review.
        ButtonNamed(ask, "No").Invoke();
        Assert.True(ButtonById(review, "FinishButton").IsEnabled);

        // Yes: carry on to the save screen, then back out without saving anything.
        ButtonById(review, "FinishButton").Invoke();
        ButtonNamed(MessageBoxContaining(review, "still need a decision"), "Yes").Invoke();
        var destination = Dialog(main, "Save the merged contacts");
        Assert.Contains("Save 12 merged contacts", destination.FindFirstDescendant(cf => cf.ByAutomationId("Heading"))!.Name);
        ButtonById(destination, "BackButton").Invoke();

        WaitForText(ById(main, "StatusText"), t => t.StartsWith("3 of 3 selected"), "the main window after backing out");
        Assert.True(ButtonById(main, "NextButton").IsEnabled);
    }
}
