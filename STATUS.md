# Status

Last updated: 2026-11-12 · plugin version 0.3.0 · protocol 3

## Working now

- Toolchain: BepInEx loads natively on Apple Silicon; build deploys into `game/`.
- Two local instances connect over UDP (`scripts/run.sh both`).
- Client follows the host into any scene, starting from the title screen.
- Remote player avatar: position, facing, walk/sprint animation, own weapon model.
- Client keeps its own weapon and takes the host's other perks.
- Damage: host's enemies hit the avatar → damage is forwarded → client's player loses health.
  Verified in logs (`Avatar was attacked by E Melee(Clone) for 2.5` on host,
  `Took 2.5 damage from the host's world` ×25 on client).
- Client saves are isolated in `mp-client/`.

## Milestones

- [x] M0 Toolchain, repo, UDP ghost
- [~] M1 Second player in the world — code complete, see "Not verified" below
- [ ] M2 Shared match state (day/night, coins + energy cores, buildings, upgrade choices)
- [ ] M3 Combat (enemy replication, client attacks hurting host enemies, revive)
- [ ] M4 Steam lobbies/invites, reconnect, Craaghelm bosses, Eternal Trials
- [ ] QoL track (nothing requested yet)

## Not verified (M1 leftovers)

- Avatar hides when the remote player dies (`Avatar hidden (peer is dead)` never observed;
  the last test run was cut short).
- Remote attack animation (code path exists; nobody has attacked in a test).
- Host perks (non-weapon) copied to the client; only weapons were exercised.
- Avatar movement while a human walks; all tests were unattended.
- Joining from a second machine.
- The night screenshot after the latest avatar changes.

## Next tasks

Simple (Sonnet-friendly):

- [ ] Log `Took … damage` once per burst, not per hit (25 lines per night today).
- [ ] Update `README.md` and the M1 row in `docs/ARCHITECTURE.md` to 0.3.0 behaviour.
- [ ] Add `LICENSE` once the user picks one (MIT suggested, not confirmed).
- [ ] Run one `--mp-test-night` to completion and confirm the "Not verified" death/hide line.
- [ ] Overlay text is tiny on Retina; scale the IMGUI label.
- [ ] `scripts/setup.sh` has never been run end to end as a script; run it once and fix.

Complex (architecture-heavy, use Opus/Fable):

- [ ] M2 design: make the client stop simulating its own world (enemy spawner, day/night,
      economy) and render host state instead. Decide per system: suppress vs. replicate.
- [ ] Reliable transport (LiteNetLib or Steam P2P) — needed for the first one-off event
      (build, upgrade choice). Today everything is resent state over raw UDP, one peer, no auth.
- [ ] Entity IDs for `TaggedObject`s so buildings/enemies can be addressed across instances.
- [ ] Client attacks affecting the host's enemies (intent message → host applies damage).

## Known quirks and dead ends

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
