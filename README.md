# rea-win

A small Windows (WPF, .NET 8) front end for [REA](https://github.com/morluto/rea) with Ghidra.

1. **Select** an application folder.
2. **Detect** every `.exe` and `.dll`, reading PE headers only (nothing is loaded or executed).
3. **Run** REA + Ghidra on a chosen binary and browse overview, functions, strings and decompiled code.

## Prerequisites

| Need | Notes |
|---|---|
| .NET 8 SDK | `dotnet --version` |
| Node.js 22.19+ / 24.11+ | REA runs on Node |
| REA | `npm install --global rea-agents` |
| Ghidra 12.1.x release | e.g. `C:\Users\you\develop\ghidra_12.1.4_PUBLIC` |
| JDK 21+ (full JDK) | set as `JAVA_HOME` in the app's settings |

Targets must be native PE files (x86, x86-64, ARM64, ARMNT) on a local NTFS drive. This is REA's
experimental, read-only "Windows Ghidra P0" boundary.

## Run

```powershell
cd src\ReaWin
dotnet run
```

Open **REA / Ghidra settings**, check the paths, and use **Run rea doctor** once.

## How the "which file is the entry?" hint works

`Services/EntryRanker.cs` scores each binary 0-100 and shows the reasons in the **Why this file** tab:

| Signal | Effect |
|---|---|
| Executable (not a DLL) | base score |
| GUI subsystem | + (console subsystem: small +) |
| In the application root | + |
| File name equals folder name, or resembles `ProductName` | + |
| Version info present | + |
| Statically imports DLLs from the same folder | + |
| Name looks like `unins*`, `setup*`, `update*`, `crash*`, `*helper`, `vc_redist*` | big − |
| DLL imported by an exe here / exports functions / same company | + |
| DLL looks like a C runtime or platform library | big − |

The best non-helper exe at 50+ is marked **Recommended**. It is a hint, not proof: static imports
cannot see plugins or `LoadLibrary` at runtime, and suites such as Git contain many valid programs.

Special cases:
- `resources\app.asar` present: treated as **Electron**. Use *Analyze as JavaScript app*.
- Managed (.NET) assemblies: routed to REA's managed inspection, because Windows Ghidra support is native-only.

## What the UI shows from REA

| Tab | REA MCP tool | Content |
|---|---|---|
| Overview | `binary_overview` | format, architecture, image base, SHA-256, sections, counts, imported libraries, limitations |
| Functions | `list_procedures`, `procedure_pseudo_code`, `batch_decompile` | program functions and imports, Ghidra's `entry` marked; the decompiled code of the selected function appears beside the list |
| Strings | `list_strings` | every Ghidra-defined string with address |

## Speed and sessions

Native analysis runs through one long-lived `rea --mcp` session per binary. Ghidra imports and analyzes the file
once (about 2.5 minutes for a 2.4 MB executable); after that the function and string lists take well under a
second and a single function decompiles in tens of milliseconds. Right after the analysis the app decompiles
every program function in the background (progress is shown in the status bar), so selecting a function is
normally an instant cache hit. *Cancel* stops the background work between batches.

## Projects

*Save project…* writes a `.reaproj` file (gzip-compressed JSON) with the overview, function list, strings and all
decompiled code so far. *Open project…* shows it again in well under a second, with no Ghidra involved.
Ghidra is started again only if you select a function whose code was not saved, and then only for that file.
A project is created only when you save one; the app does not write analysis output anywhere else.
If the analyzed file has changed since the project was saved, the Log tab says so.

## Known limitations

- The first analysis of a binary is slow, because Ghidra's auto-analysis is. REA's own `--snapshot` does not
  avoid it on reopen (it still re-imports), which is why the app saves its own project file.
- REA does not offer a per-request timeout; a single very large binary can still hit REA's 330 s call limit.
- Ghidra's `entry` is the PE entry point (the C runtime start), not `main`.
- Windows P0 is read-only: no renaming or annotating from the UI.
- The scan follows no symlinks or junctions.
