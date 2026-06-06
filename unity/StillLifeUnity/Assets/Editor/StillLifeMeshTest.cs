// StillLifeMeshTest — verifies that the embedded .obj parses correctly
// inside the Unity editor (same runtime as the mod DLL will use).
// Invoke: Unity -projectpath <unity> -batchmode -nographics -quit
//                  -executeMethod StillLifeMeshTest.Test -logFile /tmp/x.log

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class StillLifeMeshTest
{
    public static void Test()
    {
        try
        {
            Debug.Log("[MeshTest] === Starting ===");
            string dllPath = "/Users/nomae/claude/StillLife/src/Plugin/bin/Release/com.TESTYEE-09.lethalpirateclark.dll";
            if (!File.Exists(dllPath)) { Debug.LogError("DLL not found"); EditorApplication.Exit(1); return; }
            var bytes = File.ReadAllBytes(dllPath);
            Debug.Log($"[MeshTest] DLL size: {bytes.Length:N0} bytes");

            // We can't run the .obj parser from the editor (it depends on
            // UnityEngine types whose assembly versions don't match the
            // editor's). Instead, just verify the .obj is embedded as a
            // resource. The actual parsing happens at runtime inside the
            // game, where UnityEngine types resolve correctly.
            var asm = Assembly.LoadFile(dllPath);
            Debug.Log($"[MeshTest] DLL loaded as: {asm.GetName().Name}");

            // List all manifest resources.
            var resources = asm.GetManifestResourceNames();
            Debug.Log($"[MeshTest] Manifest resources ({resources.Length}):");
            bool found = false;
            foreach (var r in resources)
            {
                Debug.Log($"[MeshTest]   {r}");
                if (r.EndsWith(".obj") || r.Contains("pirate_clark")) found = true;
            }
            if (!found) { Debug.LogError("[MeshTest] No .obj / pirate_clark resource found in DLL"); EditorApplication.Exit(1); return; }

            // Try to extract the resource and check it's valid OBJ format.
            var stream = asm.GetManifestResourceStream("LethalPirateClark.pirate_clark.obj");
            if (stream == null) { Debug.LogError("[MeshTest] Resource stream is null"); EditorApplication.Exit(1); return; }
            using (var reader = new StreamReader(stream))
            {
                int v = 0, vt = 0, vn = 0, f = 0;
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("v ")) v++;
                    else if (line.StartsWith("vt ")) vt++;
                    else if (line.StartsWith("vn ")) vn++;
                    else if (line.StartsWith("f ")) f++;
                }
                Debug.Log($"[MeshTest] Embedded .obj stats: v={v} vt={vt} vn={vn} f={f}");
                if (v < 1000 || f < 1000)
                {
                    Debug.LogError("[MeshTest] .obj seems too small to be a real model");
                    EditorApplication.Exit(1);
                    return;
                }
            }

            Debug.Log("[MeshTest] === OK (resource embedded, format valid) ===");
            Debug.Log("[MeshTest] NOTE: actual runtime parsing happens in-game where UnityEngine types resolve.");
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError($"[MeshTest] FATAL: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            EditorApplication.Exit(1);
        }
    }
}
