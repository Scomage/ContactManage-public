namespace ContactManage.Core;

internal static class ContactMerger
{
    public static List<FieldConflict> FindConflicts(IReadOnlyList<Contact> members, Normalizer normalizer)
    {
        var conflicts = new List<FieldConflict>();
        foreach (var field in Enum.GetValues<ContactField>())
        {
            var options = new List<(string Key, string Value, List<string> Ids)>();
            foreach (var m in members)
            {
                var value = m.Get(field)?.Trim();
                if (string.IsNullOrEmpty(value)) continue;
                var key = normalizer.Loose(value);
                var existing = options.FindIndex(o => o.Key == key);
                if (existing >= 0) options[existing].Ids.Add(m.Id);
                else options.Add((key, value, [m.Id]));
            }
            if (options.Count > 1)
                conflicts.Add(new FieldConflict(field, options.Select(o => new ConflictOption(o.Value, o.Ids)).ToList()));
        }
        return conflicts;
    }

    public static Contact Build(MergeGroup group, Normalizer normalizer)
    {
        var merged = new Contact();
        merged.SourceIds.AddRange(group.Members.Select(m => m.Id));
        merged.Sources.AddRange(group.Members.Select(m => new ContactSource(m.SourceFolderId, m.RemoteId, m.RemoteEtag, m.Uid)));

        foreach (var field in Enum.GetValues<ContactField>())
        {
            var conflict = group.Conflicts.FirstOrDefault(c => c.Field == field);
            if (conflict is not null)
            {
                if (!conflict.IsResolved)
                    throw new InvalidOperationException($"The {field} conflict is unresolved.");
                merged.Set(field, conflict.ResolvedValue);
            }
            else
            {
                merged.Set(field, group.Members.Select(m => m.Get(field)?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v)));
            }
        }

        // Values that cannot be normalized fall back to their trimmed text, so they are kept, not dropped.
        string EmailKey(string v) => normalizer.Email(v) ?? v.Trim().ToLowerInvariant();
        string PhoneKey(string v) => normalizer.Phone(v) ?? v.Trim();

        merged.Emails.AddRange(Union(group.Members.SelectMany(m => m.Emails), EmailKey));
        merged.Phones.AddRange(Union(group.Members.SelectMany(m => m.Phones), PhoneKey));
        merged.Addresses.AddRange(Union(group.Members.SelectMany(m => m.Addresses), normalizer.Loose));

        merged.NamePrefix = First(group.Members, m => m.NamePrefix);
        merged.MiddleName = First(group.Members, m => m.MiddleName);
        merged.NameSuffix = First(group.Members, m => m.NameSuffix);
        merged.Department = First(group.Members, m => m.Department);

        CopyLabels(merged, merged.Emails, group.Members, m => m.Emails, EmailKey);
        CopyLabels(merged, merged.Phones, group.Members, m => m.Phones, PhoneKey);
        CopyLabels(merged, merged.Addresses, group.Members, m => m.Addresses, normalizer.Loose);
        MergeExtra(merged, group.Members);

        // Labels only accumulate: the merged contact belongs to every label any member had (same name in any case counts once).
        merged.Groups.AddRange(Union(group.Members.SelectMany(m => m.Groups), normalizer.Loose));
        return merged;
    }

    private static string? First(IEnumerable<Contact> members, Func<Contact, string?> get) =>
        members.Select(m => get(m)?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));

    /// <summary>A merged value keeps the labels (WORK, HOME, CELL ...) of whichever source contact had it.</summary>
    private static void CopyLabels(Contact merged, List<string> mergedValues, IReadOnlyList<Contact> members,
        Func<Contact, List<string>> values, Func<string, string> key)
    {
        var textByKey = new Dictionary<string, string>();
        foreach (var v in mergedValues) textByKey.TryAdd(key(v), v);
        foreach (var m in members)
            foreach (var v in values(m))
                if (m.Labels.TryGetValue(v, out var label) && textByKey.TryGetValue(key(v), out var text))
                    merged.Labels.TryAdd(text, label);
    }

    // vCard properties that may appear once; a second value would make an invalid card.
    private static readonly HashSet<string> SingleValueExtras = new(StringComparer.OrdinalIgnoreCase)
        { "BDAY", "ANNIVERSARY", "PHOTO", "KIND", "GENDER", "NICKNAME" };

    private static void MergeExtra(Contact merged, IReadOnlyList<Contact> members)
    {
        var seen = new HashSet<string>();
        var singles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in members.SelectMany(m => m.Extra))
        {
            var name = VCard.PropertyName(line);
            if (SingleValueExtras.Contains(name) && !singles.Add(name)) continue;
            if (seen.Add(line)) merged.Extra.Add(line);
        }
    }

    /// <summary>Distinct by key, keeping the first-seen text. Values that cannot be normalized are kept, never dropped.</summary>
    private static IEnumerable<string> Union(IEnumerable<string> values, Func<string, string> key)
    {
        var seen = new HashSet<string>();
        foreach (var v in values)
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            if (seen.Add(key(v))) yield return v.Trim();
        }
    }
}
