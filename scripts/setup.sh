#!/bin/sh
# One-time setup: clone the Steam install into ./game (APFS clone, no extra disk),
# add BepInEx, and decompile the game code into ./decompiled for reading.
# Re-run after a game update. Needs: dotnet SDK (brew install dotnet).
set -e
ROOT=$(cd "$(dirname "$0")/.." && pwd)
STEAM_GAME=${STEAM_GAME:-"$HOME/Library/Application Support/Steam/steamapps/common/Thronefall"}
BEPINEX=5.4.23.6
BEPINEX_SHA=2e711e21f6570da964fce4654b1976500dbe043f61778bce0008500078564638
# ilspycmd needs the runtime of the `dotnet` on PATH (SDK 10). A DOTNET_ROOT left over from
# another install (e.g. ~/.dotnet with only .NET 8) makes it fail, so derive it here.
dotnet_dir=$(dirname "$(readlink -f "$(command -v dotnet)")")
[ -d "$dotnet_dir/../libexec/shared" ] && dotnet_dir=$(cd "$dotnet_dir/../libexec" && pwd)
export DOTNET_ROOT="$dotnet_dir"

mkdir -p "$ROOT/game" "$ROOT/tools/dl"
[ -d "$STEAM_GAME/thronefall.app" ] || { echo "Thronefall not found at $STEAM_GAME (set STEAM_GAME)" >&2; exit 1; }
# Clone next to the old copy first, so a failed copy never leaves game/ without the app.
rm -rf "$ROOT/game/thronefall.app.new"
cp -Rc "$STEAM_GAME/thronefall.app" "$ROOT/game/thronefall.app.new"
rm -rf "$ROOT/game/thronefall.app"
mv "$ROOT/game/thronefall.app.new" "$ROOT/game/thronefall.app"

zip="$ROOT/tools/dl/bepinex-$BEPINEX.zip"
[ -f "$zip" ] || curl -sfL -o "$zip" \
    "https://github.com/BepInEx/BepInEx/releases/download/v$BEPINEX/BepInEx_macos_universal_$BEPINEX.zip"
echo "$BEPINEX_SHA  $zip" | shasum -a 256 -c -
unzip -q -o "$zip" -d "$ROOT/game"
chmod +x "$ROOT/game/run_bepinex.sh"

[ -x "$ROOT/tools/ilspy/ilspycmd" ] || dotnet tool install ilspycmd --tool-path "$ROOT/tools/ilspy"
managed="$ROOT/game/thronefall.app/Contents/Resources/Data/Managed"
for asm in Assembly-CSharp Assembly-CSharp-firstpass; do
    rm -rf "$ROOT/decompiled/$asm"
    "$ROOT/tools/ilspy/ilspycmd" -p -o "$ROOT/decompiled/$asm" "$managed/$asm.dll"
done
echo "Done. Next: scripts/run.sh"
