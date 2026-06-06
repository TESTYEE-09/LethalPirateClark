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
    // v1.3.2: track the last per-clone noteworthy-state key so we don't spam
    // the log every 0.5s. (h << 16) | (instanceID & 0xFFFF) — re-logs only
    // when the set of problems on this clone changes.
    private int _lastPerCloneKey;

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

            // v1.3.3: force the 1.25x scale on every clone, every watchdog pass.
            // The template's localScale is set to 1.25 in BuildPiratePrefab, but
            // the spawn pipeline (or NGO's NetworkTransform init) may reset it
            // on Instantiate. Re-apply it unconditionally so the model is the
            // right size no matter what.
            var s = go.transform.localScale;
            if (Mathf.Abs(s.x - 1.25f) > 0.01f ||
                Mathf.Abs(s.y - 1.25f) > 0.01f ||
                Mathf.Abs(s.z - 1.25f) > 0.01f)
            {
                go.transform.localScale = new Vector3(1.25f, 1.25f, 1.25f);
            }

            // v1.3.2 per-clone diagnostic: log a one-liner only when the
            // clone's state is non-default (inactive, scale != 1.25,
            // parented to the template, NetworkObject.IsSpawned == false).
            // Includes an int hash of the noteworthy state so we only re-log
            // when the state actually changes.
            bool isInActive = !go.activeSelf; // post-activation, this is false
            var parent = go.transform.parent;
            bool parentedToTemplate = parent != null && parent.name == "StillLifeEnemy";
            var scale = go.transform.localScale;
            bool scaleOff = Mathf.Abs(scale.x - 1.25f) > 0.01f
                         || Mathf.Abs(scale.y - 1.25f) > 0.01f
                         || Mathf.Abs(scale.z - 1.25f) > 0.01f;
            bool netSpawned = false;
            try
            {
                var no = ai.NetworkObject; // EnemyAI exposes NetworkObject
                if (no != null)
                {
                    var p = no.GetType().GetProperty("IsSpawned");
                    if (p != null) netSpawned = (bool)p.GetValue(no);
                }
            }
            catch { /* NetworkObject may be null; treat as not-spawned */ }

            if (isInActive || parentedToTemplate || scaleOff || !netSpawned)
            {
                // Compose a small int fingerprint of the noteworthy bits so we
                // re-log only when the set of problems changes per-clone.
                int h = (isInActive ? 1 : 0)
                      | (parentedToTemplate ? 2 : 0)
                      | (scaleOff ? 4 : 0)
                      | (netSpawned ? 0 : 8);
                int perCloneKey = (h << 16) | (go.GetInstanceID() & 0xFFFF);
                if (perCloneKey != _lastPerCloneKey)
                {
                    _lastPerCloneKey = perCloneKey;
                    Plugin.Log.LogInfo($"[StillLife] Watchdog per-clone state on '{go.name}' (id={go.GetInstanceID()}): " +
                        $"inactive={isInActive}, parentedToTemplate={parentedToTemplate}, " +
                        $"scale={scale:F2} (expected 1.25), NetworkObject.IsSpawned={netSpawned}.");
                }
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

