# Thronefall MP — agent guide

Open-source, macOS-first co-op (and QoL) mod for Thronefall. BepInEx 5 + Harmony plugin, C#.
Read this file and `STATUS.md` (imported at the bottom) before exploring anything.

## Rules

- **Update `STATUS.md` after every completed task and before ending a session**: state,
  next tasks, and any dead end you hit ("tried X, failed because Y"). Keep it under ~120 lines.
- Never commit anything from `game/`, `decompiled/`, `tools/` (copyrighted or downloaded).
- Never modify the Steam install; all work happens on the clone in `game/`.
- Never bypass DLC ownership (`DlcConfiguration.forceCraaghelm` stays untouched).
- Launching the game opens windows on the user's screen. Launch only to verify, keep runs
  short, and always `pkill -f "ThronefallMod/game/thronefall.app"` afterwards.
- Never take full-screen captures (`screencapture`). Use `--mp-shot file.png` (in-game only).
- Verify from logs, not by assumption. Report what was not verified.
- Keep changes small: one plugin file until a second module really needs splitting.

## Paths

| What | Where |
|---|---|
| Project root (git) | `~/Desktop/ThronefallMod` |
| Steam install (read-only) | `~/Library/Application Support/Steam/steamapps/common/Thronefall` |
| Modded game clone | `game/thronefall.app` (+ `game/BepInEx/`, `game/run_bepinex.sh`) |
| Game DLLs | `game/thronefall.app/Contents/Resources/Data/Managed/` |
| Plugin source | `src/ThronefallMP/Plugin.cs`, `ThronefallMP.csproj` |
| Built plugin | `game/BepInEx/plugins/ThronefallMP/ThronefallMP.dll` (copied by the build) |
| Decompiled game code | `decompiled/Assembly-CSharp/*.cs` (flat folder, one class per file) |
| Logs | `game/player-host.log`, `game/player-client.log`, `game/BepInEx/LogOutput.log` |
| Saves | `~/Library/Application Support/com.grizzlygames.thronefall/` (client: `mp-client/`) |

Game: v2.15, Unity 6000.3.18f1, Mono, universal binary, Steam app id 2239150.
Toolchain: dotnet SDK 10 (Homebrew), BepInEx 5.4.23.6, ilspycmd in `tools/ilspy/`.

## Commands

```bash
scripts/setup.sh                                  # once / after a game update: clone, BepInEx, decompile
dotnet build src/ThronefallMP -c Release -v q --nologo   # build + deploy into game/
scripts/run.sh both --mp-autostart                # host + client, level select
scripts/run.sh host --mp-autostart Nordfels --mp-test-night   # unattended night test (host)
scripts/run.sh client --mp-equip "Light Spear"    # client follows host from the title screen
grep -E "Thronefall MP\]|Exception" game/player-host.log      # what the mod did
pkill -f "ThronefallMod/game/thronefall.app"      # stop all instances
```

- `run.sh` builds first. Modes: `single | host | client | both`; extra args go to the game.
- Shell gotchas: redirect `run.sh` output to a file (`> out 2>&1 < /dev/null`); piping it
  hangs on the dotnet build server. ilspycmd needs `DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec`.
- Steam must be running. `SteamAppId=2239150` (set by `run.sh`) lets the clone start.
- A typical unattended check: launch host, `sleep 2`, launch client, `sleep 60–100`, grep logs, pkill.

Plugin flags: `--mp-host [port]`, `--mp-join [ip[:port]]`, `--mp-profile name`,
`--mp-autostart [scene]`, `--mp-equip a,b`, `--mp-test-night`, `--mp-shot file.png`.
Scene names: `_StartMenu`, `_LevelSelect`, `_LevelSelectCraaghelm`, `Neuland(Tutorial)`, `Nordfels`,
`Durststein`, `Frostsee`, `Uferwind`, `Sturmklamm`, `Wildbach`, `Moorweg`, `Freifort`, `Totend`,
`Craaghelm_map01`…`08`. Weapon names: Long Bow, Light Spear, Heavy Sword, Lightning Wand,
Cursed Blowpipe, Falchion and Trap, Potion Vials, Battle Axe, Blood Wand.

## Mod architecture

Host-authoritative co-op: the game is not deterministic (frame-time driven, `Random` in 81
files), so the host simulates and clients send intents and render host state.

Everything is in `src/ThronefallMP/Plugin.cs` today:

- `Plugin` (BaseUnityPlugin): CLI flags, UDP socket, 20 Hz `State` packet (`Encode`/`Decode`,
  `Protocol` byte, `SelfCheck` round-trip assert), overlay label.
- `State`: scene, position, yaw, moving/sprinting/dead, attack counter, cumulative
  `DamageToPeer`, weapon name, loadout names. State is resent constantly, so UDP loss heals itself.
- `FollowHost`: client loads whatever scene the host's player is in, with host perks + own weapon.
- `BuildGhost`/`UpdateGhost`: the remote player's avatar. Cloned from the local player inside
  `PlayerMovement.Awake` (the only moment all weapon models still exist), stripped of every
  game script except `Hp`, `TaggedObject` and `OneShotAnimationBase`. It is **not** a
  `PlayerMovement`; this avoids the ~100 single-player call sites.
- Harmony patches (bottom of file): `SaveProfilePatch`, `PlayerSpawnPatch`, `AttackPatch`,
  `DamagePatch` (host counts damage to the avatar; client's player only takes host damage).

Planned split when a second module arrives: `Net/` (transport), `Sync/` (one module per game
system), `Patches/` (thin, one file per game class), `QoL/`. Details: `docs/ARCHITECTURE.md`.

## Game code cheat-sheet (`decompiled/Assembly-CSharp/`)

| Topic | Open these | What matters |
|---|---|---|
| Player | `PlayerMovement.cs`, `PlayerMovementExt.cs` | `static instance`, `meshParent`, `meshAnimator` ("Moving"/"Sprinting" bools), `Hp`, `TeleportTo`, input via `ReInput.players.GetPlayer(0)` |
| Player registry | `PlayerManager.cs`, `TagManager.cs` | `RegisteredPlayers`, `GetClosestPlayer`; `TagManager.Players` (list; game reads `[0]` as *the* player) |
| Economy / interaction | `PlayerInteraction.cs`, `Coin.cs`, `EnergyCore.cs`, `TreasuryUI.cs` | `static instance`, `Balance`, `AddCoin`, `EnergyCoreBalance` (Craaghelm), `IsFreeToCallNight`, `EquipWeapon` |
| Weapons | `WeaponEquipper.cs`, `PlayerAttack.cs`, `ManualAttack.cs`, `PlayerAttackAnimator.cs`, `PlayerWeaponVisuals.cs` | Unequipped weapon objects self-destroy in `Start`; `TriggerAnimations()`; visuals shown at night only |
| Loadout / perks | `PerkManager.cs`, `Equippable.cs`, `EquippableWeapon.cs`, `LevelSelectManager.cs` | `CurrentlyEquipped`, `allEquippables`, `IsEquipped`; `PlayButtonPressed` starts a level |
| Health | `Hp.cs` | `TakeDamage(amount, from, …)` (virtual; subclasses `HpPlayerUnitExt`, `HpThief`, `HpCrystalDragon`), `KnockedOut`, `Revive`, `invulnerable` (cleared every dusk), `OnHit`/`OnKillOrKnockout` events |
| Tags / targeting | `TaggedObject.cs`, `TagManager.cs`, `TargetPriority.cs` | `ETag` enum; registers in `OnEnable`; `FindClosestTaggedObjectWithTags(pos, must, mayNot)` |
| Enemy AI | `PathfindMovementEnemy.cs`, `AutoAttack*.cs` | `targetPriorities` list decides where they walk; basic melee walks to buildings and hits what is in reach |
| Waves | `EnemySpawner.cs`, `Wave.cs`, `Spawn.cs`, `*WaveGen*.cs` | `static instance`, `NumberOfEnemiesOnTheMap`, `SpawningInProgress`, `StartSpawning`, `Wavenumber` |
| Day/night | `DayNightCycle.cs` | `Instance`, `CurrentTimestate`, `SwitchToNight()`, `DuskCall`/`DawnCall…`, `IDaytimeSensitive` |
| Building | `BuildSlot.cs`, `BuildingInteractor.cs`, `CastleCenter.cs`, `ChoiceManager.cs` | `TryToBuildOrUpgradeAndPay`, `ExecuteBuildOrUpgrade(player, presentChoice)`, `OnUpgradeChoiceComplete`; `CastleCenter.instance` only exists once built |
| Match state | `LocalGamestate.cs`, `ScoreManager.cs`, `EndOfMatchScoreUIHelper.cs` | `State` enum, `SetState`, `SelectedGameMode`; end screen sets `Time.timeScale = 0` |
| Scenes | `SceneTransitionManager.cs`, `TitleScreenUIHelper.cs` | `TransitionFromNullToLevel(name)`, `TransitionFromNullToLevelSelect()`, `SceneTransitionIsRunning`; additive loads, `_UI` scene stays |
| Saves | `Thronefall.SaveServices/SteamSaveServices.cs`, `SaveLoadManager.cs`, `MatchSaveLoadHandler.cs`, `LocalMatchSaveLoad.cs` | `GetFullSavePath` (patched); mid-match save finds `SaveLoadEntity` incl. inactive |
| Steam | `SteamManager.cs`, `Thronefall.GameServices/` | Steamworks.NET init, leaderboards; P2P not used by the game |
| Craaghelm DLC | `DlcConfiguration.cs`, `SteamDlcProvider.cs`, `CraaghelmStaticResources.cs`, `*Ext.cs` | `IsDlcAvailable(CRAAGHELM)`; bosses: `CrystalDragon*`, `LavaGolem*`, `TheScrapper*`, `ConstructColossus*` |
| UI | `UIFrameManager.cs`, `ThronefallUIElement.cs`, `TFUI*.cs` | frames, pause, `Application.Quit` |

Single-player assumptions to remember: `PlayerMovement.instance` (35 uses),
`PlayerInteraction.instance` (29), `GetPlayer(0)` (31), `Players[0]` (6).

@STATUS.md
