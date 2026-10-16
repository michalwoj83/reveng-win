using System.IO;
using System.Windows;
using System.Windows.Controls;
using AiSlop.LocalLlm;
using ReaWin.Models;
using ReaWin.Services;

namespace ReaWin;

/// <summary>
/// Describing the selected function with the local AI model (Ternary Bonsai 2 27B through AiSlop.LocalLlm).
/// The description is an inference: it is shown in its own panel under the code, tagged as such, stored apart from
/// REA's output, and never mixed into the code or the Overview.
/// </summary>
public partial class MainWindow
{
    // A property, not a static field: static initializers of a partial class run in an unspecified order across its files,
    // and this one reads SettingsPath, which another file initializes.
    private static string LlamaLogFile => Path.Combine(Path.GetDirectoryName(SettingsPath)!, "llama-server.log");

    private ILlmProvider _llm = new NullLlmProvider("Not initialized.");
    private CancellationTokenSource? _aiCts;
    private bool _aiBusy;
    private string? _aiBusyAddress;
    private int _analysisGeneration;   // changes when the analysis on screen is replaced
    private readonly Dictionary<string, FunctionDescription> _descriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NeighbourSummary> _summaries = new(StringComparer.Ordinal);
    private CallGraph? _callGraph;
    private CallGraphIndex? _graphIndex;
    private (string Full, string Compact)? _programCard;
    private string _aiStage = "";   // what the AI work is doing right now, in words, for the working panel

    private AiSettings CurrentAi => AiProfile.Resolve(_settings);

    /// <summary>Called once from the constructor: the library's messages in this app's words, then the provider (which starts nothing).</summary>
    private void InitializeAi()
    {
        LlmStrings.Translate = AiProfile.Translate;
        CreateLlm();
        WarmUpIfRequested();
    }

    /// <summary>
    /// Replaces the provider (and stops the old model server) so changed settings take effect. A running description
    /// is cancelled first: its server is about to go away.
    /// </summary>
    private void CreateLlm()
    {
        _aiCts?.Cancel();
        var previous = _llm;
        previous.StateChanged -= OnLlmStateChanged;

        var settings = AiProfile.ForEngine(CurrentAi, AiFiles.Locate());
        var next = LlmProviders.Create(settings, LlamaLogFile);
        next.StateChanged += OnLlmStateChanged;
        _llm = next;
        previous.Dispose();
        RefreshAiUi();
    }

    /// <summary>With "load the model when the app starts", the model loads in the background; a click meanwhile just waits for it.</summary>
    private void WarmUpIfRequested()
    {
        if (CurrentAi is not { Enabled: true, Startup: AiStartup.WithApp }) return;
        var provider = _llm;
        _ = provider.WarmUpAsync().ContinueWith(_ => Dispatcher.BeginInvoke(RefreshAiUi), TaskScheduler.Default);
    }

    private void DisposeLlm()
    {
        _aiCts?.Cancel();
        _llm.StateChanged -= OnLlmStateChanged;
        _llm.Dispose();
    }

    // StateChanged comes from the thread that started the model; the UI is touched on its own thread only.
    private void OnLlmStateChanged() => Dispatcher.BeginInvoke(() => { RefreshAiUi(); RefreshChrome(); });

    // ---- the button, its hint and the chip ------------------------------------------------------------

    /// <summary>
    /// Explain is enabled for a selected program function when AI is on and working. When it cannot be used, the button's
    /// tooltip and the line beside it say why in plain words.
    /// </summary>
    private void RefreshAiUi()
    {
        var proc = FunctionsGrid.SelectedItem as ProcedureInfo;
        var fault = _llm.Fault;

        string hint;
        if (_aiBusy) hint = _llm.State == LlmState.Starting ? "Loading the model…" : $"Describing {_aiBusyName}…";
        else if (!CurrentAi.Enabled) hint = "AI is off. Turn it on in Settings.";
        else if (fault is not null) hint = fault;
        else if (proc is null) hint = "Select a function to have it described.";
        else if (proc.IsImport) hint = "Imports have no code in this file.";
        else if (_llm.State == LlmState.Starting) hint = "Loading the model…";
        else hint = $"Describes the selected function with {AiProfile.ModelLabel}, running on this PC.";

        ExplainButton.IsEnabled = !_aiBusy && CurrentAi.Enabled && fault is null && proc is { IsImport: false };
        ExplainButton.ToolTip = hint;
        AiHint.Text = hint;
        AiStopButton.Visibility = _aiBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    private string _aiBusyName = "";

    // ---- describing ---------------------------------------------------------------------------------

    private async void Explain_Click(object sender, RoutedEventArgs e) => await DescribeSelectedAsync();

    private void AiStop_Click(object sender, RoutedEventArgs e) => _aiCts?.Cancel();

    private async Task DescribeSelectedAsync()
    {
        if (_aiBusy || FunctionsGrid.SelectedItem is not ProcedureInfo proc || proc.IsImport) return;

        var llm = _llm;
        var availability = await llm.CheckAsync();
        if (!availability.IsAvailable)
        {
            ShowAiPanel(AiPanelKind.Error, "AI is not available", availability.Reason ?? "");
            return;
        }

        var cts = new CancellationTokenSource();
        _aiCts = cts;
        var ct = cts.Token;
        var generation = _analysisGeneration;
        _aiBusy = true;
        _aiBusyAddress = proc.Address;
        _aiBusyName = proc.Name;
        _aiStage = "";
        ShowAiPanel(AiPanelKind.Working, "", "");
        RefreshAiUi();
        RefreshChrome();
        try
        {
            var code = await CodeForAiAsync(proc);
            if (code is null) return;

            // context is always on: the program, how the function is called, what it calls (the callees are summarized now if
            // that has not been done yet, and kept in the project)
            var stage = new Progress<string>(SetAiStage);
            var graph = await EnsureGraphIndexAsync(ct);
            SetAiStage("Building the program card…");
            var card = await ProgramCardAsync();
            var source = new ContextSource(
                _procedures.GroupBy(p => p.Address, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal),
                graph, _summaries, _descriptions, NeighbourCodeAsync, card);
            var (context, generated) = await ContextGatherer.GatherAsync(llm, proc, code, source, CurrentAi.ContextSize, stage, ct);
            if (generated > 0) _dirty = true;
            SetAiStage("Describing the function…");

            var description = await FunctionDescriber.DescribeAsync(
                llm, new FunctionToDescribe(proc.Name, proc.Address, code), CurrentAi.ContextSize, ct, context);

            if (generation != _analysisGeneration) return;   // another analysis or project was opened meanwhile
            _descriptions[proc.Address] = description;
            _dirty = true;
            await StoreProjectAsync();   // a description can take minutes to get: keep it with the project at once
        }
        catch (OperationCanceledException) { StatusText.Text = "Stopped."; }
        catch (LlmContextExceededException ex) { ShowAiPanel(AiPanelKind.Error, "Too much text", ex.Message); }
        catch (LlmUnavailableException ex) { Log($"AI failed:\n{ex.Message}"); ShowAiPanel(AiPanelKind.Error, "AI is not available", ex.Message); }
        catch (DescriptionException ex) { ShowAiPanel(AiPanelKind.Error, "No description", ex.Message); }
        finally
        {
            _aiBusy = false;
            _aiBusyAddress = null;
            cts.Dispose();
            _aiCts = null;
            RefreshAiUi();
            RefreshChrome();
            // show what finished (or clear the "working" panel) for whatever is selected now
            if (FunctionsGrid.SelectedItem is ProcedureInfo selected) ShowDescriptionFor(selected);
        }
    }

    // ---- context: the call graph, the program card, the neighbours' code ---------------------------------

    private void SetCallGraph(CallGraph graph)
    {
        _callGraph = graph;
        _graphIndex = new CallGraphIndex(graph);
    }

    /// <summary>
    /// Reads the direct calls of every function from the running session (seconds, once) and keeps them in the project. A
    /// failure is logged and leaves the graph missing; it is tried again when the AI next needs it.
    /// </summary>
    private async Task<bool> ComputeCallGraphAsync(ReaSession session, CancellationToken ct)
    {
        var addresses = _procedures.Where(p => !p.IsImport).Select(p => p.Address).ToList();
        if (addresses.Count == 0) return false;
        var generation = _analysisGeneration;
        var progress = new Progress<int>(n => StatusText.Text = $"Reading the call graph… {n:N0}/{addresses.Count:N0}");
        StatusText.Text = "Reading the call graph…";
        try
        {
            var graph = await session.CallGraphAsync(addresses, progress, ct);
            if (generation != _analysisGeneration) return false;   // another analysis or project replaced this one
            SetCallGraph(graph);
            _dirty = true;
            await StoreProjectAsync();
            return true;
        }
        catch (ReaException ex)
        {
            Log($"Could not read the call graph: {ex.Message}");
            return false;
        }
    }

    /// <summary>The call graph, recomputed through a (possibly newly started) Ghidra session when the project has none. Empty if that fails.</summary>
    private async Task<CallGraphIndex> EnsureGraphIndexAsync(CancellationToken ct)
    {
        if (_graphIndex is null)
        {
            SetAiStage("Reading the call graph…");
            var session = _session ?? await EnsureSessionAsync();
            if (session is not null && _graphIndex is null) await ComputeCallGraphAsync(session, ct);
        }
        // without it the facts say "Call information: not available" and the description rests on the code alone
        return _graphIndex ?? new CallGraphIndex(new CallGraph());
    }

    /// <summary>The whole-program card; the PE header is read for the subsystem and version resource if the file is still there.</summary>
    private async Task<(string Full, string Compact)> ProgramCardAsync()
    {
        if (_programCard is { } cached) return cached;
        var path = _analyzedPath!;
        var overview = _overview!;
        var procedures = _procedures;
        var strings = _strings;
        var card = await Task.Run(() =>
        {
            PeFileInfo? pe = null;
            if (File.Exists(path)) pe = PeScanner.Read(Path.GetDirectoryName(path)!, path);
            return (ProgramCard.Build(overview, path, procedures, strings, pe),
                    ProgramCard.Build(overview, path, procedures, strings, pe, compact: true));
        });
        _programCard = card;
        return card;
    }

    /// <summary>Code of a neighbouring function for the context: cached, or decompiled now. Null when REA cannot provide it.</summary>
    private async Task<string?> NeighbourCodeAsync(string address, CancellationToken ct)
    {
        if (_decompiled.TryGetValue(address, out var cached)) return cached;
        if (_decompileErrors.ContainsKey(address)) return null;
        var session = _session ?? await EnsureSessionAsync();
        if (session is null) return null;

        _interactive++;
        try
        {
            var code = await session.DecompileAsync(address, ct);
            _decompiled[address] = code;
            _dirty = true;
            return code;
        }
        catch (ReaException ex)
        {
            _decompileErrors[address] = ex.Message;
            return null;
        }
        finally { _interactive--; }
    }

    private void SetAiStage(string stage)
    {
        _aiStage = stage;
        if (_aiBusy && FunctionsGrid.SelectedItem is ProcedureInfo p && p.Address == _aiBusyAddress && _llm.State != LlmState.Starting)
            AiNote.Text = stage;
    }

    /// <summary>The function's code as REA returned it: from the cache, or decompiled now (which may start Ghidra for a loaded project).</summary>
    private async Task<string?> CodeForAiAsync(ProcedureInfo proc)
    {
        if (_decompiled.TryGetValue(proc.Address, out var cached)) return cached;
        if (_decompileErrors.TryGetValue(proc.Address, out var error))
        {
            ShowAiPanel(AiPanelKind.Error, "No code to describe", $"REA could not decompile this function: {error}");
            return null;
        }

        var session = _session ?? await EnsureSessionAsync();
        if (session is null)
        {
            ShowAiPanel(AiPanelKind.Error, "No code to describe", "The code of this function is not available and Ghidra could not be started.");
            return null;
        }

        _interactive++;
        try
        {
            var code = await session.DecompileAsync(proc.Address, CancellationToken.None);
            _decompiled[proc.Address] = code;
            _dirty = true;
            return code;
        }
        catch (ReaException ex)
        {
            _decompileErrors[proc.Address] = ex.Message;
            ShowAiPanel(AiPanelKind.Error, "No code to describe", $"REA could not decompile this function: {FirstLine(ex.Message)}");
            return null;
        }
        finally { _interactive--; }
    }

    // ---- the panel ----------------------------------------------------------------------------------

    private enum AiPanelKind { Inference, Working, Error }

    /// <summary>Shows the stored description of <paramref name="proc"/>, or the "working" panel if it is being described, or nothing.</summary>
    private void ShowDescriptionFor(ProcedureInfo proc)
    {
        if (_descriptions.TryGetValue(proc.Address, out var description))
        {
            var note = $"Written by {description.Model} from the decompiled code; it can be wrong. Check it against the code.";
            if (description.Truncated)
                note += $" Only the first {description.CodeCharsSent:N0} of {description.CodeCharsTotal:N0} characters of the function were shown to the model.";
            ShowAiPanel(AiPanelKind.Inference, description.Text, note);
            ShowAiContext(description);
        }
        else if (_aiBusy && _aiBusyAddress == proc.Address) ShowAiPanel(AiPanelKind.Working, "", "");
        else SetAiPanelVisible(false);
        RefreshAiUi();
    }

    /// <summary>The panel and its grid row appear together: with no description the code gets all the room.</summary>
    private void SetAiPanelVisible(bool visible)
    {
        AiPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        CodeRow.Height = new GridLength(visible ? 5 : 1, GridUnitType.Star);
        AiRow.Height = visible ? new GridLength(4, GridUnitType.Star) : new GridLength(0);
        AiRow.MinHeight = visible ? 110 : 0;
    }

    /// <summary>
    /// One panel for the three situations. An inference carries the "Inference · AI" tag and the model's name; a failure says
    /// what happened; while working it says so. The panel never holds REA's output.
    /// </summary>
    private void ShowAiPanel(AiPanelKind kind, string textOrTitle, string note)
    {
        SetAiPanelVisible(true);
        var inference = kind == AiPanelKind.Inference;

        AiTag.Visibility = inference ? Visibility.Visible : Visibility.Collapsed;
        AiState.Visibility = inference ? Visibility.Collapsed : Visibility.Visible;
        AiCopyButton.Visibility = AiDiscardButton.Visibility = inference ? Visibility.Visible : Visibility.Collapsed;
        if (!inference) HideAiContext();

        switch (kind)
        {
            case AiPanelKind.Inference:
                AiText.Text = textOrTitle;
                AiNote.Text = note;
                break;
            case AiPanelKind.Working:
                AiState.Text = "◔ Describing…";
                AiState.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                AiText.Text = "";
                AiNote.Text = _llm.State == LlmState.Starting
                    ? "Loading the model. This takes a while the first time."
                    : _aiStage.Length > 0 ? _aiStage
                    : "The model is reading the code. You can keep browsing; the description appears here when it is done.";
                break;
            default:
                AiState.Text = "✕ " + textOrTitle;
                AiState.SetResourceReference(TextBlock.ForegroundProperty, "ErrBrush");
                AiText.Text = note;
                AiNote.Text = "";
                break;
        }
        AiNote.Visibility = AiNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Offers the context the description was written from (older descriptions have none); starts collapsed.</summary>
    private void ShowAiContext(FunctionDescription description)
    {
        AiContextPanel.Visibility = Visibility.Collapsed;
        AiContextButton.Content = "Show context";
        AiContextText.Text = description.Context;
        AiContextButton.Visibility = description.Context.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AiForgetButton.Visibility = description.NeighbourAddresses.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HideAiContext()
    {
        AiContextPanel.Visibility = Visibility.Collapsed;
        AiContextButton.Visibility = Visibility.Collapsed;
    }

    private void AiContext_Click(object sender, RoutedEventArgs e)
    {
        var show = AiContextPanel.Visibility != Visibility.Visible;
        AiContextPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        AiContextButton.Content = show ? "Hide context" : "Show context";
    }

    /// <summary>
    /// Removes the AI's one-line notes on the neighbours used for this description from the project. A wrong note would
    /// otherwise keep coloring every description that uses it; the next Explain writes it again from the code.
    /// </summary>
    private void AiForget_Click(object sender, RoutedEventArgs e)
    {
        if (FunctionsGrid.SelectedItem is not ProcedureInfo proc || !_descriptions.TryGetValue(proc.Address, out var description)) return;
        var removed = description.NeighbourAddresses.Count(a => _summaries.Remove(a));
        if (removed > 0) _dirty = true;
        AiForgetButton.Visibility = Visibility.Collapsed;
        StatusText.Text = removed == 0
            ? "Those neighbour summaries were already gone."
            : $"Forgot {removed} neighbour summar{(removed == 1 ? "y" : "ies")}; the next Explain writes them again.";
    }

    private void AiCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(AiText.Text);
            StatusText.Text = "Description copied.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            StatusText.Text = "Could not copy: the clipboard is in use by another program.";
        }
    }

    /// <summary>Removes the stored description of the selected function from the project (the code and REA's output stay).</summary>
    private void AiDiscard_Click(object sender, RoutedEventArgs e)
    {
        if (FunctionsGrid.SelectedItem is not ProcedureInfo proc || !_descriptions.Remove(proc.Address)) return;
        _dirty = true;
        ShowDescriptionFor(proc);
        StatusText.Text = $"Description of {proc.Name} removed from the project.";
    }
}
