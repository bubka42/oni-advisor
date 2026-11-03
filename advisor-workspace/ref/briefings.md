# Briefings: the format

Write the briefing as a **draft** to `live/briefing/draft.json` (replace the whole file), never straight to
`briefing.json`. The companion then gives you a `verify-briefing` turn: check every claim that something is broken,
idle, empty, missing or running out on the building or tile itself, fix or drop what doesn't hold, and only then
write `live/briefing/briefing.json`, which the player sees. The game pauses, moves the camera, shows each step's
marks and blueprint, and reads each `say` aloud; every briefing goes into an archive the player can replay.

```json
{ "id": "c19-h2", "cycle": 19, "title": "Hydrogen in the barracks",
  "steps": [
    { "title": "Where the hydrogen comes from",
      "say": "Your electrolyzer makes more hydrogen than the generator burns, and the extra drifts up the ladder shafts.",
      "text": "Optional detail for reading (not read aloud).",
      "focus": [118, 200], "zoom": "near",
      "marks": [ { "kind": "outline", "x0": 115, "y0": 191, "tone": "info", "label": "electrolyzer", "at": "electrolyzer" },
                 { "kind": "arrow", "x0": 116, "y0": 193, "x1": 119, "y1": 201, "tone": "problem", "label": "leak path", "at": "drifts up" } ],
      "project": "h2", "projectAt": "the generator" } ] }
```
- `say`: one or two spoken sentences, no coordinates; it also appears as a large caption, numbers highlighted.
  `text`: optional detail, also without coordinates.
- `zoom`: `near` | `base` | `wide`. `marks` show during that step; `project` (an id in `projects.json`) shows that
  blueprint's ghost.
- Marks are sketched **when the narration reaches them**: give each mark `"at"`, a short phrase copied exactly from
  that step's `say`. Marks without `"at"` are spread through the narration in list order. The blueprint builds up
  piece by piece: `"projectAt": "the generator"` starts it at that phrase, or one phrase per item
  (`["generator", "a gas pump", "pipe it"]`); order a project's items in the order you would explain building them.
- **Spotlight:** each step greys out the screen except what its marks touch (else the area around `focus`). Keep a
  step's marks together; choose the area yourself with `"spotlight": [x0, y0, x1, y1]`, or `false` for an overview.
- Length follows the situation: a calm cycle one or two steps, a big design as many as it takes. After writing,
  reply with one sentence on what it covers.
