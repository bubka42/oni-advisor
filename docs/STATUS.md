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

## Redesign: one advisor, in-game panel, briefing archive, Kokoro voice (2026-10-28) — built, not yet run

From the player's feedback (live mode felt like "someone busy elsewhere"; wants Claude Code-style chat in
game with annotations; briefings forced into 4 steps with too few marks; archive; better voice):
- **One conversation**: `AdvisorSession` (companion) = one persistent Claude Code process, Sonnet at medium
  effort, full tools, streaming (`--include-partial-messages`), resumed across restarts via `SessionId`.
  Replaces the live channel and the separate briefing runs. Answers take ~5–15 s by design.
- **Follows the game**: `GameWatch` diffs `state.json` (now every 20 s; includes the game's alerts) and puts
  "Since your last message, in the game: …" into the next prompt. `Proactive` (never | serious | freely,
  default freely; ≥ `ProactiveMinSeconds` = 120 s apart, not within 45 s of the player speaking) sends a
  `trigger: update` turn; the advisor replies `(silent)` when there's nothing worth saying.
- **In-game panel** (`AdvisorPanel`, Ctrl+Shift+T): CHAT (conversation from `live/chat/chat.json`, the answer
  being written + current activity from `live/chat/stream.json`, typing box; Enter sends, Esc leaves the
  box; game keys blocked via a `KScreenManager.OnKeyDown/OnKeyUp` prefix; LOOK / BRIEF ME / STOP / NEW CHAT)
  and BRIEFINGS (archive from `live/briefing/archive/index.json`; click = replay via `Briefing.OpenFile`).
- **Briefings**: any length (instructions), archived by the companion; new mark kinds `outline` (building
  footprint), `path` (+arrow), `arrow`; `tone` problem/build/check/info.
- **Voice**: `KokoroVoice` (sherpa-onnx 1.13.8, offline), streamed sentence-by-sentence with long sentences
  split at commas; Windows voice as fallback (`TtsEngine`, `TtsModelDir`, `TtsSpeaker`, `TtsSpeed`).
  Installed model: `kokoro-int8-multi-lang-v1_1` (only 3 English voices: af_maple 0, af_sol 1, bf_vale 2;
  logs unknown-phoneme warnings). Better English: `kokoro-multi-lang-v1_0` (350 MB, af_heart = id 3) —
  not downloaded yet. Measured (warm, 4 threads): first audio ~2.4 s for a 15-word sentence, RTF ~0.7.
- **Tips with urgency** (player request): unasked tips start with [critical]/[urgent]/[later]; the companion
  writes `live/tips.json`; `TipBulb` hangs a bulb from the top of the screen in that colour (critical pauses
  the game and opens the panel; urgent pulses; hover shows the tip; click opens the panel = seen).
- **Threads + long-term planning** (player request): every tip, briefing and answer can get a Slack-style
  thread (REPLY under it; BRIEFINGS tab DISCUSS; briefing player ASK; bulb click opens the tip thread). Same
  Claude session; prompts carry `[thread: N]` + the thread so far. `chat.json` entries have `thread` /
  `briefing`; archive index has `chat`. PLAN tab shows `live/colony-plan.md` (advisor-maintained) with a
  `plan` thread. Calm moments trigger `review` turns (every `PlanReviewMinutes` = 12) to advance the plan.
- **Kokoro v1.0** downloaded (`kokoro-multi-lang-v1_0`, 54 voices; default af_heart = 3): first audio ~0.8 s,
  RTF ~0.2 ? much faster than the int8 v1.1 model (which can be deleted).
- Things most likely to need fixing on first run: typing (does the KScreenManager patch stop WASD/camera
  keys?), TMP input field focus, panel scroll/zoom, stream_event parsing, `--resume` of the old session,
  `(silent)` handling, Kokoro audio device.

## Advice quality fixes (2026-10-27, after reading the player's real exchanges) — built, not yet run in game

Findings from the advisor's own transcripts (`~/.claude/projects/C--Users-extre-OneDrive-Documents-Klei-OxygenNotIncluded-advisor/`):
live mode made **zero** tool calls in 19 answers (never queried, never opened hotkey screenshots); live and
briefing sessions never saw each other (live denied a briefing's claim); pipes/wires aren't exported; the
briefing used a wrong electrolyzer rate.

Changes (companion + advisor CLAUDE.md only; no mod redeploy needed):
- `ConversationLog` → `live/conversation.json`: live answers, look/pod/sketch answers and briefing summaries;
  the recent entries go into every prompt of both modes. Seeded from `advisor-log.json` + the last briefing.
- `LiveFacts`: before each live question the companion queries the game itself (buildings with status and
  gas map around the cursor and the selection) and puts the answers in the prompt.
- Hotkey/sketch screenshots are attached to the live prompt as an image (JPEG ≤1568 px). Verified with
  headless Claude Code: ~3 s, it described the hydrogen-filled barracks from the image.
- `LiveClaude`: turn counting so a late event from an earlier turn can't answer the next question; a
  text-only message is accepted after 600 ms unless a tool call follows (Claude Code streams blocks
  without a stop reason).
- Advisor `CLAUDE.md`: ONI numbers sheet (base game), "one conversation" rule, honest note that pipe/wire
  networks aren't exported yet.
- Step 3 (mod, deployed, not yet seen running): `Networks.cs` — live queries `pipes` (gas/liquid/power/rail
  layout with box glyphs + contents per pipe tile), `ports` (each building's pipe/power ports: connected?,
  net/circuit id, contents), `rates` (colony production/consumption per element, incl. dupes' breathing;
  also in `state.json` → `rates` and the digest); `buildings` lists conversions and wattage and skips
  pipes/wires/tiles; `cell` shows pipes/wires on the tile; rooms without oxygen-overlay data report gas
  types from the plain view (`gasTypes`); batteries show charging/draining. LiveFacts also fetches ports,
  and the pipe/wire layer when that overlay is open or the question mentions it.
- First things to check in game: `MapQuery live ports` around the electrolyzer/filter/generator (port cells
  and "NOT CONNECTED" right?), `live pipes ... gas` glyphs match the overlay, `live rates` numbers,
  `state.json` has no `meta.errors`.
- Then: measure whether a stronger live model is fast enough.

## Third round (same evening) — installed, not yet seen running

Live mode (short spoken answers, persistent fast channel, colony digest + player view in each prompt,
live map queries, escalation to briefings), briefing mode (step-by-step panel with voice, camera, marks,
projects), ONI styling, Ctrl+Shift+Q voice toggle. The game then failed to start twice with "another
instance is already running" / `Can't init Steam distribution platform` — most likely Steam state, not
the mod. See `docs/HANDOFF.md`.

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
