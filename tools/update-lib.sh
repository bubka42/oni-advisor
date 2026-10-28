#!/usr/bin/env bash
# Regenerates lib/: reference-only copies of the game assemblies the mod compiles against, so the mod can be
# built without the game (GitHub Actions, or anyone auditing the source). Refasmer strips every method body:
# the copies contain the game's type and member signatures but none of its code. Klei allows this for the
# game DLLs (as done by peterhaneve/ONIMods); the other DLLs keep their own licenses (lib/README.md).
# Run after a game update, from the repo root (Git Bash on Windows):  tools/update-lib.sh [game-dir]
set -euo pipefail
game="${1:-C:/Program Files (x86)/Steam/steamapps/common/OxygenNotIncluded}"
managed="$game/OxygenNotIncluded_Data/Managed"
[ -d "$managed" ] || { echo "Game assemblies not found in $managed (pass the game folder)" >&2; exit 1; }
dotnet="$USERPROFILE/.dotnet/dotnet.exe"; [ -x "$dotnet" ] || dotnet=dotnet
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# Exactly the assemblies src/OniAdvisor/OniAdvisor.csproj references.
dlls="Assembly-CSharp Assembly-CSharp-firstpass 0Harmony Newtonsoft.Json UnityEngine UnityEngine.CoreModule
      UnityEngine.InputLegacyModule UnityEngine.ScreenCaptureModule UnityEngine.ImageConversionModule
      UnityEngine.TextRenderingModule UnityEngine.UI UnityEngine.UIModule Unity.TextMeshPro"

cd "$(dirname "$0")/.."
mkdir -p lib
"$dotnet" tool restore > /dev/null
for name in $dlls; do
  "$dotnet" refasmer -q --omit-non-api-members=true -o "lib/$name.dll" "$managed/$name.dll"
done
# The game has no version file; identify the build by its Assembly-CSharp.dll.
version="Assembly-CSharp.dll sha256 $(sha256sum "$managed/Assembly-CSharp.dll" | cut -c1-16), dated $(date -r "$managed/Assembly-CSharp.dll" +%Y-%m-%d)"
echo "Generated from the Oxygen Not Included build with $version, on $(date +%Y-%m-%d)" > lib/GAME_VERSION.txt
echo "lib/ updated from $managed"
