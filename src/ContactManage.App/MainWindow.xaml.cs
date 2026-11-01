using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using ContactManage.Adapters;
using ContactManage.Adapters.CardDav;
using ContactManage.Adapters.Google;
using ContactManage.Adapters.IceWarp;
using ContactManage.Core;

namespace ContactManage.App;

public partial class MainWindow : Window
{
    private static readonly string BackupRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ContactManage", "Backups");

    private readonly MainViewModel _vm = new();
    private readonly CardDavContactStore _cardDavStore;
    private readonly SavedLogins _savedLogins;
    private readonly GoogleAccounts _google;
    private readonly GoogleAccountNames _googleNames = new(GoogleAccountNames.DefaultPath);
    private readonly GooglePeopleStore _gmailStore;
    private readonly IContactStore _store;
    private readonly NotDuplicates _notDuplicates =
        NotDuplicates.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ContactManage", "not-duplicates.json"));
    private readonly NotDuplicates _demoNotDuplicates = new(); // kept in memory only, so the demo never writes to disk
    private DemoContactStore _demoStore = new();
    private string? _backupDir;

    public MainWindow()
    {
        InitializeComponent();
        var vault = new WindowsCredentialVault();
        _savedLogins = new SavedLogins(vault);
        _cardDavStore = new CardDavContactStore(new SessionCredentials(this, _savedLogins).GetAsync, signedIn: _savedLogins.SignedIn);
        _google = new GoogleAccounts(vault, () => GoogleClientConfig.Load());
        _gmailStore = new GooglePeopleStore(_google, _googleNames);
        _store = new CompositeContactStore(_cardDavStore, _gmailStore, new LocalFoldersContactStore(backupRoot: BackupRoot));
        DataContext = _vm;
        Closed += (_, _) => _cardDavStore.Dispose();
        Loaded += async (_, _) => await StartAsync();
    }

    // ---- where the contacts come from

    /// <summary>--settings=&lt;file&gt; points the settings elsewhere (the screen tests use it so they never touch the user's own).</summary>
    private static string SettingsPath(string[] args) =>
        args.FirstOrDefault(a => a.StartsWith("--settings=", StringComparison.Ordinal))?["--settings=".Length..] ?? AppSettings.DefaultPath;

    private string _settingsPath = AppSettings.DefaultPath;
    private AppSettings _settings = new();

    private async Task StartAsync()
    {
        var args = Environment.GetCommandLineArgs();
        _settingsPath = SettingsPath(args);
        _settings = AppSettings.Load(_settingsPath);
        ShowPhoneRegion();
        var plan = StartPlan.Decide(_settings.Start, _vm.MailProgramName is not null, args.Contains("--demo"), args.Contains("--start-screen"));
        var mode = plan.Mode;
        if (plan.ShowScreen && !ChooseSource(out mode))
        {
            Close(); // closing the first screen means the user does not want to start
            return;
        }
        await ApplyModeAsync(mode);
    }

    private bool _showingRegion;

    private void ShowPhoneRegion()
    {
        _showingRegion = true; // filling the box is not a choice by the user
        PhoneRegionBox.ItemsSource = PhoneRegions.All();
        PhoneRegionBox.SelectedItem = PhoneRegions.All().FirstOrDefault(r => r.Code == _settings.PhoneRegion);
        _showingRegion = false;
    }

    private void PhoneRegion_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_showingRegion || PhoneRegionBox.SelectedItem is not PhoneRegion region) return;
        _settings.PhoneRegion = region.Code;
        try { _settings.Save(_settingsPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the choice just is not remembered */ }
    }

    /// <summary>Shows the screen and remembers the answer when asked to. Returns false when the user closed it.</summary>
    private bool ChooseSource(out SourceMode mode)
    {
        var screen = new StartWindow(_vm.MailProgramName, remembered: _settings.Start != StartPreference.Ask) { Owner = this };
        if (screen.ShowDialog() != true) { mode = default; return false; }
        mode = screen.Mode;
        var preference = StartPlan.After(mode, screen.Remember, _settings.Start);
        if (preference != _settings.Start)
        {
            _settings.Start = preference;
            try { _settings.Save(_settingsPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the choice just is not remembered */ }
        }
        return true;
    }

    private async Task ApplyModeAsync(SourceMode mode)
    {
        _vm.SetMode(mode);
        if (mode == SourceMode.Demo) return;
        SetBusy(true);
        _vm.StatusText = mode == SourceMode.Online ? "Looking for your online accounts..." : "Reading the mail client's folders...";
        try
        {
            await _vm.LoadAsync();
            await LoadGmailAsync();
            if (mode == SourceMode.Online && _vm.Folders.Count == 0) _vm.StatusText = "No account yet. Press Add Google account to sign in.";
        }
        finally { SetBusy(false); }
    }

    private async void SwitchSource_Click(object sender, RoutedEventArgs e)
    {
        if (ChooseSource(out var mode)) await ApplyModeAsync(mode);
    }

    private IContactStore Store => _vm.UseDemo ? _demoStore : _store;
    private NotDuplicates Remembered => _vm.UseDemo ? _demoNotDuplicates : _notDuplicates;

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.UseDemo)
        {
            _demoStore = new DemoContactStore(); // start the demo again from its original contacts
            _vm.Load();
            return;
        }
        SetBusy(true);
        _vm.StatusText = _vm.OnlineOnly ? "Refreshing your online accounts..." : "Reading the mail client's folders...";
        try
        {
            await _vm.LoadAsync(); // the client's own list first, then the servers' answer on top of it
            _vm.AddFolders(_gmailStore.SavedFolders());
            ShowGooglePlaceholders();
        }
        finally { SetBusy(false); }
        await RefreshFromServersAsync(showProblems: true);
    }

    /// <summary>
    /// Asks the servers for their folders and counts, so folders made or filled since the client last synced show up.
    /// Only accounts already signed in to (or with a remembered login) are asked; the others keep the client's numbers.
    /// </summary>
    private async Task RefreshFromServersAsync(bool showProblems)
    {
        SetBusy(true);
        try
        {
            _vm.StatusText = "Asking the servers for their folders...";
            var refresh = await Store.RefreshFoldersAsync(_vm.Folders.Select(f => f.Folder).ToList(), f => !_vm.UseDemo && _savedLogins.Find(f) is not null);
            if (refresh.Folders.Any(f => f.Kind == FolderKind.GmailApi)) _vm.RemoveFolders(f => f.Kind == FolderKind.GmailLabel); // labels may have changed
            _vm.ApplyServerFolders(refresh);
            if (showProblems && refresh.Problems.Count > 0)
                MessageBox.Show(this, "Some servers could not be asked, so their folders show the client's last numbers:\n\n" + string.Join("\n", refresh.Problems),
                    "ContactManage", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            // The user closed a sign-in prompt.
        }
        finally
        {
            SetBusy(false);
            _vm.ShowSelectionStatus();
        }
    }

    /// <summary>
    /// Shows the saved Google accounts straight away (no network), then asks Google for each one's total and labels.
    /// A failure leaves the counts blank; an expired sign-in is explained.
    /// </summary>
    private async Task LoadGmailAsync()
    {
        foreach (var email in _google.SavedEmails()) NameGoogleAccount(email); // sign-ins saved before they could be named
        _vm.AddFolders(_gmailStore.SavedFolders());
        ShowGooglePlaceholders();
        if (_gmailStore.SavedFolders().Count == 0) return;
        _vm.StatusText = "Asking Google for your contact totals...";
        var refresh = await _gmailStore.RefreshFoldersAsync([], _ => false);
        ShowGmail(refresh);
        if (refresh.Problems.Count > 0)
            MessageBox.Show(this, string.Join("\n", refresh.Problems), "ContactManage", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Expander_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SelectableFolder row }) _vm.ToggleExpanded(row);
    }

    /// <summary>
    /// One placeholder row for each Google account IceWarp knows that has no sign-in here yet, so a new user sees that Gmail is
    /// available and how to connect it. A signed-in account replaces its placeholder.
    /// </summary>
    private void ShowGooglePlaceholders()
    {
        _vm.RemoveFolders(f => f.Kind == FolderKind.GmailSignIn);
        var signedIn = _google.SavedEmails().Select(e => _googleNames.Get(e)).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        _vm.AddFolders(_vm.IceWarpGoogleNames.Where(n => !signedIn.Contains(n)).Select(n =>
            new ContactFolder($"gmail-pending-{n}:/myContacts", "My Contacts", "/myContacts", n, "Google", FolderKind.GmailSignIn,
                IsWritable: false, ContactCount: null, DisplayPath: "Not signed in. Use Add Google account (top right).")));
    }

    /// <summary>Puts a Gmail answer into the list; labels that were removed in Gmail disappear.</summary>
    private void ShowGmail(FolderRefresh refresh)
    {
        if (refresh.Folders.Count > 0) _vm.RemoveFolders(f => f.Kind == FolderKind.GmailLabel);
        _vm.AddFolders(refresh.Folders);
    }

    /// <summary>
    /// Gives a new Google account the name its IceWarp account has ("Home Gmail"). With one unused IceWarp Google account that is
    /// automatic; with several the user picks; with none the email address is the name.
    /// </summary>
    private void NameGoogleAccount(string email)
    {
        if (_googleNames.Get(email) is not null) return;
        var free = _vm.IceWarpGoogleNames.Where(n => !_googleNames.Names.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        var name = free.Count switch
        {
            0 => null,
            1 => free[0],
            _ => new PickAccountWindow(email, free) { Owner = this } is var pick && pick.ShowDialog() == true ? pick.Chosen : null,
        };
        if (name is not null) _googleNames.Set(email, name);
    }

    private async void AddGoogle_Click(object sender, RoutedEventArgs e)
    {
        var signedIn = false;
        SetBusy(true);
        try
        {
            _vm.StatusText = "Finish signing in to Google in your browser...";
            var email = await _google.SignInAsync(url =>
            {
                try { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    throw new GoogleAuthException("Your browser could not be opened for the Google sign-in: " + ex.Message);
                }
                return Task.CompletedTask;
            });
            NameGoogleAccount(email);
            _vm.AddFolders([_gmailStore.MainFolder(email)]);
            ShowGooglePlaceholders();
            signedIn = true;
        }
        catch (GoogleAuthException ex)
        {
            MessageBox.Show(this, ex.Message, "ContactManage", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
            _vm.ShowSelectionStatus();
        }
        if (signedIn) await RefreshFromServersAsync(showProblems: false); // fills in the contact count
    }

    private void ForgetPasswords_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var n = _savedLogins.ForgetAll();
            _googleNames.Clear();
            _vm.RemoveFolders(f => f.Kind is FolderKind.GmailApi or FolderKind.GmailLabel);   // their sign-ins went with the other saved logins
            ShowGooglePlaceholders();
            MessageBox.Show(this, n == 0 ? "No saved passwords were found." : $"Removed {n} saved password{(n == 1 ? "" : "s")} from Windows Credential Manager.",
                "ContactManage", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            MessageBox.Show(this, "Windows Credential Manager refused the request: " + ex.Message, "ContactManage", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        var selected = _vm.SelectedFolders;
        _backupDir = null;
        var saved = false;
        SetBusy(true);
        try
        {
            var all = new List<Contact>();
            foreach (var folder in selected)
            {
                _vm.StatusText = $"Reading {folder.Name} ({folder.AccountName})...";
                all.AddRange(await Store.ReadContactsAsync(folder));
            }

            _vm.StatusText = $"Looking for duplicates among {all.Count:N0} contacts...";
            var normalizer = new Normalizer(_settings.PhoneRegion);
            var result = await Task.Run(() => new MergeEngine(normalizer).Merge(all));
            Remembered.Apply(result);

            var review = new ReviewWindow(result, _vm.UseDemo) { Owner = this };
            if (review.ShowDialog() != true) return;

            if (await SaveAsync(selected, result, review.FinalList!, review.Summary!))
            {
                Remembered.Record(result);
                saved = true;
            }
        }
        catch (OperationCanceledException)
        {
            // The user closed a sign-in prompt.
        }
        catch (Exception ex) when (ex is CardDavException or GoogleAuthException or HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            var backup = _backupDir is null ? "" : $"\n\nBackups of the folders were saved in:\n{_backupDir}";
            MessageBox.Show(this, ex.Message + backup, "ContactManage", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
            _vm.ShowSelectionStatus();
        }

        // The save changed the server, so show its new folder and counts without waiting for the client to sync.
        if (saved) await RefreshFromServersAsync(showProblems: false);
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<BackupSet> backups;
        SetBusy(true);
        _vm.StatusText = "Looking for backups...";
        try { backups = await Task.Run(() => BackupCatalog.List(BackupRoot)); }
        finally
        {
            SetBusy(false);
            _vm.ShowSelectionStatus();
        }

        var writable = _vm.Folders.Select(f => f.Folder).Where(f => f.CanWrite).ToList();
        var window = new RestoreWindow(BackupRoot, backups, writable) { Owner = this };
        if (window.ShowDialog() == true && window.Choice is { } choice)
            await RestoreAsync(choice.File, choice.Target);
    }

    /// <summary>Puts a backup's contacts into a folder, replacing what is there. The folder's present contents are backed up first.</summary>
    private async Task RestoreAsync(BackupFile file, ContactFolder target)
    {
        _backupDir = null;
        var restored = false;
        SetBusy(true);
        try
        {
            var contacts = await Task.Run(() => VCardFiles.Load(file.Path));
            if (contacts.Count == 0)
            {
                Info("That backup holds no contacts, so there is nothing to restore.");
                return;
            }
            var now = target.ContactCount is { } n ? $"its {n:N0} present contacts" : "its present contacts";
            var isLocal = target.Kind == FolderKind.Local && !_vm.UseDemo;
            var restoreQuestion = $"Replace {now} in {target.Name} ({target.AccountName}) with the {contacts.Count:N0} contacts from the backup \"{file.Label}\"?\n\n" +
                                  $"The folder's present contents are saved first, in\n{BackupRoot}\n\n" +
                                  (isLocal ? "Contacts that are still in the folder are updated in place; the others are added again (without their pictures)." : "Restored contacts are written as new entries.") +
                                  (isLocal ? LocalOnlyWarning([target]) : "") + (_vm.UseDemo ? "" : LabelLoss.Warning([target], contacts));
            if (isLocal
                    ? MessageBox.Show(this, restoreQuestion, "ContactManage", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes
                    : !Confirm(restoreQuestion))
                return;

            _vm.StatusText = $"Backing up {target.Name} ({target.AccountName})...";
            var present = await Store.ReadContactsAsync(target);
            _backupDir = Path.GetDirectoryName(VCardFiles.Backup(BackupRoot, VCardFiles.BackupLabel(target.AccountName, target.Name), present, DateTime.Now));

            _vm.StatusText = $"Restoring {target.Name} ({target.AccountName})...";
            var report = await Store.ReplaceContactsAsync(target, contacts);
            Info(Describe([report]) + $"\n\nWhat was in the folder before is saved in:\n{_backupDir}" +
                 (isLocal ? "\n\nIceWarp Desktop Client normally shows the restored contacts straight away; if it still shows the old ones, exit it and start it again." : ""));
            restored = true;
        }
        catch (OperationCanceledException)
        {
            // The user closed a sign-in prompt.
        }
        catch (Exception ex) when (ex is CardDavException or GoogleAuthException or HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            var backup = _backupDir is null ? "" : $"\n\nWhat was in the folder before is saved in:\n{_backupDir}";
            MessageBox.Show(this, ex.Message + backup, "ContactManage", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
            _vm.ShowSelectionStatus();
        }
        if (restored) await RefreshFromServersAsync(showProblems: false);
    }

    private void SetBusy(bool busy)
    {
        Root.IsEnabled = !busy;
        _vm.IsBusy = busy;
    }

    private async Task<bool> SaveAsync(IReadOnlyList<ContactFolder> selected, MergeResult result, IReadOnlyList<Contact> final, MergeSummary summary)
    {
        var writable = _vm.Folders.Select(f => f.Folder).Where(f => f.CanWrite).ToList();
        DestinationChoice choice;
        var wantLog = false;
        while (true)
        {
            var dialog = new DestinationWindow(selected.Where(f => f.CanWrite).ToList(), writable, final.Count, _vm.UseDemo, Store.CanCreateFolder) { Owner = this };
            if (dialog.ShowDialog() != true) return false;
            choice = dialog.Choice!;
            wantLog = dialog.WantLog;
            if (choice.Kind != DestinationKind.NewFolder) break;

            // A new folder whose name is already taken in that account would otherwise end up as "Name 2", unnoticed.
            var clash = writable.Where(Store.CanCreateFolder).FirstOrDefault(f => f.AccountKey == choice.Account!.AccountKey && f.Name.Equals(choice.Name, StringComparison.OrdinalIgnoreCase));
            if (clash is null) break;
            var answer = MessageBox.Show(this,
                $"{choice.Account!.AccountName} already has a folder named \"{clash.Name}\"" +
                (clash.ContactCount is { } n ? $" with {n:N0} contacts" : "") + ".\n\n" +
                "Yes: replace what is in that folder with the merged contacts (a backup of it is saved first).\n" +
                "No: go back and pick a different name.\n" +
                "Cancel: stop without saving.",
                "ContactManage", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.No) continue;
            choice = new DestinationChoice(DestinationKind.Replace, [clash], null, null, null);
            break;
        }

        switch (choice.Kind)
        {
            case DestinationKind.File:
                VCardFiles.Save(choice.Path!, final);
                Info($"Saved {final.Count:N0} contacts to\n{choice.Path}\n\n{summary.Text}" +
                     WriteChangeLog(wantLog, result, selected, $"Saved {final.Count:N0} contacts to the file {choice.Path}", []) +
                     LocalFoldersNote(selected));
                break;

            case DestinationKind.NewFolder:
                if (!Confirm($"Create a new folder \"{choice.Name}\" in {choice.Account!.AccountName} and put {final.Count:N0} contacts in it?" +
                             (_vm.UseDemo ? "" : LabelLoss.Warning([choice.Account], final)))) return false;
                _vm.StatusText = $"Creating {choice.Name}...";
                var created = await Store.CreateFolderAsync(choice.Account, choice.Name!, final);
                Info(Describe([created]) + "\n\n" + summary.Text +
                     WriteChangeLog(wantLog, result, selected, $"Created the new folder \"{choice.Name}\" in {choice.Account.AccountName} with {final.Count:N0} contacts", []) +
                     LocalFoldersNote(selected));
                break;

            case DestinationKind.Replace:
                return await ReplaceAsync(choice.Targets, final, summary, result, selected, wantLog);
        }
        return true;
    }

    /// <summary>What the user must know before IceWarp's Local Folders are changed: nothing on a server can bring them back.</summary>
    private static string LocalOnlyWarning(IEnumerable<ContactFolder> folders) =>
        "\n\nWARNING: " + string.Join(", ", folders.Select(t => t.Name)) + " in IceWarp's Local Folders exists only on this PC. There is no copy on a server " +
        "to download again if something goes wrong. Before writing, ContactManage saves a vCard backup (Restore from backup brings the contacts back) " +
        "and a copy of IceWarp's contact database files. IceWarp Desktop Client may stay open and normally shows the change straight away; " +
        "please do not edit contacts in it while ContactManage is saving.";

    /// <summary>A Local Folder that was only read, not replaced, still holds its original contacts; say so.</summary>
    private string LocalFoldersNote(IReadOnlyList<ContactFolder> sources, IReadOnlyList<ContactFolder>? replaced = null) =>
        !_vm.UseDemo && sources.Any(f => f.Kind == FolderKind.Local && replaced?.Contains(f) != true)
            ? "\n\nThe Local Folders you read were not changed: they still hold their original contacts, duplicates included. " +
              "Once you have checked the new copy in IceWarp, delete the old Local Folders contacts there if you no longer want them."
            : "";

    private async Task<bool> ReplaceAsync(IReadOnlyList<ContactFolder> targets, IReadOnlyList<Contact> final, MergeSummary summary,
        MergeResult result, IReadOnlyList<ContactFolder> sources, bool wantLog)
    {
        var names = string.Join("\n", targets.Select(t => $"  {t.Name} ({t.AccountName})"));
        var backupNote = _vm.UseDemo
            ? "Demo mode: nothing real is changed."
            : $"A backup of each folder is saved first, in\n{BackupRoot}";
        var removalNote = summary.RemovedContacts == 0 ? "" :
            $"\n\nYou marked {summary.RemovedContacts:N0} {(summary.RemovedContacts == 1 ? "contact" : "contacts")} as obsolete. " +
            "They are deleted from the folders above. Any of them that live in a folder you are not replacing stay where they are.";
        var localTargets = targets.Where(t => t.Kind == FolderKind.Local).ToList();
        var localWarning = _vm.UseDemo || localTargets.Count == 0 ? "" : LocalOnlyWarning(localTargets);
        var labelNote = _vm.UseDemo ? "" : LabelLoss.Warning(targets, final);
        var question = $"Replace the contents of these folders with the {final.Count:N0} merged contacts?\n\n{names}\n\n{backupNote}{removalNote}{localWarning}{labelNote}";
        if (localWarning.Length > 0
                ? MessageBox.Show(this, question, "ContactManage", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes
                : !Confirm(question)) return false;

        var now = DateTime.Now;
        if (!_vm.UseDemo)
            foreach (var t in targets)
            {
                _vm.StatusText = $"Backing up {t.Name} ({t.AccountName})...";
                var current = await Store.ReadContactsAsync(t);
                var file = VCardFiles.Backup(BackupRoot, $"{t.AccountName} - {t.Name}", current, now);
                _backupDir = Path.GetDirectoryName(file);
            }

        var reports = new List<WriteReport>();
        foreach (var t in targets)
        {
            _vm.StatusText = $"Writing {t.Name} ({t.AccountName})...";
            reports.Add(await Store.ReplaceContactsAsync(t, final));
        }
        var logNote = WriteChangeLog(wantLog, result, sources,
            "Replaced the contents of " + string.Join("; ", targets.Select(t => $"{t.AccountName} - {t.Name}")) + $" with {final.Count:N0} contacts", targets);
        Info(Describe(reports) + "\n\n" + summary.Text + (_backupDir is null ? "" : $"\n\nBackups: {_backupDir}") + logNote + LocalFoldersNote(sources, targets) +
             (_vm.UseDemo || targets.All(t => t.Kind != FolderKind.CardDav) ? "" : "\n\nIceWarp shows the changes after its next sync.") +
             (_vm.UseDemo || targets.All(t => t.Kind != FolderKind.Local) ? "" :
                "\n\nIceWarp Desktop Client normally shows the new Local Folders contacts straight away; if it still shows the old ones, exit it and start it again. " +
                "A copy of its database files from before the change is next to the vCard backup."));
        return true;
    }

    private static readonly string LogDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ContactManage", "Logs");

    /// <summary>Saves the change log when the user asked for it. Returns the sentence to add to the result message, or "" when there is none.</summary>
    private string WriteChangeLog(bool wanted, MergeResult result, IReadOnlyList<ContactFolder> sources, string destination, IReadOnlyList<ContactFolder> replaced)
    {
        if (!wanted || _vm.UseDemo) return "";
        try
        {
            string FolderName(string? id) => _vm.Folders.Select(f => f.Folder).FirstOrDefault(f => f.Id == id) is { } f
                ? $"{f.AccountName} - {f.Name}" : id ?? "";
            var text = ChangeLog.Build(result, new ChangeLogContext(DateTime.Now, destination,
                sources.Select(s => $"{s.AccountName} - {s.Name}").ToList(), FolderName, replaced.Select(r => r.Id).ToHashSet(StringComparer.Ordinal)));
            Directory.CreateDirectory(LogDir);
            var path = Path.Combine(LogDir, $"changes-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(true));
            return $"\n\nChange log: {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"\n\nThe change log could not be saved: {ex.Message}";
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LogDir) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Info($"The log folder could not be opened: {ex.Message}\n\n{LogDir}");
        }
    }

    private static string Describe(IEnumerable<WriteReport> reports) => string.Join("\n", reports.Select(r =>
        $"{r.FolderName}: added {r.Added:N0}, " + (r.Updated > 0 ? $"updated {r.Updated:N0}, " : "") + $"removed {r.Removed:N0}, left alone {r.Unchanged:N0}." +
        (r.CountAfter is null ? "" : $" The server now has {r.CountAfter:N0}.") +
        (r.Verified ? "" : $" WARNING: expected {r.Expected:N0}. Check this folder.") +
        (r.Note is null ? "" : "\n" + r.Note)));

    private bool Confirm(string text) =>
        MessageBox.Show(this, text, "ContactManage", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void Info(string text) => MessageBox.Show(this, text, "ContactManage", MessageBoxButton.OK, MessageBoxImage.Information);
}
