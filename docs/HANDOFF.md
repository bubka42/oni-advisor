# Handoff notes

For an agent picking this up. Read with `README.md` (what it is) and `docs/STATUS.md` (what is tested).

**Latest (2026-10-28):** the advisor was redesigned into one conversation with an in-game chat panel,
briefing archive, richer marks and a Kokoro voice; see the top section of `docs/STATUS.md` for what changed
and what to check first. The Steam start-up problem below resolved itself: the player played with the
live/briefing version afterwards (their exchanges are in
`~/.claude/projects/C--Users-extre-OneDrive-Documents-Klei-OxygenNotIncluded-advisor/`). The sections below
describe the 2026-10-27 state; where they talk about "live mode" vs "briefing mode", that split is gone.

## Where things stood on 2026-10-27

- **Installed in the game** (mods/Dev/OniAdvisor, built 19:44): everything below, including briefing mode,
  ONI styling, live view + queries, voice toggle. **Not yet seen running**: right after installing, ONI
  failed to start twice ("another instance is already running"). `Player.log` shows our mod loaded fine,
  then `[WARNING] Can't init Steam distribution platform...` and an immediate shutdown (the YAML "Fatal
  parse error ... Thread was being aborted" lines are a side effect of that shutdown, not a cause). No
  leftover ONI/Unity process or window existed. Diagnosis given to the user: Steam still believes the game
  is running → click Stop in Steam or fully restart Steam. **Confirm with the user whether that fixed it.**
  If it persists, check whether the previous (19:01) run had the same Steam warning, and test with the
  mod disabled to rule it out.
- **Companion** (tools/Companion) is up to date and was running; it auto-copies
  `advisor-workspace/CLAUDE.md` into the advisor folder on start when newer.

## Tested in game (works)

State export, maps (upright), memory mode, MapQuery on real data, planning marks (after right-click fix),
advisor marks + project ghosts at correct tiles, toasts + status badge, companion briefing end-to-end
(old free-text style), Whisper hold-to-talk (Ctrl+Shift+V), Ctrl+Shift+C toggle without stealing focus.

## Written but untested in game

1. **Briefing mode** (`src/OniAdvisor/Briefing.cs`): advisor writes `live/briefing/briefing.json` (steps:
   say/text/focus/zoom/marks/project); Ctrl+Shift+B opens an ONI-styled panel, pauses, moves the camera
   (`CameraController.SetTargetPos`, ortho sizes near 9 / base 15 / wide 26), shows step marks and
   `ProjectStore.ForceShow(project)`, writes `playback.json`; companion speaks `say`, writes
   `playback-done.json`; mod auto-advances. Buttons Back/Next/New/Close. Panel lists recent answers from
   `live/advisor-log.json`. Likely tuning: panel size/position, scroll, zoom levels, TMP layout.
2. **ONI styling** (`OniStyle.cs`): colours sampled from game screens; header font looked up by name
   containing "GRAYSTROKE" (fallback: `Localization.FontAsset`). Verify the font was found.
3. **Live view + queries** (`LiveQueries.cs`, `MapText.cs`): `live/view.json` every 2 s (visible area,
   overlay, cursor tile via `PlayerController.GetCursorPos(KInputManager.GetMousePos())`, selection via
   `SelectTool.Instance.selected`, 40×20 text map). Queries `live/queries/req-*.json` → `res-*.txt`
   (region/cell/buildings/find/view), checked every 0.15 s. Client: `MapQuery live ...`.
   First check: `dotnet tools/MapQuery/bin/Release/net8.0/MapQuery.dll live view` with a colony loaded.
4. **Ctrl+Shift+Q** voice toggle (mod request `speak-toggle` → companion → status `notice` toast).
5. **Escalation**: live reply ending in `[[briefing: topic]]` → companion strips it, enqueues a focused
   briefing.
6. Pod options (`pod.json`) and delegation (commands/approvals/rules) — never exercised.

## Architecture in one paragraph

The mod (`src/OniAdvisor`, net48, Harmony) attaches `AdvisorRunner` to the Game object; it exports
`live/state.json` (periodic + autosave + hotkey), records map memory, renders maps, handles hotkeys,
polls files written by the advisor (marks, projects, commands, briefing) and the companion's
`advisor-status.json`, and draws HUD/marks/panel. Everything lives in
`Documents/Klei/OxygenNotIncluded/advisor/` (OneDrive). The companion (`tools/Companion`, WinForms
net8) watches `live/requests/` and talks to Claude Code headlessly: **live mode** = one persistent
`claude -p --input-format stream-json` process (Sonnet, `--effort low`, fresh session every 25 turns,
answer taken from the first pure-text assistant message, colony digest + `view.json` in each prompt);
**briefing mode** = per-request `claude -p` with `--resume` of a long-lived session (default model).
Whisper (`ggml-small.en`, `%USERPROFILE%\.oni-advisor\models`) + NAudio for voice input; System.Speech
for voice output.

## Hotkeys (all Ctrl+Shift)

A look · V hold-to-talk · T advisor panel (chat + briefings) · B briefing · Q voice on/off · C show/hide companion · P planning mode
(drag area, Shift+drag line, click point, right-click delete, Esc leave, Ctrl+Shift+P send) · X clear
marks (planning) · M overlay cycle · N project focus.

## Environment gotchas (learned the hard way)

- The Claude desktop app **virtualizes AppData** for processes it starts. .NET SDK 8 is therefore at
  `%USERPROFILE%\.dotnet\dotnet.exe` (not on PATH; call by full path). Claude Code CLI is at
  `%LOCALAPPDATA%\Claude-Data\claude_*\claude-code\<version>\<hash>\claude.exe` (version changes on app
  updates; `ClaudeRunner.FindClaude` searches all known places). It needed a one-time `/login`.
- **Deploying the mod requires ONI to be closed** (DLL locked): `dotnet build src/OniAdvisor/OniAdvisor.csproj
  -c Release -p:Deploy=true`.
- **Restarting the companion**: it may be hidden (no window title), so stop it by command line:
  `Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" | ? CommandLine -match 'OniAdvisorCompanion\.dll' | % { Stop-Process -Id $_.ProcessId }`.
  Never stop all `dotnet` processes. Start: `tools/run-companion.cmd`. It has a single-instance mutex.
- PowerShell patching with single-quoted here-strings breaks on code containing `' '` — use the Edit
  tool for such code.
- The companion has a method named `System(...)`; inside `MainForm` use `global::System.…` for BCL types.
- Game assemblies: target **net48**; the game defines its own `DateTime` (use `System.DateTime`); the
  game's Newtonsoft `JObject` has no `ContainsKey`. `BuildingDef.IsValidPlaceLocation(int cell, …)` uses
  an `out string`. API lookups were done with a metadata-only inspector (System.Reflection.Metadata);
  PowerShell reflection on the game DLLs crashes.
- Right-click inside a custom `InterfaceTool` deactivates the tool unless consumed (fixed in PlanTool).
- Restoring a minimized window steals focus; the companion hides/shows instead (`SW_HIDE` / `SW_SHOWNA`).
- Measured latency: Claude Code start-up ~2–7 s; answer on a warm process ~1.8 s (Haiku) / ~2.5 s
  (Sonnet low effort); a post-turn summary adds ~2 s after each answer (not waited for).

## The user (see also README "Design goals")

Plays ONI (Citadel colony, base game, No Sweat, Terra), wants to do most things themselves with help on
demand; goal is a stable colony that grows beyond the starting biome. Prefers short spoken live answers,
richer briefings with on-screen annotations, ONI-consistent visuals, fair play (memory mode, fog of war).
Mic: laptop Realtek Microphone Array (QC45 headphones for output only).

## Ideas parked for later

- Pod pick by command; pipe/wire placement for projects; in-game text input; streaming TTS of the first
  sentence; Haiku as live model if speed matters more; generalising into a multi-game toolset
  (core/ + games/oni/; Factorio candidate read adapter: github.com/jhjaggars/flma).
