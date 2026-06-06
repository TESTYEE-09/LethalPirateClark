# Changelog

All notable changes to LethalPirateClark are documented in this file. Dates are YYYY-MM-DD.

## [1.0.1] - 2026-06-06

### Changed
- Bumped default `Spawn.Rarity` from 35 to 200 for easier v81 testing (set to 30-50 in the config for "feels rare" gameplay)
- Wrapped `NetworkPrefabs.RegisterNetworkPrefab` call in its own try/catch — v81 ships Netcode 1.12.2 where the older API may throw
- Wrapped the entire enemy-registration block in try/catch with full stack-trace logging, so if any single LethalLib API call has been renamed in a future game version, you get a clear `[StillLife]` error line in the BepInEx log instead of silent failure
- Bumped BepInEx GUID / assembly name to `com.TESTYEE-09.lethalpirateclark` (was `com.yourname.stilllife`) for the v1.0.1 release

### Verified
- All API surface (`HasLineOfSightToPosition`, `SpawnEnemyGameObject`, `DoorLock.isLocked`, `isDoorOpened`, `OpenOrCloseDoor`, `EnemyAICollisionDetect.mainScript`, `EnemyType` fields, `PlayerControllerB.KillPlayer`) verified against Lethal Company v81's `Assembly-CSharp.dll`
- Asset bundle built successfully on Mac Unity 2022.3.9f1 for `BuildTarget.StandaloneOSX` (target-agnostic because `EnemyAICollisionDetect` is added at runtime by the mod DLL)
- Compatible with RugbugRedfern-Skinwalkers 5.0.0 (different mod category — voice mimicry on stock enemies — no code-level conflict)

## [1.0.0] - 2026-06-06

### Added
- Initial release
- **Pirate Clark** enemy, based on the Backrooms *Still Life* entity
- **Phase 1 (stalker)**: freeze-when-watched, acceleration when unobserved, light-flicker, door-knock-then-break, grab kill with eat SFX
- **Phase 2 (the turn)**: killed players rise as new Still Lifes (capped, generic copy — not a swappable mesh yet)
- Asset bundle built with one-click Unity editor menu (`StillLife ▸ Build Everything`)
- BepInEx config: `Spawn.Rarity`, `Behaviour.MoveSpeed`, `Conversion.Enabled`, `Conversion.MaxAlive`
- Verified compatibility with `BepInExPack 5.4.2100` and `LethalLib 0.16.0`

### Known
- Built and tested against Lethal Company v81; the v64 → v81 jump removed the older `NetworkPrefabs.RegisterNetworkPrefab` API in some configurations
- The C# was never executed at runtime in our test environment (no Windows + Lethal Company on this Mac) — first runtime verification happens when you install and launch
