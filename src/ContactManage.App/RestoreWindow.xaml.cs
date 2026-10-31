using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ContactManage.Core;

namespace ContactManage.App;

/// <summary>Lists the backups the app has saved and lets the user pick one to put back into a folder.</summary>
public partial class RestoreWindow : Window
{
    private sealed record Row(BackupFile File, DateTime Taken, ContactFolder? Target)
    {
        public string When => Taken.ToString("yyyy-MM-dd HH:mm:ss");
        public string Label => File.Label;
        public int Count => File.ContactCount;
        public string Match => Target is null ? "(pick a folder below)" : $"{Target.AccountName} - {Target.Name}";
    }

    private sealed record TargetChoice(ContactFolder Folder)
    {
        public override string ToString() => $"{Folder.AccountName} - {Folder.Name}" + (Folder.ContactCount is { } n ? $" ({n:N0} contacts now)" : "");
    }

    private readonly string _root;
    private readonly List<TargetChoice> _targets;

    public RestoreWindow(string backupRoot, IReadOnlyList<BackupSet> backups, IReadOnlyList<ContactFolder> writableFolders)
    {
        InitializeComponent();
        _root = backupRoot;
        _targets = writableFolders.Select(f => new TargetChoice(f)).ToList();
        TargetBox.ItemsSource = _targets;
        BackupList.ItemsSource = backups.SelectMany(s => s.Files.Select(f => new Row(f, s.Taken, BackupCatalog.FindTarget(f, writableFolders)))).ToList();
        if (backups.Count == 0)
            Title += " (no backups saved yet)";
    }

    /// <summary>The backup file to restore and the folder it goes into; set when the user clicks Restore.</summary>
    public (BackupFile File, ContactFolder Target)? Choice { get; private set; }

    private void BackupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = BackupList.SelectedItem as Row;
        RestoreButton.IsEnabled = row is not null && _targets.Count > 0;
        if (row?.Target is { } t) TargetBox.SelectedItem = _targets.FirstOrDefault(c => c.Folder.Id == t.Id);
        else TargetBox.SelectedIndex = -1;   // no match: the user must choose, never a guess
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not Row row) return;
        if (TargetBox.SelectedItem is not TargetChoice target)
        {
            MessageBox.Show(this, "Choose the folder to restore into.", "ContactManage", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Choice = (row.File, target.Folder);
        DialogResult = true;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_root);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_root}\"") { UseShellExecute = true });
    }
}
