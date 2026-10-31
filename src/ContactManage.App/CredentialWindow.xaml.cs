using System.Windows;
using ContactManage.Core;

namespace ContactManage.App;

public partial class CredentialWindow : Window
{
    public CredentialWindow(ContactFolder folder, string? error, string? suggestedUser)
    {
        InitializeComponent();
        Heading.Text = $"Sign in to {folder.AccountName} ({folder.ServerName})";
        if (error is not null)
        {
            ErrorText.Text = error + " Please try again.";
            ErrorText.Visibility = Visibility.Visible;
        }
        UserBox.Text = suggestedUser ?? (folder.AccountName.Contains('@') ? folder.AccountName : "");
        Loaded += (_, _) => (UserBox.Text.Length == 0 ? (UIElement)UserBox : PasswordBox).Focus();
    }

    public ServerCredentials? Credentials { get; private set; }
    public bool Remember { get; private set; }

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UserBox.Text) || PasswordBox.Password.Length == 0)
        {
            ErrorText.Text = "Enter both a user name and a password.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        Credentials = new ServerCredentials(UserBox.Text.Trim(), PasswordBox.Password);
        Remember = RememberBox.IsChecked == true;
        DialogResult = true;
    }
}

/// <summary>
/// Supplies the login for an account: a saved one if the user chose "remember" earlier, otherwise a prompt.
/// Passwords stay in memory for the session unless the user ticks "remember", and then only Windows Credential Manager holds them.
/// </summary>
public sealed class SessionCredentials(Window owner, SavedLogins saved)
{
    private readonly Dictionary<string, string> _userNames = new();

    public Task<ServerCredentials?> GetAsync(ContactFolder folder, string? error) =>
        owner.Dispatcher.InvokeAsync(() =>
        {
            if (error is not null) saved.Rejected(folder); // the login we offered before was refused
            else if (saved.Find(folder) is { } known) return known;

            var window = new CredentialWindow(folder, error, _userNames.GetValueOrDefault(folder.AccountKey)) { Owner = owner };
            if (window.ShowDialog() != true) return null;
            _userNames[folder.AccountKey] = window.Credentials!.UserName;
            if (window.Remember) saved.RememberAfterSignIn(folder, window.Credentials);
            return window.Credentials;
        }).Task;
}
