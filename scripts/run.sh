#!/bin/sh
# Build the mod and launch the modded game copy.
#   run.sh            single instance, mod loaded
#   run.sh host       host on 127.0.0.1:27015
#   run.sh client     join 127.0.0.1:27015 (separate save profile)
#   run.sh both       host + client on this Mac
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

case $MODE in
    single) launch single "$@" ;;
    host)   launch host --mp-host "$@" ;;
    client) launch client --mp-join --mp-profile client "$@" ;;
    both)   launch host --mp-host "$@"; sleep 2; launch client --mp-join --mp-profile client "$@" ;;
    *) echo "usage: run.sh [single|host|client|both] [game args]" >&2; exit 1 ;;
esac
echo "Launched ($MODE). Logs: game/player-*.log, game/BepInEx/LogOutput.log"
