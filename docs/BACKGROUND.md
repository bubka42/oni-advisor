# Background: how this project came about

Consolidated from the notes kept during the first session (2026-10-26/27), corrected where they had gone
stale. For the current state, read `HANDOFF.md`.

## The player and what they want

- Plays Oxygen Not Included (base game). Enjoys both planning and building, but doing everything in full
  detail is tiring and has kept them from later-game content: they have **never had a stable colony that
  outgrows its starting biome**. That is the goal the advisor works towards (milestones: renewable oxygen,
  renewable food, power without depleting fuel, closed water loop, heat under control, expansion).
- Wants to **do most things themselves**, with help on demand for whichever part they don't feel like doing
  — not automation of everything. Learning and hands-on fun matter; anything done for them should be
  explained. One-click placement must stay an optional setting (default off): they like placing blueprints
  by hand.
- **Guidance, not cheats**: the advisor only knows explored tiles; memory mode (what the player last saw,
  per overlay, with timestamps) is the default. Only actions the player could take through the UI.
- In play: short spoken live answers; richer briefings with on-screen annotations; in-game text in ONI's
  visual style; research is rarely their bottleneck — building, materials and their own planning time are.

## Phase 1: advising by screenshots (before the mod)

Claude advised each cycle by watching autosaves and taking screenshots with desktop control. It worked but
made repeated reading mistakes (a wire taken for a pipe, an electrolyzer missed, hydrogen trapped in the
barracks found late) and some UI could not be driven by clicks (Printing Pod selection, queuing research).
That motivated the mod: export the state instead of reading the screen.

Facts from that phase that may still be useful:
- Autosave every cycle is on. Saves: `Documents/Klei/OxygenNotIncluded/cloud_save_files/76561198074761684/<colony>/auto_save/`
  (Documents is OneDrive-redirected: `C:\Users\extre\OneDrive\Documents`).
- Colonies: **Citadel** (current; base game, No Sweat, Terra; cycle ~18–20 at the end of the session, 8
  duplicants) and Umbo (abandoned). The running colony plan the advisor maintains:
  `Documents/Klei/OxygenNotIncluded/advisor/citadel-plan.md`.
- Mods enabled (base game): Better Info Cards, AI Improvements, Stock Bug Fix, Thermal Tooltips, Settings
  Change Tool (was "Bulk Settings Change"), Sweep By Type, Mod Updater, Food Supply Tooltip, Critter
  Inventory; later the user added Bulk Settings Change, Show Building Ranges and others (see Player.log).
  In `mods.json`, `enabledForDlc: [""]` means enabled for the base game; the `enabled` field is legacy.
  Research Queue (Peter Han) is what allows queuing several research targets.
- If falling back to desktop control: request access to `oxygennotincluded.exe`; the first click only
  focuses the window; side-panel buttons work but research-tree tiles and the Pod's "Choose a Blueprint"
  did not respond to synthetic clicks; keyboard works once ONI has focus; Escape closes screens. The
  glowing blue icon top-left is the cosmetics screen, not the Pod.

## How the design evolved (in the user's order)

1. A mod that exports state + screenshots, with a hotkey ("look at this").
2. A map memory: live vs memory mode; per-overlay knowledge with timestamps; never unexplored tiles.
3. An in-game way to talk to Claude without switching windows → companion app with headless Claude Code;
   later voice (now: hold Ctrl+Shift+V, local Whisper).
4. Optional delegation of tedious tasks ("bot era" in Factorio): per-category off/ask/auto, logged,
   explained, standing rules run by the mod.
5. Projects: planned infrastructure shown as toggleable ghost overlays with progress and bill of materials.
6. Planning mode: pause, sketch marks, discuss; the advisor draws back.
7. Help dial per area (off/advise/plan/draft/place/maintain).
8. Live mode (instant, one or two spoken sentences, knows the cursor/selection, queries the game) vs
   briefing mode (paused, step by step, read aloud with annotations); escalation from live to briefing.
9. Longer term, explicitly "not now": generalise into a toolset for other planning-heavy games (Cities:
   Skylines II, Factorio, EU4) — generic core + per-game adapters with capability levels. For Factorio,
   evaluate github.com/jhjaggars/flma (MIT, Lua mod → files → Python CLI, read-only) as the read side.
