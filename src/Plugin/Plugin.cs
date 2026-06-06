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
using UnityEngine;
using UnityEngine.AI;

namespace StillLife;

[BepInPlugin("com.TESTYEE-09.lethalpirateclark", "LethalPirateClark", "1.3.4")]
[BepInDependency(LethalLib.Plugin.ModGUID)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "com.TESTYEE-09.lethalpirateclark";
    public const string Name = "LethalPirateClark";
    public const string Version = "1.3.4";

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

    // v1.3.3: the prefab template is built inactive and activated only after
    // the first scene loads (when RoundManager.Instance is non-null). Until
    // then, this holds the reference so the sceneLoaded callback can pick
    // it up. Set to null after activation completes.
    private GameObject? _templatePendingActivation;
    private bool _templateActivated;

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
        FreezeWhenWatched = Config.Bind("Behaviour", "FreezeWhenWatched", false,
            "If true, Pirate Clark freezes while any player looks at him (classic 'Still Life'). " +
            "If false (default), he keeps advancing even while watched — like the Backrooms movie.");
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

        // Persistent watchdog that activates spawned Pirate Clark instances
        // regardless of which mod's spawn pipeline created them.
        var watchdogGo = new GameObject("StillLifeWatchdog") { hideFlags = HideFlags.HideAndDontSave };
        UnityEngine.Object.DontDestroyOnLoad(watchdogGo);
        watchdogGo.AddComponent<StillLifeWatchdog>();

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

        // --- 2b. Activate the template (DEFERRED to first scene load) ---
        // v1.3.1's mistake: we called SetActive(true) here, in Plugin.Awake(),
        // which runs from BepInEx.Chainloader.Start() — *before* the game's
        // RoundManager.Instance singleton exists. EnemyAI.Awake() does
        // `thisEnemyIndex = RoundManager.Instance.numberOfEnemiesInScene;`
        // (verified by decompiling v81's EnemyAI). With RoundManager.Instance
        // == null, the very first line of EnemyAI.Awake NREs, leaving the
        // template's components in a broken state. Clones inherit the broken
        // state, so Spawn() succeeds but Start() never runs the AI setup.
        //
        // v1.3.3 fix: do NOT call SetActive(true) here. Register a hook on
        // SceneManager.sceneLoaded and activate the template inside the
        // callback (after waiting a frame so RoundManager is up). The
        // template stays inactive until sceneLoaded fires; LethalLib's
        // RegisterNetworkPrefab only needs the NetworkObject + hash to be
        // set, which BuildPiratePrefab already did. Clones come in inactive
        // but the watchdog will activate them after the spawn pipeline puts
        // them in a scene.
        Log.LogInfo("[StillLife] Template build complete. Will activate on first scene load (when RoundManager is up).");
        _templatePendingActivation = prefab;
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoadedActivateTemplate;

        // --- 2c. Startup fingerprint (v1.3.2 diagnostic) ---
        // Read back the template's state so a v1.3.2 log can identify which
        // step of the chain (hash set, LethalLib queue, NGO registration,
        // Netcode Spawn) actually failed. If Pirate Clark is broken in v1.3.1,
        // one of these lines will be obviously wrong.
        Log.LogInfo("[StillLife] === BUILD START ===");
        Log.LogInfo($"[StillLife] template.name='{prefab.name}', activeSelf={prefab.activeSelf}, " +
                    $"activeInHierarchy={prefab.activeInHierarchy}, " +
                    $"pos={prefab.transform.position:F2}, scale={prefab.transform.localScale:F2}");

        // networkObjectT / networkTransformT are local to BuildPiratePrefab;
        // re-resolve them here for the fingerprint. Cheap reflection.
        var fpNetworkObjectT = System.Type.GetType("Unity.Netcode.NetworkObject, Unity.Netcode.Runtime");
        var fpNetworkTransformT = System.Type.GetType("Unity.Netcode.Components.NetworkTransform, Unity.Netcode.Components");
        var templateNetObj = fpNetworkObjectT != null ? prefab.GetComponent(fpNetworkObjectT) : null;
        if (templateNetObj != null)
        {
            // Re-read GlobalObjectIdHash via reflection so we can confirm the
            // set in BuildPiratePrefab() actually stuck. If the value is 0 here,
            // the reflection set is the bug (wrong field, wrong binding flags,
            // or NGO has its own internal cache that overwrote it).
            uint hashRead = 0;
            try
            {
                var f = fpNetworkObjectT.GetField("GlobalObjectIdHash",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null) hashRead = (uint)f.GetValue(templateNetObj);
            }
            catch (Exception ex) { Log.LogWarning($"[StillLife] hash readback failed: {ex.Message}"); }
            Log.LogInfo($"[StillLife] NetworkObject on template: type={templateNetObj.GetType().FullName}, " +
                        $"GlobalObjectIdHash=0x{hashRead:X8} (expected 0x5111C1A4), enabled={((Behaviour)templateNetObj).enabled}");

            // Read IsSpawned on the template (should be false — it's never spawned itself,
            // only cloned). And any prefab hash table that NGO/LethalLib may have populated.
            try
            {
                var isSpawnedProp = fpNetworkObjectT.GetProperty("IsSpawned");
                if (isSpawnedProp != null)
                    Log.LogInfo($"[StillLife] template NetworkObject.IsSpawned={isSpawnedProp.GetValue(templateNetObj)} (false expected).");
            }
            catch (Exception ex) { Log.LogWarning($"[StillLife] IsSpawned read failed: {ex.Message}"); }
        }
        else
        {
            Log.LogWarning("[StillLife] NetworkObject component not present on template.");
        }

        var templateNt = fpNetworkTransformT != null ? prefab.GetComponent(fpNetworkTransformT) : null;
        Log.LogInfo($"[StillLife] NetworkTransform on template: present={templateNt != null}, " +
                    $"enabled={(templateNt is Behaviour b && b.enabled)}");

        var templateAi = prefab.GetComponent<StillLifeAI>();
        if (templateAi != null)
        {
            Log.LogInfo($"[StillLife] StillLifeAI on template: enabled={templateAi.enabled}, " +
                        $"enemyType={(templateAi.enemyType != null ? templateAi.enemyType.enemyName : "NULL")}, " +
                        $"voiceSource={(templateAi.voiceSource != null ? "set" : "NULL")}, " +
                        $"eatClip={(templateAi.eatClip != null ? "set" : "NULL")}");
        }

        // Check that the AI is NOT considered "spawned" on the template, which
        // would make NGO refuse to spawn clones of it.
        if (templateAi != null && templateAi.IsSpawned)
            Log.LogWarning("[StillLife] Template StillLifeAI.IsSpawned is TRUE — NGO may refuse to clone this template.");

        // --- 3. Register the network prefab (best-effort) ---
        try
        {
            NetworkPrefabs.RegisterNetworkPrefab(prefab);
            // After LethalLib queues it, peek at the LethalLib internal list so
            // we know the queue actually accepted our prefab. If the count is 0
            // (or our prefab is not in the list), LethalLib silently dropped it
            // and the next NGO Spawn() will fail.
            try
            {
                var llAsm = System.AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "LethalLib");
                if (llAsm != null)
                {
                    var listField = llAsm.GetType("LethalLib.Modules.NetworkPrefabs")
                        ?.GetField("_networkPrefabs", BindingFlags.Static | BindingFlags.NonPublic);
                    if (listField != null)
                    {
                        var list = listField.GetValue(null) as System.Collections.IList;
                        Log.LogInfo($"[StillLife] LethalLib NetworkPrefabs list count: {list?.Count ?? -1}.");
                    }
                }
            }
            catch (Exception ex) { Log.LogWarning($"[StillLife] LethalLib queue peek failed: {ex.Message}"); }
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

    // Build the visual + functional prefab. v1.1.0 uses the real Pirate Clark
    // model mesh (loaded from the embedded .obj) instead of procedural primitives.
    // Adds: NavMeshAgent, NetworkObject, NetworkTransform, AudioSource, Animator
    // (Unity engine), the AI script, EnemyAICollisionDetect (game script).
    private GameObject BuildPiratePrefab()
    {
        // Root: an empty GameObject. Built while INACTIVE so AddComponent
        // doesn't fire component Awakes mid-construction, then switched ON at
        // the very end (see SetActive(true) before return).
        var root = new GameObject("StillLifeEnemy");
        root.SetActive(false);
        // We must NOT leave the template permanently inactive: Netcode for
        // GameObjects refuses to spawn an object whose NetworkBehaviours are
        // disabled (an inactive GameObject disables them). That was the real
        // "doesn't move" bug — the clone was never truly network-spawned, so
        // IsOwner/IsServer stayed false and EnemyAI.Update early-returned.
        // Instead, hide the active template by parking it far below the map;
        // the game's Instantiate overrides the clone's position to the real
        // spawn point, so this is invisible in play.
        root.transform.position = new Vector3(0f, -8000f, 0f);
        root.transform.localScale = Vector3.one * 1.25f;  // a bit taller/bulkier

        // CRITICAL: this GameObject is our prefab TEMPLATE — the game clones it
        // every time it spawns Pirate Clark. A plain scene GameObject gets
        // destroyed by Unity on the menu->moon scene transition, which left
        // enemyType.enemyPrefab pointing at a destroyed object: LethalLib's
        // Terminal_Start NRE'd on it and RoundManager had nothing to instantiate
        // (Oracle kept *picking* the enemy but it never actually spawned).
        // DontDestroyOnLoad keeps the template alive across scene loads; the
        // hide flags keep it out of the menu scene render + out of saves.
        root.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.DontDestroyOnLoad(root);

        // --- Visual: real Pirate Clark mesh from the embedded .obj ---
        var mesh = ObjMeshLoader.LoadEmbedded("LethalPirateClark.pirate_clark.obj");
        if (mesh == null)
        {
            Log.LogWarning("[StillLife] Embedded mesh failed to load — falling back to a placeholder capsule.");
            mesh = FallbackCapsuleMesh();
        }

        // Add a MeshFilter + MeshRenderer on the root, plus a skinned
        // material. We use HDRP/Lit (Lethal Company's pipeline) if available,
        // else Standard. The model is approximately 2 units tall in its
        // source orientation; we re-orient to standing upright (Y-up) and
        // scale to ~2m tall, matching the other enemies.
        var mf = root.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;

        var mr = root.AddComponent<MeshRenderer>();
        var shader = Shader.Find("HDRP/Lit") ?? Shader.Find("Standard");
        var mat = new Material(shader) { name = "PirateClarkMat" };

        // Load the real PBR maps (embedded PNGs). Albedo carries the coat
        // colours; normal map adds surface detail. Fall back to a flat mustard
        // tint only if the textures fail to load.
        var albedo = TextureLoader.LoadEmbedded("LethalPirateClark.pirate_clark_albedo.png");
        var normal = TextureLoader.LoadEmbedded("LethalPirateClark.pirate_clark_normal.png", linear: true);
        var fallbackMustard = new Color(0.75f, 0.65f, 0.20f);

        if (shader.name == "HDRP/Lit")
        {
            if (albedo != null)
            {
                mat.SetTexture("_BaseColorMap", albedo);
                mat.SetColor("_BaseColor", Color.white);  // white so the texture shows true
            }
            else
            {
                mat.SetColor("_BaseColor", fallbackMustard);
            }
            if (normal != null)
            {
                mat.SetTexture("_NormalMap", normal);
                mat.EnableKeyword("_NORMALMAP");
                mat.SetFloat("_NormalScale", 1f);
            }
            mat.SetFloat("_Smoothness", 0.35f);
        }
        else
        {
            if (albedo != null) { mat.mainTexture = albedo; mat.color = Color.white; }
            else mat.color = fallbackMustard;
        }
        // Find the first material slot to assign to. Some renderers expect
        // an array; the built-in HDRP/Lit material we created has slot 0.
        mr.sharedMaterials = new[] { mat };
        // Allow shadows from this renderer.
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        mr.receiveShadows = true;

        // The exported model is Y-up but feet are at y≈-0.05; nudge it down
        // so the character's feet are at y=0 and the head is at y≈2.
        // (Model is approximately 2 units tall, head at y≈1.95.)
        // (The model's pivot is around the hips; we leave the pivot where
        // it is and the AI's NavMeshAgent.height=1.9 will work.)

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

        // AudioSource — for the eat SFX one-shot.
        var sfx = root.AddComponent<AudioSource>();
        sfx.playOnAwake = false;
        sfx.spatialBlend = 1f;  // 3D positional

        // Second AudioSource for the looping ambient entity sound. Both clips
        // are decoded at runtime from embedded 16-bit PCM WAVs.
        var ambientSrc = root.AddComponent<AudioSource>();
        ambientSrc.playOnAwake = false;
        ambientSrc.loop = true;
        ambientSrc.spatialBlend = 1f;
        ambientSrc.volume = 0.7f;
        ambientSrc.minDistance = 4f;
        ambientSrc.maxDistance = 32f;
        ambientSrc.clip = WavLoader.LoadEmbedded("LethalPirateClark.pc_ambient.wav");
        var eatClip = WavLoader.LoadEmbedded("LethalPirateClark.pc_eat.wav");

        // NetworkObject — required for netcode (multiplayer).
        var networkObjectT = System.Type.GetType("Unity.Netcode.NetworkObject, Unity.Netcode.Runtime");
        if (networkObjectT != null)
        {
            var netObjComp = root.AddComponent(networkObjectT);
            // A NetworkObject created at runtime has GlobalObjectIdHash == 0
            // (it's normally baked at editor build time). Netcode can't spawn a
            // prefab whose hash isn't registered, so Spawn() silently fails and
            // the clone never gets ownership — IsOwner/IsServer stay false and
            // the AI never runs. Assign a stable non-zero hash so the prefab
            // registers and Spawn() can resolve it.
            var hashField = networkObjectT.GetField("GlobalObjectIdHash",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (hashField != null)
            {
                hashField.SetValue(netObjComp, (uint)0x5111C1A4);  // stable, "StillClark"
                Log.LogInfo("[StillLife] Set NetworkObject.GlobalObjectIdHash on the runtime prefab.");
            }
            else
            {
                Log.LogWarning("[StillLife] GlobalObjectIdHash field not found — network spawn may fail.");
            }
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
        ai.voiceSource = ambientSrc;
        ai.eatClip = eatClip;

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

        // NOTE: do NOT SetActive(true) here. The template is returned in an
        // inactive state, and BuildEnemyAtRuntime activates it AFTER assigning
        // `enemyType` to the AI component. If we activate here, EnemyAI.Awake()
        // runs while `enemyType` is still null and NREs — leaving the template's
        // components in a broken state, which clones inherit (this was the
        // v1.3.0 bug that caused "Pirate Clark is floating, doesn't move,
        // no sound").
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

    // Build a fallback capsule mesh in case the .obj fails to load.
    private static Mesh FallbackCapsuleMesh()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        var m = go.GetComponent<MeshFilter>().sharedMesh;
        UnityEngine.Object.DestroyImmediate(go);
        return m;
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

    // v1.3.3: deferred template activation. Hooks SceneManager.sceneLoaded
    // and activates the template on the first scene that has RoundManager
    // ready. We can't activate at Plugin.Awake() time because RoundManager
    // singleton is null until a scene with the game loaded runs Awake on
    // its own components, and EnemyAI.Awake NREs without it.
    private void OnSceneLoadedActivateTemplate(UnityEngine.SceneManagement.Scene scene,
        UnityEngine.SceneManagement.LoadSceneMode mode)
    {
        if (_templateActivated) return;
        if (_templatePendingActivation == null) return;

        // The MainMenu scene doesn't have RoundManager. We want the Game scene
        // (the one where the spawn pipeline lives). It's loaded once the host
        // starts. We can detect it by scene name OR by waiting until
        // RoundManager.Instance is non-null. The latter is more robust — it
        // doesn't depend on a specific scene name and works across game
        // versions that may rename things.
        //
        // We attach a one-shot coroutine on a hidden GameObject so we can
        // wait a frame (RoundManager's own Awake runs during scene load).
        var runnerGo = new GameObject("StillLifeActivationRunner") { hideFlags = HideFlags.HideAndDontSave };
        UnityEngine.Object.DontDestroyOnLoad(runnerGo);
        var runner = runnerGo.AddComponent<ActivationRunner>();
        runner.StartCoroutine(runner.ActivateTemplateWhenReady(this, _templatePendingActivation,
            onActivated: (prefab) => {
                _templatePendingActivation = null;
                _templateActivated = true;
                UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoadedActivateTemplate;
            }));
    }
}

// v1.3.3: a tiny MonoBehaviour that runs a coroutine to activate the
// template once RoundManager.Instance is non-null. We can't do this inline
// in Plugin because BepInEx's BaseUnityPlugin doesn't expose a coroutine
// runner directly (we'd need a hidden GameObject anyway, so this is the
// same GameObject with one component instead of three).
internal class ActivationRunner : MonoBehaviour
{
    public IEnumerator ActivateTemplateWhenReady(Plugin plugin, GameObject prefab, Action<GameObject> onActivated)
    {
        // Wait up to 5 seconds for RoundManager.Instance to be non-null.
        // RoundManager is a DontDestroyOnLoad singleton; its Awake runs
        // when the Game scene loads.
        float deadline = Time.realtimeSinceStartup + 5f;
        while (RoundManager.Instance == null && Time.realtimeSinceStartup < deadline)
        {
            yield return null;
        }

        if (RoundManager.Instance == null)
        {
            Plugin.Log.LogWarning("[StillLife] RoundManager.Instance still null after 5s — activating template anyway and hoping for the best.");
        }
        else
        {
            Plugin.Log.LogInfo($"[StillLife] RoundManager.Instance is up; activating template (scene='{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}').");
        }

        try
        {
            prefab.SetActive(true);
            Plugin.Log.LogInfo("[StillLife] Template activated successfully (v1.3.3 deferred activation).");
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogError($"[StillLife] Template activation threw: {ex.GetType().Name}: {ex.Message}");
            Plugin.Log.LogError($"[StillLife] Stack: {ex.StackTrace}");
        }

        onActivated(prefab);
        // Self-destruct the runner GameObject.
        Destroy(gameObject);
    }
}
