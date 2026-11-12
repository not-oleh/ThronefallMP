# Status

Last updated: 2026-11-12 (night) · plugin version 0.6.0 · protocol 6 · MIT licensed

## Working now

- Toolchain (BepInEx native on Apple Silicon); two local instances over UDP; client follows the
  host into any scene from the title screen. Client saves isolated in `mp-client/`.
- Remote avatar: position, facing, walk/sprint animation, own weapon model. Client keeps its own
  weapon, takes the host's other perks. Avatar hides while the peer is dead.
- Damage: host's enemies hit the avatar → forwarded → client loses health (logged per burst).
- Unfocused window gets no input (`Patches/InputPatches.cs`); untestable on one Mac.
- Reliable messages (`Net/Reliable.cs`, seq/ack/resend, session id detects restarts) + `SelfCheck`.
- Shared day/night: client's `DayNightCycle.Update` frozen while the host is in its scene, switched
  to the host's state.
- Shared treasury: host balance pushed on change; client forwards coin/core deltas
  (`--mp-test-coins`: 8 + 10 + 5 → both read 23).
- Shared buildings + upgrade choices (`Sync/BuildSync.cs`): sync point
  `BuildSlot.OnUpgradeChoiceComplete`; slot id = `SaveLoadEntity.GUID`; host log resent on
  reconnect, client requests, idempotent by level. Tested both directions with `--mp-test-night`.
- Host-authoritative enemies (`Sync/EnemySync.cs`, M3 slice 1): client's `Spawn.Update` skipped
  while the host is in its scene. Host ids every `TagManager.EnemyUnits` entry (per-match epoch),
  spawn/despawn reliable; pos/yaw/hp of enemies + hp of damaged buildings (index = sorted
  `SlotId`) in a second 20 Hz state packet (state bodies start with a kind byte). Client builds
  puppets from the same prefab (`Resources.FindObjectsOfTypeAll`), stripped to Hp/TaggedObject/
  SimpleWalk/Healthbar + UI; puppets take no local damage; host kill → game death (coins and
  on-death spawns removed). Test 2026-11-12 (`--mp-test-night`, 3 runs): wave 1 = 3 `E Melee`
  sent/got/built/killed, a building knockout followed, 0 exceptions in both logs.
- Overlay crisp on Retina (user-verified). `scripts/setup.sh` runs end to end (outside sandbox).

## Milestones

- [x] M0 Toolchain, repo, UDP ghost
- [x] M1 Second player in the world
- [x] M2 Shared match state (day/night, coins/cores, buildings, upgrade choices)
- [~] M3 Combat — slice 1 done: enemy replication + building hp. Left: client attacks, revive
- [ ] M4 Steam lobbies/invites, reconnect, Craaghelm bosses, Eternal Trials
- [ ] QoL track (nothing requested yet)

## Not verified

- **Real-UI build sync (hold-to-fill, choice cards): the user has not tested it yet** (2026-11-12).
- Window-focus block (needs two machines). Energy cores (no DLC on this account; no null refs).
- Boosts (`ApplyBoost`) and mid-match save loads (`ApplyLoadedUpgradesDelayed`) bypass build sync.
- Same slot upgraded by both players at once: host rejects the second; client may have paid twice.
- Enemies beyond Nordfels wave 1: flying/large/boss, scene-placed, elite material, facing (root
  yaw synced; a child may be what turns), big waves (~20 B/enemy: >60 enemies exceeds a 1.2 KB
  MTU; fine on loopback). One early run built only 1 of 3 puppets (before spawn logging); not
  reproduced in 2 later runs.
- Client hits/towers on puppets do nothing (blocked); host soldiers/projectiles not shown on the
  client; coins from host kills exist only in the host world. Nothing seen by a human yet.
- Remote attack animation, non-weapon host perks, human-driven movement, second machine.

## Next tasks

Simple (Sonnet-friendly):

- [ ] Human test of build sync through the real UI (fill a slot on the client, pick a castle branch).
- [ ] Human look at client puppets at night (walk bounce, health bars, facing, death VFX).
- [ ] `--mp-shot` at night to eyeball the overlay size on Retina.

Complex (architecture-heavy, use Opus/Fable):

- [ ] M3 slice 2: client hits on puppets → intent (enemy id, amount) → host applies damage
      (`DamagePatch` already intercepts them via `Enemies.IsPuppet`).
- [ ] Longer test: several waves, flying/boss enemies, mid-night client join (`ResendAll`).
- [ ] Quantize/split the enemy state packet before the Steam transport.
- [ ] Steam P2P transport behind `Link` (same `SendState/SendMessage/Pump/Peer` surface).

## Known quirks and dead ends

- **Dual-window testing on one Mac sends keyboard input to both windows** (`isFocused` and
  `NSRunningApplication.isActive` stay true, Rewired reads raw HID). Input tests need two machines.
- **Reliable stream across restarts:** reset only on a *changed* session id, not on first contact.
- **`TagManager` never removes from its player list**; `KeepPlayersList` edits `bufferedPlayers`
  via reflection. `Players[0]` must stay the local player (6 call sites).
- **The game cannot run inside the agent sandbox** (NullGfxDevice crash, `mono_crash.*.blob`).
  Launch with sandbox bypass; `dotnet build` in the sandbox needs `DOTNET_CLI_HOME=$HOME`.
- **`DOTNET_ROOT=~/.dotnet` (.NET 8) is set in the user's shell**; ilspycmd needs 10 (`setup.sh`
  derives it from PATH).
- **Client player regenerates health**, so `--mp-test-night` kills it only sometimes.
- **Forced night without a castle has no fight**; build it first
  (`CastleCenter.instance.GetComponentInParent<BuildSlot>().ExecuteBuildOrUpgrade(pi, false)`).
- **Wave-1 melee enemies ignore players and walk to the castle**; `--mp-test-night` parks the
  avatar on the nearest enemy (`testChase`) to exercise the damage path.
- **A host with a weapon kills wave 1 before it arrives**; test with an unarmed host. An unarmed
  idle host loses Nordfels in ~40 s; the end screen sets `timeScale = 0` (stops `Invoke` timers).
- **`CastleCenter.CastleCenterPosition` is (0,0,0) until built**; use `FindFirstObjectByType`.
- **Unequipped weapon models are destroyed in `WeaponEquipper.Start`**; clone the player in
  `PlayerMovement.Awake`, under an inactive parent, stripped first (`Awake` sets singletons).
- **Strip order matters:** game-assembly scripts first (`[RequireComponent]` on RVO).
- **Stripping every non-game MonoBehaviour from an enemy clone breaks its health bar** (UI `Image`
  gone → NRE in `Healthbar.Start`). Strip game scripts + `Pathfinding.*` only.
- **`Hp.invulnerable` is reset every dusk** for everything `PlayerOwned`.
- **Saved video settings are re-applied at startup**: `Screen.SetResolution` is delayed 4 s.
- **"Peer connected" is often logged twice**: scene loads stall past the 3 s timeout. Harmless.
- **`.Reverse()` on an array resolves to the span overload**; use `Enumerable.Reverse(...)`.
- **Fresh save has nothing equipped**; use `--mp-equip`. `--mp-autostart` skips the tutorial.
- **`echo =====` fails in zsh** (`=word` expansion); use `echo -----` in test one-liners.
