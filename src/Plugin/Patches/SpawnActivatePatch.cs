using System.Reflection;
using HarmonyLib;
using StillLife.Behaviours;
using Unity.Netcode;
using UnityEngine;

namespace StillLife.Patches;

/// <summary>
/// Our enemy prefab is built at runtime as an INACTIVE GameObject so it stays
/// dormant (no rendering, no AI) while it lives in the DontDestroyOnLoad scene
/// as a clone template. The game spawns enemies with plain Instantiate, and the
/// clone inherits that inactive state — real bundle-based enemy prefabs are
/// active, so this never bites them, but it left Pirate Clark spawned-yet-invisible
/// (tracked as a live entity, but Start() never ran). Re-activate our instances
/// the moment the game spawns them.
/// </summary>
[HarmonyPatch(typeof(RoundManager), nameof(RoundManager.SpawnEnemyGameObject))]
internal static class SpawnActivatePatch
{
    private static bool _loggedFirstCall;
    // v1.3.2 diagnostic: per-spawn fingerprint. Throttle to the first N spawns
    // so a busy session doesn't flood the log.
    private static int _spawnsLogged;

    [HarmonyPostfix]
    private static void Postfix(NetworkObjectReference __result)
    {
        // One-time confirmation that the vanilla spawn path is actually being
        // used (some mods bypass it). If this never logs, the watchdog is doing
        // all the work.
        if (!_loggedFirstCall)
        {
            _loggedFirstCall = true;
            Plugin.Log.LogInfo("[StillLife] SpawnEnemyGameObject postfix is live (vanilla spawn path in use).");
        }

        if (!__result.TryGet(out NetworkObject netObj)) return;

        // includeInactive: true — the root is inactive, so a normal search misses it.
        var ai = netObj.GetComponentInChildren<StillLifeAI>(true);
        if (ai == null) return;

        var go = ai.gameObject;
        if (!go.activeSelf)
        {
            go.SetActive(true);
            Plugin.Log.LogInfo($"[StillLife] Activated spawned Pirate Clark instance at {go.transform.position:F1}.");
        }

        // v1.3.2 diagnostic: per-spawn fingerprint. Log the first 8 spawns
        // (== Spawn.MaxCount). After that, stop to keep the log readable.
        if (_spawnsLogged < 8)
        {
            _spawnsLogged++;
            // Read the resolved NetworkObject's hash (via reflection on the
            // GlobalObjectIdHash field) and IsSpawned so we can see if NGO
            // actually spawned the clone, vs the call returning a stale ref.
            uint hash = 0;
            bool isSpawned = false;
            try
            {
                var t = typeof(NetworkObject);
                var f = t.GetField("GlobalObjectIdHash", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null) hash = (uint)f.GetValue(netObj);
                var p = t.GetProperty("IsSpawned");
                if (p != null) isSpawned = (bool)p.GetValue(netObj);
            }
            catch { /* ignore — already logging from watchdog */ }

            Plugin.Log.LogInfo($"[StillLife] Spawn-pipeline fingerprint #{_spawnsLogged}: " +
                $"name='{go.name}', " +
                $"scene='{go.scene.name}', " +
                $"pos={go.transform.position:F2}, " +
                $"scale={go.transform.localScale:F2}, " +
                $"active={go.activeSelf}/{go.activeInHierarchy}, " +
                $"NetworkObjectId={netObj.NetworkObjectId}, " +
                $"GlobalObjectIdHash=0x{hash:X8}, " +
                $"NetworkObject.IsSpawned={isSpawned}, " +
                $"enemyType={(ai.enemyType != null ? ai.enemyType.enemyName : "NULL")}");
        }
    }
}

