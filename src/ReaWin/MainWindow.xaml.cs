using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ReaWin.Models;
using ReaWin.Services;

namespace ReaWin;

public partial class MainWindow : Window
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReaWin", "settings.json");

    private List<BinaryItem> _all = new();
    private List<ProcedureInfo> _procedures = new();
    private List<StringInfo> _strings = new();
    private string _root = "";
    private string? _analyzedPath;
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        var s = LoadSettings();
        GhidraBox.Text = s.GhidraInstallDir;
        JavaBox.Text = s.JavaHome;
        ReaBox.Text = s.ReaScript;
        Closing += (_, _) => { SaveSettings(); _cts?.Cancel(); };
    }

    // ---- 1. choose a directory -------------------------------------------------

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the application's folder" };
        if (dialog.ShowDialog(this) != true) return;
        _root = dialog.FolderName;
        FolderBox.Text = _root;
        await ScanAsync();
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    // ---- 2. detect exe and dll files -------------------------------------------

    private async Task ScanAsync()
    {
        if (string.IsNullOrEmpty(_root)) return;
        using var cts = BeginWork("Scanning for .exe and .dll files…");
        try
        {
            var progress = new Progress<string>(p => StatusText.Text = $"Scanning: {p}");
            var files = await PeScanner.ScanAsync(_root, progress, cts.Token);
            _all = EntryRanker.Rank(files, _root);
            RescanButton.IsEnabled = true;
            ApplyFilter();

            var electron = EntryRanker.LooksLikeElectron(_root);
            JsButton.Visibility = electron ? Visibility.Visible : Visibility.Collapsed;
            BannerText.Visibility = electron ? Visibility.Visible : Visibility.Collapsed;
            BannerText.Text = "This looks like an Electron app (resources\\app.asar). The native .exe is mostly the " +
                              "Chromium shell; the application logic is JavaScript. Use \"Analyze as JavaScript app\".";

            var rec = _all.FirstOrDefault(i => i.Recommended);
            StatusText.Text = rec is null
                ? $"Found {files.Count} binaries. No single program stood out; pick one from the list."
                : $"Found {files.Count} binaries. Recommended main program: {rec.Name}.";
            if (rec is not null) BinariesGrid.SelectedItem = rec;
        }
        catch (OperationCanceledException) { StatusText.Text = "Scan cancelled."; }
        finally { EndWork(); }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var hide = HideHelpersBox.IsChecked == true;
        var shown = _all.Where(i => !hide || i.Recommended || !i.LikelyHelper).ToList();
        BinariesGrid.ItemsSource = shown;
        CountText.Text = $"{shown.Count} shown of {_all.Count}";
    }

    private void Binaries_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var item = BinariesGrid.SelectedItem as BinaryItem;
        AnalyzeButton.IsEnabled = item is not null && _cts is null;
        AnalyzeRecommendedButton.IsEnabled = _all.Any(i => i.Recommended) && _cts is null;
        WhyBox.Text = item is null ? "" : Explain(item);
    }

    private static string Explain(BinaryItem item)
    {
        var pe = item.Pe;
        var sb = new StringBuilder();
        sb.AppendLine($"{item.Name}  —  score {item.Score}/100  —  {item.Role}");
        sb.AppendLine();
        foreach (var r in item.Reasons) sb.AppendLine($"  • {r}");
        sb.AppendLine();
        sb.AppendLine($"Path:        {pe.RelativePath}");
        sb.AppendLine($"Machine:     {pe.Machine}    Subsystem: {pe.Subsystem}");
        if (!pe.IsDll) sb.AppendLine($"Entry RVA:   0x{pe.EntryPointRva:x}");
        if (!string.IsNullOrWhiteSpace(pe.ProductName)) sb.AppendLine($"Product:     {pe.ProductName}");
        if (!string.IsNullOrWhiteSpace(pe.FileDescription)) sb.AppendLine($"Description: {pe.FileDescription}");
        if (!string.IsNullOrWhiteSpace(pe.CompanyName)) sb.AppendLine($"Company:     {pe.CompanyName}");
        if (pe.Exports.Count > 0)
            sb.AppendLine($"Exports ({pe.Exports.Count}): {string.Join(", ", pe.Exports.Take(25))}{(pe.Exports.Count > 25 ? ", …" : "")}");
        if (pe.Imports.Count > 0)
            sb.AppendLine($"Imports ({pe.Imports.Count} DLLs): {string.Join(", ", pe.Imports.Take(25))}{(pe.Imports.Count > 25 ? ", …" : "")}");
        sb.AppendLine();
        sb.AppendLine("Hints are heuristics from PE headers and file names. Static imports cannot show plugins or");
        sb.AppendLine("libraries loaded at runtime, so treat the recommendation as a starting point.");
        return sb.ToString();
    }

    // ---- 3. run REA ---------------------------------------------------------------

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (BinariesGrid.SelectedItem is BinaryItem item) await AnalyzeAsync(item);
    }

    private async void AnalyzeRecommended_Click(object sender, RoutedEventArgs e)
    {
        var rec = _all.FirstOrDefault(i => i.Recommended);
        if (rec is null) return;
        BinariesGrid.SelectedItem = rec;
        await AnalyzeAsync(rec);
    }

    private async Task AnalyzeAsync(BinaryItem item)
    {
        var path = item.Pe.FullPath;
        var client = new ReaClient(CurrentSettings());
        using var cts = BeginWork($"Running REA + Ghidra on {item.Name} (first run can take minutes)…");
        try
        {
            if (item.Pe.IsManaged)
            {
                Log($"{item.Name} is a .NET assembly; Windows Ghidra support covers native PE only. Using managed inspection.");
                using var doc = await client.RunJsonAsync(cts.Token, "inspect-managed-artifact", path);
                OverviewBox.Text = Pretty(doc);
                _procedures = new(); _strings = new(); _analyzedPath = null;
                FunctionsGrid.ItemsSource = null; StringsGrid.ItemsSource = null;
                Tabs.SelectedItem = Tabs.Items[1];
                StatusText.Text = "Managed inspection complete.";
                return;
            }

            // Three independent Ghidra sessions run in parallel; each imports and analyzes the file.
            var overview = client.AnalyzeAsync(path, cts.Token);
            var procs = client.ListProceduresAsync(path, cts.Token);
            var strs = client.ListStringsAsync(path, cts.Token);
            await Task.WhenAll(overview, procs, strs);

            _analyzedPath = path;
            _procedures = procs.Result
                .OrderByDescending(p => p.IsEntry).ThenBy(p => p.IsImport).ThenBy(p => p.IsAutoNamed).ThenBy(p => p.Address, StringComparer.Ordinal)
                .ToList();
            _strings = strs.Result;
            OverviewBox.Text = FormatOverview(item, overview.Result, _procedures);
            ApplyFunctionFilter();
            ApplyStringFilter();
            Tabs.SelectedItem = Tabs.Items[1];
            StatusText.Text = $"Done: {_procedures.Count(p => !p.IsImport)} functions, {_strings.Count} strings.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Cancelled."; }
        catch (ReaException ex) { ShowFailure(ex); }
        finally { EndWork(); }
    }

    private static string FormatOverview(BinaryItem item, OverviewResult o, List<ProcedureInfo> procedures)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"File        {item.Name}");
        sb.AppendLine($"SHA-256     {o.Sha256}");
        sb.AppendLine($"Format      {o.Format} / {o.Architecture}");
        sb.AppendLine($"Image base  {o.ImageBase}");
        sb.AppendLine($"Functions   {o.ProcedureCount} (includes imports)");
        sb.AppendLine($"Strings     {o.StringCount}");
        sb.AppendLine($"Evidence    {o.EvidenceId}");
        var entry = procedures.FirstOrDefault(p => p.IsEntry);
        if (entry is not null) sb.AppendLine($"Entry       {entry.Address}  (Ghidra's PE entry point; not necessarily main)");
        sb.AppendLine();
        sb.AppendLine("Sections");
        foreach (var s in o.Segments) sb.AppendLine($"  {s.Name,-10} {s.Start} – {s.End}  {s.Length,8} bytes");
        sb.AppendLine();
        sb.AppendLine("Imported libraries (APIs the program calls)");
        foreach (var g in procedures.Where(p => p.IsImport).GroupBy(p => p.Library).OrderByDescending(g => g.Count()))
            sb.AppendLine($"  {(g.Key.Length == 0 ? "(unspecified)" : g.Key),-28} {g.Count()} API(s)");
        sb.AppendLine();
        sb.AppendLine("Limitations reported by REA");
        foreach (var l in o.Limitations) sb.AppendLine($"  - {l}");
        return sb.ToString();
    }

    // ---- functions, strings, decompile ------------------------------------------------

    private void FuncFilter_Changed(object sender, TextChangedEventArgs e) => ApplyFunctionFilter();
    private void FuncFilter_Click(object sender, RoutedEventArgs e) => ApplyFunctionFilter();
    private void StrFilter_Changed(object sender, TextChangedEventArgs e) => ApplyStringFilter();

    private void ApplyFunctionFilter()
    {
        if (FunctionsGrid is null || FuncFilterBox is null) return;
        var text = FuncFilterBox.Text;
        var internalOnly = InternalOnlyBox.IsChecked == true;
        FunctionsGrid.ItemsSource = _procedures
            .Where(p => !internalOnly || !p.IsImport)
            .Where(p => text.Length == 0 || p.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                        p.Address.Contains(text, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void ApplyStringFilter()
    {
        if (StringsGrid is null || StrFilterBox is null) return;
        var text = StrFilterBox.Text;
        StringsGrid.ItemsSource = _strings
            .Where(s => text.Length == 0 || s.Value.Contains(text, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async void Functions_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        await DecompileSelectedAsync();

    private async void Decompile_Click(object sender, RoutedEventArgs e) => await DecompileSelectedAsync();

    private async Task DecompileSelectedAsync()
    {
        if (FunctionsGrid.SelectedItem is not ProcedureInfo proc || _analyzedPath is null) return;
        if (proc.IsImport)
        {
            StatusText.Text = "Imports are provided by another DLL and have no code in this file.";
            return;
        }
        var client = new ReaClient(CurrentSettings());
        using var cts = BeginWork($"Decompiling {proc.Name} at {proc.Address} (starts a new Ghidra session)…");
        try
        {
            CodeBox.Text = await client.DecompileAsync(_analyzedPath, proc.Address, cts.Token);
            Tabs.SelectedItem = Tabs.Items[4];
            StatusText.Text = $"Decompiled {proc.Name}.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Cancelled."; }
        catch (ReaException ex) { ShowFailure(ex); }
        finally { EndWork(); }
    }

    // ---- JavaScript / Electron ----------------------------------------------------------

    private async void Js_Click(object sender, RoutedEventArgs e)
    {
        var client = new ReaClient(CurrentSettings());
        using var cts = BeginWork("Statically analyzing JavaScript application…");
        try
        {
            using var doc = await client.RunJsonAsync(cts.Token, "analyze-javascript-application", _root);
            OverviewBox.Text = Pretty(doc);
            Tabs.SelectedItem = Tabs.Items[1];
            StatusText.Text = "JavaScript analysis complete (raw evidence shown in Overview).";
        }
        catch (OperationCanceledException) { StatusText.Text = "Cancelled."; }
        catch (ReaException ex) { ShowFailure(ex); }
        finally { EndWork(); }
    }

    // ---- doctor ---------------------------------------------------------------------------

    private async void Doctor_Click(object sender, RoutedEventArgs e)
    {
        var client = new ReaClient(CurrentSettings());
        using var cts = BeginWork("Running rea doctor…");
        try
        {
            using var doc = await client.RunJsonAsync(cts.Token, "doctor");
            var sb = new StringBuilder("rea doctor — Ghidra-related checks\n");
            foreach (var c in doc.RootElement.GetProperty("scope_checks").EnumerateArray())
            {
                var name = c.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith("ghidra", StringComparison.Ordinal) && name != "node" && name != "host") continue;
                var ok = c.GetProperty("ok").GetBoolean();
                var detail = c.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : "";
                sb.AppendLine($"  [{(ok ? "ok" : "FAIL")}] {name}: {detail}");
            }
            Log(sb.ToString());
            Tabs.SelectedItem = Tabs.Items[5];
            StatusText.Text = "Doctor finished; see Log.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Cancelled."; }
        catch (ReaException ex) { ShowFailure(ex); }
        finally { EndWork(); }
    }

    // ---- plumbing -----------------------------------------------------------------------------

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private CancellationTokenSource BeginWork(string status)
    {
        _cts = new CancellationTokenSource();
        StatusText.Text = status;
        Busy.Visibility = Visibility.Visible;
        BrowseButton.IsEnabled = RescanButton.IsEnabled = AnalyzeButton.IsEnabled = AnalyzeRecommendedButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        return _cts;
    }

    private void EndWork()
    {
        _cts = null;
        Busy.Visibility = Visibility.Collapsed;
        BrowseButton.IsEnabled = true;
        RescanButton.IsEnabled = !string.IsNullOrEmpty(_root);
        CancelButton.IsEnabled = false;
        Binaries_SelectionChanged(this, null!);
    }

    private void ShowFailure(ReaException ex)
    {
        Log($"REA failed (exit {ex.ExitCode}):\n{ex.Message}");
        Tabs.SelectedItem = Tabs.Items[5];
        StatusText.Text = "REA reported an error; see Log. Try \"Run rea doctor\" in settings.";
    }

    private void Log(string text) =>
        LogBox.Text += $"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}";

    private static string Pretty(JsonDocument doc)
    {
        var text = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        return text.Length <= 200_000 ? text : text[..200_000] + "\n… (truncated for display)";
    }

    private ReaSettings CurrentSettings() =>
        new(GhidraBox.Text.Trim(), JavaBox.Text.Trim(), ReaBox.Text.Trim(), "node");

    private static ReaSettings LoadSettings()
    {
        var defaults = ReaClient.DefaultSettings();
        try
        {
            if (File.Exists(SettingsPath))
            {
                var saved = JsonSerializer.Deserialize<ReaSettings>(File.ReadAllText(SettingsPath));
                if (saved is not null)
                    return new ReaSettings(
                        string.IsNullOrWhiteSpace(saved.GhidraInstallDir) ? defaults.GhidraInstallDir : saved.GhidraInstallDir,
                        string.IsNullOrWhiteSpace(saved.JavaHome) ? defaults.JavaHome : saved.JavaHome,
                        string.IsNullOrWhiteSpace(saved.ReaScript) ? defaults.ReaScript : saved.ReaScript,
                        "node");
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return defaults;
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(CurrentSettings()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
