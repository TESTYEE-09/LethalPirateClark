using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace StillLife;

// Decodes embedded 16-bit PCM WAV files into Unity AudioClips at runtime, the
// same self-contained way the mesh and textures are loaded (no asset bundle).
internal static class WavLoader
{
    public static AudioClip? LoadEmbedded(string resourceName)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                Plugin.Log.LogWarning($"[StillLife] Embedded audio not found: {resourceName}");
                return null;
            }
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ParseWav(ms.ToArray(), resourceName);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[StillLife] LoadEmbedded audio {resourceName} failed: {ex.Message}");
            return null;
        }
    }

    private static AudioClip? ParseWav(byte[] data, string name)
    {
        if (data.Length < 44
            || Encoding.ASCII.GetString(data, 0, 4) != "RIFF"
            || Encoding.ASCII.GetString(data, 8, 4) != "WAVE")
        {
            Plugin.Log.LogWarning($"[StillLife] {name}: not a RIFF/WAVE file.");
            return null;
        }

        int channels = 1, sampleRate = 44100, bits = 16;
        int dataOffset = -1, dataLen = 0;

        // Walk the chunk list to find "fmt " and "data" (word-aligned).
        int p = 12;
        while (p + 8 <= data.Length)
        {
            string id = Encoding.ASCII.GetString(data, p, 4);
            int size = BitConverter.ToInt32(data, p + 4);
            int body = p + 8;
            if (id == "fmt ")
            {
                channels = BitConverter.ToInt16(data, body + 2);
                sampleRate = BitConverter.ToInt32(data, body + 4);
                bits = BitConverter.ToInt16(data, body + 14);
            }
            else if (id == "data")
            {
                dataOffset = body;
                dataLen = size;
                break;
            }
            p = body + size + (size & 1);
        }

        if (dataOffset < 0)
        {
            Plugin.Log.LogWarning($"[StillLife] {name}: no data chunk.");
            return null;
        }
        if (bits != 16)
        {
            Plugin.Log.LogWarning($"[StillLife] {name}: only 16-bit PCM supported (got {bits}-bit).");
            return null;
        }

        // Clamp to the actual byte range in case the header lies.
        dataLen = Mathf.Min(dataLen, data.Length - dataOffset);
        int sampleCount = dataLen / 2;  // total int16 samples across all channels
        var floats = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
            floats[i] = BitConverter.ToInt16(data, dataOffset + i * 2) / 32768f;

        int perChannel = sampleCount / Mathf.Max(1, channels);
        var clip = AudioClip.Create(name, perChannel, channels, sampleRate, false);
        clip.SetData(floats, 0);
        Plugin.Log.LogInfo($"[StillLife] Loaded audio {name}: {perChannel} samples/ch, {channels}ch, {sampleRate}Hz.");
        return clip;
    }
}
