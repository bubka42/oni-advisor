# Projects: quests, milestones, pitches and blueprints

## Quests (`live/quests.json`)

Whole file; keep every project that still exists, whatever its state.
```json
{ "quests": [
  { "id": "water-loop", "title": "Close the water loop", "kind": "infrastructure", "horizon": "long",
    "why": "Toilets turn 600 kg of water a cycle into polluted water; recycle it instead of draining the reservoir.",
    "milestones": [ { "id": "m1", "text": "Build the polluted water reservoir", "check": { "project": "sieve", "items": [1] } },
                    { "id": "m2", "text": "Build the water sieve and its pipes", "check": { "project": "sieve", "items": [2, 3, 4] } },
                    { "id": "m3", "text": "Research Sanitation for showers", "check": { "research": "Sanitation" } } ],
    "project": "sieve", "advice": null, "status": "proposed", "pitch": { "steps": [ ] } } ] }
```
- **Milestones** (3–7) are actions the player does, in order, each with a `check` the game measures, so they tick
  themselves and show progress. Never a state unrelated events can spoil ("a cycle with nobody suffocating"); say the
  action instead. Checks:
  - `{"build": "Gas Pump", "count": 2, "area": [x0,y0,x1,y1]}` finished buildings by name (area optional)
  - `{"project": "h2", "items": [1, 2]}` blueprint pieces built (as in `h2.1`; omit items for all)
  - `{"room": "Barracks", "count": 2}` rooms the game recognises
  - `{"research": "Smelting"}` a finished tech
  - `{"dig": [x0,y0,x1,y1]}` natural tiles dug out in that area
  - `{"stored": "Copper Ore", "kg": 2000}` in storage, colony-wide

  Only if no check fits, leave it out and set `"looksDone": true` once confirmed. A reached milestone stays reached;
  the last one completes the project. Keep reached milestones' id and text unchanged.
- **Pitch**: `"pitch": {"steps": [...]}`, briefing steps (see `ref/briefings.md`) narrated when the player presses
  PITCH. 3–5 steps: the problem it solves and what it gains; where it goes (marks, the blueprint building up);
  what it takes (materials with amounts, research, dupe time, power); when to do it. Honest: if it can wait, say so.
- **Blueprint**: infrastructure proposals link a drafted blueprint with `"project"` and show it in the pitch.
- `kind`: `infrastructure` or `colony`; `horizon`: `short` or `long`; `advice`: one sentence shown on it ("Hold
  this until you have refined metal"); `"status": "scrapped"` hides a proposal that no longer fits.
- The player's decisions are in `live/quests-state.json` (written by the game; never write it). Changes arrive in
  your next message. Each project and milestone has its own thread ("quest:ID", "quest:ID:MILESTONE").

## Blueprints (`live/projects/projects.json`)

Whole file; keep the projects you still want.
```json
{ "projects": [ { "id": "h2", "name": "Hydrogen power", "purpose": "burn electrolyzer hydrogen",
  "items": [
    { "type": "building", "prefab": "GasPump", "x": 91, "y": 114, "rotation": "Neutral", "note": "top of the shaft" },
    { "type": "gaspipe", "path": [[91,113],[91,110],[64,110]] },
    { "type": "wire", "path": [[62,109],[62,100]] } ] } ] }
```
Item types: `building` (game prefab ID: GasPump, GasFilter, HydrogenGenerator, Electrolyzer, LiquidPump,
LiquidVent, GasVent, WaterPurifier, AirFilter, Refrigerator, FarmStation, MessTable, FlushToilet, WashSink, Bed,
Door, Ladder, Tile …) and runs: `gaspipe`, `liquidpipe`, `wire`, `heavywire`, `automation`, `conveyor`. A run may
name its own prefab (`InsulatedGasConduit`); any item may pick `"material"` or `"materials"`. A building's (x, y)
is its origin cell: bottom row, horizontally centred (for an even width, the cell just left of centre). After
writing, check `projects/projects-status.json` and fix anything `blocked` (it gives the game's reason); "needs
digging first" is fine. The player can SHOW, STAMP (place copies anywhere) or PLACE AS PLANNED it; placing orders
everything at once (digging, floors first). Design whole, self-contained layouts. Only place it yourself
(`placeProject`) if the player asks or the `projects` delegation is on.

## Guided planning (trigger `guide`)

The player holds a building in the build tool and wants the rest laid out around that spot. Write project
`"id": "guide"` (replacing any earlier one) with the held building there plus what makes it work: its inputs and
outputs (pumps in the right pocket, filters, vents, storage) checked against the actual gases and liquids nearby;
power (a wire to a suitable circuit) and automation; ladders or tiles for access. Fewest pieces that work; reuse
existing networks. Reply in a sentence or two: what it does, what the player must decide.
