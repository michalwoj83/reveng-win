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
  SettingsWindow.xaml(.cs)  pick the Ghidra folder and a JDK 21+
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
to check them. There is no automated test project yet. Verify changes by building, then exercising the code path.

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

## Adding a feature

1. Find the REA command: `rea --help`, then `rea <command> --help`. Run it once from a terminal against a small file
   (for example a copy of `C:\Windows\System32\find.exe`) and read the real JSON before writing a parser.
2. Add a typed method to `ReaClient` returning a record from `Models`.
3. Add a tab or control in `MainWindow.xaml` and wire it with `BeginWork`/`EndWork` so the UI shows progress and can cancel.
4. Handle `ReaException` and `OperationCanceledException`.
5. Update `README.md` (tabs table and limitations) when behavior changes.

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
