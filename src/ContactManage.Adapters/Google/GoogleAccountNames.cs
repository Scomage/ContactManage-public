using System.Text.Json;

namespace ContactManage.Adapters.Google;

/// <summary>
/// The name each signed-in Google account is shown under ("Home Gmail"), kept next to the app's other data. Only display names and
/// email addresses are stored here, never tokens: those live in the credential vault. An account without a name is shown by its email address.
/// </summary>
public sealed class GoogleAccountNames(string? path = null)
{
    private readonly Dictionary<string, string> _names = Load(path);

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ContactManage", "google-accounts.json");

    /// <summary>The names already in use, so the next account is not given the same one.</summary>
    public IReadOnlyCollection<string> Names => _names.Values;

    public string? Get(string email) => _names.GetValueOrDefault(email.ToLowerInvariant());

    public string DisplayFor(string email) => Get(email) ?? email;

    public void Set(string email, string name)
    {
        _names[email.ToLowerInvariant()] = name.Trim();
        Save();
    }

    public void Clear()
    {
        _names.Clear();
        if (path is not null && File.Exists(path)) File.Delete(path);
    }

    private static Dictionary<string, string> Load(string? path)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (path is null || !File.Exists(path)) return names;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var p in doc.RootElement.GetProperty("accounts").EnumerateObject())
                if (p.Value.GetString() is { Length: > 0 } name) names[p.Name] = name;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            names.Clear(); // a damaged names file only means accounts show by email until they are named again
        }
        return names;
    }

    private void Save()
    {
        if (path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { accounts = _names }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
