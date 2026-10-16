using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AiSlop.LocalLlm;
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
        MaxHeight = SystemParameters.WorkArea.Height * 0.95;   // never taller than the screen; the content scrolls
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

        var ai = AiProfile.Resolve(current);
        AiEnabledBox.IsChecked = ai.Enabled;
        (ai.Processor == AiProcessor.Gpu ? AiGpuRadio : AiCpuRadio).IsChecked = true;
        AiWithAppBox.IsChecked = ai.Startup == AiStartup.WithApp;
        AiContextBox.Text = ai.ContextSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AiOptions.IsEnabled = ai.Enabled;
        ShowAiStatus();
        _loading = false;
        Loaded += async (_, _) => await DetectGpuAsync();
    }

    // ---- Local AI -------------------------------------------------------------

    private void AiEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AiOptions.IsEnabled = AiEnabledBox.IsChecked == true;
    }

    /// <summary>Whether the engine and model are where the app looks; if not, the message says how to install them.</summary>
    private void ShowAiStatus()
    {
        var files = AiFiles.Locate();
        var problems = AiProfile.ForEngine(AiProfile.DefaultSettings, files).Validate()
            .Where(p => p.Contains("not installed", StringComparison.OrdinalIgnoreCase)).ToList();
        if (problems.Count == 0) SetStatus(AiStatus, $"AI engine and model found ({files.Folder}).", ok: true);
        else SetStatus(AiStatus, problems[0], ok: false);
    }

    /// <summary>
    /// Asks the engine itself which GPUs it can use (it is the PrismML build that decides, not Windows). GPU stays unavailable
    /// until it reports one, so the choice can never promise what will not run.
    /// </summary>
    private async Task DetectGpuAsync()
    {
        var files = AiFiles.Locate();
        AiGpuStatus.Text = "Checking for a usable GPU…";
        IReadOnlyList<string> devices = files.ServerPresent ? await GpuSupport.DetectAsync(files.ServerPath) : [];
        if (devices.Count > 0)
        {
            AiGpuRadio.IsEnabled = true;
            AiGpuStatus.Text = $"GPU found: {devices[0]}. GPU is much faster; it needs about 7 GB of its memory for this model.";
        }
        else
        {
            AiGpuRadio.IsEnabled = false;
            if (AiGpuRadio.IsChecked == true) AiCpuRadio.IsChecked = true;
            AiGpuStatus.Text = files.ServerPresent
                ? "The engine found no usable GPU, so GPU is not offered."
                : "GPU can be chosen once the engine is installed.";
        }
    }

    /// <summary>The context size field, checked against the same range the engine accepts; shows the problem under the field.</summary>
    private bool TryReadContextSize(out int size)
    {
        var ok = int.TryParse(AiContextBox.Text.Trim(), out size) && size is >= 512 and <= 131072;
        if (ok) AiContextStatus.Text = "";
        else SetStatus(AiContextStatus, "Context size must be a whole number between 512 and 131072.", ok: false);
        return ok;
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
        if (!TryReadContextSize(out var contextSize)) { AiContextBox.Focus(); return; }

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
                : ThemeManager.FollowWindows,
            // the port stays whatever it was; paths to the installed files are never saved
            AiProfile.Resolve(_current) with
            {
                Enabled = AiEnabledBox.IsChecked == true,
                Processor = AiGpuRadio.IsChecked == true ? AiProcessor.Gpu : AiProcessor.Cpu,
                Startup = AiWithAppBox.IsChecked == true ? AiStartup.WithApp : AiStartup.OnFirstUse,
                ContextSize = contextSize,
            });
        DialogResult = true;
    }
}
