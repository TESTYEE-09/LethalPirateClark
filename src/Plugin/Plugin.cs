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

[BepInPlugin("com.TESTYEE-09.lethalpirateclark", "LethalPirateClark", "1.0.1")]
[BepInDependency(LethalLib.Plugin.ModGUID)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "com.TESTYEE-09.lethalpirateclark";
    public const string Name = "LethalPirateClark";
    public const string Version = "1.0.1";

    internal static ManualLogSource Log = null!;

    // Tunables exposed in the BepInEx config file.
    internal static ConfigEntry<int> SpawnWeight = null!;
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

        SpawnWeight = Config.Bind("Spawn", "Rarity", 200,
            "Relative spawn weight on indoor levels. Higher = more common. " +
            "Bumped to 200 for v81 testing — set to 30-50 for a 'feels rare' experience.");
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

            Log.LogInfo($"Registered enemy 'The Still Life' with spawn weight {SpawnWeight.Value}.");
        }
        catch (System.Exception regEx)
        {
            Log.LogError($"Failed to register 'The Still Life' enemy: {regEx.GetType().Name}: {regEx.Message}");
            Log.LogError($"Stack: {regEx.StackTrace}");
            throw;  // Re-throw so Awake fails loudly instead of silently.
        }
    }
}
