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

## Installer

```powershell
./installer/build-installer.ps1            # artifacts\ReverseEngineerAI-Setup-<version>.exe, about 560 MB
./installer/build-installer.ps1 -Engine cuda-12.4   # or cpu, vulkan
```

A per-user Inno Setup installer (no admin prompt). It bundles the self-contained app and the pinned PrismML llama.cpp build (`ai\`), and
downloads the 5.9 GB model during Setup, verified by SHA-256 (offline: `Setup.exe /MODEL=<file>`). The model is kept in the standard
Hugging Face cache (`%USERPROFILE%\.cache\huggingface\hub`, or `HF_HUB_CACHE` / `HF_HOME`) and reused when it is already there, whether
an earlier Setup or another Hugging Face tool put it there; the install folder holds a hard link, so it takes the disk space once. It does not install Node, REA, Ghidra or a
JDK; the first page of Setup says so. Settings and projects in `%LocalAppData%\ReaWin` survive update and uninstall. Needs Inno Setup 6 and
the shared installer kit (`-Kit`, `AISLOP_KIT`, or `ai-tooling` next to this repository). The version is `<Version>` in `ReaWin.csproj`.
Setup is not code-signed, so SmartScreen may warn.

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

## Appearance

The app uses the Ai.Slop.AI visual system: neutral surfaces, one violet accent, cyan for work in progress, and a
header chip that says what state the analysis engine is in (*Ghidra ready*, *Analyzing*, *Decompiling*, *Setup needed*,
*Last run failed*). When there is nothing to show, the results pane explains why and offers the next step. Light and
The header has an **About** button (version, how the app works, credits). dark themes are in Settings → Appearance (default: follow Windows, including the title bar). REA's facts are tagged
*Observed · REA*; the *Explain* and *Summarize* buttons are disabled placeholders until AI features exist.

## Speed and sessions

Native analysis runs through one long-lived `rea --mcp` session per binary. Ghidra imports and analyzes the file
once (about 2.5 minutes for a 2.4 MB executable); after that the function and string lists take well under a
second and a single function decompiles in tens of milliseconds. Right after the analysis the app decompiles
every program function in the background (progress is shown in the status bar), so selecting a function is
normally an instant cache hit. *Cancel* stops the background work between batches.

## Projects

Every analysis is stored automatically in `%LOCALAPPDATA%\ReaWin\projects`: right after the analysis, again when the
background decompile finishes or is stopped, and when you close the app. They are listed in the **Projects** tab
on the left (newest first). Selecting one loads it in well under a second with no Ghidra involved. Analyzing the
same file again updates its project rather than adding a second one. *Delete selected* removes a project (never
the binary).

A project holds the overview, function list, strings and all decompiled code (gzip-compressed JSON). Ghidra is
started again only if you select a function whose code was not stored, and then only for that file. *Export…*
copies the current project to a `.reaproj` file anywhere, and *Open file…* loads one (it is then added to the
list). If the analyzed file has changed since the project was stored, the Log tab says so.

## Local AI: describe a function

Select a function and press **Explain**: the local model reads the decompiled code and writes a short description (summary, inputs, behavior, calls,
what is unclear). It runs on this PC through `AiSlop.LocalLlm`; nothing is sent anywhere. The description appears in its own panel tagged
**Inference · AI**, with the model's name and a reminder to check it against the code. It is saved in the project, so reopening a project shows it
again. *Copy* puts it on the clipboard, *Discard* removes it from the project, *Stop* cancels a running request.

Set it up once:

```powershell
./scripts/Install-Ai.ps1            # about 6 GB: the PrismML llama.cpp build (CUDA 13.3) and the Ternary Bonsai 2 27B model, both verified
```

then turn on *Local AI* in Settings. Notes:

- The model (prism-ml/Ternary-Bonsai-2-27B-gguf, PTQ1_0, Apache-2.0) **needs the PrismML build of llama.cpp**; stock llama.cpp cannot load it. The script installs
  that build. `-Engine cuda-12.4` is the maker's fallback on Windows if the default exits without a message, `-Engine cpu` works everywhere but is slow for
  a 27B model, `-Engine vulkan` has caveats on the model's known-issues page.
- The model file is 5.9 GB, so GPU use is meant for cards with roughly 8 GB or more (not tested on every card). Settings offer GPU only if the engine itself reports a usable one; the CPU is the default.
- The model thinks by default; the app turns that off for descriptions, so there is no long reasoning wait. Loading the model still takes a while the first time (see *Load the model when the app starts*).
- Functions longer than the context window are cut at a line break; the panel says how much of the function the model saw. Raise *Context size* in Settings
  for more (memory grows).
- The installer script needs the shared installer kit (`ai-tooling/windows/llm-installer`) next to this repository, or `-Kit` / `AISLOP_KIT`.

## Known limitations

- The first analysis of a binary is slow, because Ghidra's auto-analysis is. REA's own `--snapshot` does not
  avoid it on reopen (it still re-imports), which is why the app saves its own project file.
- REA does not offer a per-request timeout; a single very large binary can still hit REA's 330 s call limit.
- Ghidra's `entry` is the PE entry point (the C runtime start), not `main`.
- Windows P0 is read-only: no renaming or annotating from the UI.
- The scan follows no symlinks or junctions.
