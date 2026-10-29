# Before an open playtest (volunteers with Claude subscriptions, from GitHub)

Notes from 2026-10-29. Parked while the user playtests more; nothing here is started.

## Blockers, in order

1. **Install without building.** Today it only works from a built clone of the repo:
   - the mod finds the companion via `companion-path.txt`, written by the Deploy target with a repo path;
   - the companion finds MapQuery at `repoRoot/tools/MapQuery/bin/Release/net8.0/MapQuery.dll` (`MapQueryDll()`);
   - `EnsureWorkspace` copies `CLAUDE.md` and `player.md` from `repoRoot/advisor-workspace`.

   Fix: one release zip that unpacks to `mods/Local/OniAdvisor/` with the companion, MapQuery and the advisor
   files in subfolders; the launcher and companion resolve paths relative to themselves (falling back to the
   repo layout for dev builds). Turn CI's separate mod and companion zips into this one attested package.
   Building from source stays documented for auditors.
2. **Finding Claude Code.** `FindClaude` checks `claude.exe` on PATH plus Claude desktop folders. Add the npm
   install (`claude.cmd`) and the native installer (`~/.local/bin`). Show a clear in-game message when Claude
   Code is missing or not signed in.
3. **Mod metadata (mod.yaml / mod_info.yaml).**
   - `supportedContent: VANILLA_ID` excludes Spaced Out and other DLC players: declare all, test once on a
     Spaced Out save.
   - `staticID: extre.OniAdvisor` contains the Windows user name: change it (e.g. `bubka42.OniAdvisor`) before
     release, since changing it later loses people's settings.
   - The description is out of date ("exports state, read-only").
4. **Tighter advisor permissions.** `--allowedTools ... Bash(dotnet *)` allows any dotnet command, and colony
   names, duplicant names and game text reach the prompts. Limit it to the exact MapQuery invocation. Writes
   are already limited to the advisor folder.
5. **Cost and privacy up front**, in the README and a first-run notice in the game:
   - it uses the player's Claude subscription;
   - typical usage per hour (take figures from `advisor/usage.jsonl` after a session);
   - testers start with SPEAKS UP on Long-term by default;
   - screenshots and colony data go to Anthropic through Claude Code.
6. **One clean-install test**: fresh Windows user account (or another PC), fresh Claude Code, install from the
   release zip only.
7. **Easy problem reports**: a REPORT A PROBLEM button that zips the advisor's lines from Player.log, usage,
   recent chat and versions (with a privacy note), plus a GitHub issue template.

Estimate: items 1–4 and 7 about a day; item 6 needs a clean machine; item 5 is writing plus real usage numbers.

## Should have, not blockers

- Say plainly: Windows only (companion and voice), English only.
- Voice is optional: a "download voice models" button with sizes, or documented manual steps. Without models
  it falls back to Windows voices and voice input is off (already works).
- Known-issues list of untested parts: blueprint placement and pipe connections, guided planning, pins,
  single-key hotkeys.
- Remove the network-spike code and tools (`NetChecks.cs`, `NetSpike.cs`, `tools/NetSpikeHarness`).
- Decide whether `docs/BACKGROUND.md` and `docs/HANDOFF.md` (personal notes) stay in the public repo.

## Later

Bring-your-own API key (in-game mode, the netspike showed it is feasible), Mac/Linux.
