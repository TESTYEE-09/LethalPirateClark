using HarmonyLib;
using StillLife.Behaviours;
using Unity.Netcode;

namespace StillLife.Patches;

/// <summary>
/// Safety net: the bundle prefab spawns active, so this is normally a no-op —
/// but if some other mod's spawn pipeline (or a future game build) hands us an
/// inactive instance, re-activate it the moment it's spawned so Start() runs.
/// </summary>
[HarmonyPatch(typeof(RoundManager), nameof(RoundManager.SpawnEnemyGameObject))]
internal static class SpawnActivatePatch
{
    [HarmonyPostfix]
    private static void Postfix(NetworkObjectReference __result)
    {
        if (!__result.TryGet(out NetworkObject netObj)) return;

        // includeInactive: true — an inactive root would be missed otherwise.
        var ai = netObj.GetComponentInChildren<StillLifeAI>(true);
        if (ai == null) return;

        var go = ai.gameObject;
        if (!go.activeSelf)
        {
            go.SetActive(true);
            Plugin.Log.LogInfo($"[StillLife] Re-activated an inactive Pirate Clark instance at {go.transform.position:F1}.");
        }
    }
}

