# Drawing on the player's map: marks and pins

The player can't see coordinates: in anything they read or hear, refer to places by a mark's label or a landmark.
If a coordinate slips into a message, the companion turns it into a numbered mark ("mark 1").

## Marks

Write `live/plan/advisor-marks.json` (replace the whole file; an empty list clears). They belong to the message you
write with them: they show until your next marks replace them, and the game keeps a copy per message that the
player can switch back on. Marks about a briefing go in its steps, marks about a project in its pitch.

```json
{ "marks": [
  { "kind": "outline", "x0": 122, "y0": 205, "tone": "problem", "label": "generator: no hydrogen" },
  { "kind": "point",   "x0": 128, "y0": 205, "tone": "problem", "label": "filtered port not connected" },
  { "kind": "path",    "points": [[128,205],[128,208],[122,208]], "arrow": true, "tone": "build", "label": "new pipe" },
  { "kind": "arrow",   "x0": 116, "y0": 193, "x1": 119, "y1": 201, "tone": "info", "label": "hydrogen rises this way" },
  { "kind": "area",    "x0": 108, "y0": 201, "x1": 132, "y1": 203, "tone": "check", "label": "hydrogen pocket" },
  { "kind": "line",    "x0": 52, "y0": 100, "x1": 52, "y1": 94, "tone": "build", "label": "dig shaft" } ] }
```
- `outline`: the footprint of the building at (x0, y0), any cell of it. Best way to point at a building.
- `path`: a route through tile points (pipes, wires, digging, walking); `arrow: true` ends it in an arrow.
- `arrow`: from (x0,y0) to (x1,y1): flows, directions, "move this here".
- `tone`: `problem` (red), `build` (green: do this), `check` (yellow: look at this), `info` (cyan, default);
  `color: "#RRGGBB"` overrides.
- `note` (optional): shown when the player clicks the label; without one, the click shows your message.
- Labels: a few words. Mark what you talk about, generously: the buildings, the faulty port, the route, where to
  dig, where gas collects.

## Pins: fixes the player can apply with one click

Write `live/pins.json` (whole file; keep the pins still relevant) for a concrete, local fix:
```json
{ "pins": [ { "id": "c50-terrariums", "x": 158, "y": 188, "tone": "check", "title": "4 idle terrariums",
    "text": "They have no algae or water and only take dupe time. Switch them off until you can feed them.",
    "highlight": [155, 187, 162, 188],
    "actions": [ { "label": "Turn them off",
                   "command": { "action": "enable", "targets": [[155,187],[157,187],[159,187],[161,187]], "on": false } } ] } ] }
```
A round button at (x, y); clicking opens a card with the text, the actions as buttons, ASK and DISMISS. An action
is any delegated command (see `ref/commands.md`) and runs when the player clicks it. Check the targets first, make
each action do exactly what its label says, give every pin a stable unique `id`, and keep to the three or four most
useful. What the player did with them arrives in your next message.
