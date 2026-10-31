using ContactManage.Core;
using Xunit;

namespace ContactManage.Tests;

public class StartPlanTests
{
    [Fact]
    public void Without_a_mail_program_there_is_nothing_to_choose_so_it_goes_straight_to_online_accounts()
    {
        foreach (var preference in Enum.GetValues<StartPreference>())
            Assert.Equal(new StartPlan.Step(false, SourceMode.Online), StartPlan.Decide(preference, mailProgramFound: false, demoRequested: false));
    }

    [Fact]
    public void With_a_mail_program_and_no_saved_choice_the_screen_is_shown()
    {
        var step = StartPlan.Decide(StartPreference.Ask, mailProgramFound: true, demoRequested: false);

        Assert.True(step.ShowScreen);
    }

    [Theory]
    [InlineData(StartPreference.Online, SourceMode.Online)]
    [InlineData(StartPreference.MailProgram, SourceMode.MailProgram)]
    public void A_saved_choice_skips_the_screen(StartPreference preference, SourceMode expected) =>
        Assert.Equal(new StartPlan.Step(false, expected), StartPlan.Decide(preference, mailProgramFound: true, demoRequested: false));

    [Fact]
    public void A_saved_mail_program_choice_falls_back_to_online_when_the_program_is_gone()
    {
        var step = StartPlan.Decide(StartPreference.MailProgram, mailProgramFound: false, demoRequested: false);

        Assert.Equal(new StartPlan.Step(false, SourceMode.Online), step);
    }

    [Fact]
    public void The_demo_switch_wins_over_everything()
    {
        Assert.Equal(new StartPlan.Step(false, SourceMode.Demo), StartPlan.Decide(StartPreference.Online, true, demoRequested: true, forceScreen: true));
    }

    [Fact]
    public void The_screen_can_be_forced_for_the_screen_tests()
    {
        Assert.True(StartPlan.Decide(StartPreference.Online, mailProgramFound: false, demoRequested: false, forceScreen: true).ShowScreen);
    }

    [Theory]
    [InlineData(SourceMode.Online, true, StartPreference.Ask, StartPreference.Online)]
    [InlineData(SourceMode.MailProgram, true, StartPreference.Ask, StartPreference.MailProgram)]
    [InlineData(SourceMode.Online, false, StartPreference.MailProgram, StartPreference.Ask)]   // unticking brings the screen back
    [InlineData(SourceMode.Demo, true, StartPreference.Online, StartPreference.Online)]        // the demo is never remembered
    [InlineData(SourceMode.Demo, false, StartPreference.Ask, StartPreference.Ask)]
    public void What_is_remembered_after_a_choice(SourceMode mode, bool remember, StartPreference current, StartPreference expected) =>
        Assert.Equal(expected, StartPlan.After(mode, remember, current));
}

public class AppSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cm-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void A_missing_file_gives_the_defaults()
    {
        Assert.Equal(StartPreference.Ask, AppSettings.Load(Path.Combine(_dir, "settings.json")).Start);
    }

    [Fact]
    public void The_choice_survives_a_save_and_load_and_is_written_as_readable_text()
    {
        var path = Path.Combine(_dir, "sub", "settings.json");

        new AppSettings { Start = StartPreference.Online }.Save(path);

        Assert.Equal(StartPreference.Online, AppSettings.Load(path).Start);
        Assert.Contains("\"Online\"", File.ReadAllText(path));
    }

    [Fact]
    public void A_damaged_file_gives_the_defaults_instead_of_an_error()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ not json");

        Assert.Equal(StartPreference.Ask, AppSettings.Load(path).Start);
    }

    [Fact]
    public void The_online_source_lists_no_folders_and_says_nothing_has_to_be_installed()
    {
        var source = new OnlineContactSource();

        Assert.True(source.Detect().IsSupported);
        Assert.Empty(source.ListFolders());
    }
}
