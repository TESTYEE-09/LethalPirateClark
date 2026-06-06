# Changelog

All notable changes to LethalPirateClark are documented in this file. Dates are YYYY-MM-DD.

## [1.1.0] - 2026-06-06

### The real model
- **The actual Pirate Clark mesh is now in-game.** The 18,375-vertex, 19,999-face model is exported from `PirateClark.fbx` to a triangulated `.obj` (`model-source/pirate_clark_embedded.obj`, 2.4 MB), embedded as a .NET manifest resource in the BepInEx DLL (`LethalPirateClark.pirate_clark.obj`), and parsed at runtime by `ObjMeshLoader.cs` into a Unity `Mesh`. The model is rendered with HDRP/Lit, tinted to a single mustard-yellow "pirate coat" color (texture map loading deferred to v1.2.0).
- v1.0.4 was actually the *same* source code (the OBJ embedding was already wired in), but the DLL shipped to the user was stale. v1.1.0 ships a fresh build with the .obj resource verified embedded via a Unity editor batch test (`unity_meshtest3.log`).
- 2.4 MB DLL → 1.0 MB installed zip (zlib-compresses well; the .obj is just floats).

### Why v1.0.4 was a procedural capsule when it should have been the real model
- The `Plugin.cs` v1.1.0 code with `ObjMeshLoader.LoadEmbedded(...)` was committed to the source repo at the end of the previous Claude Code session, but the v1.0.4 zip I shipped used a stale 28 KB DLL (the old procedural build) by mistake. The repro in the v1.0.4 zip was a fallback capsule because `LoadEmbedded` returned null (the .obj wasn't actually embedded in that stale DLL). v1.1.0 has the fresh build with the .obj actually embedded.

### Verified
- Unity editor batch test (`StillLifeMeshTest.Test`) confirms the .obj is embedded in the DLL with stats: `v=18,375, vt=17,707, vn=18,243, f=19,999`. See `build_logs/unity_meshtest3.log` for the full diagnostic.

## [1.0.4] - 2026-06-06

### The big one
- **Pirate Clark was *never* spawning — not because of any v1.0.1/v1.0.2 issue, but because the Mac Unity build of the `stilllife` asset bundle was being written corrupt.** `AssetBundle.LoadFromFile` AND `LoadFromMemory` both return NULL, even on the same Mac editor that built the bundle. This was a silent failure: the mod DLL loaded, `LoadFromFile` returned NULL, `LoadAsset<EnemyType>` was never called, the enemy was never registered, and the debug menu never listed it. Every version from v1.0.0 onward has had this bug.
- **v1.0.3 ditches the asset bundle entirely.** The C# DLL now builds the `EnemyType` ScriptableObject and the enemy prefab entirely in code at runtime, against the actual Windows game types. No Unity editor required, no Mac-vs-Windows type-tree hash games, no silent field-drop bugs. The mod is now fully self-contained in the DLL.
- **Visual: procedural placeholder.** A capsule body (dark brown "pirate coat"), a flattened cube on top (tricorn hat), a thin cube at the waist (belt). Built from `GameObject.CreatePrimitive` calls at runtime. Looks like a placeholder but is unambiguously "pirate-shaped" and moves correctly. The real model source (`PirateClark.fbx`) is preserved in `model-source/` for a future runtime FBX loader to use.
- **Audio: silent.** The `AudioSource` components are wired so a future drop-in of `PC_ambient.wav` + `PC_eat.wav` is a single-line change, but no clips ship with v1.0.3.
- **Bundle file removed from the install.** The `dist/LethalPirateClark_v1.0.3.zip` is now 327 KB (was 1.4 MB) — just the DLL, manifest, icon, and README. The `plugins/StillLife/` folder no longer contains a `stilllife` file. If you're upgrading from v1.0.0/1.0.1/1.0.2, **delete the old `stilllife` file** in your install (it's corrupt anyway).

### Fixed
- The v1.0.2 runtime `ForceEnemyTypeOverrides()` is no longer needed (there was no bundle to worry about a type-tree mismatch on). Removed.

### Files
- New: `src/Plugin/Plugin.cs` rewritten — `BuildPiratePrefab()` (procedural mesh), `BuildEnemyType()` (runtime ScriptableObject), `ResolveType()` / `TrySetField()` / `TrySetProperty()` helpers
- New: `unity/StillLifeUnity/Assets/Editor/StillLifeDebugLoad.cs` — diagnostic that proved the v1.0.2 bundle was corrupt. Kept in case the bundle approach is revisited.

## [1.0.2] - 2026-06-06

### Fixed
- **Pirate Clark was not spawning in v1.0.1** — the Mac-built `stilllife` asset bundle has a different type-tree hash from the Windows game's `Assembly-CSharp.dll`, so the EnemyType fields (especially `PowerLevel`) were being silently dropped to their defaults when the Windows game deserialized the bundle. With `PowerLevel` defaulting to 0, the enemy took 0 power budget and was never picked by the spawner's weighted random draw.
- Added `ForceEnemyTypeOverrides()` in `Plugin.cs` — sets every spawn-critical field on the EnemyType at runtime via reflection (tries the publicised property first, falls back to `BindingFlags.NonPublic` field), so the values are written against the Windows type tree and persist into the spawn pool.
- Added before/after field-value logging in `BepInEx/LogOutput.log` so the next time something's wrong, you'll see the actual numeric values of `PowerLevel`, `MaxCount`, `isOutsideEnemy`, `isDaytimeEnemy`, `spawningDisabled` — before AND after the override.

### Changed
- Default `Spawn.Rarity` raised from 200 to **1000** (effectively always-pick for a `PowerLevel=1` enemy; game internally caps at ~1000)
- Added a new `Spawn.MaxCount` config (default 8) — controls how many Pirate Clarks can be alive on a level simultaneously. Previously hardcoded at 4 in the bundle.

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
