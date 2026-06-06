# Building the `stilllife` asset bundle

The C# DLL is useless without a bundle that contains the enemy's `EnemyType`,
its prefab, and the bestiary terminal entry. This half MUST be done in the
Unity Editor — there is no command-line shortcut, because asset bundles are
baked binary Unity serialization.

## 1. Editor setup
- Install **Unity 2022.3.9f1** (the exact version Lethal Company ships on; other
  2022.3.x patch versions usually work but can cause shader/serialization warnings).
- New 3D (Built-in Render Pipeline) project.
- Import these from the Asset Store / package manager or copy from a reference
  modding template:
  - **Unity Netcode for GameObjects** (matches the game's version)
  - The **publicized `Assembly-CSharp.dll`** from your game install
    (`Lethal Company/Lethal Company_Data/Managed/`) plus the game's Unity DLLs,
    dropped in `Assets/Plugins/`. These let the editor see `EnemyAI`, `EnemyType`,
    `TerminalNode`, etc., so the prefab can reference your `StillLifeAI` script.

## 2. Create the prefab
1. Model the creature (or use a placeholder capsule) → add a skinned mesh.
2. Add components: `NavMeshAgent`, `NetworkObject`, `NetworkTransform`,
   `Animator` (params: `frozen` bool, `grab` trigger, `knock` trigger), an `AudioSource`,
   and a `Collider` set as trigger on a child for the player-contact hitbox.
3. Add the `EnemyAICollisionDetect` script (from game DLL) to the hitbox child
   and link it back to the root AI.
4. Save as a prefab named `StillLifeEnemyPrefab`.
   - Do NOT add `StillLifeAI` here if the DLL adds it at runtime (it does, in
     `Plugin.LoadAssetsAndRegister`). Adding it in both places is fine too as
     long as it's the same type — but pick one to avoid duplicates.

## 3. Create the EnemyType
- Right-click → Create → (Lethal Company) EnemyType, OR create a generic
  ScriptableObject of type `EnemyType`.
- Name the asset **`StillLifeEnemy`** (must match `bundle.LoadAsset<EnemyType>("StillLifeEnemy")`).
- Fields: assign `enemyPrefab = StillLifeEnemyPrefab`, set `enemyName`, `isDaytimeEnemy = false`,
  `isOutsideEnemy = false`, `PowerLevel`, `maxCount`, `probabilityCurve`, etc.

## 4. Terminal / bestiary entry (optional but nice)
- Create a `TerminalKeyword` asset named **`StillLifeKeyword`**.
- Create a `TerminalNode` asset named **`StillLifeFile`** with the scan log text.
- These names match the `LoadAsset` calls in `Plugin.cs`.

## 5. Mark assets for the bundle
- Select `StillLifeEnemy`, the prefab, and the terminal assets.
- In the Inspector bottom bar, set **AssetBundle = `stilllife`** (lowercase).

## 6. Build the bundle
Add this editor script (`Assets/Editor/BuildBundles.cs`):

```csharp
using UnityEditor;
using System.IO;

public static class BuildBundles
{
    [MenuItem("Modding/Build AssetBundles")]
    public static void Build()
    {
        string outDir = "AssetBundles";
        Directory.CreateDirectory(outDir);
        BuildPipeline.BuildAssetBundles(outDir,
            BuildAssetBundleOptions.None, BuildTarget.StandaloneWindows64);
    }
}
```

Run **Modding → Build AssetBundles**. Grab `AssetBundles/stilllife`
(the file with no extension) — that is the bundle the DLL loads.

## 7. Ship it
Place `stilllife` next to `com.yourname.stilllife.dll` in
`BepInEx/plugins/StillLife/`. Both files load together.
