using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.AI;

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

            // Scale is left exactly as the bundle prefab defines it (no
            // runtime scale-forcing — that distorted the real model).

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
                    // v1.3.5: only intervene after a LONG stall (6s), and land
                    // him on WALKABLE GROUND near a player — not a random 8m
                    // offset. The old random-direction teleport (every 3s,
                    // because the movement bug meant he never moved) dropped
                    // him inside walls or behind the player, which read as
                    // "he disappeared". With movement fixed this should never
                    // fire; it's now a genuine last resort that keeps him
                    // visible and on the NavMesh so the AI can take over.
                    if (now - prev.time > 6f)
                    {
                        var target = FindNearestLivePlayer();
                        if (target != null)
                        {
                            Vector3 landing = target.transform.position;
                            bool onMesh = NavMesh.SamplePosition(target.transform.position,
                                out var navHit, 18f, NavMesh.AllAreas);
                            if (onMesh) landing = navHit.position;

                            var navAgent = ai.agent;
                            if (onMesh && navAgent != null && navAgent.isActiveAndEnabled && navAgent.enabled)
                            {
                                try { navAgent.Warp(landing); }
                                catch { go.transform.position = landing; }
                            }
                            else
                            {
                                go.transform.position = landing;
                            }
                            Plugin.Log.LogWarning($"[StillLife] Watchdog recovered a stuck clone onto walkable ground at {landing:F1} (was stuck at {curPos:F1} for {now - prev.time:F1}s).");
                            _stuck[id] = (landing, now);
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

