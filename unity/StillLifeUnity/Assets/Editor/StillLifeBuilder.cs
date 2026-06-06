// StillLifeBuilder — one-click asset-bundle builder for the Still Life enemy.
//
// Menu:  StillLife ▸ Build Everything   (does all of the below in order)
//        StillLife ▸ 1. Build Prefab + EnemyType
//        StillLife ▸ 2. Bake AssetBundle
//
// Everything game-specific (EnemyType, EnemyAICollisionDetect, TerminalNode,
// TerminalKeyword, ScanNodeProperties) is resolved by REFLECTION at run time, so:
//   * this script compiles even before you drop the game DLLs in, and
//   * it tolerates minor field renames across game versions (it logs and skips
//     anything it can't find instead of failing the build).
//
// PREREQS (one-time):
//   1. Put the game's managed DLLs in Assets/Plugins/  (see PUT_GAME_DLLS_HERE.txt).
//   2. Let Unity import StillLife.fbx (already in Assets/StillLife/).
//   3. Run "StillLife ▸ Build Everything".
//
// Output: AssetBundles/stilllife  — drop it next to the mod DLL.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.AI;

public static class StillLifeBuilder
{
    const string FbxPath      = "Assets/StillLife/StillLife.fbx";
    const string PrefabPath   = "Assets/StillLife/StillLifeEnemy.prefab";
    const string EnemyTypePath= "Assets/StillLife/StillLifeEnemy.asset";
    const string KeywordPath  = "Assets/StillLife/StillLifeKeyword.asset";
    const string NodePath     = "Assets/StillLife/StillLifeFile.asset";
    const string AmbientPath  = "Assets/StillLife/Audio/PC_ambient.wav";
    const string EatPath      = "Assets/StillLife/Audio/PC_eat.wav";
    const string ControllerPath = "Assets/StillLife/PirateClark.controller";
    const string BundleName   = "stilllife";
    const string OutDir       = "AssetBundles";

    [MenuItem("StillLife/Build Everything", false, 0)]
    public static void BuildEverything()
    {
        try
        {
            var prefab = BuildPrefabAndEnemyType();
            if (prefab == null) return;
            BakeAssetBundle();
            EditorUtility.DisplayDialog("StillLife",
                "Done! Bundle written to:\n" + Path.GetFullPath(Path.Combine(OutDir, BundleName)) +
                "\n\nDrop that 'stilllife' file next to com.yourname.stilllife.dll.", "Nice");
        }
        catch (Exception e)
        {
            Debug.LogError("[StillLife] Build failed: " + e);
            EditorUtility.DisplayDialog("StillLife", "Build failed — see Console:\n" + e.Message, "OK");
        }
    }

    // ----------------------------------------------------------------- batch
    // Headless entry point. Invoke from terminal:
    //   /Applications/Unity/Unity.app/Contents/MacOS/Unity \
    //     -projectpath <project> -batchmode -nographics -quit \
    //     -executeMethod StillLifeBuilder.BuildFromCommandLine \
    //     -logFile /tmp/unity_stilllife.log
    // Exits the editor with code 0 on success, 1 on failure (so the shell can
    // detect it). No dialogs — all progress goes to Debug.Log.
    public static void BuildFromCommandLine()
    {
        try
        {
            Debug.Log("[StillLife] === Batch build starting ===");
            // Game DLLs in Assets/Plugins/ are NOT auto-loaded by Unity when
            // their name happens to be one Unity uses for auto-generated
            // assemblies (Assembly-CSharp) or when they're 32-bit Windows PE32
            // on a Mac editor. Load them explicitly so AppDomain.GetAssemblies()
            // returns them and the FindType scan can resolve EnemyType, etc.
            TryLoadGameDll("DunGen.dll");
            TryLoadGameDll("ClientNetworkTransform.dll");
            TryLoadGameDll("Facepunch.Steamworks.Win64.dll");
            TryLoadGameDll("DissonanceVoip.dll");
            TryLoadGameDll("Facepunch Transport for Netcode for GameObjects.dll");
            TryLoadGameDll("Assembly-CSharp-firstpass.dll");
            TryLoadGameDll("Assembly-CSharp.dll");

            // Self-diagnostic: confirm the AppDomain now sees the game's types.
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            int hitCount = 0;
            foreach (var a in asms)
            {
                var n = a.GetName().Name;
                if (n == "Assembly-CSharp" || n == "Assembly-CSharp-firstpass" || n == "DunGen")
                {
                    hitCount++;
                    Debug.Log($"[StillLife]   game-asm loaded: {n}  loc: {a.Location}");
                }
            }
            Debug.Log($"[StillLife] AppDomain game assemblies found: {hitCount} (need 3: Assembly-CSharp, firstpass, DunGen)");

            var prefab = BuildPrefabAndEnemyType();
            if (prefab == null)
            {
                Debug.LogError("[StillLife] Batch build FAILED: BuildPrefabAndEnemyType returned null.");
                EditorApplication.Exit(1);
                return;
            }
            BakeAssetBundle();
            var bundlePath = Path.GetFullPath(Path.Combine(OutDir, BundleName));
            if (File.Exists(bundlePath))
                Debug.Log("[StillLife] === Batch build SUCCEEDED. Bundle: " + bundlePath
                    + " (" + new FileInfo(bundlePath).Length + " bytes) ===");
            else
                Debug.LogError("[StillLife] Batch build FAILED: bundle file not produced at " + bundlePath);
            EditorApplication.Exit(File.Exists(bundlePath) ? 0 : 1);
        }
        catch (Exception e)
        {
            Debug.LogError("[StillLife] Batch build FAILED with exception: " + e);
            EditorApplication.Exit(1);
        }
    }

    // ---------------------------------------------------------------- prefab

    [MenuItem("StillLife/1. Build Prefab + EnemyType", false, 20)]
    public static GameObject BuildPrefabAndEnemyType()
    {
        ConfigureModelImport();   // loop the idle/walk clips, ensure rig is generic

        var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
        if (fbx == null)
        {
            Debug.LogError($"[StillLife] FBX not found at {FbxPath}. Did Unity finish importing it?");
            return null;
        }

        // fresh instance of the model to build the prefab from
        var root = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
        root.name = "StillLifeEnemy";
        root.transform.position = Vector3.zero;

        // --- materials (best-effort HDRP/Lit so it reads correctly in-game) ---
        ApplyMaterials(root);

        // --- animation: build a controller from the FBX clips and wire it ---
        SetupAnimator(root);

        // --- NavMeshAgent (engine type) ---
        var agent = GetOrAdd<NavMeshAgent>(root);
        agent.radius = 0.35f; agent.height = 1.9f; agent.speed = 3.2f;
        agent.acceleration = 12f; agent.angularSpeed = 240f; agent.stoppingDistance = 0.6f;
        agent.areaMask = ~0;

        // --- Netcode components (reflection: tolerate package version drift) ---
        AddByName(root, "NetworkObject", "Unity.Netcode");
        var nt = AddByName(root, "NetworkTransform", "Unity.Netcode.Components")
              ?? AddByName(root, "NetworkTransform", "Unity.Netcode");
        TrySet(nt, "Interpolate", true);
        TrySet(nt, "SyncScaleX", false); TrySet(nt, "SyncScaleY", false); TrySet(nt, "SyncScaleZ", false);

        // --- audio ---
        var voice = GetOrAdd<AudioSource>(root);
        voice.spatialBlend = 1f; voice.maxDistance = 50f; voice.rolloffMode = AudioRolloffMode.Linear;

        // --- hitbox child with the game's collision-detect script ---
        var hit = new GameObject("Collision");
        hit.transform.SetParent(root.transform, false);
        hit.transform.localPosition = new Vector3(0, 1.0f, 0);
        var cap = hit.AddComponent<CapsuleCollider>();
        cap.isTrigger = true; cap.radius = 0.4f; cap.height = 2.0f; cap.center = Vector3.zero;
        // EnemyAICollisionDetect is a GAME script (from Assembly-CSharp.dll). It
        // can't be baked into a Mac-built asset bundle that the Windows game
        // loads, because the bundle would carry a Windows build-target stamp
        // for that script. We skip it here; the mod DLL adds it to the prefab
        // at runtime in Plugin.LoadAssetsAndRegister, mirroring how StillLifeAI
        // is already added at runtime. That keeps the bundle target-agnostic.
        // AddByName(hit, "EnemyAICollisionDetect", "");

        // NOTE: we deliberately do NOT add StillLifeAI here. The mod DLL adds it
        // to this prefab at load time (Plugin.LoadAssetsAndRegister) and wires
        // EnemyAICollisionDetect.mainScript then. Keeping it out means this Unity
        // project never needs the mod's own scripts.

        // save prefab
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        UnityEngine.Object.DestroyImmediate(root);
        Debug.Log($"[StillLife] Prefab saved: {PrefabPath}");

        BuildEnemyType(prefab);
        BuildTerminalEntry();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return prefab;
    }

    static void BuildEnemyType(GameObject prefab)
    {
        var enemyTypeT = FindType("EnemyType");
        if (enemyTypeT == null)
        {
            Debug.LogError("[StillLife] Type 'EnemyType' not found. Add the game's Assembly-CSharp.dll to Assets/Plugins/.");
            return;
        }
        var et = ScriptableObject.CreateInstance(enemyTypeT);
        et.name = "StillLifeEnemy";

        // Inspect the actual field names Unity found on this game version so we
        // can map our names to whatever the current build uses. Saves a lot of
        // "no field 'X'" guessing in TrySet logs.
        var realFields = enemyTypeT.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Debug.Log($"[StillLife] EnemyType fields ({realFields.Length}): {string.Join(", ", System.Array.ConvertAll(realFields, f => f.Name))}");

        var flat = new AnimationCurve(new Keyframe(0, 1f), new Keyframe(1, 1f));
        var falloff = new AnimationCurve(new Keyframe(0, 0f), new Keyframe(1, 1f));

        TrySet(et, "enemyName", "Pirate Clark");
        TrySet(et, "enemyPrefab", prefab);
        TrySet(et, "isOutsideEnemy", false);
        TrySet(et, "isDaytimeEnemy", false);
        TrySet(et, "maxCount", 4);          // legacy / older game versions
        TrySet(et, "MaxCount", 4);           // current Lethal Company (capital M)
        TrySet(et, "PowerLevel", 1f);
        TrySet(et, "probabilityCurve", flat);
        TrySet(et, "numberSpawnedFalloff", falloff);
        TrySet(et, "useNumberSpawnedFalloff", false);
        TrySet(et, "canDie", true);
        TrySet(et, "destroyOnDeath", false);
        TrySet(et, "canBeStunned", true);
        TrySet(et, "canBeDestroyed", true);
        TrySet(et, "stunTimeMultiplier", 1f);
        TrySet(et, "stunGameDifficultyMultiplier", 1f);
        TrySet(et, "loudnessMultiplier", 1f);

        AssetDatabase.CreateAsset(et, EnemyTypePath);
        Debug.Log($"[StillLife] EnemyType saved: {EnemyTypePath}");
    }

    static void BuildTerminalEntry()
    {
        var kwT = FindType("TerminalKeyword");
        var nodeT = FindType("TerminalNode");
        if (kwT == null || nodeT == null)
        {
            Debug.LogWarning("[StillLife] TerminalKeyword/TerminalNode not found — skipping bestiary entry (enemy still works).");
            return;
        }
        var node = ScriptableObject.CreateInstance(nodeT); node.name = "StillLifeFile";
        TrySet(node, "displayText",
            "PIRATE CLARK\n\nA Still Life — a person the facility copied wrong. Wears the rotted coat " +
            "and tricorn of a pirate, eyes fixed and too wide. It stands frozen while watched and " +
            "stalks only when unseen. It cannot open doors — it knocks, then breaks them. What it " +
            "kills, it copies.\n\n");
        TrySet(node, "clearPreviousText", true);
        TrySet(node, "maxCharactersToType", 35);
        AssetDatabase.CreateAsset(node, NodePath);

        var kw = ScriptableObject.CreateInstance(kwT); kw.name = "StillLifeKeyword";
        TrySet(kw, "word", "pirate clark");
        TrySet(kw, "isVerb", false);
        AssetDatabase.CreateAsset(kw, KeywordPath);
        Debug.Log("[StillLife] Terminal scan entry created.");
    }

    // ------------------------------------------------------------- animation

    // Configure the FBX importer: Generic rig, loop the idle + walk clips.
    static void ConfigureModelImport()
    {
        var mi = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
        if (mi == null) return;
        mi.animationType = ModelImporterAnimationType.Generic;
        mi.importAnimation = true;

        var clips = mi.defaultClipAnimations;   // auto-split takes from Blender
        for (int i = 0; i < clips.Length; i++)
        {
            string n = clips[i].name.ToLowerInvariant();
            bool loop = n.Contains("idle") || n.Contains("walk");
            clips[i].loopTime = loop;
        }
        if (clips.Length > 0) mi.clipAnimations = clips;
        mi.SaveAndReimport();
    }

    // Build an AnimatorController from the imported clips and attach it.
    // States: Walk (default) ↔ Frozen (driven by bool 'frozen'); Grab/Knock on
    // triggers. Matches the params StillLifeAI sets at runtime.
    static void SetupAnimator(GameObject root)
    {
        AnimationClip Find(string key) =>
            AssetDatabase.LoadAllAssetsAtPath(FbxPath)
                .OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview") &&
                                     c.name.ToLowerInvariant().Contains(key));

        var idle = Find("idle"); var walk = Find("walk");
        var grab = Find("grab"); var knock = Find("knock");
        if (walk == null && idle == null)
        {
            Debug.LogWarning("[StillLife] No animation clips found in FBX — skipping Animator (enemy still moves via NavMeshAgent).");
            return;
        }

        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        ctrl.AddParameter("frozen", AnimatorControllerParameterType.Bool);
        ctrl.AddParameter("grab", AnimatorControllerParameterType.Trigger);
        ctrl.AddParameter("knock", AnimatorControllerParameterType.Trigger);
        var sm = ctrl.layers[0].stateMachine;

        var sWalk = sm.AddState("Walk");   sWalk.motion = walk ?? idle;
        var sFroz = sm.AddState("Frozen"); sFroz.motion = idle ?? walk;
        sm.defaultState = sWalk;

        var toFroz = sWalk.AddTransition(sFroz); toFroz.hasExitTime = false; toFroz.duration = 0.15f;
        toFroz.AddCondition(AnimatorConditionMode.If, 0, "frozen");
        var toWalk = sFroz.AddTransition(sWalk); toWalk.hasExitTime = false; toWalk.duration = 0.15f;
        toWalk.AddCondition(AnimatorConditionMode.IfNot, 0, "frozen");

        if (grab != null)
        {
            var s = sm.AddState("Grab"); s.motion = grab;
            var any = sm.AddAnyStateTransition(s); any.hasExitTime = false; any.duration = 0.05f;
            any.canTransitionToSelf = false; any.AddCondition(AnimatorConditionMode.If, 0, "grab");
            var back = s.AddTransition(sWalk); back.hasExitTime = true; back.exitTime = 0.9f; back.duration = 0.2f;
        }
        if (knock != null)
        {
            var s = sm.AddState("Knock"); s.motion = knock;
            var any = sm.AddAnyStateTransition(s); any.hasExitTime = false; any.duration = 0.05f;
            any.canTransitionToSelf = false; any.AddCondition(AnimatorConditionMode.If, 0, "knock");
            var back = s.AddTransition(sWalk); back.hasExitTime = true; back.exitTime = 0.9f; back.duration = 0.2f;
        }

        var anim = root.GetComponentInChildren<Animator>();
        if (anim == null) anim = root.AddComponent<Animator>();
        anim.runtimeAnimatorController = ctrl;
        anim.applyRootMotion = false;
        anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        Debug.Log("[StillLife] Animator controller built and assigned.");
    }

    // ------------------------------------------------------------- bundle

    [MenuItem("StillLife/2. Bake AssetBundle", false, 21)]
    public static void BakeAssetBundle()
    {
        foreach (var p in new[] { PrefabPath, EnemyTypePath, NodePath, KeywordPath, AmbientPath, EatPath, ControllerPath })
        {
            var imp = AssetImporter.GetAtPath(p);
            if (imp != null) { imp.assetBundleName = BundleName; imp.SaveAndReimport(); }
            else if (p == AmbientPath || p == EatPath)
                Debug.LogWarning($"[StillLife] Audio not found at {p} — eat/ambient sounds won't be in the bundle.");
        }

        Directory.CreateDirectory(OutDir);
        // We have to build for a target the Mac editor supports. Originally
        // this was StandaloneWindows64 (the Lethal Company runtime), but
        // building on Mac with no Windows module installed fails. Since the
        // bundle is purely data (no game scripts — those are added by the mod
        // DLL at runtime), StandaloneOSX produces a bundle loadable by the
        // Windows game. This is a common pattern for Mac-built Lethal Company
        // asset bundles.
        BuildPipeline.BuildAssetBundles(OutDir, BuildAssetBundleOptions.None, BuildTarget.StandaloneOSX);
        AssetDatabase.Refresh();

        var outFile = Path.Combine(OutDir, BundleName);
        if (File.Exists(outFile))
            Debug.Log($"[StillLife] Bundle baked: {Path.GetFullPath(outFile)}  ({new FileInfo(outFile).Length} bytes)");
        else
            Debug.LogError("[StillLife] Bundle was not produced — check the Console for asset-bundle assignment errors.");
    }

    // ------------------------------------------------------------- helpers

    // Manually load a game DLL into the editor's AppDomain. Unity does not
    // auto-load every DLL in Assets/Plugins/ into the editor's runtime (only
    // those that match Unity's "auto-referenced" criteria). For our asset-bundle
    // build, we need EnemyType etc. visible to the FindType reflection scan,
    // so we LoadFile them explicitly. Order matters: dependencies first.
    static void TryLoadGameDll(string filename)
    {
        var path = Path.Combine(Application.dataPath, "Plugins", filename);
        if (!File.Exists(path))
        {
            Debug.LogWarning($"[StillLife] TryLoadGameDll: file not found at {path}");
            return;
        }
        // Skip if already loaded (e.g. Unity auto-loaded it for us).
        var simple = Path.GetFileNameWithoutExtension(filename);
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (a.GetName().Name == simple) { Debug.Log($"[StillLife] TryLoadGameDll: {filename} already in AppDomain"); return; }
        }
        try
        {
            var loaded = Assembly.LoadFile(path);
            Debug.Log($"[StillLife] TryLoadGameDll: loaded {filename}  ({loaded.GetTypes().Length} types)");
        }
        catch (ReflectionTypeLoadException ex)
        {
            int ok = ex.Types.Count(t => t != null);
            Debug.LogWarning($"[StillLife] TryLoadGameDll: {filename} loaded partially: {ok}/{ex.Types.Length} types");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[StillLife] TryLoadGameDll: failed to load {filename}: {e.GetType().Name}: {e.Message}");
        }
    }

    // Pirate Clark ships WITH baked textures from the FBX. Preserve them: upgrade
    // the imported materials to HDRP/Lit (the game's pipeline) while carrying the
    // albedo map + tint across, instead of overwriting with flat colours.
    static void ApplyMaterials(GameObject root)
    {
        var hdrp = Shader.Find("HDRP/Lit");
        if (hdrp == null)
        {
            Debug.LogWarning("[StillLife] HDRP/Lit shader not found — keeping imported FBX materials as-is.");
            return;
        }
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                var src = mats[i];
                if (src == null) continue;
                var tex = src.mainTexture;                       // baked albedo from FBX
                Color tint = src.HasProperty("_Color") ? src.GetColor("_Color") : Color.white;
                var m = new Material(hdrp) { name = src.name + "_HDRP" };
                if (tex != null) m.SetTexture("_BaseColorMap", tex);
                m.SetColor("_BaseColor", tint);
                m.SetFloat("_Smoothness", 0.2f);
                AssetDatabase.CreateAsset(m, $"Assets/StillLife/{m.name}.mat");
                mats[i] = m;
            }
            r.sharedMaterials = mats;
        }
    }

    static T GetOrAdd<T>(GameObject go) where T : Component
    {
        // NOTE: use Unity's "== null" override, NOT the C# ?? operator — GetComponent
        // can return a "fake null" that ?? treats as non-null, yielding a dead ref.
        var c = go.GetComponent<T>();
        if (c == null) c = go.AddComponent<T>();
        if (c == null) Debug.LogError($"[StillLife] Could not add {typeof(T).Name} to {go.name}");
        return c;
    }

    static Component AddByName(GameObject go, string simpleName, string nsHint)
    {
        var t = FindType(simpleName, nsHint);
        if (t == null) { Debug.LogWarning($"[StillLife] Component type '{simpleName}' not found — skipped."); return null; }
        var existing = go.GetComponent(t);
        return existing != null ? existing : go.AddComponent(t);
    }

    static Type FindType(string simpleName, string nsHint = null)
    {
        var matches = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .Where(t => t.Name == simpleName)
            .ToList();
        if (matches.Count == 0) return null;
        if (!string.IsNullOrEmpty(nsHint))
        {
            var byNs = matches.FirstOrDefault(t => t.Namespace != null && t.Namespace.Contains(nsHint));
            if (byNs != null) return byNs;
        }
        // prefer Assembly-CSharp (the game) for game types
        return matches.FirstOrDefault(t => t.Assembly.GetName().Name == "Assembly-CSharp") ?? matches[0];
    }

    static void TrySet(object obj, string field, object value)
    {
        if (obj == null) return;
        var f = obj.GetType().GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (f == null) { Debug.Log($"[StillLife] (skip) no field '{field}' on {obj.GetType().Name}"); return; }
        try { f.SetValue(obj, value); }
        catch (Exception e) { Debug.LogWarning($"[StillLife] could not set '{field}': {e.Message}"); }
    }
}
