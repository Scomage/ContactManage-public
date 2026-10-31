namespace ContactManage.Core;

/// <summary>
/// Finds duplicate contacts and builds merge groups.
/// Contacts are linked when they share a normalized email or phone number. Contacts with the same
/// multi-word name and nothing else in common are linked as "possible" duplicates. Links are transitive.
/// A group needs the user's review when it contains a name-only link, or when its members have clearly different names.
/// </summary>
public sealed class MergeEngine(Normalizer? normalizer = null)
{
    private readonly Normalizer _n = normalizer ?? new Normalizer();

    public MergeResult Merge(IEnumerable<Contact> input)
    {
        var contacts = input.ToList();
        var parent = Enumerable.Range(0, contacts.Count).ToArray();

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        void Union(int a, int b) => parent[Find(a)] = Find(b);

        var links = new List<(MatchKind Kind, string Value, List<int> Indices)>();

        void Link(MatchKind kind, Func<Contact, IEnumerable<string?>> keys, bool nameStage = false)
        {
            var buckets = new Dictionary<string, List<int>>();
            for (var i = 0; i < contacts.Count; i++)
                foreach (var key in keys(contacts[i]).Where(k => !string.IsNullOrEmpty(k)).Distinct())
                {
                    if (!buckets.TryGetValue(key!, out var list)) buckets[key!] = list = [];
                    list.Add(i);
                }

            foreach (var (key, list) in buckets)
            {
                if (list.Count < 2) continue;
                // A name-only link only matters where it joins groups that are not already joined.
                if (nameStage && list.Select(Find).Distinct().Count() < 2) continue;
                foreach (var i in list.Skip(1)) Union(list[0], i);
                links.Add((kind, key, list));
            }
        }

        Link(MatchKind.Email, c => c.Emails.Select(_n.Email));
        Link(MatchKind.Phone, c => c.Phones.Select(_n.Phone));
        Link(MatchKind.NameOnly, c => [MultiWordNameKey(c)], nameStage: true);

        var byRoot = Enumerable.Range(0, contacts.Count).GroupBy(Find).ToDictionary(g => g.Key, g => g.ToList());
        var linksByRoot = links.ToLookup(l => Find(l.Indices[0])); // once, not once per group
        var entries = new List<(int Order, Contact? Single, MergeGroup? Group)>();

        foreach (var (root, indices) in byRoot)
        {
            if (indices.Count == 1)
            {
                entries.Add((indices[0], contacts[indices[0]], null));
                continue;
            }

            var members = indices.Select(i => contacts[i]).ToList();
            var reasons = linksByRoot[root]
                .Select(l => new MatchReason(l.Kind, l.Value, l.Indices.Select(i => contacts[i].Id).ToList()))
                .ToList();
            var needsReview = reasons.Any(r => r.Kind == MatchKind.NameOnly) || !NamesAllCompatible(members);
            entries.Add((indices[0], null, new MergeGroup(members, reasons, needsReview, _n)));
        }

        return new MergeResult(entries.OrderBy(e => e.Order).Select(e => (e.Single, e.Group)).ToList());
    }

    // Single-word names ("John") are too common to link on their own.
    private string? MultiWordNameKey(Contact c)
    {
        var key = _n.NameKey(c);
        return key.Contains(' ') ? key : null;
    }

    private bool NamesAllCompatible(List<Contact> members)
    {
        var keys = members.Select(_n.NameKey).ToList();
        for (var i = 0; i < keys.Count; i++)
            for (var j = i + 1; j < keys.Count; j++)
                if (!Normalizer.NamesCompatible(keys[i], keys[j])) return false;
        return true;
    }
}
