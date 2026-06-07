using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LethalLib.Modules;
using StillLife.Behaviours;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace StillLife;

[BepInPlugin("com.TESTYEE-09.lethalpirateclark", "LethalPirateClark", "2.1.1")]
[BepInDependency(LethalLib.Plugin.ModGUID)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "com.TESTYEE-09.lethalpirateclark";
    public const string Name = "LethalPirateClark";
    public const string Version = "2.1.1";

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

    // Result of LoadAssetsAndRegister. Set during load; consumed by the
    // deferred-attachment coroutine once the game has its singletons up.
    private static GameObject? _prefab;
    private static EnemyType? _enemyType;
    private static bool _registered;

    // Single coroutine driver — created in Awake, lives for the session.
    private static Plugin? _instance;
    private Coroutine? _deferredSetupCo;

    private readonly Harmony _harmony = new(Guid);

    private void Awake()
    {
        Log = Logger;
        _instance = this;

        // v2.0.0 co-op fix: run the Netcode-weaver-injected RPC initializers.
        // The patcher (NetcodePatcher MSBuild SDK) marks them with
        // [RuntimeInitializeOnLoadMethod], which Unity would normally call on
        // class load — but our assembly isn't managed by Unity, so we invoke
        // them once here. Without this, StillLifeAI's ClientRpcs are never
        // registered and never reach remote clients.
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

        // Load the bundle + register the enemy with LethalLib. We can do this
        // synchronously — both the bundle and the EnemyType/enemyPrefab refs are
        // pure managed data and don't trigger any Unity Awake/Start callbacks.
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

        // The StillLifeAI + EnemyAICollisionDetect components are added to the
        // prefab *deferred* — their base classes call RoundManager.Instance /
        // StartOfRound.Instance during Awake, which is null at BepInEx
        // chainloader time. Defer to the first scene load (so the game has
        // constructed its singletons) before AddComponent runs.
        _deferredSetupCo = StartCoroutine(DeferredPrefabSetup());

        _harmony.PatchAll(Assembly.GetExecutingAssembly());

        // Safety-net watchdog: recovers any clone that ends up stuck/off-mesh.
        // With the bundle prefab this rarely has anything to do (clones spawn
        // active and on the NavMesh), but it's cheap insurance.
        var watchdogGo = new GameObject("StillLifeWatchdog") { hideFlags = HideFlags.HideAndDontSave };
        DontDestroyOnLoad(watchdogGo);
        watchdogGo.AddComponent<StillLifeWatchdog>();

        Log.LogInfo($"{Name} v{Version} loaded.");
    }

    // Coroutine: wait for the game's singletons to exist, then attach
    // StillLifeAI + EnemyAICollisionDetect to the bundle prefab. We don't do
    // this in Awake() because EnemyAI.Awake reads RoundManager.Instance, which
    // is null until StartOfRound.Awake fires (long after Plugin.Awake).
    private IEnumerator DeferredPrefabSetup()
    {
        // Wait up to 30s for RoundManager + StartOfRound to come up. The game
        // always constructs these before the first moon scene loads, so this
        // is a safety net more than anything.
        float deadline = Time.realtimeSinceStartup + 30f;
        while (Time.realtimeSinceStartup < deadline)
        {
            if (RoundManager.Instance != null && StartOfRound.Instance != null)
                break;
            yield return null;
        }

        if (RoundManager.Instance == null || StartOfRound.Instance == null)
        {
            Log.LogError("[StillLife] Timed out waiting for RoundManager/StartOfRound. " +
                "StillLifeAI/EnemyAICollisionDetect will not be attached — enemy will not function.");
            yield break;
        }

        if (_prefab == null || _enemyType == null)
        {
            Log.LogError("[StillLife] Deferred setup: prefab or EnemyType is null. " +
                "Bundle load must have failed earlier.");
            yield break;
        }

        try
        {
            PreparePrefab(_prefab, _enemyType);
        }
        catch (System.Exception ex)
        {
            Log.LogError($"[StillLife] Deferred PreparePrefab failed: {ex.GetType().Name}: {ex.Message}");
            Log.LogError($"[StillLife] Stack: {ex.StackTrace}");
        }
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
    // that NGO can resolve). We split this in two phases: load+register (now)
    // and AddComponent (deferred, after RoundManager exists).
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

        // v2.1.1: log a fingerprint of the bundle file on disk so users with a
        // broken install can self-diagnose by comparing it to the known-good
        // value in the release notes. This is what catches the "DLL is v2.1.0
        // but the bundle is from v1.0.0" case (the v1.x bundles are ~50 KB
        // and the v2.x bundles are ~1 MB — a 20x size delta, so even a
        // visual size check in the file explorer is conclusive).
        long bundleSize = new FileInfo(bundlePath).Length;
        string bundleMd5 = ComputeFileMd5(bundlePath);
        Log.LogInfo($"[StillLife] Bundle file: '{bundlePath}' ({bundleSize:N0} bytes, md5={bundleMd5}). " +
            $"Expected for v{Version}: ~1,070,202 bytes, md5=7031579a65ff49856e99f60d90ad68e0. " +
            "If the size/md5 don't match, the 'stilllife' file beside this DLL is from an older " +
            "version of the mod — FULLY UNINSTALL the mod in r2modman and reinstall the v" + Version + " zip.");

        _bundle = AssetBundle.LoadFromFile(bundlePath);
        if (_bundle == null)
        {
            // The two failure modes a Windows user actually hits are:
            //   (a) the bundle was built with an older Unity than the live game
            //       (the common one — every pre-1.4.1 release is broken this way),
            //   (b) the bundle was built for a different target platform (Mac).
            // We log BOTH the file size and the md5 in the message so the user
            // can see at a glance whether their bundle file is too small (case a)
            // or otherwise off, and we tell them exactly how to fix it.
            Log.LogError($"[StillLife] AssetBundle.LoadFromFile returned NULL for '{bundlePath}' " +
                $"({bundleSize:N0} bytes, md5={bundleMd5}). " +
                "Most likely the 'stilllife' file beside this DLL was built for a different " +
                "Unity version (pre-1.4.1 bundles are ~50 KB and built with Unity 2022.3.9f1, " +
                "which the live 2022.3.62 runtime rejects). " +
                "FIX: in r2modman, UNINSTALL this mod (don't just disable), then re-install " +
                "from LethalPirateClark_v" + Version + ".zip so both the DLL and the bundle " +
                "are updated together. Manual install: grab the zip from " +
                "https://github.com/TESTYEE-09/LethalPirateClark/releases/tag/v" + Version + " and " +
                "extract it so 'plugins/<author>-LethalPirateClark/StillLife/stilllife' is replaced. " +
                "Enemy NOT registered.");
            return;
        }
        var assetNames = _bundle.GetAllAssetNames();
        Log.LogInfo($"[StillLife] Bundle loaded. {assetNames.Length} assets: {string.Join(", ", assetNames)}");

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
        TrySetField(enemy, "maxCount", SpawnMaxCount.Value); // legacy field name

        // Critical: park the prefab in DontDestroyOnLoad so it survives scene
        // transitions and NetworkSpawn can find it from any moon. HideAndDontSave
        // keeps it out of saves / scene listings. We deliberately leave it
        // ACTIVE — LethalLib/RoundManager's Instantiate+NetworkSpawn expects
        // an active template, and we suppress component Awake callbacks by
        // AddComponent-ing them ONLY in DeferredPrefabSetup, after
        // RoundManager.Instance is non-null.
        DontDestroyOnLoad(prefab);
        prefab.hideFlags = HideFlags.HideAndDontSave;

        // Register the network prefab so NGO can resolve clones at spawn time.
        try
        {
            LethalLib.Modules.NetworkPrefabs.RegisterNetworkPrefab(prefab);
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

        // Guard against double-registration if Awake is somehow called twice
        // (shouldn't happen with BepInEx but the guard is cheap).
        if (!_registered)
        {
            Enemies.RegisterEnemy(
                enemy,
                SpawnWeight.Value,
                Levels.LevelTypes.All,
                Enemies.SpawnType.Default,
                infoNode,
                infoKeyword);
            _registered = true;
        }

        // Stash for the deferred PreparePrefab pass.
        _prefab = prefab;
        _enemyType = enemy;

        Log.LogInfo($"[StillLife] Registered '{enemy.enemyName}' from bundle — rarity {SpawnWeight.Value}, " +
            $"max alive {SpawnMaxCount.Value}, bestiary={(infoNode != null ? "yes" : "no")}, " +
            $"netObjHash={(prefab.GetComponent<NetworkObject>() != null ? prefab.GetComponent<NetworkObject>().GlobalObjectIdHash.ToString() : "MISSING")}.");
    }

    // Add the runtime-only components the bundle deliberately leaves out (the
    // mod's own AI script and the game's EnemyAICollisionDetect), and wire up
    // the AI's references. This runs from DeferredPrefabSetup() — which has
    // already waited for RoundManager.Instance — so the components' Awake()
    // callbacks see a valid game state.
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
            // The bundle's NavMeshAgent was authored at radius 0.35 / height
            // 1.9. Keep those as-is (model matches).
            ai.agent = navAgent;
        }
        else
        {
            Log.LogWarning("[StillLife] NavMeshAgent missing from bundle prefab. " +
                "Adding a runtime one — movement will be off-mesh fallback only.");
            navAgent = prefab.AddComponent<NavMeshAgent>();
            navAgent.radius = 0.35f;
            navAgent.height = 1.9f;
            navAgent.speed = 3.2f;
            navAgent.acceleration = 12f;
            navAgent.angularSpeed = 240f;
            navAgent.stoppingDistance = 0.6f;
            navAgent.baseOffset = 0f;
            ai.agent = navAgent;
        }

        // --- Audio: ambient loop on one source, eat one-shot on another ---
        var sources = prefab.GetComponents<AudioSource>();
        AudioSource voice = sources.Length > 0 ? sources[0] : prefab.AddComponent<AudioSource>();
        AudioSource sfx = sources.Length > 1 ? sources[1] : prefab.AddComponent<AudioSource>();

        voice.playOnAwake = false;
        voice.loop = true;
        voice.spatialBlend = 0f;   // 2D — audible anywhere on the map
        voice.volume = 1f;
        voice.minDistance = 0f;
        voice.maxDistance = 500f;
        sfx.playOnAwake = false;
        sfx.spatialBlend = 1f;     // 3D positional one-shots
        sfx.loop = false;

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
            // The bundle's Collision child has a trigger CapsuleCollider. That's
            // what EnemyAICollisionDetect reads to find player overlap. But the
            // prefab also needs a SOLID (non-trigger) collider on the root or
            // on the Collision child so the enemy physically blocks players
            // and the world (otherwise the player walks through the body and
            // the enemy walks through walls when the agent is off-mesh).
            // Use a non-trigger CapsuleCollider on the root, sized to the model.
            var bodyCol = prefab.GetComponent<CapsuleCollider>();
            if (bodyCol == null)
            {
                bodyCol = prefab.AddComponent<CapsuleCollider>();
                bodyCol.isTrigger = false;            // solid: blocks player physics
                bodyCol.radius = 0.4f;
                bodyCol.height = 2.0f;
                bodyCol.center = new Vector3(0, 1.0f, 0);
                bodyCol.direction = 1;               // Y-axis
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

    // v2.1.1: md5 of a file, used to fingerprint the bundle on disk so users
    // with broken installs can self-diagnose. Kept inside the class so it can
    // use Plugin.Log if the hashing itself throws.
    internal static string ComputeFileMd5(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = md5.ComputeHash(stream);
            return System.BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
        catch (Exception e)
        {
            return "<md5 failed: " + e.GetType().Name + ">";
        }
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
