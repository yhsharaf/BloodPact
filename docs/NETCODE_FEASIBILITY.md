# Tailscale Netcode for RoseCoop: Feasibility Notes

## Setup done
- Installed **BepInEx 6.0.0-be.789** (builds.bepinex.dev) and RoseCoop into the game folder.
  The GitHub release (6.0.0-pre.2 / be.697) **does not work** with this game: its Cpp2IL supports IL2CPP
  metadata up to v29, and the game uses v31.
- On first launch be.789 generated 133 interop assemblies, and RoseCoop loaded ("Shared Curse 0.2.0").
- Game: Unity 2022.3.62f2, IL2CPP, Steam app 4231820.

## How RoseCoop works (observed in its metadata)
- **Players:** `Players` holds up to 4 `Player` slots. Each has `Index`, `Devices` (Unity `InputDevice[]`)
  and `Split` (an `InputSplit`). Extra Roses are `Object.Instantiate` clones of the main character
  (`MainCharacterSingleton`).
- **Input:** `InputSplit` clones the game's `InputActionAsset` and creates an `InputUser` per player
  (`PerformPairingWithDevice`). It then writes each clone's `MotherBase.ActionFramework.PlayerInputData`.
  **One input device = one Rose.**

## How the game works (observed in its interop assemblies)
The game is built on the developer's own **MotherBase** ECS-style framework. Several features favour host-authoritative mirroring:

| Need | Finding |
|---|---|
| One place to switch simulation off on the guest | `SystemManager` runs every system through `SystemBase.UpdateEntities(EntityContext)`, in fixed, dynamic and late lists. A Harmony prefix can skip the chosen systems. |
| Simulation and presentation kept apart | 97 systems. Simulation: `DamageSystem`, `DeathManagementSystem`, `AIGroupBehaviourSystem`, `MobsSpawnersSystem`, `LootDropSystem`, `KinematicPhysicSimulationSystem`. Presentation: `Interpolated*PresentationSystem`, feedback and VFX systems. |
| Room sync | `RoomManager.RoomManagerSystem` streams rooms by **chunk coordinate** (`RoomChunkID`, `Vector2Int _currentChunk`) and has `OnRoomChanged` / `RoomChangeTask`. Syncing the room means sending one coordinate. |
| Same room layout on both PCs | LevelGen seeds come from positions (`GenerateTileSeed(..., Vector3Int)`, `GetObjectSeed(GameObject)`), not from a run-time RNG. |
| Stable enemy IDs | Enemies come from `MobsSpawner` objects placed in rooms. Each tracks `aliveSpawnedMobs`, `spawnedMobsCount` and `onMobSpawned`/`onMobDead`. ID = room chunk + spawner (sorted by position) + spawn number. |
| Serialization | `ISnapshot.ReadFromComponent/WriteToComponent` for each component type (`EntitySnapshotDatabase`), plus `TransformSnapshot` (position, rotation, scale). |
| Damage authority | Every hit goes through `DamageSystem.ProcessHit`, which returns a `HitResult`. On the guest, hits can be forwarded to the host instead of being applied. |

`Entity` itself is a MonoBehaviour **with no network ID**. IDs have to come from the spawner scheme above.
Lockstep (sending only inputs) is still ruled out, because Unity physics and float math aren't deterministic across PCs.

## Probe test results (2026-10-17, two game copies on one PC, game 1.1.3)
Same route through 4 rooms (StreetsPoorV2 Room40–43) in two runs: 34 vs 33 spawns from 13 spawners.
- **The IDs match.** All 13 spawners have the same scene path and position in both runs.
  Every `spawner + n` in one run maps to the same enemy type in the other (33/33 shared).
- **12 of 13 spawners are fully deterministic.** Same enemy, same position (20/20).
- **`MobsSpawnerByDensity` (zombie horde) is random.** Positions differ and the count depends on play time (14 vs 13).
  That's fine for host-authoritative sync: the guest never spawns on its own, it spawns what the host reports
  (ID + position), so randomness only happens on the host.
- **Room chunk coordinates aren't unique** (several rooms report `(0,0)`). Use the room root object name
  (e.g. `StreetsPoorV2_Room43_uid136`, the first segment of every spawner path) as the room ID instead.
- **Saves:** slot 1 = `%USERPROFILE%\AppData\LocalLow\KONAMI\Castlevania Belmont's Curse\BuildSave_0.sav` (~11 KB).
- **Two copies of the game run side by side on one PC** with BepInEx and RoseNet loaded in both,
  so host and guest can be developed and tested on a single machine.

**Verdict: go for enemy sync.** ID = room uid + spawner path + spawn number, and the host is the only side that spawns.

## Recommended architecture: "RoseNet" (a second BepInEx plugin next to RoseCoop)
- **Transport:** UDP between the two Tailscale IPs (100.x.x.x), port configurable, about 30 Hz.
  Tailscale handles NAT and encryption, so no port forwarding is needed.
- **Guest → host:** the guest's controller state. On the host it becomes a virtual Gamepad
  (`InputSystem.AddDevice` + `QueueStateEvent`), so RoseCoop treats it as a local controller and the guest
  joins on the normal join screen.
- **Host → guest:** room chunk, the transforms and action/animation state of every Rose and every
  spawner-ID'd mob, health, deaths and pickups.
- **Guest side:** skip the simulation systems for mobs, apply the snapshots, and let presentation systems interpolate.
  The guest's own Rose can be predicted locally and corrected by the host.

## Milestones and risk
1. **Input over Tailscale (low risk, short).** Usable on its own straight away, with the guest watching a stream.
2. **Room-follow + Rose mirroring (medium risk).** The guest's game follows the host between rooms and shows all Roses.
3. **Mob mirroring (main risk).** Depends on spawners producing the same mobs in the same order on both PCs.
   Can only be confirmed with both games running.
4. **Damage, deaths, loot, bosses, cutscenes, save flags (long tail).** Bosses and cutscenes are scripted
   sequences (`CutsceneSystem`, `SequenceStepActionSystem`) and will need dedicated handling.

Not answered by static analysis (needs runtime testing with two PCs): actual mob spawn order stability,
which `ActionFramework` state is enough to reproduce animations, and how boss scripts react to running without their systems.
