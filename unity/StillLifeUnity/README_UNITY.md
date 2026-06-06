# StillLife — Unity bundle project (one-click)

This project bakes the `stilllife` asset bundle the mod DLL loads. It's set up so
the only manual steps are: install Unity, drop in the game's DLLs, click once.

## Steps

1. **Install Unity 2022.3.9f1** (free, Personal). Use Unity Hub → Installs → the
   exact version `2022.3.9f1` (Lethal Company's version). Include the
   *Windows Build Support* module.

2. **Open this folder** (`StillLifeUnity/`) as a project in that Unity version.
   First open takes a minute (it pulls the Netcode + HDRP packages from
   `Packages/manifest.json`).

3. **Add the game's DLLs** — see `Assets/Plugins/PUT_GAME_DLLS_HERE.txt`.
   Drop a (publicized) `Assembly-CSharp.dll` into `Assets/Plugins/`. Wait for the
   Console to recompile with no red errors.

4. **Click the menu: `StillLife ▸ Build Everything`.**
   It auto-creates the prefab, the `StillLifeEnemy` EnemyType, the terminal scan
   entry, assigns them to the `stilllife` bundle, and bakes it.

5. **Grab `AssetBundles/stilllife`** and drop it next to the mod DLL:
   ```
   Lethal Company/BepInEx/plugins/StillLife/
   ├── com.yourname.stilllife.dll
   └── stilllife            ← this file
   ```

That's it — launch the game; the Still Life is registered to spawn.

## What the builder makes (so you can tweak)
- `Assets/StillLife/StillLifeEnemy.prefab` — model + NavMeshAgent + NetworkObject
  + NetworkTransform + AudioSource + a trigger hitbox child with
  `EnemyAICollisionDetect`. (The `StillLifeAI` script is added by the mod DLL at
  runtime, so this project never needs the mod's source.)
- `Assets/StillLife/StillLifeEnemy.asset` — the `EnemyType` (spawn weights, power
  level, can-die, etc.). Edit values here, re-run *Bake AssetBundle*.
- `Assets/StillLife/StillLifeFile.asset` / `StillLifeKeyword.asset` — the bestiary
  scan entry.
- `Assets/StillLife/*.mat` — HDRP/Lit materials (foam / innards / eyes).

The two sub-steps (`1. Build Prefab + EnemyType`, `2. Bake AssetBundle`) are also
on the menu if you want to iterate on just one half.

## If something's off
- **"EnemyType not found" in Console** → you're missing game DLLs; see the
  PUT_GAME_DLLS_HERE note's troubleshooting list.
- **Materials look flat** → the project must be HDRP (it is, via the package). If
  you started from a non-HDRP template, the builder logs a warning and leaves the
  imported materials; switch the project to HDRP or re-assign shaders.
- **Animations** → none are wired (the model isn't rigged). The enemy still moves
  via NavMeshAgent. To animate the freeze/grab/knock states, rig the model and add
  an Animator with `frozen` (bool), `grab` (trigger), `knock` (trigger) params.
