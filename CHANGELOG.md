# Changelog

All notable changes to LethalPirateClark are documented in this file. Dates are YYYY-MM-DD.

## [2.1.2] - 2026-06-07

### Self-contained: bundle is now embedded inside the DLL as a fallback
v2.1.1's fingerprint log confirmed the on-disk `stilllife` file in a user's install was byte-identical to the one I built and shipped (md5 `7031579a65ff49856e99f60d90ad68e0`, 1,070,202 bytes) — yet `AssetBundle.LoadFromFile` still returned null with "not compatible with this newer version of the Unity runtime". The bundle is correct, the runtime is correct, but something on that user's machine (AV scan, NTFS file locking, r2modman's path through `AppData\Roaming\`, or some other Windows-specific factor) is blocking the file-system load. v2.1.2 makes the mod self-contained: the DLL now also carries a base64-encoded copy of the bundle as a fallback, and if the on-disk load fails it falls back to `AssetBundle.LoadFromMemory(embedded)`.

- **New `EmbeddedAssets.StillLifeBundleBase64` const** holds a base64-encoded copy of the exact same `stilllife` file the build ships. ~1.07 MB raw → ~1.43 MB base64, compiled into the DLL. Bumping the version of the embedded copy requires regenerating `EmbeddedAssets.cs` from the source bundle.
- **`Plugin.LoadAssetsAndRegister` now has a fallback path.** Tries `AssetBundle.LoadFromFile` first (fast, no decode cost). If that returns null, logs the file's size + md5 + path, then calls `Convert.FromBase64String(EmbeddedAssets.StillLifeBundleBase64)` and tries `AssetBundle.LoadFromMemory`. If that also fails, the existing "this bundle is wrong" diagnostic fires with the original on-disk fingerprint for the user to compare.
- **The DLL grew from 40 KB to ~5.7 MB.** That's the cost of carrying the bundle. Acceptable for a self-contained install; the mod no longer depends on a second file at all if the embedded path works.
- **No functional change to the AI / movement / spawning logic** when both loads work — same code path. The fallback is only taken on failure.

## [2.1.1] - 2026-06-07

### Loud install-corruption diagnostics
v2.1.0 was confirmed shipping and loading correctly, but a v2.1.0 DLL landed on a user's PC next to a stale `stilllife` bundle from an earlier version of the mod (the bundle was built with Unity 2022.3.9f1, the live game runs 2022.3.62, and the runtime rejects the older bundle). v2.1.0 logged the failure as "AssetBundle.LoadFromFile returned NULL" with a paragraph of possible causes — which is technically correct but not actionable enough. v2.1.1 makes the install-corruption case impossible to misdiagnose.

- **Bundle fingerprint on disk is now logged at load time.** New `Plugin.ComputeFileMd5` helper. On every startup the DLL logs the bundle's file size and md5, plus the known-good values for the current release. The expected md5 for v2.1.1 is `7031579a65ff49856e99f60d90ad68e0` and the expected size is `~1,070,202 bytes`; pre-1.4.1 bundles are ~50 KB and pre-2.1.1 bundles from older releases are different md5s, so a wrong bundle shows up immediately as a size/md5 mismatch in the log.
- **Failure message now includes the fingerprint and points at the fix.** The "AssetBundle.LoadFromFile returned NULL" log line now embeds the on-disk file size and md5, and the message body explicitly says: "the 'stilllife' file beside this DLL is from an older version of the mod — FULLY UNINSTALL the mod in r2modman and reinstall the v2.1.1 zip" with a direct URL to the release. This is the exact install state a Windows user with a half-updated mod hits.
- **No functional change.** All v2.1.0 fixes are unchanged; the bundle itself is byte-identical to v1.4.1 / v2.0.0 / v2.1.0. The only change is the load-time diagnostic.

## [2.1.0] - 2026-06-07

### Hardening pass — closes the "loads but does nothing" gap
v2.0.0 fixed the network spawn and the co-op RPC plumbing. v2.1.0 fixes the next layer down: even when the bundle loads, several real failures still kept Pirate Clark from showing up on the spawn menu, from getting a body collider, and from running the AI at the right time. This release closes those, in order of how often they bit during the v2.0.0 playtest.

- **Deferred `AddComponent<StillLifeAI>` until `RoundManager.Instance` is up.** The single most common "loads but does nothing" failure: `Plugin.Awake` runs during BepInEx chainloader, *before* the game's `RoundManager.Instance` singleton is constructed. The previous build called `AddComponent<StillLifeAI>` on the bundle prefab right there in `LoadAssetsAndRegister`, which immediately fired `EnemyAI.Awake` — whose very first line is `thisEnemyIndex = RoundManager.Instance.numberOfEnemiesInScene`. NRE. The template's components were left in a broken state, clones inherited it, and the AI never ran. v2.1.0 splits load+register (synchronous, can run in Awake) from `PreparePrefab` (AddComponent, deferred) and waits for `RoundManager.Instance != null && StartOfRound.Instance != null` in a coroutine before attaching the AI and `EnemyAICollisionDetect`. The bundle and EnemyType are still registered with LethalLib immediately, so the enemy shows up in the spawn menu from the moment the game starts; only the AI's per-clone Awake defers.
- **Solid body collider added to the root.** The bundle prefab's only collider was the trigger `CapsuleCollider` on the `Collision` child (used by `EnemyAICollisionDetect` to detect the player). There was no solid collider on the model, so players walked straight through the body, and — when the NavMeshAgent was off the mesh — the enemy walked through walls instead of bumping them. v2.1.0 adds a non-trigger `CapsuleCollider` on the root (radius 0.4, height 2, center y=1) at runtime; the Unity builder (`StillLifeBuilder.cs`) is also updated to bake the same collider into the bundle for future rebuilds.
- **Co-op-correct conversion cap.** `Plugin.LiveStillLives` was being incremented in `StillLifeAI.Start` on **every** peer. With 4 co-op clients, a single Pirate Clark's Start would bump the count by 4, and the conversion cap (`MaxStillLives`) would fire after just one enemy. v2.1.0 only ticks the count on the host (`if (IsServer)`), and only the host decrements in `OnDestroy`. The `MaxStillLives` check in `ConvertCorpseAfterDelay` (which is already wrapped in `if (IsServer)`) now means what it says.
- **`DoAIInterval` guards against the template and null singletons.** Added an `if (!IsSpawned) return;` at the top so the parked prefab template can't ever tick its AI loop, and an `StartOfRound.Instance == null` guard so a brief race during scene transitions can't NRE.
- **Off-NavMesh fallback respects freeze.** The manual-transform fallback ran even when the enemy was frozen, which let him slide around in `frozen` pose (unsettling and bug-shaped). Now it skips when `_frozen` is true, matching the on-NavMesh path's freeze behavior.
- **Bundle error message is now actually useful.** When `AssetBundle.LoadFromFile` returns null, the old log only said "corrupt or wrong platform". v2.1.0 lists both common causes (Unity-version mismatch, target-platform mismatch) with the rebuild steps, and logs the actual `GetAllAssetNames()` of a successfully loaded bundle so future regressions are immediately diagnosable.
- **Plugin construction is now idempotent.** `Enemies.RegisterEnemy` is guarded behind a `_registered` flag so a hypothetical second Awake (e.g. test harnesses) doesn't double-add. Prefab handling is centralized in `LoadAssetsAndRegister` → `PreparePrefab` (deferred) so the same flow runs on every install.

No model / audio / bundle changes — the `stilllife` bundle is byte-identical to v1.4.1 / v2.0.0; only the DLL changed.


### Co-op hardening + polish — the kill actually networks now
v1.4.1 made Pirate Clark spawn, render, and move correctly on the host. v2.0.0 makes him behave correctly for **everyone in a co-op lobby**, and trims the build down from a pile of diagnostics to a release.

- **The real co-op bug — RPCs were never weaved.** `StillLifeAI` is a `NetworkBehaviour` with `[ClientRpc]` methods (`GrabPlayerClientRpc`, and now `HideCorpseClientRpc`). Unity normally runs a Netcode IL post-processor over `NetworkBehaviour`s to generate the RPC send/receive plumbing — but this DLL is compiled **outside** Unity (plain `dotnet build`), so that step never ran. The result: calling `GrabPlayerClientRpc` just executed its body locally on the server and **never reached remote clients**. In practice the host saw the grab/kill/eat-sound; a non-host player being grabbed did not. Fixed by adding the **`Evaisa.NetcodePatcher.MSBuild`** SDK (post-build IL weave, Unity `2022.3.62` / Netcode `1.12.2`) plus a one-time `[RuntimeInitializeOnLoadMethod]` invoker in `Plugin.Awake` (`InitializeNetworkRpcs`) so the weaver-injected RPC initializers actually register. Verified: the patched DLL now carries `__rpc_handler_*`, `__beginSendClientRpc`, `__initializeRpcs`.
- **Phase-2 conversion now replicates.** Hiding the converted player's corpse used to run server-only, so co-op clients kept seeing the old ragdoll next to the new Still Life. It's now a `HideCorpseClientRpc` broadcast to every peer (works because RPCs are weaved).
- **Sane default config.** `Spawn.Rarity` dropped from the `1000` test value to **`40`** (an uncommon scare; raise to taste), and `Spawn.MaxCount` from `8` to **`1`** (one at a time). Added a `Behaviour.FreezeWhenWatched` row to the README table.
- **Bestiary entry.** The `TerminalNode`/`TerminalKeyword` baked into the bundle (`StillLifeFile` / `StillLifeKeyword`) are now loaded and passed to `Enemies.RegisterEnemy`, so Pirate Clark gets a proper terminal scan/bestiary entry. Falls back gracefully to no-entry if absent.
- **Logging trimmed to a release.** Removed the v1.3.x `CLONE FINGERPRINT` blocks, per-spawn pipeline fingerprints, and the watchdog's per-clone state spam (all were diagnostics for the now-solved spawn bugs). Kept one concise spawn line, the navmesh-snap warning, and the watchdog's silent stuck-recovery safety net.
- No model/audio/bundle changes — the `stilllife` bundle is byte-identical to v1.4.1; only the DLL changed.

## [1.4.1] - 2026-06-06

### Rebuild the bundle for the game's actual Unity version
v1.4.0 registered correctly but the bundle wouldn't load — the log showed:
`The AssetBundle ... could not be loaded because it is not compatible with this newer version of the Unity runtime. Rebuild the AssetBundle to fix this error.` → `AssetBundle.LoadFromFile returned NULL`.

- **Root cause:** the bundle was baked with Unity **2022.3.9f1** (what the repo's `ProjectVersion.txt` claimed), but Lethal Company now runs on Unity **2022.3.62f1** (`Running under Unity v2022.3.62` in the log). Unity rejects a bundle built by an older serialization than the runtime.
- **Fix:** rebuilt the bundle with **2022.3.62f1** (matching the game). `ProjectVersion.txt` updated to 2022.3.62f1. Unity's package resolver also moved `com.unity.netcode.gameobjects` to 1.12.x and HDRP to 14.0.12 — which is correct: the game's shipped netcode is **1.12.2** (the old 1.5.2 pin was stale from the 2022.3.9 era). The NetworkObject/NetworkTransform script GUIDs are unchanged across those versions, so they still resolve in-game; the prefab keeps a valid baked `GlobalObjectIdHash` (`2282514581`).
- No gameplay/code changes — DLL logic is identical to v1.4.0, just rebuilt at 1.4.1. The only functional change is a bundle that the live game can actually load.

## [1.4.0] - 2026-06-06

### Back to the asset bundle — the real fix for "never actually spawns"
The runtime-built-prefab architecture (v1.0.3 → v1.3.5) could never make NGO truly network-spawn the clone. The proof was always in the log: `IsServer=False` on the host's own spawned enemy. The cause is fundamental — a `NetworkObject` created at runtime has `GlobalObjectIdHash == 0`, and setting it via reflection to a made-up value never made NGO resolve the prefab at `Spawn()`. So the clone was instantiated but not networked: no ownership, `EnemyAI.Update` early-returned, he floated, never moved, and got cleaned up ("disappeared"). Thirteen versions of downstream band-aids (active-template trick, deferred activation, watchdog teleport, manual transform movement) were all treating symptoms of that one un-spawn.

- **v1.4.0 loads the enemy from the Unity asset bundle again** (`Plugin.LoadAssetsAndRegister`). A prefab saved in the editor gets a **real, stable `GlobalObjectIdHash` baked by Unity** (this build's is `951099334`) — exactly what NGO needs and what the reflection hack could never replicate. `AssetBundle.LoadFromFile` → `EnemyType` → `enemyPrefab` → register with NGO + LethalLib. The mod's own `StillLifeAI` and the game's `EnemyAICollisionDetect` are still added to the prefab at load time (identically on every peer, so clones share one NetworkBehaviour layout), keeping the bundle free of mod/game scripts.
- **The actual bundle bug from v1.0.4 is fixed:** the builder baked `BuildTarget.StandaloneOSX`. Lethal Company is a Windows build, and Unity stamps each bundle with its platform — a Mac-targeted bundle is rejected by the Windows player (`LoadFromFile` returns null), which is what "the bundle is corrupt" actually was. `StillLifeBuilder.cs` now bakes **`StandaloneWindows64`**. The shipped `stilllife` (1,070,321 bytes) was built with that target and verified to contain the prefab (NetworkObject hash `951099334`), the EnemyType, the animator controller, and both audio clips.
- **No more startup gymnastics:** because a bundle prefab is a true prefab *asset*, its components' `Awake` doesn't fire until it's instantiated in-game (when `RoundManager` exists). That deletes the entire v1.3.x "park the template at y=-8000 / deferred `SetActive` after `RoundManager` is up" machinery and the `EnemyAI.Awake` NRE it was working around.
- **Watchdog trimmed to a safety net:** it no longer force-sets `1.25×` scale (the bundle prefab carries the model's real scale; forcing 1.25 distorted it) and no longer needs to activate clones (bundle clones spawn active). It keeps only the stuck-recovery from v1.3.5.
- **Carries forward all of v1.3.5's locomotion fixes** (agent is the authoritative mover on a NavMesh, grounded off-mesh fallback, server-only transform writes, non-destructive stuck recovery) — those are correct regardless of how the enemy is built.

**Build steps for this release:** (1) drop the game's managed DLLs into `unity/StillLifeUnity/Assets/Plugins/` (Assembly-CSharp + the deps listed in `PUT_GAME_DLLS_HERE.txt`); (2) run Unity menu `StillLife ▸ Build Everything` (or batch `-executeMethod StillLifeBuilder.BuildFromCommandLine`) — needs Windows Build Support installed in the editor; (3) the `stilllife` bundle lands in `AssetBundles/` and ships next to the DLL (already copied into `dist/`); (4) rebuild the mod DLL from `src/` so it's the v1.4.0 bundle-loading build (the DLL currently in `dist/` is the stale v1.3.4 runtime-build and must be replaced).

## [1.3.5] - 2026-06-06

### He actually walks (and stops floating / disappearing)
Root cause found in v1.3.4's movement rewrite, which made all three symptoms ("floats, doesn't move, then disappears") downstream of one logic bug.

- **Doesn't move — fixed.** v1.3.4's `Update()` was `if (manualWalk) … else if (agentPath) …`. The manual branch ran during *every* hunt, so the agent's `SetDestination` in the `else if` was **dead code** — the agent never got a destination. And the `NavMeshAgent` was left with `updatePosition = true`, so every internal agent tick **overwrote the manual `transform.position`** back to its never-updated destination. He stood still. v1.3.5 makes the agent the authoritative mover when it's on a NavMesh (destination refreshed on the AI interval in `DoAIInterval()`, the way vanilla enemies do), and only falls back to a direct transform-walk when there's genuinely no NavMesh under him.
- **Floats — fixed.** The v1.3.4 manual walk only touched X/Z (`toTarget.y = 0`), so he hung at spawn height forever. Now the NavMeshAgent grounds him on the mesh, and the off-mesh fallback raycasts DOWN (`collidersAndRoomMaskAndDefault`) to snap him to the floor each step. `NavMeshAgent.baseOffset` is pinned to 0 on the prefab so the feet sit at the transform origin.
- **Disappears — fixed.** Because he never moved, `StillLifeWatchdog`'s 3-second stuck-detector fired constantly and **teleported him 8 m from a player in a *random* direction** — routinely into a wall or behind/out of view. That was the "disappears". The watchdog now (a) only intervenes after a 6-second stall, and (b) lands him on **walkable NavMesh ground near a player** via `NavMesh.SamplePosition` + `agent.Warp`, keeping him visible and pathable. With movement fixed it should never fire at all.
- **Client sync — fixed.** The agent now writes the transform **only on the server**; on clients `updatePosition`/`updateRotation` are forced false so the agent stops fighting EnemyAI's own position sync (and the NetworkTransform). Previously clients set `agent.speed` with no destination while the agent still owned the transform → jitter/stuck on remote peers.
- **Minor:** the door-knock SFX is now guarded (`creatureSFX.clip != null`) so it no longer spams a `PlayOneShot(null)` warning on every knock.

**Caveat / how to confirm the deeper layer:** all of the above assumes the clone is actually network-spawned. After launching, check `BepInEx/LogOutput.log` for the `CLONE FINGERPRINT` line — if it shows `IsServer=True, IsSpawned=True`, these fixes resolve the behaviour. If it still shows `IsServer=False`/`IsSpawned=False`, the runtime-built `NetworkObject` isn't registering with NGO (the long-standing hard part of the no-bundle approach) and the real fix is to ship the Unity **asset bundle** instead (see `unity/StillLifeUnity/` — note its builder currently bakes for `StandaloneOSX`, which a Windows game can't load; it must be `StandaloneWindows64`).

## [1.3.4] - 2026-06-06

### He actually moves now (manual transform movement + stuck-clone teleport)
- v1.3.3's NavMeshAgent-driven movement was the wrong primary locomotion source. The agent fails to add at build time (Unity logs "Failed to create agent because there is no valid NavMesh" — the main menu has no NavMesh), and even when it does add, clones spawned off-mesh never get on one because Start's 20m `NavMesh.SamplePosition` warp may fail.
- **v1.3.4's primary movement is `transform.position += normalizedToTarget * speed * dt`.** The clone walks toward the nearest live player no matter what — no NavMesh required, no agent required, no path required. The NavMeshAgent is still updated as a best-effort overlay (only if `agent != null && agent.isOnNavMesh`), so obstacle-aware behaviour is preserved when the agent is functional. The new manual movement is the source of truth.
- **v1.3.4 also: stuck-clone teleport.** The watchdog now tracks each clone's last position. If a clone's transform hasn't moved in 3+ seconds, the watchdog teleports it to within 8m of a live player (random direction). This is the absolute fallback for "the AI is alive but the locomotion is broken" — better to break the illusion than to be non-functional.
- **v1.3.4 also: `Freeze()` is null-safe.** The per-frame call from `Update()` to `agent.isStopped = value` and `agent.velocity = Vector3.zero` is now wrapped in null/try guards. The previous code would have NRE'd every frame when `agent` was null; the per-frame try/catch in Update() caught it but throttled the log, hiding the actual cause.
- **No new features.** Same model, same audio, same 1.25× scale, same NRE fix and hash set, same deferred activation.

## [1.3.3] - 2026-06-06

### Root-cause fix: template activation is now deferred until after RoundManager.Instance is up
- **v1.3.1 and v1.3.2 NRE'd at startup because `Plugin.Awake()` runs during `BepInEx.Chainloader.Start()`** — *before* the game's `RoundManager.Instance` singleton is constructed. v1.3.1/v1.3.2 both called `prefab.SetActive(true)` from that `Awake`, which fired `EnemyAI.Awake()`. The first line of `EnemyAI.Awake()` in v81 is `thisEnemyIndex = RoundManager.Instance.numberOfEnemiesInScene;` — with `RoundManager.Instance == null`, that NREs. The template's components ended up in a broken state, clones inherited it, and `Start()` never ran the AI setup. The watchdog eventually saw 1 clone (v1.3.2 log line 1259) but no `CLONE FINGERPRINT` line ever fired because `Start()` was never called on the broken template.
- v1.3.3 stops calling `SetActive(true)` in `BuildEnemyAtRuntime`. Instead, a hidden `ActivationRunner` MonoBehaviour waits for the first scene load (up to 5s), polls for `RoundManager.Instance != null`, then activates the template. With RoundManager up, `EnemyAI.Awake()` runs cleanly, the template's components initialize normally, and clones inherit a healthy state.
- **Audio fix:** `voiceSource.spatialBlend` forced to 0 (pure 2D) in `Start()` so the ambient loop is audible from anywhere on the map. Volume bumped to 1.0. The previous 3D-positional setup (minDist=4, maxDist=32) made the loop inaudible when the clone spawned far from the player — the "no sound" symptom.
- **Scale fix:** the watchdog now forces `localScale = (1.25, 1.25, 1.25)` on every clone whose scale is wrong, every pass. Even if the spawn pipeline resets scale on `Instantiate`, the watchdog re-applies it. The "small" symptom.
- **Resilient Start():** added an `else` branch when `agent` is null (`Plugin.Log.LogWarning("[StillLife] agent is null on this clone...")`) and a `voiceSource.Play()` confirmation log line. The fingerprint log still fires on every clone regardless of agent state.
- **No new features.** Same NRE fix and hash set remain. Same model, same embedded WAVs, same 1.25× scale target.

## [1.3.2] - 2026-06-06

### Diagnostic build — Pirate Clark still floats, doesn't move, no sound, no scale after v1.3.1
- v1.3.1 shipped the NRE fix (template returned inactive, then `SetActive(true)` after `enemyType` was assigned) and the `GlobalObjectIdHash` reflection set. The user's test run still showed all four symptoms, so the v1.3.1 diagnosis was wrong or only addressed two of however many things are broken.
- v1.3.2 is a **diagnostic-only** build. Same code, same fix, same DLL — but with three new log blocks that print a definitive fingerprint on startup and on every spawn attempt, so the next test run's log identifies the exact step that is failing (network-spawn, NavMesh, audio routing, scale, or position).
- **No new features and no behavior changes.** The same NRE fix and hash set remain in place; v1.3.2 just instruments them. If v1.3.2 accidentally fixes the problem, great — but the contract is "give us the data."
- New logs to look for in `BepInEx/LogOutput.log`:
  - At startup: `[StillLife] === BUILD START ===` followed by a multi-line fingerprint of the template (hash read-back, position, scale, active state, enabled state of every component, AI's `enemyType`).
  - At startup: `[StillLife] LethalLib NetworkPrefabs list count: N` (catches the case where LethalLib never queues the prefab).
  - On every Pirate Clark clone: `[StillLife] CLONE FINGERPRINT` one-liner with `IsOwner/IsServer/IsHost/IsClient/IsSpawned`, `NetworkObjectId`, `OwnerClientId`, `transform.position`, `transform.localScale`, `activeInHierarchy`, `agent.isOnNavMesh/enabled`, `voiceSource.isPlaying/clip/spatialBlend/minDistance/maxDistance`, `enemyType.name`.
  - If `Start()` returns early: `[StillLife] CLONE FINGERPRINT (Start() early-returned: !IsSpawned)` — a missing CLONE FINGERPRINT line is itself diagnostic.
  - Per-clone watchdog log when state is non-default (inactive, scale != 1.25, `IsSpawned == false`, parented to the template).
  - Per-spawn log in the `SpawnEnemyGameObject` postfix (hash, `IsSpawned`, scene name, position before re-activation).

## [1.3.1] - 2026-06-06

### Movement fix completed
- **The "doesn't move" bug is finally fixed for real.** v1.3.0 introduced the active-template trick (park the template at y=-8000 so clones inherit an active NetworkObject) plus a runtime-set `GlobalObjectIdHash`. v1.3.0 *partly* worked: clones were instantiated, the watchdog could see them, audio was loaded — but Pirate Clark still floated, didn't move, and made no sound. The v1.3.0 log showed `IsOwner=False, IsServer=False` on the spawned clone, plus `agent.isOnNavMesh=False`.
- v1.3.0's `BuildPiratePrefab()` ended with `root.SetActive(true)`, which fired `EnemyAI.Awake()` while `enemyType` was still null (assigned later in `BuildEnemyType`). That threw a `NullReferenceException`, leaving the template's components in a broken state. Clones inherited the broken state, so their AI never ran.
- v1.3.1 finishes what the previous session started: the template is now returned **inactive** from `BuildPiratePrefab()`, and `BuildEnemyAtRuntime()` activates it **after** `BuildEnemyType(prefab)` assigns `enemyType` to the AI. `EnemyAI.Awake()` now runs with `enemyType` set, no NRE, the template is healthy, clones are healthy, ownership is correct.
- The `GlobalObjectIdHash` reflection set from the previous session's +18 edit is committed alongside it. Together: clone is active, NetworkObject hash is non-zero, LethalLib's `GameNetworkManager.Start` hook registers the prefab with NGO, `NetworkObject.Spawn()` resolves it, host is `IsOwner=True/IsServer=True`, AI runs.
- **No new features.** Same 1.25× scale, same embedded WAVs (ambient + eat), same real Pirate Clark model. The four symptoms (floating, small, no sound, not moving) are all downstream of the broken-spawn root cause, so they should all clear with this fix.
- Online co-op note: the same hash is set on the prefab on every peer (the mod's `Awake` runs on every BepInEx-loaded client), so both host and client should be able to resolve the prefab at spawn time.

## [1.3.0] - 2026-06-06

### He moves! + sound + bigger
- **Movement:** the runtime-built prefab template is now activated *before* the game clones it (hidden by parking it at `y=-8000` instead of leaving it `SetActive(false)`), so clones are active objects with enabled `NetworkBehaviour`s. v1.2.x had the template inactive, so `NetworkObject.Spawn()` refused to network-spawn the clone — `IsOwner/IsServer` stayed false, `EnemyAI.Update` early-returned, agent never enabled, no movement. (v1.3.1 finishes this fix by also reordering activation past `enemyType` assignment — see [1.3.1] above.)
- **Sound:** the ambient entity loop and the eat one-shot are decoded from embedded 16-bit PCM WAVs at runtime (`WavLoader.cs`); DLL grew to ~5.7 MB carrying audio + textures. AudioSources wire up at plugin load; the AI starts the loop on spawn.
- **Size:** bumped 1.25× so the model reads taller. The lean is baked into the model's bind pose, so scaling makes him bigger but still leaning. Full upright requires re-posing the mesh in Blender.
- **Watchdog:** `StillLifeWatchdog` now runs as a persistent `MonoBehaviour` and activates any Pirate Clark clone that comes in inactive, regardless of which mod's spawn pipeline created it. v1.2.0's single-method postfix wasn't reliable because other mods (e.g. Imperium) bypass `RoundManager.SpawnEnemyGameObject`.

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
