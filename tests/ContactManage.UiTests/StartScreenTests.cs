using Xunit;
using static ContactManage.UiTests.DemoApp;

namespace ContactManage.UiTests;

/// <summary>
/// The first screen, "Where are your contacts?". It is forced with --start-screen and given its own settings file, so the tests
/// neither depend on what is installed on this PC nor touch the real user's choices. Only the demo tile is clicked: the online
/// tile would reach for the real Google sign-ins on this PC.
/// </summary>
[Collection("UI")]
[Trait("Category", "UI")]
public class StartScreenTests : IDisposable
{
    private readonly string _settings = Path.Combine(Path.GetTempPath(), "cm-ui-" + Guid.NewGuid().ToString("N"), "settings.json");

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_settings)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private DemoApp StartWithScreen() => DemoApp.Start($"--start-screen --settings=\"{_settings}\"");

    [Fact]
    public void The_screen_offers_online_accounts_a_mail_program_and_the_demo()
    {
        using var app = StartWithScreen();

        var screen = Dialog(app.Main, "Where are your contacts?");

        Assert.True(ButtonById(screen, "OnlineTile").IsEnabled);
        Assert.True(ButtonById(screen, "DemoTile").IsEnabled);
        Assert.NotNull(ById(screen, "MailTile"));   // enabled only when a supported mail program is installed on this PC
        Assert.NotNull(ById(screen, "RememberCheck"));
    }

    [Fact]
    public void Choosing_the_demo_opens_the_sample_folders_and_remembers_nothing_by_default()
    {
        using var app = StartWithScreen();
        var screen = Dialog(app.Main, "Where are your contacts?");

        ButtonById(screen, "DemoTile").Invoke();

        WaitForText(ById(app.Main, "StatusText"), t => t.StartsWith("0 of 3 selected"), "the demo folders");
        Assert.True(RadioById(app.Main, "DemoModeRadio").IsChecked);
        Assert.False(File.Exists(_settings));
    }

    [Fact]
    public void Switch_source_brings_the_screen_back()
    {
        using var app = DemoApp.Start();   // --demo: no screen at start

        ButtonById(app.Main, "SwitchSourceButton").Invoke();

        var screen = Dialog(app.Main, "Where are your contacts?");
        Assert.True(ButtonById(screen, "DemoTile").IsEnabled);
    }
}
