# Brief: app icon for Reverse.Engineer.AI

## The app

Reverse.Engineer.AI is a Windows desktop tool (WPF, .NET 8) for reverse engineering programs. The user picks an application folder; the app finds its `.exe` and `.dll` files, suggests which one is the main program, and analyzes the chosen binary with Ghidra (through REA). It shows the program's functions, strings and decompiled C-like code. A local AI model can then explain a selected function in plain language. Everything runs on the user's computer.

Audience: developers, security researchers and curious power users. The tone is calm, precise and trustworthy: a lab instrument, not a hacker toy.

Publisher: Ai.Slop.AI. The product name leads; the publisher appears only as a small line in Settings → About.

## What the icon should say

Two ideas, in this order:
1. **Looking inside a compiled program**: opening, inspecting, seeing structure (a window, lens, layers, a cut-away, a binary turned into readable shape).
2. **Understanding it with AI**: a hint of intelligence, kept quiet. No robots, brains, sparkles, circuit boards or glowing eyes.

Suggested directions (pick one, justify it in two sentences):
- A magnifier or lens over a small block of code or `{ }` brackets.
- A cube or chip (the binary) opened or cut to show structure inside.
- An abstract "R" or "RE" built from simple geometry.
Avoid skulls, padlocks, matrix rain, bugs, and anything that reads as malware or hacking.

## Brand rules (Ai.Slop.AI visual system)

- Primary violet `#6654F5`. Lighter accent for use on dark `#8F82FF`. Pressed violet `#5140D8`.
- Dark navy for backgrounds: `#0C1020` (canvas) and `#171D31` (surface). Near-white `#F7F8FC`. Muted `#98A0B8`.
- Cyan `#42D7E8` means "work in progress" in the UI. Use it, if at all, as a tiny accent, never as a main color.
- Violet means actions and selection; use it for the single focal element, not as decoration everywhere.
- The Ai.Slop.AI mark (an arch "A" with a violet dot) belongs to the publisher. Do **not** copy, crop, redraw or restyle it. Do not put the wordmark in the icon. A violet dot may echo the family feel, but the icon must stand on its own.
- Flat, geometric, no gradients heavier than a subtle tonal step, no shadows, blur or glow.
- No text in the icon. No gradients that disappear at 16 px.

## Technical deliverables

1. **Master vector**: `AppIcon.svg`, square viewBox (for example 256×256), transparent background, shapes only (no fonts, no filters, no embedded bitmaps).
2. **Windows icon**: `AppIcon.ico` containing PNG frames at **16, 20, 24, 32, 40, 48, 64, 128 and 256 px**, 32-bit with alpha. Check that 16, 24 and 32 px are legible; if the full design is too detailed, make simplified small-size variants (fewer details, thicker strokes), as a hand-tuned set is better than an auto-scaled one.
3. **Preview sheet**: PNG showing the icon at actual size on a dark taskbar, a light taskbar and a white Explorer background, plus enlarged 256 px.
4. **Source PNG** `AppIcon-source.png` at 1024×1024, transparent.

File locations in the repo:
- `src/ReaWin/Assets/AppIcon.ico` and `AppIcon.svg`, `AppIcon-source.png`.
- The csproj will get `<ApplicationIcon>Assets\AppIcon.ico</ApplicationIcon>` and the window `Icon` will use it; the installer (`installer/build-installer.ps1`) already picks up `src/ReaWin/Assets/AppIcon.ico` automatically for Setup.

## Quality checks before delivering

- Reads at 16×16 on both a dark and a light background; the silhouette is distinct from neighboring taskbar icons.
- Works as a single glance: one focal shape, at most 3 colors from the palette above.
- Keeps a safe margin (about 8% of the canvas) so Windows does not clip it.
- Contrast of the main shape against both `#0C1020` and `#FFFFFF` is clear (violet `#6654F5` on white is fine; on dark use `#8F82FF` for thin strokes).
- No trademarks or look-alikes of Ghidra's dragon, NSA logos, or other products.

## Reference for style consistency

A sibling app, Note.me.ai, already has an icon in the same family at `../Note.me.ai/src/NoteMeAi.App/Assets/AppIcon.ico` (source `AppIcon-source.png`). Match its level of flatness and corner treatment, but make this one distinct at a glance.
