using System;
using System.Collections.Generic;
using GameNetcodeStuff;
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
    // v1.3.4: per-clone stuck-detection state. If a clone's transform hasn't
    // changed in 3 seconds, we assume it's stuck (agent not on NavMesh, or
    // AI loop not running for whatever reason) and teleport it next to a
    // live player. This is the absolute fallback for "enemy is alive but
    // not moving" — better to break the illusion than to be non-functional.
    private readonly Dictionary<int, (Vector3 pos, float time)> _stuck = new();

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

            // v1.3.4: stuck-detection. If this clone's transform hasn't moved
            // in 3+ seconds, the AI isn't running (or agent is off-mesh and
            // the v1.3.4 manual movement isn't reaching its target). Find
            // the nearest live player and teleport the clone to within 8m of
            // them. We only do this once per stuck-episode (we clear the
            // entry as soon as the clone moves) so we don't fight the AI.
            int id = go.GetInstanceID();
            Vector3 curPos = go.transform.position;
            float now = Time.realtimeSinceStartup;
            if (_stuck.TryGetValue(id, out var prev))
            {
                if (Vector3.Distance(curPos, prev.pos) < 0.25f)
                {
                    if (now - prev.time > 3f)
                    {
                        // Stuck for 3s. Teleport near the nearest player.
                        var target = FindNearestLivePlayer();
                        if (target != null)
                        {
                            // Pick a spot 8m from the player in a random
                            // direction. Doesn't have to be on a NavMesh —
                            // the manual movement in Update() will then
                            // walk the rest of the way.
                            var dir = UnityEngine.Random.insideUnitCircle.normalized;
                            var offset = new Vector3(dir.x, 0f, dir.y) * 8f;
                            var newPos = target.transform.position + offset;
                            newPos.y = target.transform.position.y;  // keep ground level
                            go.transform.position = newPos;
                            Plugin.Log.LogWarning($"[StillLife] Watchdog TELEPORTED stuck clone to {newPos:F1} (was at {curPos:F1}, stuck for {now - prev.time:F1}s).");
                            _stuck[id] = (newPos, now);
                            continue;
                        }
                    }
                }
                else
                {
                    // Moved — clear the stuck marker.
                    _stuck[id] = (curPos, now);
                }
            }
            else
            {
                _stuck[id] = (curPos, now);
            }
        }

        if (clones != _lastCloneCount)
        {
            _lastCloneCount = clones;
            Plugin.Log.LogInfo($"[StillLife] Watchdog: {clones} Pirate Clark clone(s) present (activated {activated} this pass). " +
                               "If this stays 0 while the enemy is 'spawned', the instance is never being instantiated (network-spawn issue).");
        }
    }

    private static PlayerControllerB? FindNearestLivePlayer()
    {
        if (StartOfRound.Instance == null) return null;
        // We just need any live player. Pick the lowest playerClientId as a
        // deterministic choice; the teleport target doesn't have to be the
        // closest, just a real player.
        PlayerControllerB? first = null;
        foreach (var p in StartOfRound.Instance.allPlayerScripts)
        {
            if (p == null || !p.isPlayerControlled || p.isPlayerDead) continue;
            if (first == null || p.playerClientId < first.playerClientId) first = p;
        }
        return first;
    }
}

