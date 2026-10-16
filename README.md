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
| Graph | `procedure_callees` | who calls a function and what it calls, one neighbourhood at a time (see below) |
| Strings | `list_strings` | every Ghidra-defined string with address |

### The Graph tab

The graph follows one **focus** function (press *Show in graph* on the Functions tab, search for a name or address, or start from a suggestion):
callers on the left, the focus in the middle, callees on the right, in columns by distance. It is never the whole program: a typical program is one
connected knot of thousands of functions, so the view stays readable by rules the status line spells out.

- **Depth** 1 to 3, and *Both*, *Callers* or *Callees*. A column shows up to 12 functions; the rest are one **+N more** node (click to show 25 more).
- **Library & runtime** functions (those Ghidra matched to its library signatures, known from their decompiled code) are hidden by default; the box shows them.
  A hidden function is not followed through, and the status line counts what is hidden.
- **Hubs** (more than 30 neighbours, mostly C runtime helpers) are shown when they are next to the focus but not followed through; the box follows them.
- **Imports** appear as small chips next to the focus' callees. Calls that skip a column (a caller that also calls one of the focus' callees) are drawn only
  for the node under the pointer or selected, so they do not cross the picture.
- A function that was explained shows the AI's **human-readable name** on its node (tagged *AI*; the name REA reported stays on the second line) and the AI's
  **brief** on the third line, cut with an ellipsis; hovering the node shows the complete text. Functions that were not explained show REA's name, and the
  one-line note written as context for a neighbour, if there is one. The search box also finds an explained function by its AI name.
- Click a node to select it: the panel shows REA's facts about it, the AI's name and brief if there are any (marked *Inference · AI*), what can be done with it
  (*Make focus*, *Open code*) and its neighbours as a list. Double-click, or Enter, makes it the focus; right-click opens the same actions. Back and forward
  go through the focus history. Drag to pan, scroll to zoom, arrow keys move between nodes, Home fits the picture.
- Every control has a tooltip, and so does every node (what it is, how it is connected, what its badges mean, the AI note, what a click does). The **Guide**
  button opens a short eight-step walkthrough with a legend and the keyboard shortcuts; it opens by itself while no function is chosen and closes by itself
  when one is.
- The graph holds **direct calls only**. Calls through pointers, vtables and callbacks are not in it: about a third of a typical program's functions have no direct
  caller, and only a fraction is reachable from the entry point, so the graph is not a map of the whole program. A function with no direct callers says so.

## The list of binaries and projects

The list on the left (binaries found in the folder, and stored projects) can be hidden with the **Hide list** button next to *Select app folder…* or with Ctrl+B. The workspace
(Functions, Graph, code and the AI panel) then takes the whole width; **Show list** or Ctrl+B brings the list back where it was. While it is shown, the divider between the list and
the workspace can be dragged. The choice is remembered between runs.

## Appearance

The app uses the Ai.Slop.AI visual system: neutral surfaces, one violet accent, cyan for work in progress, and a
header chip that says what state the analysis engine is in (*Ghidra ready*, *Analyzing*, *Decompiling*, *Setup needed*,
*Last run failed*). When there is nothing to show, the results pane explains why and offers the next step. Light and
dark themes are in Settings → Appearance (default: follow Windows, including the title bar). The header's **About** button shows the
version, how the app works and credits. REA's facts are tagged
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

Select a function and press **Explain**: the local model reads the decompiled code **and its context in the program** and writes a short description
(a human-readable name, a one-sentence brief for the graph, the purpose, the role, the steps that matter, the evidence, what is unclear). Explaining is **two rounds**: round one is the description; round two, which starts by itself when round one is done, writes an **alias implementation** (see below). It runs on this PC through `AiSlop.LocalLlm`; nothing is sent anywhere. The description appears in its own panel tagged
**Inference · AI**, with the model's name and a reminder to check it against the code. It is saved in the project, so reopening a project shows it
again. *Copy* puts it on the clipboard, *Discard* removes it from the project, *Stop* cancels a running request.

Set it up once:

```powershell
./scripts/Install-Ai.ps1            # about 6 GB: the PrismML llama.cpp build (CUDA 13.3) and the Ternary Bonsai 2 27B model, both verified
```

then turn on *Local AI* in Settings. Notes:

- **The model is reused when it is already on the PC.** Before downloading, the script looks for a copy with the pinned SHA-256 in: a file you give with
  `-ModelFile`, the Hugging Face cache (`HF_HUB_CACHE`, `HF_HOME\hub`, `%USERPROFILE%\.cache\huggingface\hub`; where Setup and Hugging Face tools keep it),
  an installed copy of the app (`%LocalAppData%\Programs\Reverse.Engineer.AI\ai\model`), and the script's own download folder. A copy is used only after its
  hash matches, and it is hard-linked into place (no second 6 GB on disk) when the drive allows, otherwise copied. A `-ModelFile` that is not the pinned model
  stops the script with the reason instead of falling back to another copy. `-SkipEngine` installs only the model, `-SkipModel` only the engine.

- The model (prism-ml/Ternary-Bonsai-2-27B-gguf, PTQ1_0, Apache-2.0) **needs the PrismML build of llama.cpp**; stock llama.cpp cannot load it. The script installs
  that build. `-Engine cuda-12.4` is the maker's fallback on Windows if the default exits without a message, `-Engine cpu` works everywhere but is slow for
  a 27B model, `-Engine vulkan` has caveats on the model's known-issues page.
- The model file is 5.9 GB, so GPU use is meant for cards with roughly 8 GB or more (not tested on every card). Settings offer GPU only if the engine itself reports a usable one; the CPU is the default.
- The model thinks by default; the app turns that off for descriptions, so there is no long reasoning wait. Loading the model still takes a while the first time (see *Load the model when the app starts*).
- **Context is always on.** Besides the code the model is given a program card (file type, version resource, imports grouped by what they suggest, a few strings),
  facts from REA (who calls the function, what it calls, whether Ghidra matched it to a library), the code around the places where callers call it, and one-line
  notes on the functions it calls. Those notes are written by the model, **on demand**: the first Explain on a function summarizes its not-yet-summarized
  callees in one batch (up to 8 at a time, 1,600 characters of each) and keeps them in the project, so each neighbour is paid for once. The panel's
  *Show context* lists exactly what the model was given; *Forget the neighbour summaries used here* removes the AI notes so they are written again.
  Notes are labeled unverified in the prompt and the model is told to trust the code, then the facts, then the call-site code, and the notes last.
- The call graph (direct calls, from REA's `procedure_callees`) is read once after an analysis (seconds) and stored in the project. A project saved without it
  recomputes it the first time it is needed, which starts Ghidra. The app does not export or show the graph itself.
- The context size defaults to **32,768 tokens** (a settings file that still held the old 8,192 is moved to it once). Most requests use a few thousand: the card,
  facts and notes get at most 45% of the window, so the function's code is never crowded out, and even the largest function of a real program (about 9,000 tokens)
  fits whole. On an 8 GB card the prompt is read at roughly 180 tokens a second, so a request with context takes seconds to a minute, not the whole window's
  worth. Above 16,384 tokens the server stores its key/value cache in 8 bits (`--cache-type-k/v q8_0`) so the window fits next to the 6 GB model; that
  choice is not yet measured for speed or quality on every card. Functions that still do not fit are cut at a line break and the panel says how much the model saw.
- The installer script needs the shared installer kit (`ai-tooling/windows/llm-installer`) next to this repository, or `-Kit` / `AISLOP_KIT`.

### The alias implementation (round two)

After a function is described, the model is given its code again together with that description and writes the **same code with readable names and comments**:
the function, its parameters, locals, labels and globals get descriptive `snake_case` names (`tree_node`, not `tree node`), and comments explain the steps. It is stored
in the project and shown in the code pane: when a function has one, *Decompiled* and *Alias* appear above the code (the alias is tagged *Inference · AI*). The alias name of
the function appears in the **AI name** column of the Functions list and on the graph node, next to the name REA reported, which is never replaced. Nothing is written back
to REA or Ghidra.

The model's word is not taken for "same code". The app compares the alias with the original **token by token** and rejects it unless the only differences are comments and
names, and only the names it may change: the function's own name and Ghidra's placeholders (`param_1`, `local_28`, `iVar1`, `DAT_…`, `LAB_…`). Types, operators, numbers,
strings and real names such as `atexit` must be identical; a name must be changed the same way everywhere, two things cannot share a name, and a new name must be a readable
`snake_case` identifier (not a keyword, not another placeholder). Other functions' placeholder names (`FUN_…`) stay unless a name was already chosen for that function, so one
function has one name wherever it is called. A rejected reply is retried once with the reason; if that also fails nothing is saved and the panel says why. Some variables may be
left with their placeholder names (the readable names are best effort, the identical code is not).

*Write alias code* runs round two on its own for a function that already has a description. Library and runtime code (matched by Ghidra) keeps its names and gets no alias, and a
function too long to rewrite inside the context window, or cut when it was explained, gets none either, with the reason. Discarding a description also discards its alias.

### The AI workspace

**AI workspace** (next to *Explain* and *Write alias code*) opens a panel over the whole window for the selected function: the **explanation** with its names
(alias name, readable name, brief) on the left, the **decompiled code** and the **alias code** side by side (scrolling can be linked), and a box at the bottom for a **request**
about the code. *Esc* closes it; anything still running keeps running.

A request is a **single round, not a chat**: type what you want to know or have changed (*why is the second parameter compared with 0x18?*, *use networking terms in the names*,
*what happens if the allocation fails?*) and press the send button next to the field (Ctrl+Enter). The field is locked while the model works (the send button becomes *Stop*) and is cleared once the model has answered; if the run was stopped or failed before an answer, your text stays so you can send it again. The request that produced an explanation is shown with it. The model gets the decompiled code, the same context an explanation gets (program card, facts about
callers and callees, call sites, notes on the callees), the explanation the function already has and the alias it already has, plus your request, and the same two rounds run:
the explanation is rewritten with your request worked into it as extra context (there is no separate answer and the text never says that anything was asked: it reads as one explanation of the code, in its usual form, with its name and brief), then the alias code is rewritten for it. Anything that did not exist for the
function is generated and stored with it. The explanation is kept even if the alias round fails (the old alias then stays, and the reason is shown); the alias is checked against the
original token by token as always, so a request can steer names and comments but never the code.

The model is told to handle only requests about this code: a request that is not about it (small talk, other code, an attempt to change the rules or the reply form) is refused with a
reason and nothing changes. Your text is given to the model as data between `<request>` tags, never as part of its instructions. Nothing is remembered between requests except what is
stored for the function, which the next request gets as context.

## Known limitations

- The first analysis of a binary is slow, because Ghidra's auto-analysis is. REA's own `--snapshot` does not
  avoid it on reopen (it still re-imports), which is why the app saves its own project file.
- REA does not offer a per-request timeout; a single very large binary can still hit REA's 330 s call limit.
- Ghidra's `entry` is the PE entry point (the C runtime start), not `main`.
- Windows P0 is read-only: no renaming or annotating from the UI.
- The scan follows no symlinks or junctions.
