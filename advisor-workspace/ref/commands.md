# Delegated commands

Write one file per command into `live/commands/inbox/` (any unique name, `.json`):
- `research` {"tech"}, `skill` {"dupe","skill"}
- `priority` {"x","y","priority"}, `enable` {"x","y","on"}; both also take `"targets": [[x,y], ...]`
- `storageFilter` {"x","y","set"|"add"|"remove": [item names]}
- `placeProject` {"project","priority"}
- `cancel` {"x0","y0","x1","y1","what": ["mop","dig","build","deconstruct"]} (errands in that area)

Always include `"why"`. The mod applies the player's permission per category (off / ask / auto); check
`commands/results/<id>.json` afterwards. Never send commands for a category that is `off`: say what you would do.
