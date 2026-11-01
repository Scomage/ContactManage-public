using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContactManage.Core;

/// <summary>Where the contacts come from in this run of the app.</summary>
public enum SourceMode
{
    /// <summary>Online accounts only (Gmail today): no mail program is read, so none needs to be installed.</summary>
    Online,
    /// <summary>The mail program installed on this PC (IceWarp Desktop Client today), plus any online account.</summary>
    MailProgram,
    Demo,
}

/// <summary>What the user chose to happen at start-up.</summary>
public enum StartPreference
{
    /// <summary>Show the "Where are your contacts?" screen.</summary>
    Ask,
    Online,
    MailProgram,
}

/// <summary>Per-user choices kept between runs in <c>%LOCALAPPDATA%\ContactManage\settings.json</c>. Holds no contact data or credentials.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public StartPreference Start { get; set; } = StartPreference.Ask;

    private string _phoneRegion = PhoneRegions.Default;

    /// <summary>Country (two-letter code) that a phone number without a country code is read as when contacts are compared. US unless the user changes it.</summary>
    public string PhoneRegion
    {
        get => _phoneRegion;
        set => _phoneRegion = PhoneRegions.Normalize(value);
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ContactManage", "settings.json");

    /// <summary>Reads the settings; a missing or damaged file gives the defaults, never an error.</summary>
    public static AppSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings() : new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }
}

/// <summary>Whether the start screen is needed, and if not, which source to use.</summary>
public static class StartPlan
{
    public sealed record Step(bool ShowScreen, SourceMode Mode);

    /// <param name="mailProgramFound">A supported mail program is installed and has a profile.</param>
    /// <param name="demoRequested">The app was started with --demo.</param>
    /// <param name="forceScreen">Always show the screen (the --start-screen switch, used by the screen tests).</param>
    public static Step Decide(StartPreference preference, bool mailProgramFound, bool demoRequested, bool forceScreen = false)
    {
        if (demoRequested) return new Step(false, SourceMode.Demo);
        if (forceScreen) return new Step(true, SourceMode.Online);
        // With no mail program on the PC there is nothing to choose between.
        if (!mailProgramFound) return new Step(false, SourceMode.Online);
        return preference switch
        {
            StartPreference.Online => new Step(false, SourceMode.Online),
            StartPreference.MailProgram => new Step(false, SourceMode.MailProgram),
            _ => new Step(true, SourceMode.MailProgram),
        };
    }

    /// <summary>The preference to store after the user chose <paramref name="mode"/> on the screen. The demo is never remembered.</summary>
    public static StartPreference After(SourceMode mode, bool remember, StartPreference current) =>
        mode == SourceMode.Demo ? current
        : !remember ? StartPreference.Ask
        : mode == SourceMode.Online ? StartPreference.Online : StartPreference.MailProgram;
}
