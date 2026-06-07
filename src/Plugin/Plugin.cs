using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LethalLib.Modules;
using StillLife.Behaviours;
using UnityEngine;
using UnityEngine.AI;

namespace StillLife;

[BepInPlugin("com.TESTYEE-09.lethalpirateclark", "LethalPirateClark", "2.0.0")]
[BepInDependency(LethalLib.Plugin.ModGUID)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "com.TESTYEE-09.lethalpirateclark";
    public const string Name = "LethalPirateClark";
    public const string Version = "2.0.0";

    internal static ManualLogSource Log = null!;

    // Tunables exposed in the BepInEx config file.
    internal static ConfigEntry<int> SpawnWeight = null!;
    internal static ConfigEntry<int> SpawnMaxCount = null!;
    internal static ConfigEntry<float> MoveSpeed = null!;
    internal static ConfigEntry<bool> FreezeWhenWatched = null!;
    internal static ConfigEntry<bool> ConversionEnabled = null!;
    internal static ConfigEntry<int> MaxStillLives = null!;

    // Tracks how many Still Lifes are alive so player-conversion can't snowball
    // the level into a swarm. Maintained by the StillLifeAI lifecycle.
    internal static int LiveStillLives;

    // Held so the bundle (and the prefab/EnemyType assets it owns) stay loaded
    // for the whole session. Never unloaded.
    private static AssetBundle? _bundle;

    private readonly Harmony _harmony = new(Guid);

    private void Awake()
    {
        Log = Logger;

        // v2.0.0 co-op fix: run the Netcode-weaver-injected RPC initializers.
        // The patcher (NetcodePatcher MSBuild SDK) marks them with
        // [RuntimeInitializeOnLoadMethod], which Unity would normally call on
        // class load — but our assembly isn't managed by Unity, so we invoke
        // them once here. Without this, StillLifeAI's ClientRpcs are never
        // registered and never reach remote clients. Guarded so a missing
        // weaver (e.g. an unpatched debug build) can't break plugin load.
        InitializeNetworkRpcs();

        SpawnWeight = Config.Bind("Spawn", "Rarity", 40,
            "Relative spawn weight on indoor levels. Higher = more common. " +
            "Default 40 reads as 'an uncommon scare'. Raise toward 200-1000 to " +
            "test/encounter him constantly (the game caps the weight internally).");
        SpawnMaxCount = Config.Bind("Spawn", "MaxCount", 1,
            "Hard cap on how many Pirate Clarks can be alive at once on a level. " +
            "Default 1 ('one at a time'). Raise for a swarm. Note Phase-2 " +
            "conversions are bounded separately by Conversion.MaxAlive.");
        MoveSpeed = Config.Bind("Behaviour", "MoveSpeed", 3.2f,
            "Base movement speed (m/s) when unobserved. Ramps up the longer it goes unseen.");
        FreezeWhenWatched = Config.Bind("Behaviour", "FreezeWhenWatched", false,
            "If true, Pirate Clark freezes while any player looks at him (classic 'Still Life'). " +
            "If false (default), he keeps advancing even while watched — like the Backrooms movie.");
        ConversionEnabled = Config.Bind("Conversion", "Enabled", true,
            "Phase 2: when the Still Life kills a player, the corpse rises as a new Still Life.");
        MaxStillLives = Config.Bind("Conversion", "MaxAlive", 4,
            "Hard cap on simultaneous Still Lifes so conversions can't snowball endlessly.");

        try
        {
            LoadAssetsAndRegister();
        }
        catch (System.Exception ex)
        {
            Log.LogError($"[StillLife] LoadAssetsAndRegister failed: {ex.GetType().Name}: {ex.Message}");
            Log.LogError($"[StillLife] Stack: {ex.StackTrace}");
            // Don't re-throw — let the rest of BepInEx keep working.
        }

        _harmony.PatchAll(Assembly.GetExecutingAssembly());

        // Safety-net watchdog: recovers any clone that ends up stuck/off-mesh.
        // With the bundle prefab this rarely has anything to do (clones spawn
        // active and on the NavMesh), but it's cheap insurance.
        var watchdogGo = new GameObject("StillLifeWatchdog") { hideFlags = HideFlags.HideAndDontSave };
        UnityEngine.Object.DontDestroyOnLoad(watchdogGo);
        watchdogGo.AddComponent<StillLifeWatchdog>();

        Log.LogInfo($"{Name} v{Version} loaded.");
    }

    // Invoke every [RuntimeInitializeOnLoadMethod] in this assembly exactly
    // once. The Netcode weaver injects one such method per NetworkBehaviour
    // (InitializeRPCS_*) to register its RPC handlers with NGO's static
    // dispatch tables. See NetcodePatcher README. Runs only once (Awake).
    private static void InitializeNetworkRpcs()
    {
        try
        {
            var types = Assembly.GetExecutingAssembly().GetTypes();
            int invoked = 0;
            foreach (var type in types)
            {
                var methods = type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance |
                                              BindingFlags.Static | BindingFlags.Public);
                foreach (var method in methods)
                {
                    if (method.GetCustomAttributes(typeof(RuntimeInitializeOnLoadMethodAttribute), false).Length > 0)
                    {
                        method.Invoke(null, null);
                        invoked++;
                    }
                }
            }
            Log.LogInfo($"[StillLife] Netcode RPC init: invoked {invoked} weaver method(s).");
            if (invoked == 0)
                Log.LogWarning("[StillLife] No RPC initializers found — DLL may be unpatched. " +
                    "Co-op RPCs (grab/eat/state) will NOT reach remote clients.");
        }
        catch (System.Exception ex)
        {
            Log.LogError($"[StillLife] Netcode RPC init failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // v1.4.0: load the enemy from the Unity-built asset bundle instead of
    // building the prefab procedurally at runtime. The bundle's NetworkObject
    // carries a GlobalObjectIdHash baked by the editor (a stable, valid hash
    // that NGO can resolve) — the thing the runtime reflection-set could never
    // do reliably, which is why every 1.0.x–1.3.x build spawned a clone that
    // was never truly network-spawned (IsServer=False) and so floated, never
    // moved, and got cleaned up ("disappeared").
    private void LoadAssetsAndRegister()
    {
        Log.LogInfo("[StillLife] === Loading Pirate Clark from asset bundle ===");

        // The bundle ships next to this DLL (BepInEx/plugins/.../stilllife).
        string dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        string bundlePath = Path.Combine(dllDir, "stilllife");
        if (!File.Exists(bundlePath))
        {
            Log.LogError($"[StillLife] Asset bundle not found at '{bundlePath}'. " +
                "The 'stilllife' file must sit next to the mod DLL. Enemy NOT registered.");
            return;
        }

        _bundle = AssetBundle.LoadFromFile(bundlePath);
        if (_bundle == null)
        {
            Log.LogError($"[StillLife] AssetBundle.LoadFromFile returned NULL for '{bundlePath}'. " +
                "The bundle is corrupt or was built for the wrong platform (it must be StandaloneWindows64). " +
                "Enemy NOT registered.");
            return;
        }
        Log.LogInfo($"[StillLife] Bundle loaded. Assets: {string.Join(", ", _bundle.GetAllAssetNames())}");

        // The EnemyType ScriptableObject carries every spawn-critical field,
        // baked in the editor against the real game types (no runtime guessing).
        var enemy = _bundle.LoadAsset<EnemyType>("StillLifeEnemy")
                    ?? _bundle.LoadAllAssets<EnemyType>().FirstOrDefault();
        if (enemy == null)
        {
            Log.LogError("[StillLife] No EnemyType found in the bundle. Enemy NOT registered.");
            return;
        }

        var prefab = enemy.enemyPrefab;
        if (prefab == null)
        {
            Log.LogError("[StillLife] EnemyType.enemyPrefab is null in the bundle. Enemy NOT registered.");
            return;
        }

        // Honour the config cap (the bundle bakes a default of 4).
        TrySetField(enemy, "MaxCount", SpawnMaxCount.Value);

        PreparePrefab(prefab, enemy);

        // Register the network prefab so NGO can resolve clones at spawn time.
        try
        {
            NetworkPrefabs.RegisterNetworkPrefab(prefab);
        }
        catch (System.Exception npEx)
        {
            Log.LogWarning($"[StillLife] NetworkPrefabs.RegisterNetworkPrefab failed (non-fatal): {npEx.Message}");
        }

        // v2.0.0: wire the bestiary/terminal scan entry baked into the bundle
        // so "Pirate Clark" shows up in the terminal's bestiary after a scan.
        // Optional — if the assets are missing the enemy still spawns fine.
        var infoNode = _bundle.LoadAsset<TerminalNode>("StillLifeFile");
        var infoKeyword = _bundle.LoadAsset<TerminalKeyword>("StillLifeKeyword");

        Enemies.RegisterEnemy(
            enemy,
            SpawnWeight.Value,
            Levels.LevelTypes.All,
            Enemies.SpawnType.Default,
            infoNode,
            infoKeyword);

        Log.LogInfo($"[StillLife] Registered '{enemy.enemyName}' from bundle — rarity {SpawnWeight.Value}, " +
            $"max alive {SpawnMaxCount.Value}, bestiary={(infoNode != null ? "yes" : "no")}.");
    }

    // Add the runtime-only components the bundle deliberately leaves out (the
    // mod's own AI script and the game's EnemyAICollisionDetect), and wire up
    // the AI's references. This runs identically on every peer at load, before
    // the prefab is registered or cloned, so all clones share one NetworkBehaviour
    // layout (required for NGO to keep host and clients in sync).
    private void PreparePrefab(GameObject prefab, EnemyType enemy)
    {
        // --- StillLifeAI (an EnemyAI subclass → a NetworkBehaviour) ---
        var ai = prefab.GetComponent<StillLifeAI>();
        if (ai == null) ai = prefab.AddComponent<StillLifeAI>();
        ai.enemyType = enemy;
        ai.creatureAnimator = prefab.GetComponentInChildren<Animator>();

        // NavMeshAgent is baked into the bundle prefab; pin baseOffset to 0 so
        // his feet sit at the transform origin (no floating) and hand the ref to
        // the AI explicitly (EnemyAI also finds it in Start, but be safe).
        var navAgent = prefab.GetComponent<NavMeshAgent>();
        if (navAgent != null)
        {
            navAgent.baseOffset = 0f;
            ai.agent = navAgent;
        }

        // --- Audio: ambient loop on one source, eat one-shot on another ---
        var sources = prefab.GetComponents<AudioSource>();
        AudioSource voice = sources.Length > 0 ? sources[0] : prefab.AddComponent<AudioSource>();
        AudioSource sfx = sources.Length > 1 ? sources[1] : prefab.AddComponent<AudioSource>();

        voice.playOnAwake = false;
        voice.loop = true;
        voice.spatialBlend = 0f;   // 2D — audible anywhere on the map (v1.3.3 intent)
        voice.volume = 1f;
        sfx.playOnAwake = false;
        sfx.spatialBlend = 1f;     // 3D positional one-shots

        foreach (var clip in _bundle!.LoadAllAssets<AudioClip>())
        {
            string n = clip.name.ToLowerInvariant();
            if (n.Contains("ambient")) voice.clip = clip;
            else if (n.Contains("eat")) ai.eatClip = clip;
        }
        ai.voiceSource = voice;
        ai.creatureSFX = sfx;
        TrySetField(ai, "creatureVoice", voice);  // EnemyAI base ref, best-effort

        // --- EnemyAICollisionDetect on the "Collision" child (game script) ---
        var enemyAICollisionT = ResolveType("EnemyAICollisionDetect");
        if (enemyAICollisionT != null)
        {
            Transform colChild = prefab.transform.Find("Collision");
            GameObject hitbox;
            if (colChild != null)
            {
                hitbox = colChild.gameObject;
            }
            else
            {
                hitbox = new GameObject("Collision");
                hitbox.transform.SetParent(prefab.transform, false);
                hitbox.transform.localPosition = new Vector3(0, 1.0f, 0);
                var cap = hitbox.AddComponent<CapsuleCollider>();
                cap.isTrigger = true;
                cap.radius = 0.4f;
                cap.height = 2.0f;
                cap.center = Vector3.zero;
            }
            var existingCd = hitbox.GetComponent(enemyAICollisionT);
            var cd = existingCd != null ? existingCd : hitbox.AddComponent(enemyAICollisionT);
            TrySetField(cd, "mainScript", ai);
        }
        else
        {
            Log.LogError("[StillLife] EnemyAICollisionDetect type not found — enemy won't detect player collisions.");
        }

        Log.LogInfo($"[StillLife] Prefab '{prefab.name}' prepared: StillLifeAI + collision wired, " +
                    $"animator={(ai.creatureAnimator != null ? "set" : "NULL")}, agent={(navAgent != null ? "set" : "NULL")}, " +
                    $"ambient={(voice.clip != null ? "set" : "NULL")}, eat={(ai.eatClip != null ? "set" : "NULL")}.");
    }

    // Resolve a type by simple name, scanning all loaded assemblies.
    // Prefers Assembly-CSharp (the game).
    internal static Type? ResolveType(string simpleName)
    {
        var matches = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .Where(t => t.Name == simpleName)
            .ToList();
        if (matches.Count == 0) return null;
        return matches.FirstOrDefault(t => t.Assembly.GetName().Name == "Assembly-CSharp") ?? matches[0];
    }

    internal static void TrySetField(object obj, string name, object? value)
    {
        if (obj == null || value == null) return;
        var t = obj.GetType();
        var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (f != null) { try { f.SetValue(obj, value); } catch (Exception e) { Log.LogWarning($"[StillLife] Set {name} failed: {e.Message}"); } }
    }

    internal static void TrySetProperty(object obj, string name, object? value)
    {
        if (obj == null || value == null) return;
        var t = obj.GetType();
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (p != null && p.CanWrite) { try { p.SetValue(obj, value, null); } catch (Exception e) { Log.LogWarning($"[StillLife] Set prop {name} failed: {e.Message}"); } }
    }
}
