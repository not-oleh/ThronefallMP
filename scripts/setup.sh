#!/bin/sh
# One-time setup: clone the Steam install into ./game (APFS clone, no extra disk),
# add BepInEx, and decompile the game code into ./decompiled for reading.
# Re-run after a game update. Needs: dotnet SDK (brew install dotnet).
set -e
ROOT=$(cd "$(dirname "$0")/.." && pwd)
STEAM_GAME=${STEAM_GAME:-"$HOME/Library/Application Support/Steam/steamapps/common/Thronefall"}
BEPINEX=5.4.23.6
BEPINEX_SHA=2e711e21f6570da964fce4654b1976500dbe043f61778bce0008500078564638
export DOTNET_ROOT=${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}

mkdir -p "$ROOT/game" "$ROOT/tools/dl"
rm -rf "$ROOT/game/thronefall.app"
cp -Rc "$STEAM_GAME/thronefall.app" "$ROOT/game/"

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
