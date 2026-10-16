using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ReaWin.Models;
using ReaWin.Services;

namespace ReaWin;

/// <summary>Lets the user pick the Ghidra folder and one of the detected JDKs (21+), plus the rea.mjs path.</summary>
public partial class SettingsWindow : Window
{
    private readonly ReaSettings _current;
    private bool _loading = true;

    /// <summary>The settings chosen by the user; valid when ShowDialog returned true.</summary>
    public ReaSettings Result { get; private set; }

    public SettingsWindow(ReaSettings current)
    {
        InitializeComponent();
        _current = current;
        Result = current;

        ThemeManager.Attach(this);
        GhidraBox.Text = current.GhidraInstallDir;
        ReaBox.Text = current.ReaScript;
        (current.Theme switch
        {
            ThemeManager.Light => ThemeLightRadio,
            ThemeManager.Dark => ThemeDarkRadio,
            _ => ThemeSystemRadio,
        }).IsChecked = true;
        LoadJavaList(current.JavaHome);
        ShowGhidraStatus();
        _loading = false;
    }

    // ---- Ghidra ---------------------------------------------------------------

    private void BrowseGhidra_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the extracted Ghidra release folder" };
        if (!string.IsNullOrWhiteSpace(GhidraBox.Text) && Directory.Exists(GhidraBox.Text))
            dialog.InitialDirectory = GhidraBox.Text;
        if (dialog.ShowDialog(this) != true) return;

        // Accept the wrapper folder of an extracted zip and use the real Ghidra root inside it.
        var info = GhidraLocator.Inspect(dialog.FolderName);
        GhidraBox.Text = info.IsValid ? info.Dir : dialog.FolderName;
    }

    private void GhidraBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading) ShowGhidraStatus();
    }

    private void ShowGhidraStatus()
    {
        var info = GhidraLocator.Inspect(GhidraBox.Text);
        var warn = !info.IsValid || (info.Version.Length > 0 && !info.Version.StartsWith("12.1", StringComparison.Ordinal));
        SetStatus(GhidraStatus, info.Message, ok: !warn);
    }

    /// <summary>A status line with an icon and words, in the theme's success or error color (never color alone).</summary>
    private static void SetStatus(TextBlock target, string text, bool ok)
    {
        target.Text = (ok ? "✓ " : "✕ ") + text;
        target.SetResourceReference(TextBlock.ForegroundProperty, ok ? "OkBrush" : "ErrBrush");
    }

    // ---- Java -----------------------------------------------------------------

    private void LoadJavaList(string preferredHome)
    {
        var found = JavaLocator.Detect(preferredHome);
        JavaCombo.ItemsSource = found;

        // Keep the saved choice if it is usable; otherwise the oldest supported JDK (closest to 21).
        var pick = found.FirstOrDefault(j => j.IsSupported &&
                       string.Equals(j.Home, preferredHome?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                   ?? found.Where(j => j.IsSupported).OrderBy(j => j.Major).FirstOrDefault();
        JavaCombo.SelectedItem = pick;
        ShowJavaStatus(found.Count);
    }

    private void RescanJava_Click(object sender, RoutedEventArgs e) =>
        LoadJavaList((JavaCombo.SelectedItem as JavaInstall)?.Home ?? _current.JavaHome);

    private void BrowseJava_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select a JDK folder (the one containing bin\\java.exe)" };
        if (dialog.ShowDialog(this) != true) return;

        var install = JavaLocator.Inspect(dialog.FolderName);
        if (install is null)
        {
            SetStatus(JavaStatus, "That folder is not a JDK (bin\\java.exe and bin\\javac.exe are required).", ok: false);
            return;
        }

        var list = ((IEnumerable<JavaInstall>)JavaCombo.ItemsSource).ToList();
        var existing = list.FirstOrDefault(j => string.Equals(j.Home, install.Home, StringComparison.OrdinalIgnoreCase));
        if (existing is null) { list.Add(install); existing = install; }
        JavaCombo.ItemsSource = list.OrderByDescending(j => j.Major).ToList();
        JavaCombo.SelectedItem = ((IEnumerable<JavaInstall>)JavaCombo.ItemsSource).First(j => j.Home == existing.Home);
        ShowJavaStatus(list.Count);
    }

    private void JavaCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (JavaCombo.ItemsSource is IEnumerable<JavaInstall> list) ShowJavaStatus(list.Count());
    }

    private void ShowJavaStatus(int count)
    {
        if (JavaCombo.SelectedItem is JavaInstall j)
        {
            SetStatus(JavaStatus, $"Selected: {j.Home}", ok: true);
        }
        else
        {
            SetStatus(JavaStatus, count == 0
                ? $"No JDK found. Install JDK {JavaInstall.MinimumMajor}+ (for example Eclipse Temurin) or use Browse…."
                : $"No JDK {JavaInstall.MinimumMajor}+ found among the {count} detected. Install one or use Browse….", ok: false);
        }
    }

    // ---- rea.mjs --------------------------------------------------------------

    private void BrowseRea_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select rea.mjs",
            Filter = "REA script (rea.mjs)|rea.mjs|JavaScript modules (*.mjs)|*.mjs|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true) ReaBox.Text = dialog.FileName;
    }

    // ---- OK -------------------------------------------------------------------

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var ghidra = GhidraLocator.Inspect(GhidraBox.Text);
        var problems = new List<string>();
        if (!ghidra.IsValid) problems.Add("Ghidra folder: " + ghidra.Message);
        if (JavaCombo.SelectedItem is not JavaInstall) problems.Add($"No JDK {JavaInstall.MinimumMajor}+ selected.");
        if (problems.Count > 0 &&
            MessageBox.Show(this, string.Join("\n\n", problems) + "\n\nSave anyway?", "REA settings",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        Result = new ReaSettings(
            ghidra.IsValid ? ghidra.Dir : GhidraBox.Text.Trim(),
            (JavaCombo.SelectedItem as JavaInstall)?.Home ?? "",
            ReaBox.Text.Trim(),
            _current.NodeExe,
            ThemeLightRadio.IsChecked == true ? ThemeManager.Light
                : ThemeDarkRadio.IsChecked == true ? ThemeManager.Dark
                : ThemeManager.FollowWindows);
        DialogResult = true;
    }
}
