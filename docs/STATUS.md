# Status and test plan

**Everything below is untested in the game** unless marked otherwise. It was written on 2026-10-27
against the installed game build (U59-744825) by reading the game's assemblies; all of it compiles.

| Part | Where | Tested? |
|---|---|---|
| State export (`state.json`, history, screenshots, requests, pod options) | `src/OniAdvisor/Snapshot.cs`, `AdvisorRunner.cs`, `PodReader.cs` | no |
| Map memory / live mode, map images, `map.bin` | `MapMemory.cs`, `MapExport.cs` | no |
| Construction backlog in `state.json` | `Snapshot.Construction` | no |
| Planning mode (drawing tool, marks, advisor marks) | `PlanMode.cs`, `PlanMarks.cs`, `MarkRenderer.cs` | no |
| Projects (ghost overlay, status, bill of materials) | `ProjectStore.cs`, `Overlays.cs` | no |
| Delegation commands, approvals, standing rules | `Commands.cs`, `Config.cs` | no |
| MapQuery reader | `tools/MapQuery` | **yes, against a synthetic file** (`MapQuery selftest`); not yet against a real export |
| Companion app (chat, approvals, request handling) | `tools/Companion` | builds; not run |
| Headless Claude Code from the companion | `ClaudeRunner.cs` | **partly**: flags and JSON output confirmed; the command-line Claude Code needs a one-time `/login` first |
| Speech (read aloud, push-to-talk) | `Voice.cs` | no (experimental) |

## Results of the first in-game test (2026-10-27, Citadel cycle 18)

- Mod loads; `state.json` exported with no section errors; units correct (duplicant kcal, colony foodKcal).
- Ctrl+Shift+A produced screenshot, maps, request. Map image orientation correct (upright).
- MapQuery works against a real `map.bin` (summary, region, find).
- Fixed after the test (needs redeploy): output went to `mods/advisor/live` (trailing slash in the mods
  path); `<link=...>` markup in names; 1,276 "rooms" (every enclosed rock pocket, incl. unexplored ones)
  → now only fully explored spaces containing the player's buildings.
- Ctrl+Shift+A is not bound in the game (no side effects).
- Planning mode works (pause, drawing, marks). Bug fixed: right-click also left the tool (ending planning
  mode and unpausing) → now right-click only deletes; Escape leaves without sending; Ctrl+Shift+P sends.
- Advisor marks and project ghosts render at the right tiles (a mis-placed test box was a wrong coordinate
  in the test file, not a rendering offset). Project status + bill of materials correct (GasPump + 5 wire
  tiles = 175 kg metal).
- Memory mode works: the oxygen layer held exactly the tiles viewed with the oxygen overlay open.
- Added after the test (needs redeploy): on-screen toasts + advisor status badge (`AdvisorHud`), fed by
  `live/advisor-status.json` from the companion.
- Not yet checked: pod.json, delegation, companion auto-reaction to requests (blocked by the folder bug).

## Second session (same evening)

- Folder fix confirmed: Ctrl+Shift+A → companion reacted on its own and produced a real briefing.
- On-screen toasts + status badge work.
- Voice: Win+H automation was unreliable (focus timing, voice panel stayed open) → replaced by
  in-game hold-to-talk (hold Ctrl+Shift+V) with local Whisper (ggml-small.en in
  `%USERPROFILE%\.oni-advisor\models`, NAudio recording). Works; transcription was correct.
  Offline check: 5.9 s of speech transcribed in 1.6 s on CPU.
- Ctrl+Shift+C shows/hides the companion without taking focus from the game (hide, not minimize —
  restoring a minimized window steals focus).
- Environment notes: the Claude desktop app virtualizes `AppData` for processes it starts, so .NET lives in
  `%USERPROFILE%\.dotnet` and Claude Code is found under `AppData\Local\Claude-Data\...\claude-code`;
  the command-line Claude Code needed a one-time `/login`. The laptop mic had been switched off in Sound
  settings ("Don't allow") during setup.

## First test session (suggested order)

1. **Deploy**: close the game, run `tools/deploy.ps1`, start the game, enable *ONI Advisor* in Mods,
   restart. Check `Player.log` (`%USERPROFILE%\AppData\LocalLow\Klei\Oxygen Not Included\Player.log`)
   for `[OniAdvisor]` lines and errors.
2. **Export**: load Citadel, wait a minute, open `advisor/live/state.json`.
   - `meta.errors` should be empty; any entry names the broken section.
   - `colony.foodKcal` and each duplicant's `kcal` should look like the game's numbers (unit check).
3. **Hotkey**: press Ctrl+Shift+A. Expect a screenshot in `live/screenshots/`, maps in `live/map/`
   and a file in `live/requests/`. Check Ctrl+Shift+A does not also trigger a game action.
4. **Maps**: open `live/map/elements.png`. It should be upright (sky at the top). If it is upside
   down, flip the row order in `MapExport.WritePng` and the `pixelToTile` formula.
   Run `MapQuery summary` and `MapQuery region` around the base and compare with the game.
5. **Memory mode**: open the oxygen overlay over part of the base, press Ctrl+Shift+A, check that
   `oxygen.png` shows only the area you looked at and that `rooms[].known` reflects it.
6. **Pod**: open the Printing Pod screen; `live/pod.json` should list the options.
7. **Planning mode**: Ctrl+Shift+P. The game should pause and a yellow hint label appear. Drag an
   area, Shift+drag a line, click a point, right-click to delete. Ctrl+Shift+P again → screenshot,
   `plan/player-marks.json`, request. Things likely to need tuning: mark z-order/visibility, label size
   (`MarkRenderer` constants), and whether the custom tool activates (otherwise the fallback is used).
8. **Advisor marks / projects**: copy the examples from `advisor-workspace/CLAUDE.md` into
   `live/plan/advisor-marks.json` and `live/projects/projects.json` (use real coordinates); cycle the
   overlay with Ctrl+Shift+M and the focus with Ctrl+Shift+N. Check `projects-status.json`.
9. **Delegation** (optional): set `"research": "ask"` in `advisor/config.json`, drop
   `{"action":"research","tech":"Ranching","why":"test"}` into `live/commands/inbox/test1.json`,
   approve it in the companion, check the game and `commands/results/test1.json`.
10. **Companion**: one-time `claude` + `/login` in a terminal; then run
    `tools/run-companion.cmd` (the .exe alone cannot find the per-user .NET install) and press "Briefing".

## Known gaps / ideas not done yet

- Printing Pod pick by command (needs the selection screen's internals) — not implemented.
- Project placement places buildings only; pipes and wires stay manual (their connections depend on
  the drag path). Intentional for now.
- No in-game chat panel: chat lives in the companion window (always on top). An in-game panel would
  need careful keyboard-focus handling; revisit after testing.
- Mark labels typed in-game are not supported; refer to marks by number (#1, #2) in chat.
- The help dial (`config.json` → `help`) is read by the advisor only; the mod does not enforce it.
