using UnityEngine;

namespace StillLife.Behaviours;

/// <summary>
/// Persistent watchdog that activates spawned Pirate Clark instances no matter
/// HOW they were spawned. Our prefab template is inactive (so it stays dormant),
/// and clones inherit that inactive state — but patching RoundManager.
/// SpawnEnemyGameObject isn't enough because other mods (e.g. Imperium) run
/// their own spawn pipeline and bypass it. So instead of trusting one spawn
/// path, we poll for any StillLifeAI clone in the scene and switch it on.
///
/// Doubles as a diagnostic: it logs how many clones it sees, so a log can tell
/// us definitively whether instances exist-but-inactive (this fixes it) or are
/// never instantiated at all (a network-spawn problem — different fix).
/// </summary>
internal class StillLifeWatchdog : MonoBehaviour
{
    private float _timer;
    private int _lastCloneCount = -1;

    private void Update()
    {
        _timer += Time.deltaTime;
        if (_timer < 0.5f) return;
        _timer = 0f;

        // FindObjectsOfTypeAll includes INACTIVE objects (and the prefab
        // template), which is exactly what we need — a normal FindObjectsOfType
        // would never see an inactive clone.
        var all = Resources.FindObjectsOfTypeAll<StillLifeAI>();
        int clones = 0;
        int activated = 0;

        foreach (var ai in all)
        {
            if (ai == null) continue;
            var go = ai.gameObject;
            // The prefab template is named "StillLifeEnemy"; real spawns are
            // "StillLifeEnemy(Clone)". Never touch the template.
            if (!go.name.Contains("(Clone)")) continue;

            clones++;
            if (!go.activeSelf)
            {
                go.SetActive(true);
                activated++;
                Plugin.Log.LogInfo($"[StillLife] Watchdog ACTIVATED a Pirate Clark clone at {go.transform.position:F1} (was inactive).");
            }
        }

        if (clones != _lastCloneCount)
        {
            _lastCloneCount = clones;
            Plugin.Log.LogInfo($"[StillLife] Watchdog: {clones} Pirate Clark clone(s) present (activated {activated} this pass). " +
                               "If this stays 0 while the enemy is 'spawned', the instance is never being instantiated (network-spawn issue).");
        }
    }
}
