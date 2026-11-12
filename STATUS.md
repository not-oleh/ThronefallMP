# Status

Last updated: 2026-11-12 (night) · plugin version 0.4.0 · protocol 4 · MIT licensed

## Working now

- Toolchain: BepInEx loads natively on Apple Silicon; build deploys into `game/`.
- Two local instances connect over UDP (`scripts/run.sh both`).
- Client follows the host into any scene, starting from the title screen.
- Remote player avatar: position, facing, walk/sprint animation, own weapon model.
- Client keeps its own weapon and takes the host's other perks.
- Damage: host's enemies hit the avatar → damage is forwarded → client's player loses health.
  Client logs once per burst (gap 3 s, max 10 s), e.g. `Took 27.5 damage from the host's world
  (11 hits, hp now 23.45)`: 7 lines per night instead of ~55.
- Avatar hides when the peer dies: `Avatar hidden (peer is dead)` then `Avatar shown` on revive
  (host log, 2026-11-12 test-night run 1; the client regenerates, so death is not reliable).
- Unfocused window gets no input: `Rewired.Player` `GetAxis*/GetButton*/GetNegativeButton*` return
  nothing unless this process is frontmost (`Patches/InputPatches.cs`, MP only). "Frontmost" =
  `NSRunningApplication.currentApplication.isActive` via libobjc, cached per frame; Unity's
  `Application.isFocused` is stuck when two instances share one bundle (human-reported: true on the
  host, false on the client). Falls back to `isFocused` off macOS. Focus changes are logged:
  `Window focus: False (Unity says True, input blocked)`. Unattended run confirmed both signals
  disagree (AppKit False, Unity True) and the plugin still ran; **a human click test is still open**.
- Overlay is crisp on Retina (verified by the user).
- Reliable messages (`Net/Reliable.cs`, seq/ack/resend, in order, session id detects restarts);
  startup `SelfCheck` covers packets and a lossy reliable run.
- Shared day/night: host's cycle rules; client's `DayNightCycle.Update` is frozen while the host is
  in its scene and it is switched to the host's state (`Match: time follows host -> Night`).
- Shared treasury: host balance pushed on change; client forwards its own coin/core deltas.
  Test (`--mp-test-coins`): 8 start, host +10, client +5 → both read 23, no double counting.
- Overlay font scales with window height (14–48 px), with a drop shadow.
- `scripts/setup.sh` runs end to end (needs to run outside the agent sandbox: Steam dir + GPU).
- Client saves are isolated in `mp-client/`.

## Milestones

- [x] M0 Toolchain, repo, UDP ghost
- [x] M1 Second player in the world (human checks below are leftovers, not blockers)
- [~] M2 Shared match state — done: code split, reliable layer, day/night, coins/cores. Left: buildings, upgrade choices, wave state
- [ ] M3 Combat (enemy replication, client attacks hurting host enemies, revive)
- [ ] M4 Steam lobbies/invites, reconnect, Craaghelm bosses, Eternal Trials
- [ ] QoL track (nothing requested yet)

## Not verified

- Window-focus block with a human: click each window, confirm only the clicked King moves. Expect
  `Window focus: True/False` lines flipping per click. If AppKit's `isActive` misbehaves the log
  shows `AppKit focus query unavailable` (then it falls back to Unity's flag).
- Energy cores sync: no DLC in the test runs (cores stayed 0); code path untested.
- Day→night→day with the client following (night start verified; the return to day was not reached).
- Client coin pickups from its own world are forwarded as income (its world is still its own
  simulation until M3), so a client in a coin-rich world inflates the shared treasury.
- Remote attack animation, host perks (non-weapon), human-driven movement, second machine, night screenshot.

## Next tasks

Simple (Sonnet-friendly):

- [ ] Two-window human test: walk in one window, confirm the other character stays put (+ read `Window focus:` lines).
- [x] Avatar re-show after death logs `players=2` (2026-11-12, `--mp-test-night`, twice).
- [ ] `--mp-shot` at night to eyeball the overlay size on Retina.

Complex (architecture-heavy, use Opus/Fable):

- [ ] Entity IDs for `TaggedObject`s so buildings/enemies can be addressed across instances.
- [ ] M2 next slices: building placement + upgrade choices via reliable messages (host executes
      `BuildSlot.ExecuteBuildOrUpgrade`), then wave/enemy state (client must stop its own spawner).
- [ ] Client attacks affecting the host's enemies (intent message → host applies damage).
- [ ] Steam P2P transport behind `Link` (same `SendState/SendMessage/Pump/Peer` surface).

## Known quirks and dead ends

- **`ReInput.configuration.ignoreInputWhenAppNotInFocus` + patching `MoveScript`/`TryToAttack` did not
  stop input** in a two-window human test (removed). Blocking at `Rewired.Player` level replaces it.
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
- Two early launches exited for unknown reasons (possibly windows closed by hand). Not
  reproduced since.
