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
        ShowAiPanel(AiPanelKind.Working, "", "");
        RefreshAiUi();
        RefreshChrome();
        try
        {
            var code = await CodeForAiAsync(proc);
            if (code is null) return;

            var description = await FunctionDescriber.DescribeAsync(
                llm, new FunctionToDescribe(proc.Name, proc.Address, code), CurrentAi.ContextSize, ct);

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
