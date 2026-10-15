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

| Tab | REA command | Content |
|---|---|---|
| Overview | `analyze` | format, architecture, image base, SHA-256, sections, counts, imported libraries, limitations |
| Functions | `search --kind procedures` | program functions and imports, Ghidra's `entry` marked |
| Strings | `search --kind strings` | every Ghidra-defined string with address |
| Decompiled | `decompile <address>` | C-like pseudocode for the selected function |

## Known limitations

- Each REA call is its own Ghidra session (import and auto-analysis repeat), so decompiling a function
  takes tens of seconds. A long-lived `rea mcp` session would avoid this and is the natural next step.
- Ghidra's `entry` is the PE entry point (the C runtime start), not `main`.
- Windows P0 is read-only: no renaming or annotating from the UI.
- The scan follows no symlinks or junctions.
