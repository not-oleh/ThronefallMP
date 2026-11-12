# Status

Last updated: 2026-11-13 · plugin version 0.7.0 · protocol 7 · MIT licensed

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
  reconnect, client requests, idempotent by level. Tested both directions with `--mp-test-night`;
  real UI (hold-to-fill, choice cards) user-verified in a 2-window run (2026-11-13).
- Host-authoritative enemies (`Sync/EnemySync.cs`, M3 slice 1): client's `Spawn.Update` skipped
  while the host is in its scene. Host ids every `TagManager.EnemyUnits` entry (per-match epoch),
  spawn/despawn reliable; pos/yaw/hp of enemies + hp of damaged buildings (index = sorted
  `SlotId`) in a second 20 Hz state packet (state bodies start with a kind byte). Client builds
  puppets from the same prefab (`Resources.FindObjectsOfTypeAll`), stripped to Hp/TaggedObject/
  SimpleWalk/Healthbar + UI; puppets take no local damage; host kill → game death (coins and
  on-death spawns removed). Test 2026-11-12 (`--mp-test-night`, 3 runs): wave 1 = 3 `E Melee`
  sent/got/built/killed, a building knockout followed, 0 exceptions in both logs. Puppet visuals
  (walk bounce, health bars, death animation) user-verified (2026-11-13).
- Client attacks (M3 slice 2): `DamagePatch` sends a reliable `MsgHit` (epoch, enemy id, amount)
  when a puppet is hit with the local player as source (`Weapon` passes the attacker through
  projectiles); client towers/units are dropped (the host simulates its own copies). Host checks
  epoch/id/alive, clamps the amount, applies `TakeDamage` from the avatar; hp and kill replicate
  back. Test 2026-11-13 (`--mp-test-night`, client `Light Spear`, `TestClientAttack` swings via
  `ManualAttack.TryToAttack` next to the nearest puppet): 9 hits applied, enemy 1 killed on the
  host, client played the kill, 0 exceptions in both logs.
- Enemy state split into MTU-safe chunks (`EncodeState`, body ≤ 1150 B, buildings in chunk 0
  only); `EnemySync.SelfCheck` encodes 300 enemies + 40 buildings and round-trips them.
- Knockout/revive: each player is knocked out and revived only in its own world (`AutoRevive`,
  dawn via synced day/night); `State.Dead` hides the avatar so enemies stop targeting it. The game
  has no player-revives-player mechanic. Local transitions logged ("Local player knocked out/revived").
- Overlay crisp on Retina (user-verified). `scripts/setup.sh` runs end to end (outside sandbox).

## Milestones

- [x] M0 Toolchain, repo, UDP ghost
- [x] M1 Second player in the world
- [x] M2 Shared match state (day/night, coins/cores, buildings, upgrade choices)
- [x] M3 Combat — enemy replication, building hp, client hits via host, MTU-safe enemy state
- [ ] M4 Steam lobbies/invites, reconnect, Craaghelm bosses, Eternal Trials
- [ ] QoL track (nothing requested yet)

## Not verified

- **Knockout/revive transitions were not hit in the 2026-11-13 run** (client hp stayed ≥ 45);
  logic unchanged from M1, only logging added. Multi-chunk state only exercised by `SelfCheck`.
- Sourceless player damage (explosions, burns, Blood Wand bonus, `isPlayerWeapon` effects without
  attacker) is not forwarded; Potion Vials etc. may do little to puppets. Only Light Spear tested.
- Window-focus block (needs two machines). Energy cores (no DLC on this account; no null refs).
- Boosts (`ApplyBoost`) and mid-match save loads (`ApplyLoadedUpgradesDelayed`) bypass build sync.
- Same slot upgraded by both players at once: host rejects the second; client may have paid twice.
- Enemies beyond Nordfels wave 1: flying/large/boss, scene-placed, elite material, facing (root
  yaw synced; a child may be what turns). One early run built only 1 of 3 puppets; not reproduced.
- Host soldiers/projectiles not shown on the client; coins from host kills exist only in the host
  world. Client hit feedback (hp bar) lags one round trip.
- Remote attack animation, non-weapon host perks, human-driven movement, second machine.

## Next tasks

Simple (Sonnet-friendly):

- [ ] `--mp-shot` at night to eyeball the overlay size on Retina.
- [ ] Human test: client fights with several weapons (bow, potion vials, blowpipe) in a 2-window run.
- [ ] Get the client knocked out in a run and check both logs for knockout → avatar hidden → revive.

Complex (architecture-heavy, use Opus/Fable):

- [ ] Forward sourceless player damage (explosions/burns) — needs attacker tagging per effect.
- [ ] Longer test: several waves, flying/boss enemies, mid-night client join (`ResendAll`).
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
