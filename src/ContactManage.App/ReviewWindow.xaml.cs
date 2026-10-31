using System.Windows;
using ContactManage.Core;

namespace ContactManage.App;

public partial class ReviewWindow : Window
{
    private readonly ReviewViewModel _vm;

    public ReviewWindow(MergeResult result, bool demo = false)
    {
        InitializeComponent();
        if (demo) Title += " (demo data)";
        _vm = new ReviewViewModel(result);
        DataContext = _vm;
    }

    /// <summary>The combined list once the user pressed Finish.</summary>
    public IReadOnlyList<Contact>? FinalList { get; private set; }

    /// <summary>What was merged, kept apart and left undecided.</summary>
    public MergeSummary? Summary { get; private set; }

    private void Back_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        var result = _vm.Result;
        if (!result.IsComplete)
        {
            var summary = result.Summary;
            var answer = MessageBox.Show(this,
                $"{summary.Deferred:N0} duplicate {(summary.Deferred == 1 ? "set still needs" : "sets still need")} a decision.\n\n" +
                "Everything you have decided will be applied and saved. The undecided sets are left exactly as they are, " +
                "with their contacts not merged, so you can deal with them another time.\n\nSave what you have decided so far?",
                "ContactManage", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }

        FinalList = result.BuildPartialList();
        Summary = result.Summary;
        DialogResult = true;
    }
}
