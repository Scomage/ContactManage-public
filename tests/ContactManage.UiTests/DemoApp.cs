using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using Xunit.Sdk;

namespace ContactManage.UiTests;

/// <summary>
/// Starts the real app in demo mode and drives it through UI Automation patterns (invoke, toggle, select),
/// so the tests never move the mouse or type on the keyboard of whoever is sitting at the PC.
/// Demo mode reads no real contacts, asks for no password and writes nothing to disk.
/// </summary>
public sealed class DemoApp : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly Application _app;
    private readonly UIA3Automation _automation;

    public Window Main { get; }

    private DemoApp(Application app, UIA3Automation automation, Window main)
    {
        _app = app;
        _automation = automation;
        Main = main;
    }

    public static DemoApp Start(string arguments = "--demo")
    {
        var exe = FindExe();
        var app = Application.Launch(new ProcessStartInfo(exe, arguments) { WorkingDirectory = Path.GetDirectoryName(exe)! });
        var automation = new UIA3Automation();
        try
        {
            var main = app.GetMainWindow(automation, TimeSpan.FromSeconds(30))
                ?? throw new XunitException("The app's main window did not appear.");
            return new DemoApp(app, automation, main);
        }
        catch
        {
            app.Kill();
            automation.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try { if (!_app.HasExited) _app.Kill(); } catch (InvalidOperationException) { }
        _automation.Dispose();
    }

    private static string FindExe()
    {
        // .../tests/ContactManage.UiTests/bin/<Config>/net9.0-windows/  ->  .../src/ContactManage.App/bin/<Config>/net9.0-windows/
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var config = baseDir.Parent!.Name;
        var root = baseDir;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ContactManage.sln"))) root = root.Parent;
        if (root is null) throw new XunitException("Could not find the solution folder.");

        var exe = Path.Combine(root.FullName, "src", "ContactManage.App", "bin", config, baseDir.Name, "ContactManage.App.exe");
        return File.Exists(exe) ? exe : throw new XunitException($"The app has not been built: {exe}");
    }

    // ----- finding things -----

    public static T Wait<T>(Func<T?> find, string what) where T : class
    {
        var result = Retry.WhileNull(find, Timeout, TimeSpan.FromMilliseconds(150));
        return result.Result ?? throw new XunitException($"Timed out waiting for {what}.");
    }

    public static AutomationElement ById(AutomationElement root, string automationId) =>
        Wait(() => root.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), automationId);

    public static Button ButtonById(AutomationElement root, string automationId) => ById(root, automationId).AsButton();

    public static Button ButtonNamed(AutomationElement root, string name) =>
        Wait(() => root.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName(name))), $"button {name}").AsButton();

    public static RadioButton RadioById(AutomationElement root, string automationId) => ById(root, automationId).AsRadioButton();

    public static RadioButton RadioNamed(AutomationElement root, string name) =>
        Wait(() => root.FindFirstDescendant(cf => cf.ByControlType(ControlType.RadioButton).And(cf.ByName(name))), $"radio button {name}").AsRadioButton();

    public static string TextOf(AutomationElement element) => element.Name ?? "";

    /// <summary>An owned window (a dialog) of <paramref name="parent"/> whose title contains the text.</summary>
    public static Window Dialog(AutomationElement parent, string titleContains) =>
        Wait(() => parent.FindAllChildren(cf => cf.ByControlType(ControlType.Window))
            .Select(w => w.AsWindow())
            .FirstOrDefault(w => (w.Title ?? "").Contains(titleContains, StringComparison.Ordinal)), $"window \"{titleContains}\"");

    /// <summary>A message box owned by <paramref name="parent"/> (its caption is exactly "ContactManage").</summary>
    public static Window MessageBox(AutomationElement parent) =>
        Wait(() => parent.FindAllChildren(cf => cf.ByControlType(ControlType.Window))
            .Select(w => w.AsWindow())
            .FirstOrDefault(w => w.Title == "ContactManage"), "a message box");

    /// <summary>A message box owned by <paramref name="parent"/> whose text contains the given text.</summary>
    public static Window MessageBoxContaining(AutomationElement parent, string text) =>
        Wait(() => parent.FindAllChildren(cf => cf.ByControlType(ControlType.Window))
            .Select(w => w.AsWindow())
            .FirstOrDefault(w => w.Title == "ContactManage" && MessageText(w).Contains(text, StringComparison.Ordinal)), $"a message saying \"{text}\"");

    /// <summary>All radio buttons with this exact name, or null when there are none yet.</summary>
    public static List<RadioButton>? FindAll(AutomationElement root, string name)
    {
        var found = root.FindAllDescendants(cf => cf.ByControlType(ControlType.RadioButton).And(cf.ByName(name))).Select(r => r.AsRadioButton()).ToList();
        return found.Count > 0 ? found : null;
    }

    public static string MessageText(Window box) =>
        string.Join("\n", box.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(t => t.Name));

    public static IReadOnlyList<CheckBox> FolderChecks(AutomationElement main) =>
        ById(main, "FolderList").FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox)).Select(c => c.AsCheckBox()).ToList();

    public static IReadOnlyList<string> GroupNames(Window review) =>
        ById(review, "GroupList").FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)).Select(i => i.Name).ToList();

    public static void SelectGroup(Window review, string namePrefix)
    {
        var item = Wait(() => ById(review, "GroupList").FindAllChildren(cf => cf.ByControlType(ControlType.ListItem))
            .FirstOrDefault(i => i.Name.StartsWith(namePrefix, StringComparison.Ordinal)), $"group {namePrefix}");
        item.AsListBoxItem().Select();
    }

    /// <summary>Polls until the element's text satisfies the predicate (text changes arrive a moment after a click).</summary>
    public static void WaitForText(AutomationElement element, Func<string, bool> predicate, string what)
    {
        var ok = Retry.WhileFalse(() => predicate(element.Name ?? ""), Timeout, TimeSpan.FromMilliseconds(150));
        if (!ok.Success) throw new XunitException($"Timed out waiting for {what}. Last text: \"{element.Name}\"");
    }
}
