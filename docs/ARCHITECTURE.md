# Architecture and roadmap

## What the game is made of

Findings from game version 2.15 (Steam build 26487600), macOS.

| Area | Finding | Consequence |
|---|---|---|
| Engine | Unity 6000.3.18f1, **Mono** backend, universal binary, ad-hoc signed | BepInEx 5 + Harmony work natively on Apple Silicon; code decompiles to readable C# |
| Libraries | Steamworks.NET, Epic Online Services SDK, Rewired (input), A* Pathfinding Project with RVO, I2 Localization, MoreMountains Feedbacks | Steam P2P networking is available without shipping new native code |
| Simulation | Driven by `Update` + `Time.deltaTime` (196 files), `UnityEngine.Random` in 81 files, one `FixedUpdate` | **Not deterministic.** Lockstep is out; the host must be authoritative and replicate state |
| Player | `PlayerManager.RegisteredPlayers` and `TagManager.Players` are lists, and enemies use `GetClosestPlayer` | Targeting already tolerates several players |
| Player (cont.) | …but 35 uses of `PlayerMovement.instance`, 29 of `PlayerInteraction.instance`, 31 of `ReInput.players.GetPlayer(0)`, 6 of `Players[0]` | These ~100 call sites are the main work for a real second player |
| World registry | `TagManager` tracks every `TaggedObject` (units, buildings, coins) | Natural place to assign network entity IDs |
| Match flow | `LocalGamestate` (state machine), `DayNightCycle` (`DuskCall`, `DawnCall…`), `EnemySpawner` + wave generators, `ChoiceManager`, `BuildSlot`/`BuildingInteractor`, `Hp` | Each is a separate sync module |
| Scenes | `SceneTransitionManager` owns all scene changes (`_StartMenu`, `_LevelSelect`, levels, additive `_UI`) | One place to make the client follow the host |
| Saves | `SteamSaveServices.GetFullSavePath`, plus a mid-match save (`MatchSave`, `MatchSaveLoadHandler`) | Already patched for per-instance profiles; the match save is a candidate for join snapshots |
| Craaghelm DLC | Gated by `DlcConfiguration.IsDlcAvailable(CRAAGHELM)` via Steam DLC ownership; own level-select scene, maps `Craaghelm_map*`, a second currency (`EnergyCore`), new bosses (Crystal Dragon, Lava Golem, The Scrapper, Construct Colossus) | Economy sync must carry two currencies; boss phase controllers need their own sync. The mod never bypasses the ownership check |

## Design

**Host-authoritative co-op.** The host runs the real simulation. Clients send *intents*
(move, attack, build, pick an upgrade, start the night) and render the state the host sends back.
Clients keep running their own player movement for responsiveness; everything else that
matters is decided by the host.

```
Plugin (bootstrap, command line, overlay)
 ├─ Net        Link (framing, session ids, reliable stream) over ITransport (raw datagrams)
 │              · UdpTransport: local/LAN/port-forwarded (--mp-host / --mp-join)
 │              · SteamTransport: Steam P2P + friends-only lobby (--mp-steam-host / -join)
 ├─ Sync       one module per game system, each owning its messages
 │              players · scene flow · day/night · economy · buildings · enemies · choices
 ├─ Patches    thin Harmony patches, one file per game class, that call into Sync
 └─ QoL        independent tweaks behind config toggles; work in single player too
```

Rules that keep it maintainable:

- Patches contain no logic. They forward to a Sync module, so a game update breaks one small file.
- Every message carries a protocol version; host and client refuse to pair on mismatch.
- The mod is inert in single player unless a QoL toggle is on. No save-format changes.

The split is done: `Net/` (`Link` + `Reliable` over `ITransport`), `Sync/` (`PlayerSync`,
`MatchSync`, `BuildSync`, `EnemySync`), `Patches/`, and a thin `Plugin.cs` (flags, update loop,
scene following, overlay).

**Transport.** `ITransport` moves unreliable datagrams to one peer (`Send`, `TryReceive`,
`HasPeer`); `Link` adds everything else, so both transports behave identically.
`SteamTransport` uses the game's own Steamworks.NET (initialised and pumped by `SteamManager`;
the transport waits for it): the host creates a friends-only lobby with data
`thronefallmp=<protocol>`, the client joins by id (`--mp-steam-join <id>`), by a Steam invite
(`GameLobbyJoinRequested_t`), or by Steam's `+connect_lobby <id>` launch argument; its peer is the
lobby owner. The host accepts P2P sessions only from lobby members. Host receives on channel 0,
client on channel 1, so one Steam account can run both ends on one Mac.

**Player damage attribution (client).** Hits on puppets are forwarded to the host only if they
are the local player's. Direct hits name the player as attacker. Indirect ones (explosions,
puddles, traps, curse blasts, Blood Wand bonus) pass no attacker, so `PlayerDamageScope` is
opened around the player's own code: every `ManualAttack` subclass's `Attack`/`Update`/coroutines,
`Weapon.DealDamage` with the player as attacker, `Update` of projectiles `firedBy` the player, and
`Update`/`OnTriggerEnter` of pooled AoE objects (`HealAndDamageArea`, `PuddleDamageDealer`,
`AttackByTimeOrCollision`) that were enabled inside a scope. Sourceless damage outside a scope
(our towers/units) is dropped; the host simulates its own copies.

## Local testing on one Mac

`scripts/run.sh both` starts host and client from the same game copy. What makes that work:

- `SteamAppId=2239150` in the environment lets a copy outside the Steam library start.
- `Application.runInBackground` keeps the unfocused window simulating.
- `--mp-profile client` redirects the client's saves to `mp-client/`.
- Steam: launch the game directly with `--mp-steam-host`, read the lobby id from the log, start
  the second instance with `--mp-steam-join <id> --mp-profile client` (`run.sh host/client` add the
  UDP flags, which win).
- `--mp-autostart` and `--mp-shot` allow unattended smoke tests: launch, wait, check the logs
  for `Peer connected`, `Following host to …` and `Avatar targetable`.

## Roadmap

| Milestone | Scope | Done when |
|---|---|---|
| **M0 — Toolchain** ✅ | BepInEx on arm64, decompile, repo, launcher, UDP ghost | Two local instances show each other's ghost |
| **M1 — Second player in the world** 🟡 (0.3.0) | Done: client follows host scene changes and the host's perks while keeping its own weapon; remote player is an avatar cloned from the local player (own weapon model, walk/sprint/attack animation), registered with `TagManager` so enemies target it; the host forwards damage dealt to the avatar and the client's player takes it; the avatar hides while the peer is dead; only the focused window reads input. Open: remote attack animation, perk copy and human-driven movement not yet observed in a test; second machine untested | Both players walk into a level together and enemies target either |
| **M2 — Shared match state** ✅ | Day/night, coins and energy cores, build slots and upgrades, upgrade choices, win/lose (host's end state pushed, M3 close-out) | A full level can be played to the end with building and economy in sync |
| **M3 — Combat** ✅ | Enemy spawn/position/health replication, player attacks and damage, unit commands, revive | Nights look the same on both screens |
| **M4 — Release quality** 🟡 (slice 1: Steam P2P + lobby) | Steam lobbies and invites, reconnect, version handshake UI, Craaghelm bosses, Eternal Trials | A friend can join via Steam overlay |
| **QoL track** (parallel) | Small independent toggles, added on request | Each ships on its own |

Decisions taken in M1:

- **The remote player is not a `PlayerMovement`.** It is a stripped clone carrying only visuals,
  colliders, `TaggedObject` and `Hp`. That sidesteps the ~100 single-player call sites instead of
  rewriting them; they only matter again if the host must run a second *controllable* player.
- **No LiteNetLib.** `Net/Reliable.cs` (seq/ack/resend) covers one-off events; state is resent
  20 times a second, so loss is self-healing.

Biggest risks: clients currently run their own simulation, so M2/M3 must switch the client to
rendering host state; and bandwidth for enemy replication on late waves (M3).
