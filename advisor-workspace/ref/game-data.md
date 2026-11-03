# Reading the game: files, the player's view, MapQuery

## Files (all under `live/`)

| File | What |
|---|---|
| `state.json` | duplicants (skills, interests, traits, needs, problems), rooms (gas g/tile, temp, germs, freshness), power circuits, buildings with problems, construction backlog (`construction`), crops (`crops`: planted, growing, stalled with reason), what lies around the base (`frontier`), projects summary, research (incl. `researchedNotBuilt`), resources, pod timer |
| `history/cycle-NNNN.json` | autosave snapshot per cycle (compare with the previous cycle for trends) |
| `screenshots/*.png` | autosave / hotkey / plan screenshots (look only when the picture adds something) |
| `map/*.png`, `map/map-info.json` | whole-map layer images; map-info explains pixel→tile coordinates and colours |
| `map/map.bin` | query with MapQuery instead of reading images for exact values |
| `pod.json` | Printing Pod options while the pod screen is open |
| `plan/player-marks.json` | the player's sketch from planning mode (`#1`, `#2`, ... — they refer to them by number) |
| `projects/projects-status.json` | each blueprint's items: built / ordered / ready / blocked (+reason), materials still needed |
| `commands/results/`, `commands/log.jsonl` | outcomes of delegated commands |
| `../config.json` | help levels, delegation permissions, standing rules |

## What the player is looking at

Every live prompt includes the **player's view** (`live/view.json`): visible area, active overlay, the tile under
the **cursor** (element, gas, temperature, building, room), the **selected** object with its status, and a text
map of ~40×20 tiles around the cursor. "Here", "this", "that spot" mean the cursor tile or the selection.
For questions the companion has already fetched the buildings, ports and gas around the cursor and the selection
under "Fetched from the game just now". Hotkey and sketch prompts come with the screenshot attached.

## Asking the running game (MapQuery live)

Fresh data in well under a second, with memory mode and the fog of war applied. Don't say you can't see a place:
ask.

```
dotnet <MapQuery.dll> live region x0 y0 x1 y1 [terrain|gas|temp]   (≤ 2400 tiles; rows top to bottom)
dotnet <MapQuery.dll> live cell x y                                  (element, mass, temp, germs, building + status, room)
dotnet <MapQuery.dll> live buildings x0 y0 x1 y1                     (buildings with status lines, what they convert, watts)
dotnet <MapQuery.dll> live find coal [x y]                           (known deposits, nearest first)
dotnet <MapQuery.dll> live pipes x0 y0 x1 y1 [gas|liquid|power|rail] (network layout + what is in each pipe)
dotnet <MapQuery.dll> live ports x0 y0 x1 y1                         (each building's ports: connected? which net? contents)
dotnet <MapQuery.dll> live rates                                     (colony production/consumption per element)
dotnet <MapQuery.dll> live view                                      (refresh the view)
```

**How to run them (or they are denied):** one plain `dotnet` command per Bash call, written out in full with the
quoted MapQuery path from the prompt: `dotnet "C:\...\MapQuery.dll" live buildings 99 195 125 199`. Nothing else
on the line: no `cd`, no variables, no `;`, `&&` or pipes, no redirects; the PowerShell tool is denied too. Run
several queries as several calls. For files use Read, Glob, Grep, Write and Edit. If a query is denied, fix the
command and run it again; don't tell the player the shell is blocked.

**Pipes, wires and rails.** `pipes` draws a network layer: box-drawing glyphs show how segments connect (up = the
row above), then for gas/liquid an element letter and a fill digit (9 = a full packet: 1 kg gas, 10 kg liquid;
blank = empty pipe); for power a circuit letter. `ports` is usually the faster way to find a fault: every port with
"NOT CONNECTED (no pipe)" / "NOT CONNECTED (no wire)", or the net/circuit id and what is in the pipe right there.
Two ports are on the same line only if they share a net id. A pipe reading "no packet at this instant" is not a
dry pipe: pipes carry packets with gaps.

## The saved map (no game needed)

```
dotnet <MapQuery.dll> summary
dotnet <MapQuery.dll> region x0 y0 x1 y1 [elem|gas|temp|germs|age|build]   (max 6000 tiles)
dotnet <MapQuery.dll> cell x y
dotnet <MapQuery.dll> find coal
```
Coordinates are game tiles; y grows upward.
