# ONI Advisor

An Oxygen Not Included mod plus a small companion app that let Claude act as an advisor: it reads the
colony's state (instead of the screen), talks with you in a window next to the game, draws suggestions
on your map, and — only if you switch it on — helps with tasks you don't feel like doing.

> **Status:** everything is written and compiles, but almost nothing has been tested in the game yet.
> See [docs/STATUS.md](docs/STATUS.md) for what is tested and the first-test checklist.

## Design goals

- **The player plays.** By default the mod only advises; every kind of help is opt-in, per area,
  and can be switched off again at any time.
- **Help with whatever part you don't feel like doing**, so a growing base doesn't stall:
  advice → planning help → project plans shown as overlays → optional placement → optional
  standing rules for routine upkeep.
- **Guidance, not cheats.** Only what the player knows (fog of war, optional memory mode); only actions
  the player could take through the normal UI, at normal cost.
- **Keep it a learning game.** Anything done for the player is logged and explained.
- **Aim:** a stable, self-sustaining colony that can grow beyond its starting biome.

## In the game

| Key | What |
|---|---|
| Ctrl+Shift+A | "Look at this": snapshot + screenshot + maps; the companion asks the advisor |
| Ctrl+Shift+P | Planning mode on/off. Pauses the game. Drag = area, Shift+drag = line, click = point, right-click = delete, Ctrl+Shift+X = clear. Turning it off sends your sketch to the advisor. |
| Ctrl+Shift+M | Cycle the advisor overlay: off → marks → marks + projects → projects |
| Ctrl+Shift+N | Cycle project focus: all projects / one at a time |

Opening the Printing Pod screen sends its options automatically. Every autosave sends a cycle snapshot.

## Settings (`Documents/Klei/OxygenNotIncluded/advisor/config.json`)

- `mapMode`: **memory** (default; the advisor knows each tile as you last saw it, per overlay —
  terrain in any view, gas in the Oxygen overlay, temperature in the Temperature overlay, germs in the
  Germ overlay, each with a timestamp) or **live** (current state of revealed tiles). Never unrevealed
  tiles. Reload the colony after changing.
- `help`: per area, how much the advisor should do: `off | advise | plan | draft | place | maintain`.
- `delegation`: per category (`research, skills, priorities, buildings, storage, projects`), whether
  commands from the advisor are `off` (refused, the default), `ask` (you approve each in the companion)
  or `auto`.
- `rules`: standing rules the mod runs itself — `researchPlan` (tech list) and `skillPlans`
  (per duplicant skill list) — each only when its delegation category is `auto`.

## Files (`advisor/live/`)

`state.json` (duplicants, rooms, power, problem buildings, construction backlog, projects, research,
resources, pod timer) · `history/` · `screenshots/` · `map/` (layer images, `map-info.json`, `map.bin`)
· `pod.json` · `plan/` (your marks, advisor marks) · `projects/` (`projects.json` from the advisor,
`projects-status.json` from the mod) · `commands/` (inbox, pending, decisions, results, log) ·
`requests/` (one file per event).

## Companion app

`tools/Companion`: an always-on-top window with the chat, quick buttons (Briefing, Pod pick, What's
wrong?, Review marks), approvals for delegated commands, and optional speech (read aloud;
push-to-talk dictation; Windows voice typing with Win+H also works). It runs Claude Code headlessly
(`claude -p`, one continuing conversation) in the advisor folder, using `advisor-workspace/CLAUDE.md`
as the advisor's instructions (copied there automatically).

One-time setup: Claude Code's command-line login is separate from the desktop app — run `claude` in a
terminal once and type `/login`.

## Build

Needs only the .NET SDK 8 (version pinned in `global.json`); the game is not needed to build. The mod
compiles against `lib/`, reference-only copies of the game's assemblies with all code removed (see
`lib/README.md`; regenerate after a game update with `tools/update-lib.sh`). Builds are reproducible.

```
dotnet build src/OniAdvisor/OniAdvisor.csproj -c Release -p:Deploy=true   # also copies the mod into mods/Dev (close the game first)
dotnet build tools/MapQuery/MapQuery.csproj -c Release
dotnet build tools/Companion/Companion.csproj -c Release
```

Enable "ONI Advisor" in the game's Mods menu and restart the game.

**Releases** are built by GitHub Actions (`.github/workflows/build.yml`) from a `v*` tag, with a signed
build-provenance attestation for every file: `gh attestation verify <file> -R bubka42/oni-advisor`. To
check the mod DLL yourself, build the tag with `-p:ContinuousIntegrationBuild=true` and compare its
SHA-256 with `SHA256SUMS.txt` in the release.

## Stages

1. Data export, screenshots, hotkey — written
2. Map memory/live, images, MapQuery — written (MapQuery self-tested)
3. Construction backlog — written
4. Planning mode (marks, both ways) — written
5. Projects with ghost overlays, progress, bill of materials — written
6. Chat — written as the companion app (in-game panel: later, if wanted)
7. Delegation (optional): commands, approvals, standing rules, project placement — written
8. Voice — basic version in the companion (experimental)
