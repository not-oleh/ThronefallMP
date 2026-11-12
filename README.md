# Thronefall MP

An open-source, macOS-first multiplayer (and quality-of-life) mod for
[Thronefall](https://store.steampowered.com/app/2239150/Thronefall/), built on BepInEx 5 + Harmony.

**Status: prototype (0.2.0).** Two game instances connect over UDP. The client follows the
host into levels with the host's loadout, and each side shows the other player as an avatar
that enemies can target. Each instance still simulates its own world (enemies, buildings and
coins are not shared yet) — see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the roadmap.

You need to own the game on Steam. This repository contains no game files.

## Setup (once, and after each game update)

```bash
brew install dotnet
```

```bash
scripts/setup.sh
```

This clones your Steam install into `game/` (an APFS clone: instant, no extra disk space, and
your Steam install is never modified), installs BepInEx 5.4.23.6 into the clone, and decompiles
the game code into `decompiled/` for reference. All three folders are gitignored.

## Run

Steam must be running.

```bash
scripts/run.sh both --mp-autostart
```

| Command | What it does |
|---|---|
| `scripts/run.sh` | One instance with the mod loaded |
| `scripts/run.sh host` | Host on UDP port 27015 |
| `scripts/run.sh client` | Join `127.0.0.1:27015`, using a separate save profile |
| `scripts/run.sh both` | Host + client on the same Mac, windowed |

Flags (after the mode): `--mp-autostart` skips the title screen (`--mp-autostart Nordfels` goes
straight into that level), `--mp-join ip:port` joins another machine, `--mp-profile name` keeps saves in `mp-<name>/`, `--mp-shot file.png` saves a
screenshot when the other player appears.

Walk around in one window and the other player's avatar moves in the other. Enter a level
on the host and the client follows. The top-left label shows the
mod version, role, and whether the peer is connected.

Logs: `game/BepInEx/LogOutput.log`, `game/player-host.log`, `game/player-client.log`.

## Layout

```
src/ThronefallMP/   the plugin (builds into game/BepInEx/plugins/ThronefallMP)
scripts/            setup.sh, run.sh
docs/               architecture and roadmap
game/               modded game copy            (gitignored)
decompiled/         ILSpy output, for reading   (gitignored)
tools/              ilspycmd, downloads         (gitignored)
```
