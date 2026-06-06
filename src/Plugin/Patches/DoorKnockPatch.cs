using HarmonyLib;
using StillLife.Behaviours;
using UnityEngine;

namespace StillLife.Patches;

/// <summary>
/// Lore: a Still Life can't open doors — it knocks, then destroys them.
/// The game already pathfinds enemies through doors via OpenDoorClientRpc on
/// the agent. We intercept the moment a Still Life's path is blocked by a
/// closed door and route it through the knock-then-break routine on the AI.
/// </summary>
[HarmonyPatch(typeof(EnemyAICollisionDetect),
    nameof(EnemyAICollisionDetect.OnTriggerStay))]
internal static class DoorKnockPatch
{
    [HarmonyPostfix]
    private static void Postfix(EnemyAICollisionDetect __instance, Collider other)
    {
        if (__instance.mainScript is not StillLifeAI stillLife) return;
        var door = other.GetComponentInParent<DoorLock>();
        if (door != null && door.isLocked == false && door.isDoorOpened == false)
            stillLife.OnBlockedByDoor(door);
    }
}
