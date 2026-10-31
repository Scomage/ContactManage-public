using System.Text.Json;

namespace ContactManage.Core;

/// <summary>
/// The pairs of contacts the user has said are different people, so the next run does not ask again.
/// Contacts are identified by account and vCard UID (or server address), never by name, so the file holds no personal data.
/// Contacts without such an identity (for example freshly merged ones) cannot be remembered.
/// </summary>
public sealed class NotDuplicates
{
    private const char Separator = '\u0001';

    private readonly HashSet<string> _pairs = new(StringComparer.Ordinal);
    private readonly string? _path;

    /// <param name="path">Where to keep the list. Null keeps it in memory only (demo mode).</param>
    public NotDuplicates(string? path = null) => _path = path;

    public int Count => _pairs.Count;

    /// <summary>Loads the list. A missing file gives an empty list; an unreadable one is set aside so it is never silently overwritten.</summary>
    public static NotDuplicates Load(string path)
    {
        var list = new NotDuplicates(path);
        if (!File.Exists(path)) return list;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var pair in doc.RootElement.GetProperty("pairs").EnumerateArray())
                list._pairs.Add(PairKey(pair[0].GetString()!, pair[1].GetString()!));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException or NullReferenceException)
        {
            list._pairs.Clear();
            try { File.Move(path, path + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); } catch (IOException) { }
        }
        return list;
    }

    public void Save()
    {
        if (_path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var pairs = _pairs.Order(StringComparer.Ordinal).Select(p => p.Split(Separator)).ToList();
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new { version = 1, pairs }, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, overwrite: true);
    }

    public bool IsKnown(Contact a, Contact b)
    {
        var (ka, kb) = (KeyOf(a), KeyOf(b));
        return ka is not null && kb is not null && _pairs.Contains(PairKey(ka, kb));
    }

    /// <summary>
    /// Marks every group whose members are all known to be different people as kept separate, and flags it as remembered.
    /// Returns how many groups were settled this way.
    /// </summary>
    public int Apply(MergeResult result)
    {
        var settled = 0;
        foreach (var g in result.Groups)
        {
            if (!AllPairsKnown(g.Members)) continue;
            g.Decision = GroupDecision.KeepSeparate;
            g.Remembered = true;
            settled++;
        }
        return settled;
    }

    /// <summary>
    /// Updates the list from the user's decisions and saves it: sets kept separate are remembered, and sets that were
    /// remembered but the user has now chosen to merge are forgotten. Undecided sets change nothing.
    /// </summary>
    public void Record(MergeResult result)
    {
        foreach (var g in result.Groups)
        {
            if (g.Decision == GroupDecision.KeepSeparate) AddAllPairs(g.Members);
            else if (g.Remembered && g.Decision == GroupDecision.Merge) RemoveAllPairs(g.Members);
        }
        Save();
    }

    private bool AllPairsKnown(IReadOnlyList<Contact> members)
    {
        for (var i = 0; i < members.Count; i++)
            for (var j = i + 1; j < members.Count; j++)
                if (!IsKnown(members[i], members[j])) return false;
        return members.Count >= 2;
    }

    private void AddAllPairs(IReadOnlyList<Contact> members) => ForEachPair(members, k => _pairs.Add(k));
    private void RemoveAllPairs(IReadOnlyList<Contact> members) => ForEachPair(members, k => _pairs.Remove(k));

    private static void ForEachPair(IReadOnlyList<Contact> members, Action<string> action)
    {
        for (var i = 0; i < members.Count; i++)
            for (var j = i + 1; j < members.Count; j++)
            {
                var (a, b) = (KeyOf(members[i]), KeyOf(members[j]));
                if (a is not null && b is not null) action(PairKey(a, b));
            }
    }

    private static string PairKey(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? a + Separator + b : b + Separator + a;

    /// <summary>Account plus UID (or server address). Null when the contact has no lasting identity.</summary>
    internal static string? KeyOf(Contact c)
    {
        var id = c.Uid ?? c.RemoteId;
        if (string.IsNullOrWhiteSpace(id)) return null;
        var account = c.SourceFolderId is { } f && f.Contains(':') ? ContactFolder.AccountKeyOf(f) : "";
        return account + "|" + id;
    }
}
