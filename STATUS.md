# Status

Last updated: 2026-11-13 · plugin version 0.9.0 · protocol 9 · MIT licensed

## Working now

- M0–M3 (details in git history and `docs/ARCHITECTURE.md`): client follows the host's scene;
  avatar with own weapon (hidden while the peer is dead); host-forwarded damage; reliable stream
  + `SelfCheck`s; synced day/night, treasury (`--mp-test-coins` 8+10+5 → 23), buildings + upgrade
  choices (UI user-verified); host-authoritative enemies as stripped puppets in MTU-safe chunks
  (visuals user-verified); client hits direct + indirect (`PlayerDamageScope`; Blowpipe/Blood
  Wand verified); per-world knockout/revive; match end follows host (forced defeat verified).
  `--mp-test-night`: host runs 3 nights then resigns; client drops to 20% hp at its first night.
- M4 slice 1: `ITransport` + `UdpTransport` + `SteamTransport` (P2P, friends-only lobby);
  3-night test passed over Steam on one Mac with one account.
- M4 slice 2 (v0.9.0): Co-op panel (F8 or click the overlay): host Steam lobby, invite friend,
  copy lobby id / join id from clipboard, rejoin, disconnect; shows peer + ping (Link Ping/Pong,
  1 s). Connections can start/stop at runtime (`Plugin.StartLink`/`Disconnect`); a Steam invite
  (`GameLobbyJoinRequested_t`) is accepted at any time. Host drops a peer that left the lobby.
  `run.sh steam-host` / `steam-client [lobby]` (lobby id read from `player-host.log`). Verified:
  Steam loopback run, host rtt 8 ms, 0 exceptions.
- Rejoin snapshot: client entering the host's level sends `MsgSnapshot`; host resends time + wave
  number, treasury, build log, live enemies. Builds deduped on the client; puppets rebuilt after a
  scene reload. Client resign → `MsgLeave` (host plays on, avatar hidden), client does not
  follow back until Rejoin/host moves on. Verified (`--mp-test-rejoin`): resign → level select →
  rejoin → castle lv1+2 re-applied, puppets rebuilt, wave 0→1 followed, 0 exceptions.
- Bosses: knock-out bosses (`getsKnockedOutInsteadOfDying`: Crystal Dragon, Lava Golem) stay
  tracked at hp 0 instead of despawning; puppet max hp grows with The Scrapper. Eternal Trials:
  client does not follow a host in an ET run (state flag bit 8), logs why.
- Race fixes (`SelfCheck`-covered): autostart/`FollowHost` wait for an idle `SceneTransitionManager`
  + ready `_UI`/`UIFrameManager`/`PerkManager`, then `CloseAllFrames()`; client dawn coin/core
  pickups and dusk/dawn perks are host-authoritative (`CurrencyPickupScopePatch`; `CancelFill`
  refunds stay local); simultaneous `BuildSlot` upgrades: client sends `MsgRequest` before mutating,
  host refunds a duplicate and re-sends `MsgBuilt`. `--mp-panel` opens F8 panel for `--mp-shot`.
- Smooth remote motion (`Sync/Interp.cs`, `SelfCheck`-covered): avatar and enemy puppets render
  90 ms in the past between the two bracketing 20 Hz snapshots (Lerp/Slerp, `Plugin.LateUpdate`),
  snap on jumps > 8 units; avatar walk/sprint bools come from its shown speed (>1.5 / >18.5 u/s).
  While connected: vsync off, `targetFrameRate = 60` (re-applied if the game's settings reset it).
  Log-verified: `--mp-test-night` puppets hit/killed, 0 exceptions; smoothness itself not eyeballed.
- Wave-spawn screen markers no longer follow the remote avatar: `BuildGhost` deletes the cloned
  `CameraRig` (its `Camera` survived stripping and became `Camera.main`). Log-verified `camera rigs
  removed=1` both sides, avatar targetable, 0 exceptions; marker steadiness not eyeballed.
- Overlay crisp on Retina (user-verified). `scripts/setup.sh` runs end to end (outside sandbox).

## Milestones

- [x] M0 Toolchain, repo, UDP ghost · [x] M1 Second player · [x] M2 Shared match state · [x] M3 Combat
- [ ] M4 slices 1–2 done (Steam P2P, lobby panel, live invites, rejoin snapshot, resign);
      open: invites between two accounts, Craaghelm bosses in a real run, Eternal Trials co-op
- [ ] QoL track (nothing requested yet)

## Not verified

- Interpolation looks smooth (needs a human in 2-window play). Snapshots are stamped with
  receive time, so Steam jitter > ~40 ms would show; fix = sender timestamps in State packets.

- **Steam between two accounts / two machines** (only same-account loopback tested). Steam
  overlay invites and `+connect_lobby` launches untested; multi-chunk state only in `SelfCheck`.
- Indirect damage: Falchion trap (`AttackByTimeOrCollision`) and pooled puddles not observed in a
  run; `PlayerBallMovement`/`RidingDamager` damage not scoped. Bow, Wand, Axe, Sword untested.
- Match end: only host defeat tested, not victory; restarting the same level is not followed.
- Co-op panel `--mp-shot` (`--mp-panel`), fresh client process mid-match restart, dawn pickup
  suppression, and simultaneous build race refund are compiled and wired into `--mp-test-night`,
  but could not be executed in-game in this session because the agent tool environment enforces the
  macOS sandbox (`NullGfxDevice` URP crash on launch). Boss phase visuals and energy-core drops: no
  DLC on this account, reasoning only.
- Joining while already inside a solo level of the same scene does not reload it. Window-focus block
  needs two machines. Boosts (`ApplyBoost`) and mid-match save loads bypass build sync.
- Enemies beyond Nordfels wave 1: flying/large/boss, scene-placed, elite material, facing.
- Host soldiers/projectiles not shown on the client; client hit hp bar lags one round trip.
- Remote attack animation, non-weapon host perks, human-driven movement, second machine.

## Next tasks

- [ ] Outside sandbox: run `scripts/run.sh host --mp-autostart Nordfels --mp-test-night --mp-panel --mp-shot /tmp/mp-panel.png` + `scripts/run.sh client --mp-equip "Light Spear" --mp-test-night` to capture F8 panel screenshot and log-verify dawn harvest + simultaneous build refund.
- [ ] Outside sandbox: kill only the client mid-night and relaunch a fresh client process; check snapshot catch-up in logs.
- [ ] `--mp-test-night` with Falchion and Trap; check "(indirect)" hits from the trap.
- [ ] Two friends on two Macs: Co-op panel → Host → Invite friend; Eternal Trials co-op; later waves.

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
- **The player prefab contains the `CameraRig`** (it unparents itself in `Start`). Anything cloned
  from the player must drop it, or `Camera.main` may resolve to the clone.
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
- **Steam tests: `run.sh steam-host`, wait (bounded) for "lobby <id> open", then `steam-client`.**
- **Potion Vials is not an AoE-without-attacker weapon**; use Blowpipe/Blood Wand to test indirect hits.
- **Test waits must be bounded** (`for i in $(seq 1 60); do grep -q … && break; sleep 3; done`).
  Once (not reproduced) the host never left the title screen; an unbounded `until grep` hung.
- **`echo =====` fails in zsh** (`=word` expansion); use `echo -----` in test one-liners.
