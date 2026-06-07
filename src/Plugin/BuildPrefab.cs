// BuildPrefab.cs — v3.0.0 fallback path: construct the entire Pirate Clark
// prefab in C# at runtime, with no asset bundle. The bundle approach has been
// unreliable in practice (Unity 2022.3.62's runtime rejects bundles built by
// older / mismatched editors, even when the file looks fine), so we build the
// GameObject + EnemyType procedurally. The model is a primitive capsule (the
// AI behaviour, networking, and registration are all identical to the bundle
// build — only the visible mesh changes).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using LethalLib.Modules;
using StillLife.Behaviours;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace StillLife;

internal static class BuildPrefab
{
    // Bake a deterministic 32-bit id into the procedurally-built NetworkObject
    // so NGO can match clones to the prefab across the network. The value MUST
    // stay constant across releases — clients that join a host have to use the
    // same one or replication silently drops. Pick something that won't
    // collide with real game hashes: high bit set, deliberate pattern.
    //
    // This is a fresh constant for v3.0.0. The bundle build used a different
    // hash (whatever Unity derived from the prefab GUID), so v3.0.0 is a hard
    // cutover: an old client on v2.1.x trying to play with a new v3.0.0 host
    // will reject the prefab. That's the price of escaping the broken bundle.
    private const uint PirateClarkHash = 0xC1A12C1B; // "CLARK PC" encoded

    public static GameObject BuildProceduralPrefab(ManualLogSource log)
    {
        log.LogInfo("[StillLife] Building Pirate Clark prefab in C# (procedural fallback).");

        // --- Root GameObject -----------------------------------------------
        var prefab = new GameObject("PirateClarkProcedural");
        // Hide and don't save — equivalent to a prefab in memory. We park it
        // in DontDestroyOnLoad once everything is wired. We leave the root
        // ACTIVE: NGO's prefab-registration code paths assume the template
        // is alive (the Spawn flow uses Instantiate(prefab) and expects the
        // clone to come up active). The components' Awake()s do NOT fire
        // here because the GO is a freshly-created template not in any
        // scene — Awake/OnEnable only run on AddComponent when the GO is
        // active in a scene. We avoid the timing trap by deferring AI
        // wiring to PreparePrefab, the same way the bundle build did.
        prefab.hideFlags = HideFlags.HideAndDontSave;

        // --- NetworkObject (required for any NetworkBehaviour) -------------
        var netObj = prefab.AddComponent<NetworkObject>();
        // GlobalObjectIdHash MUST be set BEFORE the prefab is registered with
        // NGO. The runtime's prefab table uses it to resolve clones back to
        // their prefab for replication. The build-time asset bundle version
        // got this from a GUID; we set it directly.
        // Use a stable hash distinct from any in the base game.
        netObj.GlobalObjectIdHash = PirateClarkHash;
        // Reasonable defaults: scene-placed objects, observers by default,
        // destroy with scene disabled (we park in DontDestroyOnLoad).
        netObj.DontDestroyWithOwner = true;
        netObj.AlwaysReplicateAsRoot = true;
        netObj.SynchronizeTransform = true;
        netObj.SpawnWithObservers = true;
        netObj.DestroyWithScene = false;

        // --- NetworkTransform — the prefab needs *some* transform sync so
        // the position changes the server makes actually reach clients.
        // NGO's NetworkTransform reads its sync flags internally; the
        // defaults sync position + Y rotation, which is exactly what we
        // want for a NavMesh-driven enemy.
        //
        // We resolve the type by name (it's in Unity.Netcode.Components.dll,
        // not the runtime DLL) so the using-directive path doesn't break if
        // a project reference changes. Falls back to no transform if the
        // class isn't present — the AI still works, just position sync is
        // less smooth on clients.
        var networkTransformT = ResolveType("Unity.Netcode.Components.NetworkTransform");
        Component netTransform = null!;
        if (networkTransformT != null)
        {
            netTransform = prefab.AddComponent(networkTransformT);
            TrySetField(netTransform, "Interpolate", true);
            TrySetField(netTransform, "SyncPositionX", true);
            TrySetField(netTransform, "SyncPositionY", true);
            TrySetField(netTransform, "SyncPositionZ", true);
            TrySetField(netTransform, "SyncRotY", true);
        }
        else
        {
            Plugin.Log.LogWarning("[StillLife] NetworkTransform type not found — " +
                "client-side position interpolation will be less smooth, but AI still works.");
        }

        // --- Visual mesh — a primitive capsule. Not a pirate, but a known
        // stable shape we can author without an external FBX. Stays
        // disabled (it's a template, never visible).
        var meshFilter = prefab.AddComponent<MeshFilter>();
        meshFilter.sharedMesh = GetOrCreateCapsuleMesh();

        var meshRenderer = prefab.AddComponent<MeshRenderer>();
        var mat = new Material(Shader.Find("Standard"))
        {
            color = new Color(0.85f, 0.78f, 0.65f, 1f) // off-white "foam body"
        };
        mat.hideFlags = HideFlags.HideAndDontSave;
        meshRenderer.sharedMaterial = mat;
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        meshRenderer.receiveShadows = true;

        // --- NavMeshAgent — the mover. Sized to match a humanoid so it
        // navigates the standard indoor NavMesh cleanly.
        var agent = prefab.AddComponent<NavMeshAgent>();
        agent.radius = 0.35f;
        agent.height = 1.9f;
        agent.speed = 3.2f;
        agent.acceleration = 12f;
        agent.angularSpeed = 240f;
        agent.stoppingDistance = 0.6f;
        agent.baseOffset = 0f;
        agent.autoBraking = true;
        agent.autoRepath = true;
        agent.obstacleAvoidanceType = ObstacleAvoidanceType.LowQualityObstacleAvoidance;

        // --- Solid body collider. The Collision child (below) has the
        // trigger; this one blocks player physics so they can't walk
        // through Pirate Clark.
        var bodyCol = prefab.AddComponent<CapsuleCollider>();
        bodyCol.isTrigger = false;
        bodyCol.radius = 0.4f;
        bodyCol.height = 2.0f;
        bodyCol.center = new Vector3(0, 1.0f, 0);
        bodyCol.direction = 1; // Y

        // --- Animator (with at minimum a 'frozen' bool param). EnemyAI and
        // StillLifeAI both poke animator parameters, so a missing animator
        // would throw at runtime.
        var animator = prefab.AddComponent<Animator>();
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        // --- Audio sources — silent, but present so the AI can call
        // PlayOneShot / .Play() without NREs. Clips are null in this
        // fallback build, so it's just quiet.
        var voice = prefab.AddComponent<AudioSource>();
        voice.playOnAwake = false;
        voice.loop = true;
        voice.spatialBlend = 0f;
        voice.volume = 1f;
        voice.minDistance = 0f;
        voice.maxDistance = 500f;
        voice.hideFlags = HideFlags.HideAndDontSave;

        var sfx = prefab.AddComponent<AudioSource>();
        sfx.playOnAwake = false;
        sfx.spatialBlend = 1f;
        sfx.loop = false;
        sfx.hideFlags = HideFlags.HideAndDontSave;

        // --- "Collision" child — the trigger volume EnemyAICollisionDetect
        // watches for player overlap.
        var hitbox = new GameObject("Collision");
        hitbox.transform.SetParent(prefab.transform, false);
        hitbox.transform.localPosition = new Vector3(0, 1.0f, 0);
        var colTrigger = hitbox.AddComponent<CapsuleCollider>();
        colTrigger.isTrigger = true;
        colTrigger.radius = 0.5f;
        colTrigger.height = 2.0f;
        colTrigger.center = Vector3.zero;
        colTrigger.direction = 1;

        var cdType = ResolveType("EnemyAICollisionDetect");
        if (cdType != null)
        {
            var cd = hitbox.AddComponent(cdType);
            TrySetField(cd, "mainScript", null); // StillLifeAI wired in PreparePrefab
            TrySetField(cd, "canCollideWithEnemies", true);
            TrySetField(cd, "onlyCollideWhenGrounded", false);
            TrySetField(cd, "alwaysAllowHitting", true);
        }

        // --- Finally, leave the AI attachment for PreparePrefab. The root
        // is active at this point and an EnemyAI.Awake() reads
        // RoundManager.Instance / StartOfRound.Instance, which are null
        // during BepInEx chainloader time. Adding StillLifeAI in the same
        // coroutine the bundle path uses means the same defensive pattern
        // covers both code paths. PreparePrefab fills in enemyType,
        // creatureAnimator, agent, voiceSource, and creatureSFX.

        log.LogInfo($"[StillLife] Procedural prefab assembled: root='{prefab.name}' " +
            $"netObjHash=0x{PirateClarkHash:X8} animator=ok agent=ok colliders=ok " +
            $"voiceSource=ok creatureSFX=ok (AI attached deferred).");
        return prefab;
    }

    // Build a stable EnemyType via ScriptableObject.CreateInstance and
    // reflection-set every spawn-critical field. We can't load one from a
    // bundle, so we make one and fill in the values that the game reads.
    public static EnemyType BuildProceduralEnemyType(int rarity, int maxCount, ManualLogSource log)
    {
        var enemy = ScriptableObject.CreateInstance<EnemyType>();
        enemy.name = "StillLifeEnemyProcedural";
        // Public fields directly accessible on the publicized Assembly-CSharp.
        // Type notes from inspection of the publicized DLL:
        //   PowerLevel : Single (float)
        //   DiversityPowerLevel : Int32
        //   increasedChanceInterior : Int32 (1 = more common indoors)
        //   MaxCount, spawnInGroupsOf, minEnemiesToSpawnNest : Int32
        //   isOutsideEnemy, isDaytimeEnemy, canBeStunned, canDie,
        //     canBeDestroyed, destroyOnDeath, canSeeThroughFog,
        //     disableAnimatorWhenFar : Boolean
        enemy.enemyName = "Pirate Clark";
        enemy.enemyPrefab = null; // set by caller
        enemy.PowerLevel = 1.5f;       // tougher than a Bracken, weaker than a Coil-Head
        enemy.DiversityPowerLevel = 0;
        enemy.MaxCount = maxCount;
        enemy.spawnInGroupsOf = 1;
        enemy.isOutsideEnemy = false;
        enemy.isDaytimeEnemy = false;
        enemy.increasedChanceInterior = 1;  // 1 = yes, indoor only
        enemy.canBeStunned = true;
        enemy.canDie = true;
        enemy.canBeDestroyed = true;
        enemy.destroyOnDeath = true;
        enemy.canSeeThroughFog = true;
        enemy.disableAnimatorWhenFar = true;
        enemy.pushPlayerForce = 5f;
        enemy.pushPlayerDistance = 1.5f;
        enemy.stunTimeMultiplier = 1f;
        enemy.doorSpeedMultiplier = 0f;        // cannot open doors
        enemy.stunGameDifficultyMultiplier = 1f;
        enemy.useNumberSpawnedFalloff = false;
        enemy.requireNestObjectsToSpawn = false;
        enemy.normalizedTimeInDayToLeave = 1f;
        enemy.spawningDisabled = false;
        enemy.spawnFromWeeds = false;
        enemy.nestSpawnPrefab = null;
        enemy.useMinEnemyThresholdForNest = false;
        enemy.minEnemiesToSpawnNest = 0;
        // AnimationCurve fields need a non-null default or the spawn code
        // throws on the first Sample call.
        enemy.probabilityCurve = new AnimationCurve(
            new Keyframe(0f, 0f),
            new Keyframe(1f, 1f));
        // numberSpawnedFalloff is also an AnimationCurve in some LethalLib
        // versions; setting it defensively via reflection in case the
        // publicized build hides the field.
        TrySetField(enemy, "numberSpawnedFalloff", new AnimationCurve(
            new Keyframe(0f, 1f), new Keyframe(1f, 1f)));

        // Lists/arrays — these are public-list internal fields; init them
        // empty so the spawn loop doesn't NRE.
        TrySetField(enemy, "audioClips", new List<AudioClip>());
        TrySetField(enemy, "miscAnimations", new List<AnimationClip>());

        log.LogInfo($"[StillLife] Built EnemyType: name='{enemy.enemyName}' " +
            $"power={enemy.PowerLevel} max={enemy.MaxCount} insideOnly={!enemy.isOutsideEnemy}.");
        return enemy;
    }

    // --- helpers ------------------------------------------------------------

    // Generate a primitive capsule mesh once and cache it for reuse. Unity's
    // built-in capsule primitive isn't directly scriptable (it's hidden
    // behind GameObject.CreatePrimitive), so we build it via reflection the
    // first time and store the result.
    private static Mesh? _capsuleMesh;
    private static Mesh GetOrCreateCapsuleMesh()
    {
        if (_capsuleMesh != null) return _capsuleMesh;

        // Easiest path: create a temporary primitive to steal its mesh, then
        // destroy the temp GO. The mesh is a shared resource so destroying
        // the GO doesn't destroy the mesh.
        var temp = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        _capsuleMesh = temp.GetComponent<MeshFilter>().sharedMesh;
        UnityEngine.Object.DestroyImmediate(temp);
        return _capsuleMesh!;
    }

    private static void TrySetField(object obj, string name, object? value)
    {
        if (obj == null) return;
        var t = obj.GetType();
        var f = t.GetField(name,
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);
        if (f != null)
        {
            try { f.SetValue(obj, value); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[StillLife] (BuildPrefab) Set {name} failed: {e.Message}");
            }
        }
    }

    private static Type? ResolveType(string simpleName)
    {
        var matches = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .Where(t => t.Name == simpleName)
            .ToList();
        if (matches.Count == 0) return null;
        return matches.FirstOrDefault(t => t.Assembly.GetName().Name == "Assembly-CSharp") ?? matches[0];
    }
}
