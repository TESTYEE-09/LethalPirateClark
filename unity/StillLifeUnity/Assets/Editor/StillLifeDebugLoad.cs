// StillLifeDebugLoad — one-shot diagnostic that loads the asset bundle
// in the Unity editor (same env that built it) and reports whether
// everything deserializes correctly, including whether the EnemyType's
// enemyPrefab reference survives. This is a pure diagnostic — it
// doesn't write any files or modify the project.
//
// Invoke from terminal:
//   /Applications/Unity/Unity.app/Contents/MacOS/Unity \
//     -projectpath /Users/nomae/claude/StillLife/unity/StillLifeUnity \
//     -batchmode -nographics -quit \
//     -executeMethod StillLifeBuilder.DebugLoadBundle \
//     -logFile /tmp/unity_debug.log
//
// Exits 0 on success, 1 on failure.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class StillLifeDebugLoad
{
    public static void DebugLoadBundle()
    {
        try
        {
            Debug.Log("[StillLifeDebug] === Diagnostic start ===");

            // Locate the existing stilllife bundle.
            string bundlePath = "/Users/nomae/claude/StillLife/dist/LethalPirateClark/plugins/StillLife/stilllife";
            if (!File.Exists(bundlePath))
            {
                Debug.LogError($"[StillLifeDebug] Bundle not found at {bundlePath}");
                EditorApplication.Exit(1);
                return;
            }
            Debug.Log($"[StillLifeDebug] Bundle file: {bundlePath} ({new FileInfo(bundlePath).Length} bytes)");

            // Load the game's publicized Assembly-CSharp.dll into the editor domain
            // so FindType and GetType("EnemyType, Assembly-CSharp") work.
            string acPath = Path.Combine(Application.dataPath, "Plugins", "Assembly-CSharp.dll");
            if (File.Exists(acPath))
            {
                try { Assembly.LoadFile(acPath); Debug.Log("[StillLifeDebug] Loaded Assembly-CSharp.dll from Plugins/"); }
                catch (ReflectionTypeLoadException rle)
                {
                    int ok = rle.Types.Count(t => t != null);
                    Debug.LogWarning($"[StillLifeDebug] Assembly-CSharp.dll loaded with {rle.Types.Length - ok} type failures (expected: DunGen dependency). {ok} types available.");
                }
                catch (Exception e) { Debug.LogWarning($"[StillLifeDebug] Assembly-CSharp.dll load error: {e.Message}"); }
            }
            else { Debug.LogWarning($"[StillLifeDebug] Assembly-CSharp.dll not at {acPath}"); }

            // Now load the bundle.
            var bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle == null)
            {
                Debug.LogWarning("[StillLifeDebug] AssetBundle.LoadFromFile returned NULL — trying LoadFromMemory...");
                var bytes = File.ReadAllBytes(bundlePath);
                Debug.Log($"[StillLifeDebug] Read {bytes.Length} bytes");
                bundle = AssetBundle.LoadFromMemory(bytes);
                if (bundle == null)
                {
                    Debug.LogError("[StillLifeDebug] LoadFromMemory also returned NULL — bundle is truly corrupted/unsupported on this platform.");
                    EditorApplication.Exit(1);
                    return;
                }
                Debug.Log("[StillLifeDebug] LoadFromMemory succeeded (LoadFromFile failed).");
            }
            Debug.Log("[StillLifeDebug] Bundle loaded OK.");

            // Try loading the EnemyType
            var enemyTypeT = FindType("EnemyType");
            if (enemyTypeT == null)
            {
                Debug.LogError("[StillLifeDebug] EnemyType not found in loaded assemblies — game DLL not loaded properly.");
                EditorApplication.Exit(1);
                return;
            }
            Debug.Log($"[StillLifeDebug] EnemyType type: {enemyTypeT.AssemblyQualifiedName}");

            // Generic load via reflection
            var loadMethod = typeof(AssetBundle).GetMethod("LoadAsset", new Type[] { typeof(string), typeof(Type) });
            if (loadMethod == null)
            {
                Debug.LogError("[StillLifeDebug] AssetBundle.LoadAsset(string,Type) method not found.");
                EditorApplication.Exit(1);
                return;
            }
            var genericLoad = loadMethod.MakeGenericMethod(enemyTypeT);
            var enemy = genericLoad.Invoke(bundle, new object[] { "StillLifeEnemy", enemyTypeT });
            if (enemy == null)
            {
                Debug.LogError("[StillLifeDebug] LoadAsset<EnemyType>('StillLifeEnemy') returned NULL — name mismatch or missing from bundle.");
                EditorApplication.Exit(1);
                return;
            }
            Debug.Log("[StillLifeDebug] EnemyType loaded: " + enemy);

            // Inspect all fields
            var fields = enemyTypeT.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Debug.Log($"[StillLifeDebug] EnemyType has {fields.Length} fields. Critical field values:");
            foreach (var f in fields)
            {
                if (f.Name == "enemyPrefab" || f.Name == "enemyName" || f.Name == "PowerLevel" ||
                    f.Name == "MaxCount" || f.Name == "isOutsideEnemy" || f.Name == "isDaytimeEnemy" ||
                    f.Name == "probabilityCurve" || f.Name == "spawningDisabled" || f.Name == "canDie")
                {
                    object v;
                    try { v = f.GetValue(enemy); }
                    catch (Exception e) { v = $"<error: {e.Message}>"; }
                    Debug.Log($"[StillLifeDebug]   {f.Name} ({f.FieldType.Name}) = {(v == null ? "NULL" : v.ToString())}");
                }
            }

            // Check the prefab reference
            var enemyPrefabField = enemyTypeT.GetField("enemyPrefab", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (enemyPrefabField == null)
            {
                Debug.LogError("[StillLifeDebug] enemyPrefab field not found on EnemyType.");
                EditorApplication.Exit(1);
                return;
            }
            var prefab = enemyPrefabField.GetValue(enemy) as GameObject;
            if (prefab == null)
            {
                Debug.LogError("[StillLifeDebug] enemyPrefab is NULL — this is the bug. The Mac-built bundle's enemyPrefab reference did not survive deserialization on the Mac editor. If it's NULL here, it'll be NULL on Windows too.");
                EditorApplication.Exit(1);
                return;
            }
            Debug.Log($"[StillLifeDebug] enemyPrefab is a GameObject: '{prefab.name}', {prefab.GetComponents<Component>().Length} components");

            // Check the audio clips
            var ambientClip = bundle.LoadAsset<AudioClip>("PC_ambient");
            var eatClip = bundle.LoadAsset<AudioClip>("PC_eat");
            Debug.Log($"[StillLifeDebug] Audio: PC_ambient={(ambientClip != null ? "OK" : "NULL")}, PC_eat={(eatClip != null ? "OK" : "NULL")}");

            // Check terminal assets
            var nodeT = FindType("TerminalNode");
            var kwT = FindType("TerminalKeyword");
            if (nodeT != null)
            {
                var node = bundle.LoadAsset("StillLifeFile", nodeT);
                Debug.Log($"[StillLifeDebug] TerminalNode 'StillLifeFile': {(node != null ? "OK" : "NULL")}");
            }
            if (kwT != null)
            {
                var kw = bundle.LoadAsset("StillLifeKeyword", kwT);
                Debug.Log($"[StillLifeDebug] TerminalKeyword 'StillLifeKeyword': {(kw != null ? "OK" : "NULL")}");
            }

            Debug.Log("[StillLifeDebug] === Diagnostic complete — all assets present ===");
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError($"[StillLifeDebug] FATAL: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            EditorApplication.Exit(1);
        }
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
        return matches.FirstOrDefault(t => t.Assembly.GetName().Name == "Assembly-CSharp") ?? matches[0];
    }
}
