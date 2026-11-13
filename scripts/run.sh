#!/bin/sh
# Build the mod and launch the modded game copy.
#   run.sh            single instance, mod loaded
#   run.sh host       host on 127.0.0.1:27015
#   run.sh client     join 127.0.0.1:27015 (separate save profile)
#   run.sh both       host + client on this Mac
#   run.sh steam-host            host over Steam P2P (friends-only lobby)
#   run.sh steam-client [lobby]  join over Steam; lobby id defaults to the one in player-host.log
# Extra args are passed to the game, e.g. `run.sh both --mp-autostart`.
set -e
ROOT=$(cd "$(dirname "$0")/.." && pwd)
MODE=${1:-single}; [ $# -gt 0 ] && shift

dotnet build "$ROOT/src/ThronefallMP" -c Release -v q --nologo

launch() { # name, args...
    name=$1; shift
    # SteamAppId lets the copy outside the Steam library initialise Steamworks.
    (cd "$ROOT/game" && SteamAppId=2239150 ./run_bepinex.sh thronefall.app \
        -logFile "$ROOT/game/player-$name.log" "$@" >/dev/null 2>&1 &)
}

# Window args for a corner (tr = top right, bl = bottom left) of the usable screen (no menu bar,
# no Dock): the largest 16:9 size at which two such windows, placed diagonally, never overlap.
# Height is 48% of the usable height, less if two title bars (28 pt each) would not fit; width
# is capped at 62% of the usable width. Unity wants pixels, AppKit gives points. Prints nothing
# without a screen, so the game then keeps its default size.
geom() {
    osascript -l JavaScript -e 'ObjC.import("AppKit"); function run(a) {
        var s = $.NSScreen.mainScreen, v = s.visibleFrame, f = s.frame, k = s.backingScaleFactor, bar = 28;
        var uw = v.size.width, uh = v.size.height;
        var h = Math.min(Math.floor(uh * 0.48), Math.floor(uh / 2) - bar), w = Math.floor(h * 16 / 9);
        if (w > Math.floor(uw * 0.62)) { w = Math.floor(uw * 0.62); h = Math.floor(w * 9 / 16); }
        var top = f.size.height - v.origin.y - uh;
        var x = a[0] == "tr" ? v.origin.x + uw - w : v.origin.x, y = a[0] == "tr" ? top : top + uh - h - bar;
        return ["-screen-width", w * k, "-screen-height", h * k, "-screen-fullscreen", 0, "--mp-window-pos", x * k + "," + y * k].join(" "); }' "$1" 2>/dev/null || true
}

case $MODE in
    single) launch single "$@" ;;
    host)   launch host --mp-host $(geom tr) "$@" ;;
    client) launch client --mp-join --mp-profile client $(geom bl) "$@" ;;
    both)   launch host --mp-host $(geom tr) "$@"; sleep 2; launch client --mp-join --mp-profile client $(geom bl) "$@" ;;
    steam-host) launch host --mp-steam-host $(geom tr) "$@" ;;
    steam-client)
        case $1 in [0-9]*) LOBBY=$1; shift ;; *)
            LOBBY=$(grep -oE "lobby [0-9]+ open" "$ROOT/game/player-host.log" 2>/dev/null | tail -1 | grep -oE "[0-9]+") ;; esac
        [ -n "$LOBBY" ] || { echo "no lobby id (start steam-host first or pass one)" >&2; exit 1; }
        launch client --mp-steam-join "$LOBBY" --mp-profile client $(geom bl) "$@" ;;
    *) echo "usage: run.sh [single|host|client|both|steam-host|steam-client [lobby]] [game args]" >&2; exit 1 ;;
esac
echo "Launched ($MODE). Logs: game/player-*.log, game/BepInEx/LogOutput.log"
