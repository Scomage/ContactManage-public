using FlaUI.Core.AutomationElements;
using Xunit;
using static ContactManage.UiTests.DemoApp;

namespace ContactManage.UiTests;

/// <summary>The phone region box: US to start with, a saved choice comes back, and a new choice is written to the settings file.</summary>
[Collection("UI")]
[Trait("Category", "UI")]
public class PhoneRegionTests : IDisposable
{
    private readonly string _settings = Path.Combine(Path.GetTempPath(), "cm-ui-" + Guid.NewGuid().ToString("N"), "settings.json");

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_settings)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private DemoApp Start() => DemoApp.Start($"--demo --settings=\"{_settings}\"");

    [Fact]
    public void The_region_starts_as_the_US_and_a_new_choice_is_remembered()
    {
        using var app = Start();
        var box = ById(app.Main, "PhoneRegionBox").AsComboBox();
        WaitForText(box, _ => box.SelectedItem?.Name == "United States (+1)", "the US as the starting region");
        Assert.False(File.Exists(_settings));   // showing the default is not a choice

        box.Select("Germany (+49)");

        Wait(() => File.Exists(_settings) ? _settings : null, "the settings file");
        Assert.Contains("\"PhoneRegion\": \"DE\"", File.ReadAllText(_settings));
    }

    [Fact]
    public void A_saved_region_is_shown_again_on_the_next_start()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settings)!);
        File.WriteAllText(_settings, "{ \"PhoneRegion\": \"GB\" }");

        using var app = Start();

        var box = ById(app.Main, "PhoneRegionBox").AsComboBox();
        WaitForText(box, _ => box.SelectedItem?.Name == "United Kingdom (+44)", "the saved region");
    }
}
