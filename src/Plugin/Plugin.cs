using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LethalLib.Modules;
using StillLife.Behaviours;
using UnityEngine;

namespace StillLife;

[BepInPlugin("com.TESTYEE-09.lethalpirateclark", "LethalPirateClark", "1.0.2")]
[BepInDependency(LethalLib.Plugin.ModGUID)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "com.TESTYEE-09.lethalpirateclark";
    public const string Name = "LethalPirateClark";
    public const string Version = "1.0.2";

    internal static ManualLogSource Log = null!;

    // Tunables exposed in the BepInEx config file.
    internal static ConfigEntry<int> SpawnWeight = null!;
    internal static ConfigEntry<int> SpawnMaxCount = null!;
    internal static ConfigEntry<float> MoveSpeed = null!;
    internal static ConfigEntry<bool> ConversionEnabled = null!;
    internal static ConfigEntry<int> MaxStillLives = null!;

    // Tracks how many Still Lifes are alive so player-conversion can't snowball
    // the level into a swarm. Maintained by the StillLifeAI lifecycle.
    internal static int LiveStillLives;

    private readonly Harmony _harmony = new(Guid);

    private void Awake()
    {
        Log = Logger;

        SpawnWeight = Config.Bind("Spawn", "Rarity", 1000,
            "Relative spawn weight on indoor levels. Higher = more common. " +
            "Bumped to 1000 for v81 testing — set to 30-50 for a 'feels rare' experience. " +
            "Maximum useful value is around 1000 (game caps it internally).");
        SpawnMaxCount = Config.Bind("Spawn", "MaxCount", 8,
            "Hard cap on how many Pirate Clarks can be alive at once on a level. " +
            "Bumped from 4 to 8 for testing — set to 1 for a 'one at a time' experience.");
        MoveSpeed = Config.Bind("Behaviour", "MoveSpeed", 3.2f,
            "Base movement speed (m/s) when unobserved. Ramps up the longer it goes unseen.");
        ConversionEnabled = Config.Bind("Conversion", "Enabled", true,
            "Phase 2: when the Still Life kills a player, the corpse rises as a new Still Life.");
        MaxStillLives = Config.Bind("Conversion", "MaxAlive", 4,
            "Hard cap on simultaneous Still Lifes so conversions can't snowball endlessly.");

        LoadAssetsAndRegister();

        _harmony.PatchAll(Assembly.GetExecutingAssembly());
        Log.LogInfo($"{Name} v{Version} loaded.");
    }

    private void LoadAssetsAndRegister()
    {
        // Asset bundle is built in the Unity Editor (see README) and shipped next
        // to this DLL. Must contain an EnemyType SO "StillLifeEnemy" plus its
        // prefab and the bestiary TerminalNode/Keyword.
        string dir = Path.GetDirectoryName(Info.Location)!;
        string bundlePath = Path.Combine(dir, "stilllife");
        var bundle = AssetBundle.LoadFromFile(bundlePath);
        if (bundle == null)
        {
            Log.LogError($"Could not load asset bundle at '{bundlePath}'. " +
                         "Enemy will NOT spawn. Build it in Unity first.");
            return;
        }

        var enemy = bundle.LoadAsset<EnemyType>("StillLifeEnemy");
        var node = bundle.LoadAsset<TerminalNode>("StillLifeFile");
        var keyword = bundle.LoadAsset<TerminalKeyword>("StillLifeKeyword");

        if (enemy == null)
        {
            Log.LogError("Bundle loaded but 'StillLifeEnemy' EnemyType was not found.");
            return;
        }

        // --- RUNTIME OVERRIDE of EnemyType fields ---
        // The EnemyType is serialized into the bundle by the Mac Unity build
        // (because we don't have Windows Build Support). When the Windows
        // game deserializes it, the type tree hash mismatches and any field
        // whose type signature disagrees between Mac and Windows is silently
        // dropped to its default. The critical one is PowerLevel: if it
        // drops to 0, the enemy takes 0 power budget and is never picked
        // by the spawner's weighted random draw, even with rarity=200.
        //
        // We set every spawn-critical field at runtime via reflection so
        // the values are written against the Windows type tree, not the
        // Mac one. This is robust regardless of bundle build origin.
        Log.LogInfo($"[StillLife] EnemyType loaded: name='{enemy.enemyName}' " +
                   $"PowerLevel={enemy.PowerLevel} MaxCount={enemy.MaxCount} " +
                   $"isOutside={enemy.isOutsideEnemy} isDaytime={enemy.isDaytimeEnemy} " +
                   $"spawningDisabled={enemy.spawningDisabled}");
        ForceEnemyTypeOverrides(enemy);
        Log.LogInfo($"[StillLife] EnemyType AFTER overrides: " +
                   $"PowerLevel={enemy.PowerLevel} MaxCount={enemy.MaxCount} " +
                   $"isOutside={enemy.isOutsideEnemy} isDaytime={enemy.isDaytimeEnemy} " +
                   $"spawningDisabled={enemy.spawningDisabled}");

        // The Unity prefab ships WITHOUT the AI script and WITHOUT the game's
        // EnemyAICollisionDetect script (so the Unity project never needs the
        // game's source). Add both here at load time, link the hitbox's
        // EnemyAICollisionDetect.mainScript back to the AI, and stamp the
        // EnemyType. See StillLifeBuilder.cs for why EnemyAICollisionDetect is
        // added at runtime (Mac-vs-Windows build target).
        var ai = enemy.enemyPrefab.GetComponent<StillLifeAI>()
                 ?? enemy.enemyPrefab.AddComponent<StillLifeAI>();
        ai.enemyType = enemy;
        ai.creatureAnimator = enemy.enemyPrefab.GetComponentInChildren<Animator>();
        ai.creatureSFX = enemy.enemyPrefab.GetComponent<AudioSource>();
        // Find the hitbox child (the one with the trigger collider) and add
        // the game's EnemyAICollisionDetect script to it, linking its
        // mainScript back to the AI.
        var enemyAICollisionT = System.Type.GetType("EnemyAICollisionDetect, Assembly-CSharp");
        if (enemyAICollisionT == null)
        {
            // Fall back: scan loaded assemblies.
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                enemyAICollisionT = asm.GetType("EnemyAICollisionDetect");
                if (enemyAICollisionT != null) break;
            }
        }
        if (enemyAICollisionT != null)
        {
            foreach (var cd in enemy.enemyPrefab.GetComponentsInChildren(enemyAICollisionT, true))
            {
                var mainScriptField = enemyAICollisionT.GetField("mainScript",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (mainScriptField != null) mainScriptField.SetValue(cd, ai);
            }
            // If the prefab doesn't have one yet, add it to the root (Unity
            // is permissive about which GameObject the script lives on).
            if (enemy.enemyPrefab.GetComponentInChildren(enemyAICollisionT, true) == null)
            {
                var cd = enemy.enemyPrefab.AddComponent(enemyAICollisionT);
                var mainScriptField = enemyAICollisionT.GetField("mainScript",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (mainScriptField != null) mainScriptField.SetValue(cd, ai);
                Log.LogInfo("Added EnemyAICollisionDetect to prefab root (no hitbox child was found).");
            }
        }
        else
        {
            Log.LogWarning("EnemyAICollisionDetect type not found — enemy won't detect player collisions. " +
                           "Check that LethalCompany.GameLibs.Steam NuGet version matches the game version.");
        }

        // --- audio (clips baked into the bundle by the Unity builder) ---
        var ambientClip = bundle.LoadAsset<AudioClip>("PC_ambient");
        var eatClip = bundle.LoadAsset<AudioClip>("PC_eat");
        ai.eatClip = eatClip;

        // Looping ambient "normal entity" voice on its own 3D source so the
        // one-shot eat sound (on creatureSFX) never cuts it off.
        var voice = enemy.enemyPrefab.AddComponent<AudioSource>();
        voice.clip = ambientClip;
        voice.loop = true;
        voice.playOnAwake = true;
        voice.spatialBlend = 1f;          // fully 3D/positional
        voice.minDistance = 3f;
        voice.maxDistance = 35f;
        voice.rolloffMode = AudioRolloffMode.Linear;
        voice.volume = 0.9f;
        ai.voiceSource = voice;
        if (ambientClip == null || eatClip == null)
            Log.LogWarning("PC_ambient/PC_eat not found in bundle — rebuild the bundle with the audio added.");

        // Register the enemy. Wrap in try/catch so if any single API call
        // has been renamed in the current game version, we get a clear
        // BepInEx log line instead of the whole mod failing to load silently.
        try
        {
            // Network prefab registration. The Netcode-for-GameObjects API
            // changed between versions: RegisterNetworkPrefab (older) vs
            // AddNetworkPrefab via NetworkManager (newer). LethalLib's
            // RegisterEnemy also handles this internally, so the explicit
            // call here is best-effort and not required.
            try
            {
                NetworkPrefabs.RegisterNetworkPrefab(enemy.enemyPrefab);
            }
            catch (System.Exception npEx)
            {
                Log.LogWarning($"NetworkPrefabs.RegisterNetworkPrefab failed (non-fatal, LethalLib may handle it): {npEx.Message}");
            }

            Enemies.RegisterEnemy(
                enemy,
                SpawnWeight.Value,
                Levels.LevelTypes.All,
                Enemies.SpawnType.Default,
                node,
                keyword);

            Log.LogInfo($"Registered enemy 'The Still Life' with spawn weight {SpawnWeight.Value} and max count {SpawnMaxCount.Value}.");
        }
        catch (System.Exception regEx)
        {
            Log.LogError($"Failed to register 'The Still Life' enemy: {regEx.GetType().Name}: {regEx.Message}");
            Log.LogError($"Stack: {regEx.StackTrace}");
            throw;  // Re-throw so Awake fails loudly instead of silently.
        }
    }

    // Force-override the spawn-critical fields on an EnemyType at runtime.
    // The Mac-built bundle can have its serialized field values silently
    // dropped when the Windows game deserializes it (type-tree hash mismatch).
    // Setting the fields at runtime via reflection writes them against the
    // Windows type tree, so they persist into the spawn pool regardless.
    //
    // We use BOTH the publicised fields (when the publicised NuGet has the
    // right field names) AND reflection fallbacks (when the publicised NuGet
    // is out of date with the actual game version), so this is robust across
    // game updates.
    private static void ForceEnemyTypeOverrides(EnemyType enemy)
    {
        // PowerLevel: most critical. If this is 0, the enemy takes 0 power
        // budget and is never picked. Force to 1.
        SafeSetField(enemy, "PowerLevel", 1f, "float");
        SafeSetField(enemy, "MaxCount", SpawnMaxCount.Value, "int");
        // probabilityCurve: a flat curve means equal probability across all
        // hours of the day. Default if missing would be a null curve, which
        // can also silently disable the enemy.
        SafeSetField(enemy, "probabilityCurve", new AnimationCurve(
            new Keyframe(0f, 1f), new Keyframe(1f, 1f)), "AnimationCurve");
        SafeSetField(enemy, "numberSpawnedFalloff", new AnimationCurve(
            new Keyframe(0f, 0f), new Keyframe(1f, 1f)), "AnimationCurve");
        SafeSetField(enemy, "useNumberSpawnedFalloff", false, "bool");
        // Spawn flags: must be indoor-only, not daytime.
        SafeSetField(enemy, "isOutsideEnemy", false, "bool");
        SafeSetField(enemy, "isDaytimeEnemy", false, "bool");
        SafeSetField(enemy, "spawningDisabled", false, "bool");
        // Combat config: must be killable, destroyable, stunnable. The Mac
        // bundle's defaults would let the game treat this as invincible.
        SafeSetField(enemy, "canDie", true, "bool");
        SafeSetField(enemy, "canBeDestroyed", true, "bool");
        SafeSetField(enemy, "canBeStunned", true, "bool");
        SafeSetField(enemy, "destroyOnDeath", false, "bool");
        SafeSetField(enemy, "stunTimeMultiplier", 1f, "float");
        SafeSetField(enemy, "stunGameDifficultyMultiplier", 1f, "float");
        SafeSetField(enemy, "loudnessMultiplier", 1f, "float");
    }

    // Reflection-safe field setter. Tries the typed property first (works
    // when the publicised NuGet matches the actual game). Falls back to
    // GetField with BindingFlags.NonPublic (works for private fields). Logs
    // and skips anything it can't find.
    private static void SafeSetField(object obj, string name, object value, string typeName)
    {
        var t = obj.GetType();
        // Try the property first (publicised fields become properties).
        var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (prop != null && prop.CanWrite)
        {
            try { prop.SetValue(obj, value); return; }
            catch (System.Exception ex) { Log.LogWarning($"[StillLife] Set property {name} failed: {ex.Message}"); }
        }
        // Fall back to the underlying field.
        var field = t.GetField(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null)
        {
            // Not an error — just means this field doesn't exist on this
            // game version. Skip silently.
            return;
        }
        try { field.SetValue(obj, value); }
        catch (System.Exception ex)
        {
            Log.LogWarning($"[StillLife] Set field {name} ({typeName}) failed: {ex.Message}");
        }
    }
}
