using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace StillLife;

// Loads the embedded .obj mesh (the real Pirate Clark model) and builds a
// Unity Mesh at runtime. The .obj is triangulated by Blender at export time,
// so each face line has exactly 3 vertices in the format v/vt/vn.
internal static class ObjMeshLoader
{
    public static Mesh? LoadEmbedded(string resourceName)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                Plugin.Log.LogError($"[StillLife] Embedded resource not found: {resourceName}");
                return null;
            }
            using var reader = new StreamReader(stream);
            return ParseObj(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[StillLife] LoadEmbedded({resourceName}) failed: {ex.Message}");
            return null;
        }
    }

    public static Mesh? ParseObj(string objText)
    {
        // Source lists (1-based in OBJ, 0-based here).
        var positions = new List<Vector3>(20000);
        var uvs = new List<Vector2>(20000);
        var normals = new List<Vector3>(20000);
        // Output (each face expands its 3 verts — no vertex sharing).
        var meshVerts = new List<Vector3>(60000);
        var meshUvs = new List<Vector2>(60000);
        var meshNormals = new List<Vector3>(60000);
        var triangles = new List<int>(60000);

        int lineNo = 0;
        using var reader = new StringReader(objText);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lineNo++;
            if (line.Length == 0 || line[0] == '#') continue;

            // Fast path dispatch by leading char.
            char c0 = line[0];
            if (c0 != 'v' && c0 != 'f' && c0 != 's') continue;
            char c1 = line.Length > 1 ? line[1] : ' ';

            // NOTE: startIdx is an index into the whitespace-split token array,
            // where the directive ("v"/"vt"/"vn") is token 0 and the numbers
            // start at token 1 — NOT a character offset into the line.
            if (c0 == 'v' && c1 == ' ')
            {
                if (TryReadVec3(line, 1, out var v)) positions.Add(v);
            }
            else if (c0 == 'v' && c1 == 't')
            {
                if (TryReadVec2(line, 1, out var v)) uvs.Add(v);
            }
            else if (c0 == 'v' && c1 == 'n')
            {
                if (TryReadVec3(line, 1, out var v)) normals.Add(v);
            }
            else if (c0 == 'f' && c1 == ' ')
            {
                if (!TryReadFace(line, positions, uvs, normals,
                        out var p, out var uv, out var nrm))
                {
                    Plugin.Log.LogWarning($"[StillLife] obj line {lineNo} face had an out-of-range index, skipping");
                    continue;
                }
                int baseIdx = meshVerts.Count;
                meshVerts.Add(p[0]); meshVerts.Add(p[1]); meshVerts.Add(p[2]);
                meshUvs.Add(uv[0]); meshUvs.Add(uv[1]); meshUvs.Add(uv[2]);
                meshNormals.Add(nrm[0]); meshNormals.Add(nrm[1]); meshNormals.Add(nrm[2]);
                triangles.Add(baseIdx);
                triangles.Add(baseIdx + 1);
                triangles.Add(baseIdx + 2);
            }
            // Ignore mtllib, usemtl, o, g, s, and other directives.
        }

        var mesh = new Mesh
        {
            name = "PirateClark",
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
        };
        mesh.SetVertices(meshVerts);
        mesh.SetUVs(0, meshUvs);
        mesh.SetNormals(meshNormals);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        Plugin.Log.LogInfo($"[StillLife] Loaded mesh: {meshVerts.Count} verts, {triangles.Count / 3} tris, {uvs.Count} uvs, {normals.Count} normals");
        return mesh;
    }

    // Read a face line: "f v1/vt1/vn1 v2/vt2/vn2 v3/vt3/vn3" (3 verts, triangulated).
    private static bool TryReadFace(
        string line,
        List<Vector3> positions, List<Vector2> uvs, List<Vector3> normals,
        out Vector3[] p, out Vector2[] uv, out Vector3[] nrm)
    {
        p = new Vector3[3];
        uv = new Vector2[3];
        nrm = new Vector3[3];

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return false;

        for (int i = 0; i < 3; i++)
        {
            var sp = parts[i + 1].Split('/');
            int pIdx = ParseObjIndex(sp[0], positions.Count);
            int uvIdx = (sp.Length > 1 && sp[1].Length > 0) ? ParseObjIndex(sp[1], uvs.Count) : -1;
            int nrmIdx = (sp.Length > 2 && sp[2].Length > 0) ? ParseObjIndex(sp[2], normals.Count) : -1;
            if (pIdx < 0 || pIdx >= positions.Count) return false;
            p[i] = positions[pIdx];
            uv[i] = (uvIdx >= 0 && uvIdx < uvs.Count) ? uvs[uvIdx] : Vector2.zero;
            nrm[i] = (nrmIdx >= 0 && nrmIdx < normals.Count) ? normals[nrmIdx] : Vector3.up;
        }
        return true;
    }

    private static bool TryReadVec3(string line, int startIdx, out Vector3 v)
    {
        v = default;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < startIdx + 3) return false;
        return TryParseF(parts[startIdx], out v.x)
            && TryParseF(parts[startIdx + 1], out v.y)
            && TryParseF(parts[startIdx + 2], out v.z);
    }

    private static bool TryReadVec2(string line, int startIdx, out Vector2 v)
    {
        v = default;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < startIdx + 2) return false;
        return TryParseF(parts[startIdx], out v.x)
            && TryParseF(parts[startIdx + 1], out v.y);
    }

    private static bool TryParseF(string s, out float f)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f);

    private static int ParseObjIndex(string s, int max)
    {
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return -1;
        if (n > 0) return n - 1;
        if (n < 0) return max + n;  // -1 = last, etc.
        return -1;
    }
}
