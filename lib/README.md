# lib/ — reference assemblies for building without the game

The mod compiles against these instead of the game's own DLLs, so anyone can build and audit it without
owning the game, and GitHub Actions can build releases. They were generated with
[JetBrains Refasmer](https://github.com/JetBrains/Refasmer) (`tools/update-lib.sh`): every method body is
removed, leaving only type and member signatures. **They contain no game code** and cannot run.

- `Assembly-CSharp.dll`, `Assembly-CSharp-firstpass.dll`: Oxygen Not Included, © Klei Entertainment.
  Klei allows reference assemblies of the game DLLs to be shared for modding (as in
  [peterhaneve/ONIMods](https://github.com/peterhaneve/ONIMods/tree/main/Lib)).
- `UnityEngine*.dll`, `Unity.TextMeshPro.dll`: Unity Technologies, under their own licenses.
- `0Harmony.dll`: [Harmony](https://github.com/pardeike/Harmony), MIT.
- `Newtonsoft.Json.dll`: [Json.NET](https://github.com/JamesNK/Newtonsoft.Json), MIT.

`GAME_VERSION.txt` says which game build they came from. Regenerate after a game update that changes
anything the mod uses: `tools/update-lib.sh` (Git Bash), then rebuild.
