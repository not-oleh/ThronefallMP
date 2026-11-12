# Status

Last updated: 2026-11-12 (late) · plugin version 0.5.0 · protocol 5 · MIT licensed

## Working now

- Toolchain (BepInEx native on Apple Silicon); two local instances over UDP; client follows the
  host into any scene from the title screen.
- Remote player avatar: position, facing, walk/sprint animation, own weapon model.
- Client keeps its own weapon and takes the host's other perks.
- Damage: host's enemies hit the avatar → forwarded → client loses health (logged once per burst).
- Avatar hides when the peer dies: `Avatar hidden (peer is dead)` then `Avatar shown` on revive
  (host log, 2026-11-12 test-night run 1; the client regenerates, so death is not reliable).
- Unfocused window gets no input (`Patches/InputPatches.cs`, MP only): `Rewired.Player` reads return
  nothing unless AppKit says this process is active (falls back to `Application.isFocused`).
  Untestable on one Mac (see dead ends).
- Reliable messages (`Net/Reliable.cs`, seq/ack/resend, in order, session id detects restarts);
  startup `SelfCheck` covers packets and a lossy reliable run.
- Shared day/night: host's cycle rules; client's `DayNightCycle.Update` is frozen while the host is
  in its scene and it is switched to the host's state (`Match: time follows host -> Night`).
- Shared treasury: host balance pushed on change; client forwards its own coin/core deltas.
  Test (`--mp-test-coins`): 8 start, host +10, client +5 → both read 23, no double counting.
- Shared buildings + upgrade choices (`Sync/BuildSync.cs`): sync point is
  `BuildSlot.OnUpgradeChoiceComplete` (slot, new level, branch). Slot id = `SaveLoadEntity.GUID`
  (scene-serialized; fallback hierarchy path + position); 60 slots indexed identically on Nordfels.
  Host logs + broadcasts every upgrade (resent on reconnect, epoch per match); client applies its
  own at once and sends a request, host applies and echoes; idempotent by level, no coins charged
  (the interactor already spent them via the treasury). Test 2026-11-12 (`--mp-test-night`):
  host castle L1/L2 → client `Build: follows host`; client Archery Range → host `Build: peer built`.
- Night → day with the client following: `Match: time follows host -> Day` (host Long Bow cleared wave 1).
- Overlay: crisp on Retina (user-verified), font scales with window height.
- `scripts/setup.sh` runs end to end (needs to run outside the agent sandbox: Steam dir + GPU).
- Client saves are isolated in `mp-client/`.

## Milestones

- [x] M0 Toolchain, repo, UDP ghost
- [x] M1 Second player in the world (human checks below are leftovers, not blockers)
- [~] M2 Shared match state — done: code split, reliable layer, day/night, coins/cores, buildings,
      upgrade choices. Left: wave/enemy state (moves into M3)
- [ ] M3 Combat (enemy replication, client attacks hurting host enemies, revive)
- [ ] M4 Steam lobbies/invites, reconnect, Craaghelm bosses, Eternal Trials
- [ ] QoL track (nothing requested yet)

## Not verified

- Window-focus block: cannot be tested on one Mac (see dead ends); needs two machines.
- Energy cores sync: no DLC on this account (`craaghelm=False`, cores stayed 0, no null refs);
  the DLC path is still untested.
- Build sync via the real UI (hold-to-fill, choice frame), boosts (`ApplyBoost`, not synced),
  perk-driven upgrades on load, mid-match save loads (`ApplyLoadedUpgradesDelayed` bypasses the
  hook, so a client would not get them).
- Both players upgrading the same slot at once: host rejects the second (logged), the client keeps
  its local result and may have paid twice. Treasury can go negative in that race.
- Client coin pickups from its own world are forwarded as income (its world is still its own
  simulation until M3), so a client in a coin-rich world inflates the shared treasury.
- Remote attack animation, host perks (non-weapon), human-driven movement, second machine, night screenshot.

## Next tasks

Simple (Sonnet-friendly):

- [ ] Human test of build sync through the real UI (fill a slot on the client, pick a castle branch).
- [ ] `--mp-shot` at night to eyeball the overlay size on Retina.

Complex (architecture-heavy, use Opus/Fable):

- [ ] Entity IDs for enemies (buildings now use `BuildSync.SlotId`).
- [ ] Wave/enemy state (client must stop its own spawner), building damage/destruction sync.
- [ ] Client attacks affecting the host's enemies (intent message → host applies damage).
- [ ] Steam P2P transport behind `Link` (same `SendState/SendMessage/Pump/Peer` surface).

## Known quirks and dead ends

- **Dual-window testing on one Mac sends keyboard input to both windows.** Both
  `Application.isFocused` and `NSRunningApplication.isActive` stay active across duplicate `.app`
  launches, and Rewired reads raw HID input. Focus-gating (`InputPatches.cs`) is left as-is; the
  two-window human test is dropped. Real input tests need two machines.

- **Reliable stream across restarts:** a restarted peer starts at seq 0; without a session id the other
  side would drop everything as duplicates. Reset only on a *changed* session, not on first contact
  (first contact on the client would otherwise wipe its already-acked stream).

- **`TagManager` never removes from its player list** (`RemoveTaggedObject` skips
  `bufferedPlayers`; every `OnEnable` appends). Hiding/showing the avatar left a stale entry and
  a duplicate (`players=3`). The old "toggle `tag.enabled` to move it off `[0]`" trick made it
  worse. `KeepPlayersList` now edits `bufferedPlayers` directly via reflection.
- **The game cannot run inside the agent sandbox**: NullGfxDevice, crash on start (leaves
  `mono_crash.*.blob` in `game/`). Launch with sandbox bypass. `dotnet build` in the sandbox
  needs `DOTNET_CLI_HOME=$HOME`.
- **`DOTNET_ROOT=~/.dotnet` (.NET 8 only) is set in the user's shell**; ilspycmd needs 10.
  `setup.sh` now derives `DOTNET_ROOT` from the `dotnet` on PATH. It also clones the app to
  `thronefall.app.new` before deleting the old copy (a failed `cp` used to leave no app).
- **Client player regenerates health**, so `--mp-test-night` kills it only sometimes (1 of 3).

- **Forced night without a castle does nothing useful.** `DayNightCycle.SwitchToNight()` alone
  gives a night with no fight. Build the castle first: `CastleCenter.instance
  .GetComponentInParent<BuildSlot>().ExecuteBuildOrUpgrade(PlayerInteraction.instance, false)`.
- **Wave-1 melee enemies ignore players and walk to the castle.** Standing an avatar 3.5–9
  units from the castle never got it hit. `--mp-test-night` therefore parks the avatar on the
  nearest enemy (`testChase`); that is how the damage path was verified.
- **A host with a weapon kills wave 1 before it arrives** (Long Bow). Test nights with an
  unarmed host.
- **An unarmed, idle host loses Nordfels in ~40 s of night**; the end screen sets
  `Time.timeScale = 0`, which also stops `Invoke`-based test timers. Not a hang.
- **`CastleCenter.CastleCenterPosition` is (0,0,0) until the castle is built** on that
  instance (set in `Start`). Use `FindFirstObjectByType<CastleCenter>(FindObjectsInactive.Include)`.
- **Unequipped weapon models are destroyed in `WeaponEquipper.Start`.** Cloning the player
  later yields only the local weapon; clone inside `PlayerMovement.Awake` instead.
- **Cloning the player without stripping scripts overwrites singletons** (`PlayerMovement
  .instance`, `PlayerInteraction.instance`, `CommandUnits.instance`, `PlayerUpgradeManager
  .instance` are set in `Awake`). Always clone under an inactive parent and strip first.
- **Strip order matters:** `DestroyImmediate(RVOController)` fails while `PlayerMovement`
  exists (`[RequireComponent]`). Remove game-assembly scripts first.
- **`TagManager.Players[0]` must stay the local player** (6 call sites do
  `Players[0].GetComponent<PlayerInteraction>()`). `UpdateGhost` re-registers the avatar if it
  ever ends up first.
- **`Hp.invulnerable` is reset every dusk** for everything `PlayerOwned`; don't rely on it.
- **The game re-applies saved video settings at startup**, so `Screen.SetResolution` must be
  delayed (4 s `Invoke`); `-screen-width` flags are ignored.
- **"Peer connected" is often logged twice on the host**: scene loads stall longer than the
  3 s peer timeout. Harmless.
- **`.Reverse()` on an array resolves to the span overload** (returns void) with this SDK;
  use `Enumerable.Reverse(...)`.
- **Fresh save has nothing equipped**; use `--mp-equip` in tests. Title-screen "Play" would
  force the tutorial; `--mp-autostart` skips it without touching the save.
- **Client experience today is odd by design:** it runs its own enemies, which cannot hurt
  it, while invisible host enemies can. Goes away when M3 replicates enemies.
