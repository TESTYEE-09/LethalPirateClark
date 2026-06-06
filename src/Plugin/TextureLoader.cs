using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace StillLife;

// Loads embedded PNG texture maps (the real Pirate Clark PBR maps) into Unity
// Texture2D at runtime, the same self-contained way the mesh is loaded.
internal static class TextureLoader
{
    public static Texture2D? LoadEmbedded(string resourceName, bool linear = false)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                Plugin.Log.LogWarning($"[StillLife] Embedded texture not found: {resourceName}");
                return null;
            }
            using var ms = new MemoryStream();
            stream.CopyTo(ms);

            // mipChain true for nicer distance rendering; linear for normal maps.
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true, linear: linear);
            if (!tex.LoadImage(ms.ToArray()))
            {
                Plugin.Log.LogWarning($"[StillLife] Texture2D.LoadImage failed for {resourceName}");
                return null;
            }
            tex.name = resourceName;
            tex.wrapMode = TextureWrapMode.Repeat;
            Plugin.Log.LogInfo($"[StillLife] Loaded texture {resourceName}: {tex.width}x{tex.height}");
            return tex;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[StillLife] LoadEmbedded texture {resourceName} failed: {ex.Message}");
            return null;
        }
    }
}
