using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using ContactManage.Core;

namespace ContactManage.App;

public abstract class Notifier : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record MemberRow(string Name, string Emails, string Phones, string Company, string Labels, string Folder);

/// <summary>One way to settle a conflict: one of the values, both, or a typed value.</summary>
public sealed class ChoiceVm(ConflictVm owner, string label, int index) : Notifier
{
    private bool _isChecked;
    public string Label { get; } = label;
    public int Index { get; } = index;
    public bool IsCustom => Index == owner.CustomIndex;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnChanged();
            if (value) owner.Select(Index);
        }
    }
}

public sealed class ConflictVm : Notifier
{
    private readonly FieldConflict _conflict;
    private readonly Action _changed;
    private string _customText = "";

    public ConflictVm(FieldConflict conflict, Action changed)
    {
        _conflict = conflict;
        _changed = changed;
        var n = conflict.Options.Count;
        Choices = [];
        for (var i = 0; i < n; i++)
        {
            var o = conflict.Options[i];
            var count = o.SourceContactIds.Count;
            Choices.Add(new ChoiceVm(this, $"{o.Value}   (from {count} {(count == 1 ? "contact" : "contacts")})", i));
        }
        Choices.Add(new ChoiceVm(this, "Keep both", n));
        Choices.Add(new ChoiceVm(this, "Leave blank", n + 1));
        Choices.Add(new ChoiceVm(this, "Type a value:", n + 2));
    }

    public int CustomIndex => _conflict.Options.Count + 2;
    private int BothIndex => _conflict.Options.Count;
    private int BlankIndex => _conflict.Options.Count + 1;

    public string FieldLabel => _conflict.Field switch
    {
        ContactField.DisplayName => "Name",
        ContactField.GivenName => "First name",
        ContactField.FamilyName => "Last name",
        ContactField.JobTitle => "Job title",
        var f => f.ToString(),
    };

    public List<ChoiceVm> Choices { get; }
    public bool IsResolved => _conflict.IsResolved;

    public string CustomText
    {
        get => _customText;
        set
        {
            _customText = value;
            OnChanged();
            if (Choices[CustomIndex].IsChecked) Apply(CustomIndex);
        }
    }

    internal void Select(int index)
    {
        foreach (var c in Choices) if (c.Index != index) c.IsChecked = false;
        Apply(index);
    }

    private void Apply(int index)
    {
        if (index < BothIndex) _conflict.Choose(index);
        else if (index == BothIndex) _conflict.KeepBoth();
        else if (index == BlankIndex) _conflict.Blank();
        else if (string.IsNullOrWhiteSpace(_customText)) _conflict.Reset();
        else _conflict.Custom(_customText);
        OnChanged(nameof(IsResolved));
        _changed();
    }
}

public sealed class GroupVm : Notifier
{
    private readonly Action _changed;

    public GroupVm(MergeGroup group, Action changed)
    {
        Group = group;
        _changed = changed;
        Members = group.Members.Select(m => new MemberRow(
            m.DisplayName ?? $"{m.GivenName} {m.FamilyName}".Trim(),
            string.Join(", ", m.Emails), string.Join(", ", m.Phones),
            m.Company ?? "", string.Join(", ", m.Groups), FolderLabel(m.SourceFolderId))).ToList();
        Conflicts = group.Conflicts.Select(c => new ConflictVm(c, Refresh)).ToList();
        Title = Members[0].Name;
        ReasonsText = string.Join("\n", group.Reasons.Select(r => r.Description));
    }

    public MergeGroup Group { get; }
    public string Title { get; }
    public string ReasonsText { get; }
    public IReadOnlyList<MemberRow> Members { get; }
    public IReadOnlyList<ConflictVm> Conflicts { get; }

    public bool IsMerge
    {
        get => Group.Decision == GroupDecision.Merge;
        set { if (value) Decide(GroupDecision.Merge); }
    }

    public bool IsKeepSeparate
    {
        get => Group.Decision == GroupDecision.KeepSeparate;
        set { if (value) Decide(GroupDecision.KeepSeparate); }
    }

    public bool IsRemove
    {
        get => Group.Decision == GroupDecision.Remove;
        set { if (value) Decide(GroupDecision.Remove); }
    }

    public bool ShowConflicts => IsMerge && Conflicts.Count > 0;

    public bool NeedsAttention => Group.Decision == GroupDecision.Pending || Group.HasUnresolvedConflicts;

    public override string ToString() => $"{Title} ({Status})";

    public string Status =>
        Group.Decision == GroupDecision.Pending ? "Needs decision"
        : Group.HasUnresolvedConflicts ? "Resolve conflicts"
        : IsRemove ? "Will remove all"
        : IsMerge ? "Will merge" : Group.Remembered ? "Keep separate (remembered from before)" : "Keep separate";

    private void Decide(GroupDecision d)
    {
        Group.Decision = d;
        Refresh();
    }

    private void Refresh()
    {
        OnChanged(nameof(IsMerge));
        OnChanged(nameof(IsKeepSeparate));
        OnChanged(nameof(IsRemove));
        OnChanged(nameof(ShowConflicts));
        OnChanged(nameof(NeedsAttention));
        OnChanged(nameof(Status));
        _changed();
    }

    private static string FolderLabel(string? id)
    {
        if (id is null) return "";
        var i = id.IndexOf(':');
        return i >= 0 ? id[(i + 1)..] : id;
    }
}

public sealed class ReviewViewModel : Notifier
{
    private GroupVm? _selected;
    private bool _attentionOnly;

    public ReviewViewModel(MergeResult result)
    {
        Result = result;
        Groups = new ObservableCollection<GroupVm>(result.Groups.Select(g => new GroupVm(g, Update)));
        View = CollectionViewSource.GetDefaultView(Groups);
        Selected = Groups.FirstOrDefault(g => g.NeedsAttention) ?? Groups.FirstOrDefault();
    }

    public MergeResult Result { get; }
    public ObservableCollection<GroupVm> Groups { get; }
    public ICollectionView View { get; }

    public GroupVm? Selected { get => _selected; set { _selected = value; OnChanged(); } }

    public bool AttentionOnly
    {
        get => _attentionOnly;
        set
        {
            _attentionOnly = value;
            View.Filter = value ? o => ((GroupVm)o).NeedsAttention : null;
            OnChanged();
        }
    }

    public bool CanFinish => true;

    public string StatusText
    {
        get
        {
            if (Groups.Count == 0) return "No duplicates found.";
            var remembered = Groups.Count(g => g.Group.Remembered && g.IsKeepSeparate);
            var note = remembered == 0 ? "" : $" {remembered} set{(remembered == 1 ? " was" : "s were")} kept separate because you said so earlier.";
            return (Result.IsComplete
                ? $"{Groups.Count} duplicate sets reviewed. Ready to finish."
                : $"{Result.PendingGroups.Count} awaiting a decision, {Result.GroupsWithConflicts.Count} with conflicts to resolve. You can finish now; those stay untouched.") + note;
        }
    }

    private void Update()
    {
        // Re-filter so items leave the "needs attention" view as they are settled.
        if (_attentionOnly) View.Refresh();
        OnChanged(nameof(CanFinish));
        OnChanged(nameof(StatusText));
    }
}
