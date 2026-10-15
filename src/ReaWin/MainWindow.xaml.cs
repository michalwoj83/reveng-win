using System.IO;
using System.Security.Cryptography;
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

    /// <summary>Functions per background batch. Small enough that a click never waits long behind one.</summary>
    private const int BulkChunk = 10;

    private List<BinaryItem> _all = new();
    private List<ProcedureInfo> _procedures = new();
    private List<StringInfo> _strings = new();
    private string _root = "";
    private string? _analyzedPath;
    private OverviewResult? _overview;
    private readonly ProjectLibrary _library = new(ProjectLibrary.DefaultFolder);
    private string? _projectId;        // catalog id of the current analysis once it has been stored
    private bool _dirty;               // code was added since the project was last stored
    private bool _suppressProjectSelection;
    private CancellationTokenSource? _cts;
    private ReaSettings _settings;

    // One Ghidra session per analyzed binary. Decompiled code is cached by address as REA returned it.
    private ReaSession? _session;
    private Task<ReaSession?>? _sessionStart;
    private readonly Dictionary<string, string> _decompiled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _decompileErrors = new(StringComparer.Ordinal);
    private CancellationTokenSource? _bulkCts;
    private Task _bulkTask = Task.CompletedTask;
    private int _interactive;          // on-demand decompiles in flight; the background loop yields to them
    private int _selectionVersion;     // lets a late answer be cached without overwriting a newer selection

    public MainWindow()
    {
        InitializeComponent();
        _settings = LoadSettings();
        ShowSettingsSummary();
        RefreshProjectList(null);
        if (ProjectsList.Items.Count > 0) LeftTabs.SelectedItem = ProjectsTab;
        Closing += (_, _) =>
        {
            SaveSettings();
            _cts?.Cancel();
            _bulkCts?.Cancel();
            SaveOnExit();
            DisposeSession();
        };
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _settings = dialog.Result;
        SaveSettings();
        ShowSettingsSummary();
    }

    private void ShowSettingsSummary()
    {
        var ghidra = string.IsNullOrWhiteSpace(_settings.GhidraInstallDir) ? "not set" : _settings.GhidraInstallDir;
        var java = string.IsNullOrWhiteSpace(_settings.JavaHome) ? "not set" : _settings.JavaHome;
        SettingsSummary.Text = $"Ghidra: {ghidra}   |   Java: {java}";
        SettingsSummary.Foreground = string.IsNullOrWhiteSpace(_settings.GhidraInstallDir) ||
                                     string.IsNullOrWhiteSpace(_settings.JavaHome)
            ? System.Windows.Media.Brushes.Firebrick
            : System.Windows.Media.Brushes.Gray;
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
        await StopBackgroundWorkAsync();
        await StoreProjectAsync();   // keep code added to the project being replaced
        DisposeSession();
        ClearAnalysis();

        using var cts = BeginWork($"Running REA + Ghidra on {item.Name}…");
        try
        {
            if (item.Pe.IsManaged)
            {
                Log($"{item.Name} is a .NET assembly; Windows Ghidra support covers native PE only. Using managed inspection.");
                var client = new ReaClient(CurrentSettings());
                using var doc = await client.RunJsonAsync(cts.Token, "inspect-managed-artifact", path);
                OverviewBox.Text = Pretty(doc);
                Tabs.SelectedItem = OverviewTab;
                StatusText.Text = "Managed inspection complete.";
                return;
            }

            // One session: Ghidra imports and analyzes the file once (the slow part), then every query is fast.
            var session = await ReaSession.OpenAsync(CurrentSettings(), path, cts.Token);
            OverviewResult overview;
            List<ProcedureInfo> procs;
            List<StringInfo> strs;
            try
            {
                StatusText.Text = $"Analyzing {item.Name} with Ghidra (done once; usually 1–3 minutes)…";
                overview = await session.OverviewAsync(cts.Token);
                StatusText.Text = "Reading functions and strings…";
                var procsTask = session.ListProceduresAsync(cts.Token);
                var strsTask = session.ListStringsAsync(cts.Token);
                await Task.WhenAll(procsTask, strsTask);
                procs = procsTask.Result;
                strs = strsTask.Result;
            }
            catch
            {
                session.Dispose();
                throw;
            }

            _session = session;
            _analyzedPath = path;
            _overview = overview;
            _procedures = procs
                .OrderByDescending(p => p.IsEntry).ThenBy(p => p.IsImport).ThenBy(p => p.IsAutoNamed).ThenBy(p => p.Address, StringComparer.Ordinal)
                .ToList();
            _strings = strs;
            OverviewBox.Text = FormatOverview(item.Name, overview, _procedures);
            ApplyFunctionFilter();
            ApplyStringFilter();
            SaveProjectButton.IsEnabled = true;
            Tabs.SelectedItem = FunctionsTab;
            StatusText.Text = $"Done: {_procedures.Count(p => !p.IsImport)} functions, {_strings.Count} strings. " +
                              "Select a function to read its code.";
            _dirty = true;
            await StoreProjectAsync();   // the analysis is the expensive part; keep it before anything else can fail
            StartBulkDecompile(session);
        }
        catch (OperationCanceledException) { StatusText.Text = "Cancelled."; }
        catch (ReaException ex) { ShowFailure(ex); }
        finally { EndWork(); }
    }

    private static string FormatOverview(string name, OverviewResult o, List<ProcedureInfo> procedures)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"File        {name}");
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

    // ---- functions, strings ----------------------------------------------------------------

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

    // ---- decompile ------------------------------------------------------------------------------

    private async void Functions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FunctionsGrid.SelectedItem is ProcedureInfo proc) await ShowFunctionAsync(proc);
    }

    /// <summary>Shows one function's code: from the cache when present, otherwise one fast query to the session.</summary>
    private async Task ShowFunctionAsync(ProcedureInfo proc)
    {
        var version = ++_selectionVersion;
        CodeHeader.Text = $"{proc.Name}   @ {proc.Address}";

        if (proc.IsImport)
        {
            CodeBox.Text = $"// {proc.Name} is an import. Its code is in another DLL, not in this file.";
            return;
        }
        if (_decompiled.TryGetValue(proc.Address, out var cached))
        {
            CodeBox.Text = cached;
            return;
        }
        if (_decompileErrors.TryGetValue(proc.Address, out var knownError))
        {
            CodeBox.Text = $"// REA could not decompile this function:\n// {knownError}";
            return;
        }

        CodeBox.Text = _session is null
            ? "// Starting Ghidra for this project (about as long as the first analysis)…"
            : "// Decompiling…";
        try
        {
            var session = _session ?? await EnsureSessionAsync();
            if (session is null) return;

            _interactive++;
            string code;
            try { code = await session.DecompileAsync(proc.Address, CancellationToken.None); }
            finally { _interactive--; }

            _decompiled[proc.Address] = code;
            _dirty = true;
            if (version == _selectionVersion) CodeBox.Text = code;
        }
        catch (ReaException ex)
        {
            _decompileErrors[proc.Address] = ex.Message;
            Log($"Decompile {proc.Name} @ {proc.Address} failed:\n{ex.Message}");
            if (version == _selectionVersion)
                CodeBox.Text = $"// REA could not decompile this function (details in the Log tab):\n// {FirstLine(ex.Message)}";
        }
        catch (OperationCanceledException)
        {
            if (version == _selectionVersion) CodeBox.Text = "// Cancelled. Select the function again to retry.";
        }
    }

    /// <summary>
    /// Opens a Ghidra session for a project that was loaded from disk. Needed only for functions whose code was
    /// not saved; everything else in the project is viewable without it.
    /// </summary>
    private Task<ReaSession?> EnsureSessionAsync()
    {
        if (_session is not null) return Task.FromResult<ReaSession?>(_session);
        // Rapid clicks share one start; a finished start that gave no session (file missing) may be retried.
        if (_sessionStart is null || _sessionStart.IsCompleted) _sessionStart = StartSessionAsync();
        return _sessionStart;
    }

    private async Task<ReaSession?> StartSessionAsync()
    {
        var path = _analyzedPath;
        if (path is null || !File.Exists(path))
        {
            CodeBox.Text = $"// The analyzed file is not available, so code that was not saved cannot be decompiled:\n// {path}";
            return null;
        }

        using var cts = BeginWork($"Starting Ghidra for {Path.GetFileName(path)} (the saved project stays usable meanwhile)…");
        try
        {
            var session = await ReaSession.OpenAsync(CurrentSettings(), path, cts.Token);
            try { await session.OverviewAsync(cts.Token); }   // forces the one-time import and analysis
            catch
            {
                session.Dispose();
                throw;
            }
            _session = session;
            StatusText.Text = "Ghidra is ready; functions now decompile on demand.";
            StartBulkDecompile(session);   // fill in whatever the saved project did not contain
            return session;
        }
        finally { EndWork(); }
    }

    private void StartBulkDecompile(ReaSession session)
    {
        var todo = _procedures
            .Where(p => !p.IsImport && !_decompiled.ContainsKey(p.Address) && !_decompileErrors.ContainsKey(p.Address))
            .Select(p => p.Address)
            .ToList();
        if (todo.Count == 0) return;

        _bulkCts = new CancellationTokenSource();
        _bulkTask = RunBulkDecompileAsync(session, todo, _bulkCts.Token);
    }

    /// <summary>
    /// Decompiles every program function in small batches while the user browses, so that selecting a function
    /// is a cache hit and the saved project holds all the code. Cancellation is checked between batches only.
    /// </summary>
    private async Task RunBulkDecompileAsync(ReaSession session, List<string> todo, CancellationToken ct)
    {
        var total = todo.Count;
        var done = 0;
        BulkBar.Maximum = total;
        BulkBar.Value = 0;
        BulkText.Text = $"Decompiling 0/{total}";
        BulkPanel.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true;
        try
        {
            for (var i = 0; i < total; i += BulkChunk)
            {
                ct.ThrowIfCancellationRequested();
                while (_interactive > 0) await Task.Delay(15, ct);   // a click is waiting; let it go first

                var part = todo.GetRange(i, Math.Min(BulkChunk, total - i));
                var results = await session.DecompileBatchAsync(part, ct);
                foreach (var r in results)
                {
                    if (r.Code is not null) _decompiled[r.Address] = r.Code;
                    else if (r.Error is not null) _decompileErrors[r.Address] = r.Error;
                }
                _dirty = true;
                done += part.Count;
                BulkBar.Value = done;
                BulkText.Text = $"Decompiling {done}/{total}";
            }
            StatusText.Text = $"All {total} functions are decompiled and cached. Save the project to reopen it without Ghidra.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"Background decompile stopped at {done}/{total}. Other functions still decompile when selected.";
        }
        catch (ReaException ex) { ShowFailure(ex); }
        finally
        {
            BulkPanel.Visibility = Visibility.Collapsed;
            _bulkCts?.Dispose();
            _bulkCts = null;
            CancelButton.IsEnabled = _cts is not null;
        }
        await StoreProjectAsync();   // whatever was decompiled is kept, even if the run was stopped
    }

    private async Task StopBackgroundWorkAsync()
    {
        _bulkCts?.Cancel();
        try { await _bulkTask; }
        catch (OperationCanceledException) { }
    }

    private void DisposeSession()
    {
        _session?.Dispose();
        _session = null;
    }

    /// <summary>Forgets the previous analysis so nothing from it can be clicked while a new one runs.</summary>
    private void ClearAnalysis()
    {
        _analyzedPath = null;
        _overview = null;
        _projectId = null;
        _dirty = false;
        _procedures = new();
        _strings = new();
        _decompiled.Clear();
        _decompileErrors.Clear();
        _selectionVersion++;
        FunctionsGrid.ItemsSource = null;
        StringsGrid.ItemsSource = null;
        CodeBox.Text = "";
        CodeHeader.Text = "Select a function to see its decompiled code.";
        SaveProjectButton.IsEnabled = false;
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOf('\n');
        var line = end < 0 ? text : text[..end];
        return line.Length <= 300 ? line : line[..300] + "…";
    }

    // ---- projects ---------------------------------------------------------------------------------

    private async void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        if (_analyzedPath is null || _overview is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "Export REA project",
            Filter = $"REA project (*{ProjectStore.Extension})|*{ProjectStore.Extension}",
            DefaultExt = ProjectStore.Extension,
            FileName = Path.GetFileNameWithoutExtension(_analyzedPath) + ProjectStore.Extension,
        };
        if (dialog.ShowDialog(this) != true) return;

        var project = BuildProjectFile();
        var totalFunctions = _procedures.Count(p => !p.IsImport);
        var target = dialog.FileName;

        SaveProjectButton.IsEnabled = false;
        StatusText.Text = "Exporting project…";
        try
        {
            await Task.Run(() => ProjectStore.SaveAsync(target, project));
            var size = new FileInfo(target).Length / 1_048_576.0;
            StatusText.Text = $"Exported {target} ({size:0.0} MB, code for {project.Decompiled.Count} of {totalFunctions} functions).";
        }
        catch (ReaException ex) { ShowFailure(ex); }
        finally { SaveProjectButton.IsEnabled = _analyzedPath is not null; }
    }

    /// <summary>
    /// Snapshot of the current analysis. Copied on the UI thread because the background decompile mutates the
    /// dictionaries; serializing and writing can then happen off it.
    /// </summary>
    private ProjectFile BuildProjectFile() => new()
    {
        TargetPath = _analyzedPath!,
        Root = _root,
        Overview = _overview!,
        Procedures = _procedures.ToList(),
        Strings = _strings.ToList(),
        Decompiled = new Dictionary<string, string>(_decompiled),
        DecompileErrors = new Dictionary<string, string>(_decompileErrors),
    };

    /// <summary>Stores the current analysis in the app's project folder and refreshes the Projects list.</summary>
    private async Task StoreProjectAsync()
    {
        if (_analyzedPath is null || _overview is null || !_dirty) return;
        var id = ProjectLibrary.IdFor(_analyzedPath, _overview.Sha256);
        var project = BuildProjectFile();
        _dirty = false;
        try
        {
            await Task.Run(() => _library.SaveAsync(id, project));
            _projectId = id;
            RefreshProjectList(id);
        }
        catch (ReaException ex)
        {
            _dirty = true;
            Log($"Could not store the project automatically:\n{ex.Message}");
        }
    }

    /// <summary>Last-chance store when the window closes; bounded so closing never hangs.</summary>
    private void SaveOnExit()
    {
        if (!_dirty || _analyzedPath is null || _overview is null) return;
        var id = ProjectLibrary.IdFor(_analyzedPath, _overview.Sha256);
        var project = BuildProjectFile();
        try { Task.Run(() => _library.SaveAsync(id, project)).Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
    }

    private void RefreshProjectList(string? selectId)
    {
        var projects = _library.List();
        _suppressProjectSelection = true;
        try
        {
            ProjectsList.ItemsSource = projects;
            ProjectsList.SelectedItem = projects.FirstOrDefault(p => p.Id == selectId);
        }
        finally { _suppressProjectSelection = false; }
        ProjectsHint.Text = projects.Count == 0
            ? "Analyses appear here automatically."
            : $"{projects.Count} stored in {_library.Folder}";
        DeleteProjectButton.IsEnabled = ProjectsList.SelectedItem is not null;
    }

    private async void Projects_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DeleteProjectButton.IsEnabled = ProjectsList.SelectedItem is not null;
        if (_suppressProjectSelection || ProjectsList.SelectedItem is not ProjectMeta meta) return;
        if (_cts is not null)
        {
            StatusText.Text = "Wait for the current operation to finish (or cancel it), then select the project again.";
            RefreshProjectList(_projectId);
            return;
        }
        await OpenProjectFileAsync(_library.ProjectPath(meta.Id));
    }

    private void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectsList.SelectedItem is not ProjectMeta meta) return;
        var answer = MessageBox.Show(this,
            $"Delete the stored analysis of {meta.Name}?\n\nThe binary itself is not touched; analyzing it again recreates the project.",
            "Delete project", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            _library.Delete(meta.Id);
            if (_projectId == meta.Id) _projectId = null;
            RefreshProjectList(_projectId);
            StatusText.Text = $"Deleted the project of {meta.Name}.";
        }
        catch (ReaException ex) { ShowFailure(ex); }
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open REA project",
            Filter = $"REA project (*{ProjectStore.Extension})|*{ProjectStore.Extension}|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        await OpenProjectFileAsync(dialog.FileName);
    }

    /// <summary>Loads a project file (from the library or anywhere else) and shows it; no Ghidra is involved.</summary>
    private async Task OpenProjectFileAsync(string file)
    {
        await StopBackgroundWorkAsync();
        await StoreProjectAsync();   // keep code added to the project being left
        DisposeSession();

        ProjectFile project;
        using (var cts = BeginWork("Opening project…"))
        {
            try
            {
                project = await Task.Run(() => ProjectStore.LoadAsync(file, cts.Token), cts.Token);
            }
            catch (OperationCanceledException) { StatusText.Text = "Cancelled."; EndWork(); return; }
            catch (ReaException ex) { ShowFailure(ex); EndWork(); return; }

            ClearAnalysis();
            _analyzedPath = project.TargetPath;
            _overview = project.Overview;
            _projectId = ProjectLibrary.IdFor(project.TargetPath, project.Overview.Sha256);
            // A file from outside the project folder is added to it, so it shows up in the list from now on.
            var inLibrary = string.Equals(Path.GetFullPath(file), Path.GetFullPath(_library.ProjectPath(_projectId)),
                StringComparison.OrdinalIgnoreCase);
            _dirty = !inLibrary;
            _root = project.Root;
            FolderBox.Text = _root;
            _procedures = project.Procedures;
            _strings = project.Strings;
            foreach (var (address, code) in project.Decompiled) _decompiled[address] = code;
            foreach (var (address, error) in project.DecompileErrors) _decompileErrors[address] = error;

            OverviewBox.Text = FormatOverview(Path.GetFileName(project.TargetPath), project.Overview, _procedures);
            ApplyFunctionFilter();
            ApplyStringFilter();
            SaveProjectButton.IsEnabled = true;
            Tabs.SelectedItem = FunctionsTab;
            EndWork();
        }

        var functions = _procedures.Count(p => !p.IsImport);
        var missing = functions - _decompiled.Count - _decompileErrors.Count;
        var status = $"Project opened: {functions} functions, {_strings.Count} strings, code saved for {_decompiled.Count}.";
        if (missing > 0)
            status += $" {missing} functions were not decompiled when it was saved; selecting one starts Ghidra.";

        // Refresh the binaries list if the folder is still there; the project itself does not depend on it.
        if (Directory.Exists(_root))
        {
            await ScanAsync();
            var match = _all.FirstOrDefault(i => string.Equals(i.Pe.FullPath, _analyzedPath, StringComparison.OrdinalIgnoreCase));
            if (match is not null) BinariesGrid.SelectedItem = match;
        }
        StatusText.Text = status;

        await StoreProjectAsync();            // adds a project that came from outside the folder
        RefreshProjectList(_projectId);       // highlight the open project
        await WarnIfTargetChangedAsync(project);
    }

    /// <summary>On-demand decompiles analyze the file on disk, so tell the user when it is not the file in the project.</summary>
    private async Task WarnIfTargetChangedAsync(ProjectFile project)
    {
        var path = project.TargetPath;
        if (!File.Exists(path))
        {
            Log($"The analyzed file is not at its saved location: {path}. Saved code is shown; new decompiles are not possible.");
            return;
        }
        string sha;
        try
        {
            sha = await Task.Run(async () =>
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true);
                return Convert.ToHexString(await SHA256.HashDataAsync(stream));
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        if (!string.Equals(sha, project.Overview.Sha256, StringComparison.OrdinalIgnoreCase))
            Log($"Warning: {path} has changed since this project was saved (SHA-256 differs). " +
                "Saved code describes the old file; functions that are decompiled now come from the new one.");
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
            Tabs.SelectedItem = OverviewTab;
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
            Tabs.SelectedItem = LogTab;
            StatusText.Text = "Doctor finished; see Log.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Cancelled."; }
        catch (ReaException ex) { ShowFailure(ex); }
        finally { EndWork(); }
    }

    // ---- plumbing -----------------------------------------------------------------------------

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _bulkCts?.Cancel();
    }

    private CancellationTokenSource BeginWork(string status)
    {
        _cts = new CancellationTokenSource();
        StatusText.Text = status;
        Busy.Visibility = Visibility.Visible;
        BrowseButton.IsEnabled = RescanButton.IsEnabled = AnalyzeButton.IsEnabled = AnalyzeRecommendedButton.IsEnabled = false;
        OpenProjectButton.IsEnabled = SaveProjectButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        return _cts;
    }

    private void EndWork()
    {
        _cts = null;
        Busy.Visibility = Visibility.Collapsed;
        BrowseButton.IsEnabled = true;
        OpenProjectButton.IsEnabled = true;
        SaveProjectButton.IsEnabled = _analyzedPath is not null;
        RescanButton.IsEnabled = !string.IsNullOrEmpty(_root);
        CancelButton.IsEnabled = _bulkCts is not null;   // the background decompile can still be stopped
        Binaries_SelectionChanged(this, null!);
    }

    private void ShowFailure(ReaException ex)
    {
        Log($"REA failed (exit {ex.ExitCode}):\n{ex.Message}");
        Tabs.SelectedItem = LogTab;
        StatusText.Text = "REA reported an error; see Log. Try \"Run rea doctor\" in settings.";
    }

    private void Log(string text) =>
        LogBox.Text += $"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}";

    private static string Pretty(JsonDocument doc)
    {
        var text = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        return text.Length <= 200_000 ? text : text[..200_000] + "\n… (truncated for display)";
    }

    private ReaSettings CurrentSettings() => _settings;

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
