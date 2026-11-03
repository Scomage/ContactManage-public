using System.Windows;
using ContactManage.Core;

namespace ContactManage.App;

/// <summary>The first screen: online accounts, a mail program on this PC, or the demo.</summary>
public partial class StartWindow : Window
{
    /// <param name="mailProgramName">The mail program found on this PC, or null when there is none.</param>
    /// <param name="remembered">Whether the screen is currently being skipped at start-up.</param>
    public StartWindow(string? mailProgramName, bool remembered)
    {
        InitializeComponent();
        MailTile.IsEnabled = mailProgramName is not null;
        MailText.Text = mailProgramName is not null
            ? $"Found: {mailProgramName}. Its contact folders, plus any Gmail or Microsoft account you add."
            : "No supported mail program was found on this PC. Planned: Thunderbird, Outlook, eM Client.";
        RememberCheck.IsChecked = remembered;
    }

    public SourceMode Mode { get; private set; }

    public bool Remember => RememberCheck.IsChecked == true;

    private void Choose(SourceMode mode)
    {
        Mode = mode;
        DialogResult = true;
    }

    private void Online_Click(object sender, RoutedEventArgs e) => Choose(SourceMode.Online);
    private void Mail_Click(object sender, RoutedEventArgs e) => Choose(SourceMode.MailProgram);
    private void Demo_Click(object sender, RoutedEventArgs e) => Choose(SourceMode.Demo);
}
