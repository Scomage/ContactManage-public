using System.Windows;
using System.Windows.Controls;

namespace ContactManage.App;

public partial class App : Application
{
    /// <summary>WPF's default text size is 12; the app uses 14 everywhere (headings that set their own size are 2 larger than before).</summary>
    private const double BaseFontSize = 14;

    protected override void OnStartup(StartupEventArgs e)
    {
        // A default style for Window itself: an implicit style in App.xaml would not reach the derived MainWindow, ReviewWindow and so on.
        // Controls and text blocks inherit the size from their window.
        var style = new Style(typeof(Window));
        style.Setters.Add(new Setter(Control.FontSizeProperty, BaseFontSize));
        FrameworkElement.StyleProperty.OverrideMetadata(typeof(Window), new FrameworkPropertyMetadata(style));
        base.OnStartup(e);
    }
}
