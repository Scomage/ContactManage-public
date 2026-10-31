using System.ComponentModel;
using System.Windows;
using ContactManage.Core;
using Microsoft.Win32;

namespace ContactManage.App;

public enum DestinationKind { Replace, NewFolder, File }

/// <param name="Targets">Folders to replace (Replace only).</param>
/// <param name="Account">A folder of the account that gets the new folder (NewFolder only).</param>
public sealed record DestinationChoice(DestinationKind Kind, IReadOnlyList<ContactFolder> Targets, ContactFolder? Account, string? Name, string? Path);

public sealed class PickFolder(ContactFolder folder) : INotifyPropertyChanged
{
    public ContactFolder Folder { get; } = folder;
    public string Text { get; } = $"{folder.Name}  ({folder.AccountName}, {folder.ServerName})";
    public bool IsChecked { get; set; } = true;
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed class ExistingChoice(ContactFolder folder)
{
    public ContactFolder Folder { get; } = folder;
    public string Text { get; } = $"{folder.Name}  ({folder.AccountName}, {folder.ServerName}" +
        (folder.ContactCount is { } n ? $", {n:N0} contacts)" : ")");
}

public sealed class AccountChoice(ContactFolder folder)
{
    public ContactFolder Folder { get; } = folder;
    public override string ToString() => $"{Folder.AccountName} ({Folder.ServerName})";
}

public partial class DestinationWindow : Window
{
    private readonly List<PickFolder> _targets;

    /// <param name="replaceTargets">Selected folders that can be written to.</param>
    /// <param name="writableFolders">Every folder that can be written to; one per account is offered for a new folder.</param>
    /// <param name="canCreateFolder">Whether the account of a folder can get a new folder (a server may not allow it).</param>
    public DestinationWindow(IReadOnlyList<ContactFolder> replaceTargets, IReadOnlyList<ContactFolder> writableFolders, int contactCount, bool demo,
        Func<ContactFolder, bool> canCreateFolder)
    {
        InitializeComponent();
        Heading.Text = $"Save {contactCount:N0} merged contacts" + (demo ? " (demo: nothing real is changed)" : "");

        _targets = replaceTargets.Select(f => new PickFolder(f)).ToList();
        ReplaceList.ItemsSource = _targets;
        ReplaceRadio.IsEnabled = _targets.Count > 0;

        var accounts = writableFolders.Where(canCreateFolder).GroupBy(f => f.AccountKey).Select(g => new AccountChoice(g.First())).ToList();
        AccountBox.ItemsSource = accounts;
        AccountBox.SelectedIndex = accounts.Count > 0 ? 0 : -1;
        NewRadio.IsEnabled = accounts.Count > 0;

        var existing = writableFolders.Select(f => new ExistingChoice(f)).ToList();
        ExistingBox.ItemsSource = existing;
        ExistingBox.SelectedIndex = existing.Count > 0 ? 0 : -1;
        ExistingRadio.IsEnabled = existing.Count > 0;

        (_targets.Count > 0 ? ReplaceRadio : accounts.Count > 0 ? NewRadio : FileRadio).IsChecked = true;

        LogCheck.IsChecked = _lastWantLog && !demo;
        LogCheck.IsEnabled = !demo;
        Closing += (_, _) => { if (!demo) _lastWantLog = LogCheck.IsChecked == true; };
    }

    public DestinationChoice? Choice { get; private set; }

    // The box keeps the last answer for the rest of the session; the log is off in demo mode, where nothing is written to disk.
    private static bool _lastWantLog = true;
    public bool WantLog => LogCheck.IsChecked == true;

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "vCard files (*.vcf)|*.vcf", FileName = "merged-contacts.vcf", DefaultExt = ".vcf" };
        if (dialog.ShowDialog(this) == true) PathBox.Text = dialog.FileName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string? problem = null;
        if (ReplaceRadio.IsChecked == true)
        {
            var picked = _targets.Where(t => t.IsChecked).Select(t => t.Folder).ToList();
            if (picked.Count == 0) problem = "Tick at least one folder to replace.";
            else Choice = new DestinationChoice(DestinationKind.Replace, picked, null, null, null);
        }
        else if (ExistingRadio.IsChecked == true)
        {
            // Same operation as replacing a selected folder, so it gets the same backup and safety checks.
            if (ExistingBox.SelectedItem is not ExistingChoice existing) problem = "Choose a folder.";
            else Choice = new DestinationChoice(DestinationKind.Replace, [existing.Folder], null, null, null);
        }
        else if (NewRadio.IsChecked == true)
        {
            if (AccountBox.SelectedItem is not AccountChoice account) problem = "Choose an account.";
            else if (string.IsNullOrWhiteSpace(NameBox.Text)) problem = "Enter a name for the new folder.";
            else Choice = new DestinationChoice(DestinationKind.NewFolder, [], account.Folder, NameBox.Text.Trim(), null);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(PathBox.Text)) problem = "Choose a file to save to.";
            else Choice = new DestinationChoice(DestinationKind.File, [], null, null, PathBox.Text.Trim());
        }

        if (problem is not null)
        {
            ErrorText.Text = problem;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }
}
