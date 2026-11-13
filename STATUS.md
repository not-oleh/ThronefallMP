# Status

Last updated: 2026-11-13 · plugin version 0.9.0 · protocol 10 · MIT licensed

## Working now

- M0–M3 + M4 slices 1–2 (details in git history and `docs/ARCHITECTURE.md`): client follows the
  host's scene; avatar with own weapon; host-forwarded damage; reliable stream + `SelfCheck`s;
  synced day/night, treasury, buildings + upgrade choices; host-authoritative enemies as puppets;
  client hits direct + indirect; per-world knockout/revive; match end follows host; UDP and Steam
  P2P transports; Co-op panel (F8: host lobby, invite, copy/join id, rejoin, disconnect, ping);
  rejoin snapshot + client resign (`MsgLeave`); knock-out bosses tracked at hp 0; Eternal Trials
  not followed (logged); startup/dawn/build race fixes; snapshot interpolation (`Sync/Interp.cs`,
  90 ms, vsync off + 60 FPS while connected); cloned `CameraRig` removed from the avatar.
  Test flags: `--mp-test-night` (3 nights, then host resigns), `--mp-test-coins`, `--mp-test-rejoin`,
  `--mp-panel`, `run.sh steam-host` / `steam-client [lobby]`.
- Dual-window layout: `run.sh host|client|both` asks AppKit (JXA) for the usable screen and passes
  `-screen-width/-height` (pixels) + `--mp-window-pos`; `Plugin.Windowed` enforces them 4 s in
  (`Screen.SetResolution` + `Screen.MoveMainWindowTo`, frame top-left in pixels). Host top right,
  client bottom left, largest 16:9 with no overlap (842x474 pt on a 1728x1117 screen; verified
  with `CGWindowList`: frames touch at y=536, 0 overlap).
- `--mp-qa` (`Qa.cs`): command file `/tmp/tf_qa_<role>.cmd`: `move dx dz s`, `build slot [choice]`,
  `night`, `panel on|off`, `hurt fraction`, `shot file.png` (+ a "QA status" log line: coins,
  income, score, positions, marker positions, built slots). Visual QA pass on Nordfels, both
  windows screenshotted per step, 0 exceptions:
  - spawn/HUD identical (8 coins, wave badge); red wave badge stays put on each side while the
    *other* King moves (marker positions logged: unchanged), so the CameraRig fix holds;
  - fixed: white "commanding" ring was drawn under the remote avatar (`CommandUnits` indicators
    are now switched off on the clone);
  - fixed: F8 panel had no background (unreadable over the game's tooltip): opaque,
    content-sized box;
  - buildings: host castle + house, client tower → same slots/levels and same coins on both;
  - night: same enemies/positions, avatar with spear shown, kills followed;
  - fixed: client's suppressed dawn pickups were still forwarded as a delta → 7 coins instead
    of 6; now 6 on both;
  - fixed: night score differed (client time bonus always max): `MsgTime` now carries the host's
    night length + spawn period (protocol 10); 499 = 499;
  - knockout (`hurt 1`): avatar hidden on the host, revived and shown again at dawn.
- Overlay crisp on Retina (user-verified). `scripts/setup.sh` runs end to end (outside sandbox).

## Milestones

- [x] M0 Toolchain, repo, UDP ghost · [x] M1 Second player · [x] M2 Shared match state · [x] M3 Combat
- [ ] M4 slices 1–2 done (Steam P2P, lobby panel, live invites, rejoin snapshot, resign);
      open: invites between two accounts, Craaghelm bosses in a real run, Eternal Trials co-op
- [ ] QoL track (nothing requested yet)

## Not verified

- QA pass gaps: upgrade *choice* via `build castle 1` (6 coins, costs 7); enemy hp bar on the
  client not seen in the one frame where the host showed it; Steam-only panel buttons (copy/
  invite) not shown under UDP; realm-protection score if a building falls.
- Interpolation looks smooth (needs a human; screenshots are single frames). Snapshots are stamped
  with receive time, so Steam jitter > ~40 ms would show; fix = sender timestamps in State packets.
- **Steam between two accounts / two machines** (only same-account loopback tested). Steam
  overlay invites and `+connect_lobby` launches untested; multi-chunk state only in `SelfCheck`.
- Indirect damage: Falchion trap (`AttackByTimeOrCollision`) and pooled puddles not observed in a
  run; `PlayerBallMovement`/`RidingDamager` damage not scoped. Bow, Wand, Axe, Sword untested.
- Match end: only host defeat tested, not victory; restarting the same level is not followed.
- Fresh client process mid-match restart; simultaneous build race refund (wired into
  `--mp-test-night`, not run since). Boss phase visuals and energy-core drops: no DLC here.
- Joining while already inside a solo level of the same scene does not reload it. Window-focus block
  needs two machines. Boosts (`ApplyBoost`) and mid-match save loads bypass build sync.
- Enemies beyond Nordfels wave 1: flying/large/boss, scene-placed, elite material, facing.
- Host soldiers/projectiles not shown on the client; client hit hp bar lags one round trip.
- Remote attack animation, non-weapon host perks, human-driven movement, second machine.

## Next tasks

- [ ] `--mp-qa` pass with more coins: castle lv2 choice, later waves, enemy hp bars on the client.
- [ ] Kill only the client mid-night, relaunch a fresh client process; check snapshot catch-up.
- [ ] `--mp-test-night` with Falchion and Trap; check "(indirect)" hits from the trap.
- [ ] Two friends on two Macs: Co-op panel → Host → Invite friend; Eternal Trials co-op; later waves.

## Known quirks and dead ends

- **Harmony postfixes run even when a prefix skipped the original**; take `bool __runOriginal`.
- **A `Texture2D` referenced only by a `GUIStyle` is unloaded on scene change** (HideAndDontSave).
- **Stale logs fool wait loops**: delete `game/player-*.log` before a scripted launch.
- **The player is invulnerable by day and a client ignores local damage**; QA `hurt` works at
  night through `applyingHostDamage`.
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
