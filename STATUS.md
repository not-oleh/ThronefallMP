# Status

Last updated: 2026-11-13 · plugin version 0.8.0 · protocol 8 · MIT licensed

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
- Shared buildings + upgrade choices (`Sync/BuildSync.cs`, hooked at
  `BuildSlot.OnUpgradeChoiceComplete`, slot id = `SaveLoadEntity.GUID`, idempotent by level);
  real UI user-verified in a 2-window run.
- Host-authoritative enemies (`Sync/EnemySync.cs`): client waves off; host ids enemies (per-match
  epoch), spawn/despawn reliable, pos/yaw/hp + building hp in MTU-safe 20 Hz chunks (`SelfCheck`
  round-trips 300 enemies + 40 buildings). Client puppets are stripped prefab clones; host kill →
  game death. Puppet visuals user-verified.
- Client attacks: puppet hits by the local player → reliable `MsgHit` → host checks epoch/id/alive,
  clamps, applies from the avatar; hp/kill replicate back.
- Knockout/revive: each player is knocked out and revived only in its own world (`AutoRevive`,
  dawn via synced day/night); `State.Dead` hides the avatar so enemies stop targeting it. The game
  has no player-revives-player mechanic. Verified 2026-11-13: client knocked out → host "Avatar
  hidden (peer is dead)" → client revived → avatar shown, twice per 3-night run.
- Indirect player damage (`Patches/PlayerDamagePatches.cs`, `PlayerDamageScope`): sourceless hits
  on puppets inside the local player's attack code / projectiles / AoE objects are forwarded
  (logged "(indirect)"); towers/units still dropped. Verified: Cursed Blowpipe curse blasts and
  Blood Wand bonus hits forwarded and applied (526/560 applied, rest hit dead enemies). Potion
  Vials damage turned out to be direct (projectile + splash with attacker). Blood Wand's
  self-damage now applies on the client (was blocked).
- Match end (`MatchSync` `MsgEnd`): host's Victory/Defeat pushed once per match; client calls
  `SetState` with the same state. Verified with a forced host defeat ("end follows host").
- `--mp-test-night`: host runs 3 nights then resigns; client drops to 20% hp at its first night.
- Transport split (M4 slice 1): `Net/ITransport` + `UdpTransport` (unchanged behaviour) +
  `SteamTransport` (SteamNetworking P2P, friends-only lobby, join by id / invite /
  `+connect_lobby`). Full 3-night test passed over Steam P2P on one Mac with one account.
- Overlay crisp on Retina (user-verified). `scripts/setup.sh` runs end to end (outside sandbox).

## Milestones

- [x] M0 Toolchain, repo, UDP ghost
- [x] M1 Second player in the world
- [x] M2 Shared match state (day/night, coins/cores, buildings, upgrade choices)
- [x] M3 Combat — enemy replication, building hp, client hits (direct + indirect), match end
- [ ] M4 slice 1 done (Steam P2P + lobby); open: invites in practice, reconnect, lobby UI,
      Craaghelm bosses, Eternal Trials
- [ ] QoL track (nothing requested yet)

## Not verified

- **Steam between two accounts / two machines** (only same-account loopback tested). Steam
  overlay invites and `+connect_lobby` launches untested; host never notices a lobby member leaving
  (relies on peer timeout). Multi-chunk state only exercised by `SelfCheck`.
- Indirect damage: Falchion trap (`AttackByTimeOrCollision`) and pooled puddles not observed in a
  run; `PlayerBallMovement`/`RidingDamager` damage not scoped. Bow, Wand, Axe, Sword untested.
- Match end: only host defeat tested, not victory; client resign ends only the client (it then
  follows the host back in). Restarting the same level is not followed by the client.
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
- [ ] `--mp-test-night` with Falchion and Trap; check "(indirect)" hits from the trap.
- [ ] Human test: two friends on two Macs, `--mp-steam-host` + Steam invite.

Complex (architecture-heavy, use Opus/Fable):

- [ ] Lobby UI: host/join from the title screen instead of flags; join an invite while running
      without `--mp-steam-join` (needs a transport that can start after launch).
- [ ] Later waves: flying/boss enemies, mid-night client join (`ResendAll`).
- [ ] Reconnect: lobby member left → drop peer; client rejoins the same lobby.

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
- **Same Steam account on one Mac shares one SteamID**: both instances read the same P2P
  channel. `SteamTransport` uses channel 0 for host-bound, 1 for client-bound packets.
- **`run.sh host/client` always add `--mp-host/--mp-join`** (UDP wins); launch Steam tests via
  `run_bepinex.sh` directly. Wait for "lobby <id> open" in the host log before starting the client.
- **Potion Vials is not an AoE-without-attacker weapon**; use Blowpipe/Blood Wand to test indirect hits.
- **`echo =====` fails in zsh** (`=word` expansion); use `echo -----` in test one-liners.
