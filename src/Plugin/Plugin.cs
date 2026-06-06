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

[BepInPlugin("com.TESTYEE-09.lethalpirateclark", "LethalPirateClark", "1.0.3")]
[BepInDependency(LethalLib.Plugin.ModGUID)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "com.TESTYEE-09.lethalpirateclark";
    public const string Name = "LethalPirateClark";
    public const string Version = "1.0.3";

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

        try
        {
            BuildEnemyAtRuntime();
        }
        catch (System.Exception ex)
        {
            Log.LogError($"[StillLife] BuildEnemyAtRuntime failed: {ex.GetType().Name}: {ex.Message}");
            Log.LogError($"[StillLife] Stack: {ex.StackTrace}");
            // Don't re-throw — the rest of BepInEx can keep working. Just the enemy
            // won't spawn. Better than taking down the whole mod loader.
        }

        _harmony.PatchAll(Assembly.GetExecutingAssembly());
        Log.LogInfo($"{Name} v{Version} loaded.");
    }

    // Build the EnemyType + prefab ENTIRELY in C# at runtime. No asset bundle,
    // no Unity editor required, no Mac-vs-Windows type-tree mismatch. This is
    // the v1.0.3 architecture: the mod is self-contained.
    //
    // Visual: a capsule with a tricorn-shaped hat (cube) and a coat-coloured
    // body. Looks like a placeholder but unrecognizable-as-a-cube. Better
    // than a flat box, worse than the real model. Real model can be added
    // back later via a runtime FBX loader.
    //
    // Audio: silent for now (no clip shipped). The eat SFX is a one-shot on
    // the creatureSFX source; without it, no sound plays. Fine for v1.0.3.
    private void BuildEnemyAtRuntime()
    {
        Log.LogInfo("[StillLife] === Building Pirate Clark at runtime ===");

        // --- 1. Build the visual GameObject (the prefab's source) ---
        var prefab = BuildPiratePrefab();
        Log.LogInfo($"[StillLife] Prefab built: '{prefab.name}' with {prefab.GetComponentsInChildren<Component>(true).Length} components");

        // --- 2. Build the EnemyType ScriptableObject ---
        var enemy = BuildEnemyType(prefab);
        Log.LogInfo($"[StillLife] EnemyType built: '{enemy.enemyName}', PowerLevel={enemy.PowerLevel}, MaxCount={enemy.MaxCount}");

        // --- 3. Register the network prefab (best-effort) ---
        try
        {
            NetworkPrefabs.RegisterNetworkPrefab(prefab);
        }
        catch (System.Exception npEx)
        {
            Log.LogWarning($"[StillLife] NetworkPrefabs.RegisterNetworkPrefab failed (non-fatal): {npEx.Message}");
        }

        // --- 4. Register with LethalLib ---
        // No TerminalNode/TerminalKeyword — they're optional, the enemy still
        // spawns without a bestiary entry. We can add them later if needed.
        Enemies.RegisterEnemy(
            enemy,
            SpawnWeight.Value,
            Levels.LevelTypes.All,
            Enemies.SpawnType.Default,
            null,
            null);

        Log.LogInfo($"[StillLife] Registered '{enemy.enemyName}' — rarity {SpawnWeight.Value}, max alive {SpawnMaxCount.Value}.");
    }

    // Build the visual + functional prefab entirely from primitive GameObjects.
    // Adds: NavMeshAgent, NetworkObject, NetworkTransform, AudioSource, Animator
    // (Unity engine), the AI script, EnemyAICollisionDetect (game script).
    private GameObject BuildPiratePrefab()
    {
        // Root: an empty GameObject (no visible mesh itself).
        var root = new GameObject("StillLifeEnemy");
        root.SetActive(false);  // Hide the template prefab from the scene.
        // NOTE: Do NOT mark as DontDestroyOnLoad — that's the loader's job.

        // Position at origin (the spawner will move the actual instance).
        root.transform.position = Vector3.zero;

        // --- Visual: capsule body + a flat "tricorn" cube on top ---
        // Body: capsule, dark "pirate coat" color
        var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        body.name = "Body";
        UnityEngine.Object.DestroyImmediate(body.GetComponent<Collider>());  // we add our own
        body.transform.SetParent(root.transform, false);
        body.transform.localPosition = new Vector3(0, 1.0f, 0);
        body.transform.localScale = new Vector3(0.5f, 0.6f, 0.5f);  // thinner
        var bodyRenderer = body.GetComponent<Renderer>();
        if (bodyRenderer != null)
        {
            // Try HDRP first (Lethal Company uses HDRP), fall back to Standard.
            Shader shader = Shader.Find("HDRP/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader) { name = "PirateCoatMat" };
            if (shader.name == "HDRP/Lit")
            {
                mat.SetColor("_BaseColor", new Color(0.25f, 0.15f, 0.10f));  // dark brown
                mat.SetFloat("_Smoothness", 0.3f);
            }
            else
            {
                mat.color = new Color(0.25f, 0.15f, 0.10f);
            }
            bodyRenderer.sharedMaterial = mat;
        }

        // Hat: a flattened cube on top of the head, "tricorn" look.
        var hat = GameObject.CreatePrimitive(PrimitiveType.Cube);
        hat.name = "Hat";
        UnityEngine.Object.DestroyImmediate(hat.GetComponent<Collider>());
        hat.transform.SetParent(root.transform, false);
        hat.transform.localPosition = new Vector3(0, 1.85f, 0);
        hat.transform.localScale = new Vector3(0.7f, 0.08f, 0.7f);
        var hatRenderer = hat.GetComponent<Renderer>();
        if (hatRenderer != null)
        {
            Shader shader = Shader.Find("HDRP/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader) { name = "PirateHatMat" };
            if (shader.name == "HDRP/Lit")
            {
                mat.SetColor("_BaseColor", new Color(0.10f, 0.08f, 0.08f));  // near-black
                mat.SetFloat("_Smoothness", 0.1f);
            }
            else mat.color = new Color(0.10f, 0.08f, 0.08f);
            hatRenderer.sharedMaterial = mat;
        }

        // Belt: thin cube at the waist for visual interest.
        var belt = GameObject.CreatePrimitive(PrimitiveType.Cube);
        belt.name = "Belt";
        UnityEngine.Object.DestroyImmediate(belt.GetComponent<Collider>());
        belt.transform.SetParent(root.transform, false);
        belt.transform.localPosition = new Vector3(0, 0.95f, 0);
        belt.transform.localScale = new Vector3(0.55f, 0.06f, 0.55f);
        var beltRenderer = belt.GetComponent<Renderer>();
        if (beltRenderer != null)
        {
            Shader shader = Shader.Find("HDRP/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader) { name = "PirateBeltMat" };
            if (shader.name == "HDRP/Lit")
            {
                mat.SetColor("_BaseColor", new Color(0.4f, 0.3f, 0.2f));
                mat.SetFloat("_Smoothness", 0.5f);
            }
            else mat.color = new Color(0.4f, 0.3f, 0.2f);
            beltRenderer.sharedMaterial = mat;
        }

        // --- Functional components ---

        // NavMeshAgent — how the AI moves around the level.
        var agent = root.AddComponent<NavMeshAgent>();
        agent.radius = 0.35f;
        agent.height = 1.9f;
        agent.speed = 3.2f;
        agent.acceleration = 12f;
        agent.angularSpeed = 240f;
        agent.stoppingDistance = 0.6f;
        agent.areaMask = ~0;

        // AudioSource — for the eat SFX (and future ambient).
        var sfx = root.AddComponent<AudioSource>();
        sfx.playOnAwake = false;
        sfx.spatialBlend = 1f;  // 3D positional

        // NetworkObject — required for netcode (multiplayer).
        var networkObjectT = System.Type.GetType("Unity.Netcode.NetworkObject, Unity.Netcode.Runtime");
        if (networkObjectT != null)
        {
            root.AddComponent(networkObjectT);
        }
        else
        {
            Log.LogWarning("[StillLife] Unity.Netcode.NetworkObject type not found — enemy may not work in multiplayer.");
        }

        // NetworkTransform — syncs position across the network.
        var networkTransformT = System.Type.GetType("Unity.Netcode.Components.NetworkTransform, Unity.Netcode.Components");
        if (networkTransformT != null)
        {
            var nt = root.AddComponent(networkTransformT);
            // Set Interpolate = true for smoother movement, disable scale sync.
            TrySetProperty(nt, "Interpolate", true);
            TrySetProperty(nt, "SyncScaleX", false);
            TrySetProperty(nt, "SyncScaleY", false);
            TrySetProperty(nt, "SyncScaleZ", false);
        }
        else
        {
            Log.LogWarning("[StillLife] Unity.Netcode.Components.NetworkTransform type not found — enemy may not work in multiplayer.");
        }

        // Animator — required by EnemyAI base class, even if no clips.
        // Create a minimal Animator with no controller; the base class won't crash
        // because all our SetTrigger/SetBool calls in StillLifeAI are guarded.
        root.AddComponent<Animator>();

        // The AI script itself.
        var ai = root.AddComponent<StillLifeAI>();
        ai.enemyType = null;  // set below when EnemyType is built
        ai.creatureAnimator = root.GetComponent<Animator>();
        ai.creatureSFX = sfx;

        // EnemyAICollisionDetect — game script, added at runtime so the Mac
        // build target doesn't have to bake it in.
        var enemyAICollisionT = ResolveType("EnemyAICollisionDetect");
        if (enemyAICollisionT != null)
        {
            // Add to a child hitbox so the trigger collider doesn't fight with
            // the NavMeshAgent's body collider.
            var hitbox = new GameObject("Collision");
            hitbox.transform.SetParent(root.transform, false);
            hitbox.transform.localPosition = new Vector3(0, 1.0f, 0);
            var cap = hitbox.AddComponent<CapsuleCollider>();
            cap.isTrigger = true;
            cap.radius = 0.4f;
            cap.height = 2.0f;
            cap.center = Vector3.zero;
            var cd = hitbox.AddComponent(enemyAICollisionT);
            // Link mainScript back to the AI.
            TrySetField(cd, "mainScript", ai);
        }
        else
        {
            Log.LogError("[StillLife] EnemyAICollisionDetect type not found — enemy won't detect player collisions.");
        }

        return root;
    }

    // Build the EnemyType ScriptableObject entirely in code.
    private EnemyType BuildEnemyType(GameObject prefab)
    {
        var enemyTypeT = ResolveType("EnemyType");
        if (enemyTypeT == null)
            throw new InvalidOperationException("EnemyType type not found in any loaded assembly — is LethalLib installed?");

        var enemy = ScriptableObject.CreateInstance(enemyTypeT);
        enemyTypeT.GetField("name", BindingFlags.Public | BindingFlags.Instance)?.SetValue(enemy, "StillLifeEnemy");
        TrySetField(enemy, "enemyName", "Pirate Clark");
        TrySetField(enemy, "enemyPrefab", prefab);
        TrySetField(enemy, "isOutsideEnemy", false);
        TrySetField(enemy, "isDaytimeEnemy", false);
        TrySetField(enemy, "MaxCount", SpawnMaxCount.Value);
        TrySetField(enemy, "PowerLevel", 1f);
        // probabilityCurve: flat (equal probability across all hours of day).
        // Without this, the default null curve can prevent the enemy from
        // being picked at all.
        TrySetField(enemy, "probabilityCurve", new AnimationCurve(
            new Keyframe(0f, 1f), new Keyframe(1f, 1f)));
        TrySetField(enemy, "numberSpawnedFalloff", new AnimationCurve(
            new Keyframe(0f, 0f), new Keyframe(1f, 1f)));
        TrySetField(enemy, "useNumberSpawnedFalloff", false);
        TrySetField(enemy, "spawningDisabled", false);
        TrySetField(enemy, "canDie", true);
        TrySetField(enemy, "canBeDestroyed", true);
        TrySetField(enemy, "canBeStunned", true);
        TrySetField(enemy, "destroyOnDeath", false);
        TrySetField(enemy, "stunTimeMultiplier", 1f);
        TrySetField(enemy, "stunGameDifficultyMultiplier", 1f);
        TrySetField(enemy, "loudnessMultiplier", 1f);

        // Now link the prefab's AI back to this EnemyType.
        var ai = prefab.GetComponent<StillLifeAI>();
        if (ai != null) TrySetField(ai, "enemyType", enemy);

        return (EnemyType)enemy;
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
