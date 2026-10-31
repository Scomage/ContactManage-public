namespace ContactManage.Core;

public enum MatchKind
{
    Email,
    Phone,
    NameOnly,
}

/// <summary>Why a set of contacts was grouped.</summary>
public sealed record MatchReason(MatchKind Kind, string Value, IReadOnlyList<string> ContactIds)
{
    public string Description => Kind switch
    {
        MatchKind.Email => $"Same email: {Value}",
        MatchKind.Phone => $"Same phone: {Value}",
        _ => $"Same name: {Value} (no email or phone in common)",
    };
}

public sealed record ConflictOption(string Value, IReadOnlyList<string> SourceContactIds);

public enum ResolutionKind
{
    Unresolved,
    Chosen,
    KeepBoth,
    Custom,

    /// <summary>The user wants the field empty in the merged contact.</summary>
    Blank,
}

/// <summary>Members of a group disagree on a single-value field. The user picks one value, keeps both, or types a new one.</summary>
public sealed class FieldConflict
{
    internal FieldConflict(ContactField field, IReadOnlyList<ConflictOption> options)
    {
        Field = field;
        Options = options;
    }

    public ContactField Field { get; }
    public IReadOnlyList<ConflictOption> Options { get; }
    public ResolutionKind Resolution { get; private set; }
    public string? ResolvedValue { get; private set; }
    public bool IsResolved => Resolution != ResolutionKind.Unresolved;

    public void Choose(int optionIndex)
    {
        ResolvedValue = Options[optionIndex].Value;
        Resolution = ResolutionKind.Chosen;
    }

    public void KeepBoth()
    {
        var separator = Field == ContactField.Notes ? "\n\n" : "; ";
        ResolvedValue = string.Join(separator, Options.Select(o => o.Value));
        Resolution = ResolutionKind.KeepBoth;
    }

    public void Custom(string value)
    {
        ResolvedValue = value.Trim();
        Resolution = ResolutionKind.Custom;
    }

    public void Blank()
    {
        ResolvedValue = null;
        Resolution = ResolutionKind.Blank;
    }

    public void Reset()
    {
        ResolvedValue = null;
        Resolution = ResolutionKind.Unresolved;
    }
}

public enum GroupDecision
{
    /// <summary>A possible duplicate waiting for the user.</summary>
    Pending,
    Merge,
    KeepSeparate,

    /// <summary>The contacts are obsolete: none of them is kept. Dropped from the result, and deleted from any folder that is replaced.</summary>
    Remove,
}

/// <summary>Two or more contacts that appear to be the same person.</summary>
public sealed class MergeGroup
{
    private readonly Normalizer _normalizer;

    internal MergeGroup(IReadOnlyList<Contact> members, IReadOnlyList<MatchReason> reasons, bool needsReview, Normalizer normalizer)
    {
        Members = members;
        Reasons = reasons;
        NeedsReview = needsReview;
        _normalizer = normalizer;
        Conflicts = ContactMerger.FindConflicts(members, normalizer);
        Decision = needsReview ? GroupDecision.Pending : GroupDecision.Merge;
    }

    public IReadOnlyList<Contact> Members { get; }
    public IReadOnlyList<MatchReason> Reasons { get; }

    /// <summary>True for name-only matches, and for matches whose members have clearly different names (e.g. a shared family phone).</summary>
    public bool NeedsReview { get; }

    public GroupDecision Decision { get; set; }

    /// <summary>True when the user already said, in an earlier run, that these contacts are different people.</summary>
    public bool Remembered { get; internal set; }

    public IReadOnlyList<FieldConflict> Conflicts { get; }

    public bool WillMerge => Decision == GroupDecision.Merge;
    public bool WillRemove => Decision == GroupDecision.Remove;
    public bool HasUnresolvedConflicts => WillMerge && Conflicts.Any(c => !c.IsResolved);

    /// <summary>The merged contact. Throws while any conflict is unresolved.</summary>
    public Contact BuildMerged() => ContactMerger.Build(this, _normalizer);
}

public sealed class MergeResult
{
    private readonly List<(Contact? Single, MergeGroup? Group)> _entries;

    internal MergeResult(List<(Contact? Single, MergeGroup? Group)> entries)
    {
        _entries = entries;
        Groups = entries.Where(e => e.Group is not null).Select(e => e.Group!).ToList();
    }

    /// <summary>Every set of duplicates found, in input order.</summary>
    public IReadOnlyList<MergeGroup> Groups { get; }

    /// <summary>Contacts that were not part of any duplicate set and pass through as they are.</summary>
    public IReadOnlyList<Contact> Unduplicated => _entries.Where(e => e.Single is not null).Select(e => e.Single!).ToList();

    public IReadOnlyList<MergeGroup> PendingGroups => Groups.Where(g => g.Decision == GroupDecision.Pending).ToList();
    public IReadOnlyList<MergeGroup> GroupsWithConflicts => Groups.Where(g => g.HasUnresolvedConflicts).ToList();
    public bool IsComplete => PendingGroups.Count == 0 && GroupsWithConflicts.Count == 0;

    /// <summary>How many duplicate sets were merged, kept apart, or left for later.</summary>
    public MergeSummary Summary => new(
        Groups.Count(g => g.WillMerge && !g.HasUnresolvedConflicts),
        Groups.Count(g => g.Decision == GroupDecision.KeepSeparate),
        PendingGroups.Count + GroupsWithConflicts.Count,
        Groups.Count(g => g.WillRemove),
        Groups.Where(g => g.WillRemove).Sum(g => g.Members.Count));

    /// <summary>
    /// The combined list for a user who stops early. Every finished decision is applied; a set still waiting for a decision,
    /// or marked for merging with conflicts left unresolved, is not touched: its contacts pass through exactly as they were.
    /// </summary>
    public IReadOnlyList<Contact> BuildPartialList()
    {
        var result = new List<Contact>();
        foreach (var (single, group) in _entries)
        {
            if (single is not null) result.Add(single);
            else if (group!.WillRemove) continue;
            else if (group.WillMerge && !group.HasUnresolvedConflicts) result.Add(group.BuildMerged());
            else result.AddRange(group.Members);
        }
        return result;
    }

    /// <summary>
    /// The combined list. Merged groups become one contact; groups kept separate pass through unchanged.
    /// Throws while any group awaits a decision or any conflict is unresolved, so nothing is decided silently.
    /// </summary>
    public IReadOnlyList<Contact> BuildFinalList()
    {
        if (!IsComplete)
            throw new InvalidOperationException(
                $"{PendingGroups.Count} possible duplicates await a decision and {GroupsWithConflicts.Count} merged contacts have unresolved conflicts.");

        var result = new List<Contact>();
        foreach (var (single, group) in _entries)
        {
            if (single is not null) result.Add(single);
            else if (group!.WillRemove) continue;
            else if (group.WillMerge) result.Add(group.BuildMerged());
            else result.AddRange(group.Members);
        }
        return result;
    }
}

/// <param name="Merged">Duplicate sets combined into one contact.</param>
/// <param name="KeptSeparate">Sets the user decided are different people.</param>
/// <param name="Deferred">Sets still undecided or with unresolved conflicts; their contacts stay as they were.</param>
/// <param name="Removed">Sets whose contacts were all marked obsolete and dropped.</param>
/// <param name="RemovedContacts">How many contacts those sets held.</param>
public sealed record MergeSummary(int Merged, int KeptSeparate, int Deferred, int Removed = 0, int RemovedContacts = 0)
{
    public string Text =>
        $"{Merged:N0} duplicate {(Merged == 1 ? "set" : "sets")} merged, {KeptSeparate:N0} kept separate" +
        (Removed > 0 ? $", {Removed:N0} removed ({RemovedContacts:N0} {(RemovedContacts == 1 ? "contact" : "contacts")})" : "") +
        (Deferred > 0 ? $", {Deferred:N0} left undecided and unchanged" : "") + ".";
}
