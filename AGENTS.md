# Repository Guidelines

## What this is

`rea-win` is a Windows desktop front end (WPF, .NET 8) for [REA](https://github.com/morluto/rea), a local
reverse-engineering CLI/MCP server that drives Ghidra headless. The user selects an application folder, the app
detects `.exe`/`.dll` files, suggests which one is the main program, and runs REA + Ghidra on a chosen binary.

The app is a thin client. It does not disassemble or decompile anything itself; REA and Ghidra do that. Keep it that way.

## Layout

```
src/ReaWin/
  MainWindow.xaml(.cs)      UI and orchestration (code-behind, no MVVM framework)
  Services/PeScanner.cs     enumerate exe/dll, read PE headers (never loads or runs files)
  Services/EntryRanker.cs   score binaries, produce human-readable reasons
  Services/ReaClient.cs     run `node rea.mjs <cmd> --format json`, parse evidence (one-shot commands)
  Services/ReaSession.cs    long-lived `rea --mcp` session: analyze once, then fast queries and decompiles
  Services/ProjectStore.cs  save/open `.reaproj` files (gzip JSON) so a finished analysis can be reopened
  Services/ProjectLibrary.cs app project folder: `.reaproj` + small `.json` catalog entry, keyed by name + SHA-256
  Services/JavaLocator.cs   find installed JDKs from registry/folders/PATH (reads `release`, never runs java)
  Services/GhidraLocator.cs validate a Ghidra release folder by file layout
  Services/AiProfile.cs     what is specific to the shipped model: server args, request fields, defaults, messages
  Services/AiFiles.cs       where the AI engine and model live (override, beside the app, or the user folder)
  Services/FunctionPrompts.cs  the "describe this function" prompt, truncation and reply clean-up (tested)
  Services/FunctionDescriber.cs asks the local model to describe one function
  Services/ContextGatherer.cs   collects a function's context: callers, callees, call-site code, and lazy one-line summaries of callees
  Services/FunctionContext.cs   the sections of that context (facts, call-site excerpts) and library detection
  Services/NeighbourSummaries.cs batch prompt, reply parsing and one-line reuse for neighbour summaries
  Services/ProgramCard.cs       whole-program card from REA's overview, imports, strings and the PE header (no AI)
  Services/CallGraphIndex.cs    callers/callees lookups over the stored call graph (callers derived from callees)
  Services/GraphModel.cs        the neighbourhood drawn in the Graph tab: caps, hubs, library hiding, minor edges (pure, tested)
  Services/GraphLayout.cs       layered left-to-right layout of that neighbourhood (pure, deterministic, tested)
  GraphCanvas.cs                one self-drawing element: pan, zoom, hover, select, keyboard; reports events, knows no actions
  MainWindow.Graph.cs           the Graph tab: focus and history, filters, side panel, node actions (see below)
  Services/AliasVerifier.cs     C-like tokenizer and the check that an alias is the same code (only names and comments differ)
  Services/AliasWriter.cs       round two: prompt, reply parsing, one retry with the reason; never trusts the model's word
  Services/ContextBudget.cs     token estimates (decompiled C is about 2.4 chars/token with the model's vocabulary)
  MainWindow.Ai.cs          the Explain button, the AI panel, the provider lifecycle
  scripts/Install-Ai.ps1    installs the PrismML llama.cpp build and the Bonsai model, reusing a verified copy from the Hugging Face cache, an installed app or -ModelFile (pins in scripts/ai-pins.json)
  tests/ReaWin.Tests        prompts, clean-up, settings and project compatibility, AI files; real-model test is env-gated
  installer/                ReaWin.iss + build-installer.ps1: per-user Inno Setup using the ai-tooling installer kit (pins: scripts/ai-pins.json)
  SettingsWindow.xaml(.cs)  pick the Ghidra folder, a JDK 21+, the appearance, and the local AI options
  ThemeManager.cs           light/dark/follow-Windows: swaps the token dictionary, dark title bar
  AboutWindow.xaml(.cs)     About: logo, version, manifest, how it works, credits (header "About" button)
  Themes/Tokens.*.xaml      semantic color brushes (light, dark); same keys in both
  Themes/Controls.xaml      control templates and shared styles (buttons, tabs, grids, lists, scroll bars…)
  Models/Models.cs          records shared across layers
```

Dependencies flow one way: `Models` <- `Services` <- `MainWindow`. Services must not reference WPF types.

## Run and build

```powershell
cd src\ReaWin
dotnet build      # must finish with 0 warnings, 0 errors
dotnet run
```

Prerequisites: .NET 8 SDK, Node.js 22.19+/24.11+, `npm install --global rea-agents`, a Ghidra 12.1.x **release**
(not a source clone), and a full JDK 21+. Paths are set in the app's settings expander; use "Run rea doctor"
to check them. `dotnet test tests\ReaWin.Tests` runs the unit tests (the real-model test is skipped unless `REAWIN_LLAMA_SERVER` and
`REAWIN_LLAMA_MODEL` are set). Verify UI changes by building, then running the app.

## How REA is called

`ReaClient` runs `node <npm>\rea-agents\scripts\rea.mjs <command> <args> --format json` as a child process.

- Pass arguments through `ProcessStartInfo.ArgumentList`. Never build a command line string, and never go through `cmd.exe`.
- Set only `GHIDRA_INSTALL_DIR`, `JAVA_HOME` and `REA_ANALYSIS_PROVIDER` on the child. Do not write to the user's environment.
- One-shot CLI commands (`ReaClient`): `analyze-javascript-application`, `inspect-managed-artifact`, `doctor`.
- Native analysis uses one long-lived `node rea.mjs --mcp` server per binary (`ReaSession`, JSON-RPC over stdio).
  Tools in use: `open_binary`, `binary_overview`, `list_procedures`, `list_strings`, `procedure_pseudo_code`,
  `batch_decompile`, `close_binary`. List tools with `tools/list`; do not guess argument names.
- To discover options for a CLI command, run `rea <command> --help` or `rea <command> --schema`.
- Output is an evidence envelope: `evidence_id`, `subject`, `normalized_result`, `limitations`. Lists return an array
  of `{address, value}`; `procedure_pseudo_code` returns a string; `batch_decompile` returns `items`. Parse with
  `System.Text.Json` and tolerate missing fields. Provider errors arrive as `{"error": {...}}` inside a normal result.
- The first query on a session runs Ghidra's import and auto-analysis (minutes for a 2 MB file); later queries take
  milliseconds. Never cancel an in-flight request on the server: REA then discards the database and re-imports on
  the next query. Abandon the wait instead (`ReaSession` does this), and stop background work between batches.
- Do not run several Ghidra *sessions* for one file in parallel; they compete for CPU and one hits REA's 330 s
  per-call timeout. Overlapping requests inside one session are fine.
- Long operations go through `BeginWork`/`EndWork` with a `CancellationToken`. The one exception is the per-click
  decompile of a single function, which is a millisecond cache lookup and must not lock the UI.
- REA's `--snapshot` / `snapshot_path` does not skip re-analysis on reopen. The app's own `.reaproj` project file does.

## Things the REA contract requires

- Keep observations, inferences and unknowns distinct in the UI. Show REA's `limitations`; do not hide or soften them.
- A "recommended entry" is a heuristic. Label it as a hint and show the reasons. Never present it as a fact.
- Ghidra's `entry` is the C runtime start, not `main`. Do not label it `main`.
- Windows Ghidra support is native PE only (x86, x86-64, ARM64, ARMNT), read-only, local NTFS. Managed (.NET) goes to
  managed inspection; Electron goes to JavaScript analysis. Do not add rename/annotate features for Windows.
- Preserve source values: show addresses and names exactly as REA returns them (`EXTERNAL:0x5`, `DLL::API`).
  Do not normalize them if they will be used to call REA again.
- Do not guess that data is secret. Do not log anything, and persist nothing except the user's settings and analysis
  projects (overview, functions, strings, decompiled code). Projects live in `%LOCALAPPDATA%\ReaWin\projects` (stored
  automatically after each analysis, listed in the Projects tab, deletable by the user) or in a `.reaproj` file the
  user exports. Never store them anywhere else.

## Conventions

- C# 12, nullable enabled, 4-space indent, file-scoped namespaces, `PascalCase` members, `_camelCase` private fields.
- Add `///` summaries to public types and methods. Explain why, not what.
- Catch specific exceptions only. Surface failures as `ReaException` with REA's own message so the Log tab shows
  actionable remediation. Do not swallow errors silently.
- No new NuGet packages without a clear need. The project currently has none.
- The WPF SDK omits `System.IO` from implicit usings; the csproj adds it.
- Do not read, load, or execute scanned binaries. Only parse headers.

## Look and feel (Ai.Slop.AI visual system)

- Product name in the UI is **Reverse.Engineer.AI**. Ai.Slop.AI appears only as the publisher: the line in Settings, and the logo in the About window (header button, `AboutWindow`; white logo on dark, dark logo on light; both transparent PNGs from the brand kit, never recolored or cropped).
- Colors come from the semantic brushes in `Themes/Tokens.*.xaml` through `DynamicResource` (`SurfaceBrush`, `TextBrush`,
  `PrimaryBrush`, `LiveBrush`, `ErrBrush`…). No literal hex or `Brushes.*` in views or code-behind; in code use
  `SetResourceReference`. Add a token to both theme files or to neither.
- One violet `PrimaryButton` per region; everything else is outlined, tonal or text. Violet means actions and selection,
  cyan (`LiveBrush`) only means work in progress.
- State is an icon plus words (✓ ready, ◔ working, ! setup, ✕ failed), never color alone.
- Keep REA's facts and any AI output visibly apart: REA content is "Observed · REA"; AI output, when it exists, goes in its
  own panel tagged as inference. Do not add AI controls that look wired when they are not (they stay disabled until real).
- Performance: no shadows, blur or effects on lists, grids or the code pane. Custom templates must keep row virtualization.
- Custom `TabControl` templates must keep `PART_SelectedContentHost`, or UI Automation cannot see the tab content.

## Adding a feature

1. Find the REA command: `rea --help`, then `rea <command> --help`. Run it once from a terminal against a small file
   (for example a copy of `C:\Windows\System32\find.exe`) and read the real JSON before writing a parser.
2. Add a typed method to `ReaClient` returning a record from `Models`.
3. Add a tab or control in `MainWindow.xaml` and wire it with `BeginWork`/`EndWork` so the UI shows progress and can cancel.
4. Handle `ReaException` and `OperationCanceledException`.
5. Update `README.md` (tabs table and limitations) when behavior changes.

## Layout

- The list of binaries and projects (`LeftPane`) is hideable (`PanelToggleButton`, Ctrl+B, `ReaSettings.ListHidden`, saved with the settings). Hiding collapses its column and the
  splitter column so the workspace column, the only star column left, takes all the width; showing restores the widths the user had. Anything that points the user at the list
  (empty-state text) must say what to do when it is hidden.

## Local AI (describing functions)

- **What it does:** the Explain button describes the selected function with Ternary Bonsai 2 27B (PTQ1_0) through `AiSlop.LocalLlm`. Nothing leaves the
  machine. It is off until the user turns it on in Settings.
- **The model needs PrismML's llama.cpp build.** Stock llama.cpp rejects its quantization types. `scripts/Install-Ai.ps1` installs the pinned build
  (default CUDA 13.3; `-Engine cuda-12.4|cpu|vulkan` for the others) and the model, verified by SHA-256, into `%LocalAppData%\ReverseEngineerAI\ai`
  (override: `REAWIN_AI_DIR`). Do not switch to a stock build or another quantization without checking the model card's known issues.
- **It is a reasoning model.** Every request sends `reasoning_effort: "none"`; without it the model thinks for thousands of tokens. Sampling follows the
  model card's non-thinking set except the presence penalty (describing code repeats identifiers). All of this lives in `AiProfile`.
- **Inference stays apart from observation.** A description is shown in its own panel tagged "Inference · AI" with the model's name, stored in the project
  under `Descriptions` (never mixed into `Decompiled`), and never edits or replaces code. It is removed only by Discard.
- **Prompts live in `FunctionPrompts.cs`** with tests: the code is the only source of truth, `FUN_`/`DAT_`/`param_` names are placeholders, "probably"
  marks inference, "Unclear" says what one function cannot show. Long functions are cut at a line break and the UI says how much the model saw.
- **Context is always on, and layered by trust.** Prompt order is: instructions and example, program card, facts (observed), call-site code (as decompiled),
  notes on neighbours (AI-written), then the function. The prompt labels each layer and tells the model to trust them in that order. Stable parts come first so
  a server that reuses a common prefix can (not verified on this hybrid model). Optional layers get at most 45% of the window (`FunctionPrompts.OptionalShare`).
- **Neighbour summaries are lazy and stored** (`ProjectFile.Summaries`): only callees without one are summarized, in batches, when Explain needs them. They are
  inferences: never mix them into `Decompiled` or show them as observations, and keep "Forget" working so a wrong note can be removed.
- **The call graph is stored in the project** (`ProjectFile.CallGraph`, callees only) and recomputed through a session when missing (`ComputeCallGraphAsync`).
  Do not add a way to export or browse it separately. REA's `procedure_callers` is the exact inverse of `procedure_callees`, so only callees are saved.
- **Defaults:** context 32,768 (`AiProfile.DefaultContextSize`, `ReaSettings.Revision` migrates a saved 8,192 once); above 16,384 the KV cache is `q8_0`.
- **No new dependency beyond `AiSlop.LocalLlm`** (private feed, `nuget.config`; see the `aislop-nuget-packages` skill).

## AI workspace

- **A panel over the whole window for one function** (`WorkspaceOverlay`, `MainWindow.Workspace.cs`): explanation and names, decompiled code and alias side by side, a request box.
  Everything from the model is tagged *Inference · AI*; the decompiled code is REA's and is shown as Observed.
- **A request is one round, never a conversation** (`AskRunner`): no history, no follow-up questions, no greeting. It runs the explanation and the alias rounds with the request given to
  both (`FunctionPrompts.Build` in request mode, `AliasWriter` with a request), so everything the model generated for the function is refreshed and missing pieces are generated.
  The previous explanation and alias are context. Keep the scope rules in `FunctionPrompts.RequestRules`: only requests about this code, refuse the rest with `Refused:` and a reason
  (`RequestRefusedException`), the request is data between `<request>` tags and cannot change the rules or the form. Do not turn this into a chat or a general assistant.
- **The code is never up for negotiation.** A request cannot loosen `AliasVerifier`; if the alias round fails the explanation is kept and the old alias stays.
- With no request the explanation prompt is exactly what it was (tests pin this); request mode only adds to it.

## Alias implementation (round two of Explain)

- **Round one is unchanged** (description, name, brief). Round two runs after it, or alone from *Write alias code*, and gets the code plus the round-one description.
- **"Same code" is checked, not asked for.** `AliasVerifier` compares token by token: comments and layout may differ; names may differ only for the function itself and
  Ghidra placeholders; everything else (types, operators, numbers, strings, real names) must be identical; renames must be consistent and injective; new names must be
  readable snake_case, not keywords, not placeholders, not already used. Other functions' `FUN_` names change only to a name already chosen for them. A reply that fails is
  retried once with the reason, then dropped. Never store or show an alias that did not pass, and never loosen the check to make more replies pass.
- **An alias is an inference and display-only.** It is stored on the project (`ProjectFile.Aliases`), tagged *Inference · AI*, and shown beside REA's code and REA's name, never
  instead of them. Do not write it back to REA or Ghidra (no rename or annotate features for Windows).
- The alias name appears in the Functions list (`AI name` column) and on graph nodes (first line, tagged AI; the REA name stays on the second line). Library code and
  truncated or oversized functions get no alias, with the reason.

## Graph tab

- **A focused neighbourhood, never the whole graph.** A real program is one connected knot (a 3,039-function program had 2,872 in one component, and two
  hops from a function reach a median of 159 and a 90th percentile of 835 functions through runtime hubs). Keep the caps, hub rule and library hiding in
  `GraphModel`; do not add a "show everything" mode or a force-directed layout.
- **Library and runtime functions are hidden by default**, and hubs are not followed through; the status line must say what is hidden. Library status comes
  from Ghidra's "Library Function" comment in the decompiled code (`FunctionContextBuilder.IsLibraryCode`), so it is known only for decompiled functions.
- **Layout and model are pure** (no WPF types) and tested; keep rendering in `GraphCanvas`, one element that draws, with viewport culling and level of detail.
  Do not build one control per node. Colors come from the tokens, looked up when drawing; `ThemeManager.Changed` repaints.
- **Node actions are a list, not code in the canvas.** `MainWindow.Graph.cs` holds `_graphActions` (`GraphAction`: id, label, availability, run). The side panel
  and the right-click pop-up are both built from it. To add an action (explain, copy, mark) add one entry in `RegisterGraphActions`; do not touch the canvas.
  Only actions that work are listed: the planned ones (explain this function, explain with neighbours) are noted in that method and not shown until real.
- **Keep the help in step with the tab.** Every Graph control and node has a tooltip (XAML for controls, `DescribeGraphNode` for nodes) and `GraphGuide` in
  `MainWindow.Graph.cs` is the short guide, with the legend and keys. When a control, a badge or a shortcut changes, change its tooltip and the guide with it.
- **The graph shows REA's observations.** The AI's title and brief on a node are marked as inference (an "AI" tag, accent color), and the name REA reported
  always stays on the node, exactly as reported: a title is shown beside it, never instead of it. The graph is direct calls only and says so.
- **Two artifacts come with every explanation:** a human-readable `Title` (2 to 6 words) and a `Brief` (one sentence, at most 20 words), both asked for in the reply
  (`Name:` and `Brief:` lines) and read by `FunctionPrompts.ExtractArtifacts`, which cleans them (quotes, snake_case to words, placeholders and sentences dropped,
  a missing brief falls back to the first sentence of the purpose). They are stored on `FunctionDescription` (`Title`, `Brief`), never mixed into REA's data.
  A brief is also the one-liner handed to other functions as context (`NeighbourSummaries.OneLineFrom`).

Good next features:
- Find `main` or the WinMain-like function from the entry's callees.
- Cross-reference view (`trace`, `function` commands) and call graph.
- Compare two application versions (`compare` commands).
- Export evidence JSON (`evidence-export`).

## Verification

Before declaring work done:

- `dotnet build` is clean.
- The app starts (`dotnet run`, window appears).
- For REA-facing changes, run the underlying `rea` command from a terminal and compare its JSON with what the UI shows.
- State what you did not verify (for example GUI interaction, large folders, non-x64 targets).

## Safety

Analyzing untrusted binaries delegates parsing to Ghidra with the user's permissions. REA is not a sandbox. Do not add
any feature that executes scanned files. If one is ever needed, it must be an explicit, separate, clearly labeled action.

Do not commit binaries, Ghidra projects, analysis output, `bin/`, `obj/`, or user settings.
