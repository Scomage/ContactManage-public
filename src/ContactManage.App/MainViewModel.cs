using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using ContactManage.Adapters.IceWarp;
using ContactManage.Core;

namespace ContactManage.App;

public sealed class SelectableFolder(ContactFolder folder, Action changed) : INotifyPropertyChanged
{
    private bool _isSelected;
    public ContactFolder Folder { get; } = folder;

    public override string ToString() => Folder.Name;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            changed();
        }
    }

    /// <summary>True for a Gmail main list that has label rows under it, which then get an expand/collapse arrow.</summary>
    public bool HasChildren
    {
        get => _hasChildren;
        set { if (_hasChildren == value) return; _hasChildren = value; Raise(nameof(HasChildren)); }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; Raise(nameof(IsExpanded)); Raise(nameof(Glyph)); }
    }

    public string Glyph => IsExpanded ? "▾" : "▸";   // down arrow when open, right arrow when closed

    private bool _hasChildren;
    private bool _isExpanded = true;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IContactSource _mailSource = new IceWarpContactSource();
    private readonly IContactSource _onlineSource = new OnlineContactSource();
    private readonly IContactSource _demoSource = new DemoContactSource();
    private bool _useDemo;
    private bool _onlineOnly;
    private bool _isBusy;
    private string _clientText = "";
    private string _statusText = "";

    public ObservableCollection<SelectableFolder> Folders { get; } = [];

    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);   // accounts whose label rows are folded away
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);       // accounts already seen, so only a new one starts folded

    /// <summary>What the list shows: <see cref="Folders"/> without the label rows of a collapsed account.</summary>
    public ICollectionView FolderView { get; }

    public MainViewModel()
    {
        FolderView = CollectionViewSource.GetDefaultView(Folders);
        FolderView.Filter = o => o is SelectableFolder f && FolderTree.IsVisible(f.Folder, _collapsed);
    }

    /// <summary>Folds or unfolds the label rows under a Gmail main list.</summary>
    public void ToggleExpanded(SelectableFolder main)
    {
        if (!main.HasChildren) return;
        var key = main.Folder.AccountKey;
        if (!_collapsed.Remove(key)) _collapsed.Add(key);
        main.IsExpanded = !_collapsed.Contains(key);
        FolderView.Refresh();
    }

    private void UpdateExpanders()
    {
        foreach (var f in Folders.Where(f => f.Folder.Kind == FolderKind.GmailApi))
        {
            var key = f.Folder.AccountKey;
            if (_known.Add(key)) _collapsed.Add(key);   // labels start folded; after that the user's choice stands across refreshes
            f.HasChildren = Folders.Any(c => c.Folder.Kind == FolderKind.GmailLabel && c.Folder.AccountKey == key);
            f.IsExpanded = !_collapsed.Contains(key);
        }
        FolderView.Refresh();
    }

    public bool UseDemo
    {
        get => _useDemo;
        set
        {
            if (_useDemo == value) return;
            _useDemo = value;
            RaiseSourceChanged();
            Load();
        }
    }

    public bool UseReal { get => !_useDemo; set => UseDemo = !value; }

    /// <summary>True when the user works with online accounts only: no mail program is read.</summary>
    public bool OnlineOnly => _onlineOnly && !_useDemo;

    /// <summary>The name of the installed mail program the app can read, or null when there is none.</summary>
    public string? MailProgramName => _mailSource.Detect() is { IsSupported: true } client ? client.Name : null;

    /// <summary>
    /// Chooses where contacts come from. Demo loads straight away; for the other two the caller loads afterwards
    /// (<see cref="LoadAsync"/>), because that reads the mail program's databases off the UI thread.
    /// </summary>
    public void SetMode(SourceMode mode)
    {
        _onlineOnly = mode == SourceMode.Online;
        if (mode == SourceMode.Demo) { UseDemo = true; return; }
        _useDemo = false;
        RaiseSourceChanged();
    }

    private void RaiseSourceChanged()
    {
        OnChanged(nameof(UseDemo));
        OnChanged(nameof(UseReal));
        OnChanged(nameof(OnlineOnly));
        OnChanged(nameof(HelpText));
    }

    /// <summary>The line of advice above the folder list.</summary>
    public string HelpText => OnlineOnly
        ? "Press Add Google account and sign in with your Google account in the browser. Its contacts and labels then show here, straight from Google. " +
          "Select one list or label to clean up its duplicates (one row per Google account at a time)."
        : "Select one folder to clean up its duplicates, or several folders to merge them. Counts are from the client's last sync. " +
          "For a Gmail account use Add Google account: its contacts and labels then show here, straight from Google.";

    public bool IsBusy { get => _isBusy; set { Set(ref _isBusy, value); OnChanged(nameof(CanContinue)); } }

    public string ClientText { get => _clientText; private set => Set(ref _clientText, value); }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public int SelectedCount => Folders.Count(f => f.IsSelected);
    public IReadOnlyList<ContactFolder> SelectedFolders => Folders.Where(f => f.IsSelected).Select(f => f.Folder).ToList();
    private int SelectedContacts => Folders.Where(f => f.IsSelected).Sum(f => f.Folder.ContactCount ?? 0);
    public string? SelectionProblem => FolderList.SelectionProblem(SelectedFolders);
    public bool CanContinue => SelectedCount >= 1 && !IsBusy && SelectionProblem is null;

    /// <summary>What the mail client reported, read without touching anything on screen so it can run off the UI thread.</summary>
    private sealed record Snapshot(ClientInfo Client, IReadOnlyList<ContactFolder> Folders, string? Error);

    private static Snapshot Fetch(IContactSource source)
    {
        var client = source.Detect();
        if (!client.IsSupported) return new Snapshot(client, [], null);
        try
        {
            return new Snapshot(client, source.ListFolders(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return new Snapshot(client, [], "Could not read the client's folders: " + ex.Message);
        }
    }

    private IContactSource CurrentSource => _useDemo ? _demoSource : _onlineOnly ? _onlineSource : _mailSource;

    public void Load() => Show(Fetch(CurrentSource));

    /// <summary>Same as <see cref="Load"/>, but the client's databases are read in the background so the window stays responsive.</summary>
    public async Task LoadAsync()
    {
        var source = CurrentSource;
        Show(await Task.Run(() => Fetch(source)));
    }

    private void Show(Snapshot s)
    {
        ClientText = _useDemo
            ? "Demo mode: made-up contacts. Nothing real is read or changed."
            : OnlineOnly ? "Online accounts (no mail program needed)"
            : s.Client.IsSupported ? $"Mail client: {s.Client.Name}" : $"Mail client: {s.Client.Name} ({s.Client.Note})";
        // IceWarp's cached copy of a Google account (its labels as folders) is not shown: Google accounts come from Google directly
        // (Add Google account). Only the account names are kept, to name those accounts the way the user knows them.
        IceWarpGoogleNames = s.Folders.Where(f => f.Kind == FolderKind.Google).Select(f => f.AccountName).Distinct().Order().ToList();
        var shown = s.Folders.Where(f => f.Kind != FolderKind.Google).ToList();

        // The status goes first: filling the list then replaces it with the selection summary, as before.
        StatusText = s.Error ?? (OnlineOnly ? "" : s.Client.IsSupported ? $"{shown.Count} contact folders found" : "");
        ReplaceFolders(shown);
    }

    /// <summary>The Google accounts IceWarp knows, by the names it shows for them (for example "Home Gmail").</summary>
    public IReadOnlyList<string> IceWarpGoogleNames { get; private set; } = [];

    /// <summary>Adds folders to the list (or updates ones already there), keeping the folders the user ticked.</summary>
    public void AddFolders(IEnumerable<ContactFolder> folders) =>
        ReplaceFolders(FolderList.Splice(Folders.Select(f => f.Folder).ToList(), folders.ToList()));

    public void RemoveFolders(Func<ContactFolder, bool> remove) =>
        ReplaceFolders(Folders.Select(f => f.Folder).Where(f => !remove(f)).ToList());

    /// <summary>Puts what the servers reported into the list, keeping the folders the user ticked.</summary>
    public void ApplyServerFolders(FolderRefresh refresh) =>
        ReplaceFolders(FolderList.Splice(Folders.Select(f => f.Folder).ToList(), refresh.Folders));

    /// <summary>Swaps the list for <paramref name="folders"/>; folders that were ticked and are still there stay ticked.</summary>
    private void ReplaceFolders(IEnumerable<ContactFolder> folders)
    {
        var ticked = Folders.Where(f => f.IsSelected).Select(f => f.Folder.Id).ToHashSet(StringComparer.Ordinal);
        Folders.Clear();
        foreach (var f in folders)
            Folders.Add(new SelectableFolder(f, RaiseSelection) { IsSelected = ticked.Contains(f.Id) });
        UpdateExpanders();
        RaiseSelection();
    }

    public void ShowSelectionStatus()
    {
        if (SelectionProblem is { } problem)
            StatusText = problem;
        else if (Folders.Any(f => f.Folder.HasCheckbox))
            StatusText = $"{SelectedCount} of {Folders.Count(f => f.Folder.HasCheckbox)} selected, {SelectedContacts:N0} contacts";
    }

    private void RaiseSelection()
    {
        OnChanged(nameof(SelectedCount));
        OnChanged(nameof(CanContinue));
        ShowSelectionStatus();
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        field = value;
        OnChanged(name);
    }

    private void OnChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}
