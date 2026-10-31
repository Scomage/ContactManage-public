using System.Windows;

namespace ContactManage.App;

/// <summary>Asks which of IceWarp's Google accounts a freshly signed-in Google account is, so it shows under the name the user knows.</summary>
public partial class PickAccountWindow : Window
{
    private const string UseEmail = "Just use the email address";

    public PickAccountWindow(string email, IReadOnlyList<string> names)
    {
        InitializeComponent();
        Question.Text = $"You signed in as {email}. Which of your accounts in the mail client is this?";
        NameBox.ItemsSource = names.Append(UseEmail).ToList();
        NameBox.SelectedIndex = 0;
    }

    /// <summary>The chosen name, or null for "use the email address".</summary>
    public string? Chosen { get; private set; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Chosen = NameBox.SelectedItem is string s && s != UseEmail ? s : null;
        DialogResult = true;
    }
}
