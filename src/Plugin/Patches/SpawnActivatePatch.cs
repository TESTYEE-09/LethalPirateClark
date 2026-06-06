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
    [HarmonyPostfix]
    private static void Postfix(NetworkObjectReference __result)
    {
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
    }
}
