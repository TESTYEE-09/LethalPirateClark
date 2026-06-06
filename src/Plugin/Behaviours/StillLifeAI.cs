using System.Collections;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace StillLife.Behaviours;

/// <summary>
/// "The Still Life" — based on the Kane Pixels Backrooms entity: a failed copy
/// of a human made by the Backrooms. Faithful traits, weaponised for gameplay:
///
///  - Reads as a frozen, uncanny mannequin until provoked (its "paralyzed" trait).
///  - FREEZES while any player looks at it; stalks only when unobserved.
///  - Flickers the nearest lights while active; cuts them right before it lunges.
///  - Cannot open doors — knocks, then tears them down.
///  - On contact it GRABS rather than mauls.
///
/// Phase 2 — "the turn": a player it kills is not left as a corpse. The Backrooms
/// COPIES them: after a delay the body fills with white foam and rises as a new
/// Still Life wearing a warped version of that player, which hunts the survivors.
/// </summary>
public class StillLifeAI : EnemyAI
{
    private enum State { Dormant, Stalking, Grabbing }

    [SerializeField] private float baseSpeed = 3.2f;
    [SerializeField] private float maxSpeed = 8f;
    [SerializeField] private float accelPerSecondUnseen = 0.9f;
    [SerializeField] private float grabRange = 1.5f;
    [SerializeField] private float conversionDelay = 4f;

    // Set true on Still Lifes that were spawned from a converted player, so the
    // swarm doesn't recursively convert forever (configurable cap handled in Plugin).
    public bool spawnedFromPlayer;
    public int copiedFromPlayerId = -1;

    // Audio wired by the mod DLL at load time (clips live in the asset bundle).
    public AudioClip? eatClip;     // played on every client when it kills a player
    public AudioSource? voiceSource; // loops the ambient entity sound

    private PlayerControllerB? _target;
    private float _unseenTime;
    private bool _frozen;
    private float _nextFlickerToggle;
    private Coroutine? _doorRoutine;

    // v1.3.2 diagnostic: only log the full spawn fingerprint once per clone
    // (Start() can fire on inactive templates too, so without this guard we
    // would log the fingerprint for the template + every clone).
    private bool _startLogged;

    public override void Start()
    {
        try
        {
            if (!IsSpawned)
            {
                // The inert prefab template (kept active so Netcode can clone
                // it, but never network-spawned itself). Log a one-line fingerprint
                // for the template so a v1.3.2 log can tell template from clone.
                if (!_startLogged)
                {
                    _startLogged = true;
                    Plugin.Log.LogInfo($"[StillLife] CLONE FINGERPRINT (template, !IsSpawned): " +
                        $"name='{name}', pos={transform.position:F2}, scale={transform.localScale:F2}, " +
                        $"active={gameObject.activeSelf}/{gameObject.activeInHierarchy}");
                }
                return;
            }
            base.Start();
            baseSpeed = Plugin.MoveSpeed.Value;
            currentBehaviourStateIndex = (int)State.Dormant;
            Plugin.LiveStillLives++;
            // v1.0.4: log exactly where we spawned so the user can find us.
            Plugin.Log.LogInfo($"[StillLife] Pirate Clark SPAWNED at {transform.position:F1} (round time {StartOfRound.Instance?.currentLevel?.name ?? "unknown"}). LiveStillLives={Plugin.LiveStillLives}");

            // v1.3.3: force the voice source to 2D audio so it's audible from
            // anywhere on the map. The previous 3D positional setup (minDist=4,
            // maxDist=32) made the ambient loop inaudible when the clone
            // spawned far from the player. We keep the AudioListener-aware
            // settings but kill the spatial blend so volume is constant.
            if (voiceSource != null)
            {
                voiceSource.spatialBlend = 0f;  // pure 2D
                voiceSource.minDistance = 0f;
                voiceSource.maxDistance = 500f;
                voiceSource.volume = 1f;
            }

            // v1.3.3 diagnostic: one comprehensive line per clone with every
            // field that could explain "floats, doesn't move, no sound, small".
            if (!_startLogged)
            {
                _startLogged = true;
                string voiceStatus = voiceSource != null
                    ? $"play={voiceSource.isPlaying} clip={(voiceSource.clip != null ? "set" : "NULL")} " +
                      $"blend={voiceSource.spatialBlend:F1} min={voiceSource.minDistance:F1} max={voiceSource.maxDistance:F1}"
                    : "NULL";
                string agentStatus = agent != null
                    ? $"onNavMesh={agent.isOnNavMesh} enabled={agent.enabled} stopped={agent.isStopped}"
                    : "NULL";
                Plugin.Log.LogInfo($"[StillLife] CLONE FINGERPRINT: " +
                    $"name='{name}', " +
                    $"pos={transform.position:F2}, scale={transform.localScale:F2}, " +
                    $"active={gameObject.activeSelf}/{gameObject.activeInHierarchy}, " +
                    $"IsOwner={IsOwner}, IsServer={IsServer}, IsHost={IsHost}, IsClient={IsClient}, IsSpawned={IsSpawned}, " +
                    $"NetworkObjectId={(NetworkObject?.NetworkObjectId ?? 0)}, " +
                    $"OwnerClientId={(NetworkObject?.OwnerClientId ?? 0)}, " +
                    $"enemyType={(enemyType != null ? enemyType.enemyName : "NULL")}, " +
                    $"agent=({agentStatus}), voice=({voiceStatus})");
            }

            // Make sure the NavMeshAgent is actually on a NavMesh, or he can't
            // move at all (and the game logs nothing). Snap to the nearest mesh
            // point if the spawn dropped him just off it; warn if there's no
            // NavMesh nearby (e.g. spawned on the ship rather than the facility).
            if (agent != null)
            {
                if (!agent.isOnNavMesh)
                {
                    if (NavMesh.SamplePosition(transform.position, out var hit, 20f, NavMesh.AllAreas))
                    {
                        agent.Warp(hit.position);
                        Plugin.Log.LogInfo($"[StillLife] Snapped onto NavMesh at {hit.position:F1}.");
                    }
                    else
                    {
                        Plugin.Log.LogWarning("[StillLife] No NavMesh within 20m of spawn — Pirate Clark can't path here.");
                    }
                }
            }
            else
            {
                Plugin.Log.LogWarning("[StillLife] agent is null on this clone — NavMeshAgent failed to attach. AI cannot move.");
            }

            // Begin the looping ambient entity sound.
            if (voiceSource != null && voiceSource.clip != null && !voiceSource.isPlaying)
            {
                voiceSource.Play();
                Plugin.Log.LogInfo($"[StillLife] voiceSource.Play() called. isPlaying={voiceSource.isPlaying}");
            }
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogError($"[StillLife] Start() failed: {ex.GetType().Name}: {ex.Message}");
            Plugin.Log.LogError($"[StillLife] Stack: {ex.StackTrace}");
        }
    }

    public override void DoAIInterval()
    {
        base.DoAIInterval();
        if (isEnemyDead || StartOfRound.Instance.allPlayersDead) return;

        _target = FindNearestPlayer(50f);
        if (_target == null)
        {
            SwitchState(State.Dormant);
            return;
        }

        if (currentBehaviourStateIndex == (int)State.Dormant)
            SwitchState(State.Stalking);

        if (!_frozen)
            SetDestinationToPosition(_target.transform.position, checkForPath: false);
    }

    public override void Update()
    {
        try
        {
            if (!IsSpawned) return;
            base.Update();
            if (isEnemyDead) return;

            bool seen = AnyPlayerSeesMe();
            bool freeze = Plugin.FreezeWhenWatched.Value && seen;
            if (freeze)
            {
                _unseenTime = 0f;
                Freeze(true);
            }
            else
            {
                // Movie behaviour (default): keep advancing even while watched.
                // Only ramp the unseen-speed bonus while he's actually unseen;
                // hold base speed when observed.
                if (!seen) _unseenTime += Time.deltaTime;
                Freeze(false);
                agent.speed = Mathf.Min(maxSpeed, baseSpeed + _unseenTime * accelPerSecondUnseen);
            }

            if (currentBehaviourStateIndex != (int)State.Dormant)
                FlickerNearbyLights(active: !seen);

            if (IsServer && _target != null && !_frozen)
                TryGrab();
        }
        catch (System.Exception ex)
        {
            // Catch all per-frame exceptions so one bad call doesn't kill the
            // enemy silently. Throttle logging to once per second so we don't
            // flood the BepInEx log.
            if (Time.time - _lastUpdateErrorTime > 1f)
            {
                _lastUpdateErrorTime = Time.time;
                Plugin.Log.LogError($"[StillLife] Update() exception: {ex.GetType().Name}: {ex.Message}");
                Plugin.Log.LogError($"[StillLife] Stack: {ex.StackTrace}");
            }
        }
    }

    private float _lastUpdateErrorTime;

    // --- Core hunting -------------------------------------------------------

    private void TryGrab()
    {
        float dist = Vector3.Distance(transform.position, _target!.transform.position);
        if (dist <= grabRange && currentBehaviourStateIndex != (int)State.Grabbing)
        {
            SwitchState(State.Grabbing);
            GrabPlayerClientRpc((int)_target.playerClientId);
        }
    }

    [ClientRpc]
    private void GrabPlayerClientRpc(int playerId)
    {
        var player = StartOfRound.Instance.allPlayerScripts[playerId];
        creatureAnimator?.SetTrigger("grab");

        // The "eating" sound — only ever plays when Pirate Clark kills a player.
        if (eatClip != null && creatureSFX != null)
            creatureSFX.PlayOneShot(eatClip);

        if (player == GameNetworkManager.Instance.localPlayerController)
        {
            // Lethal kill — flagged so the corpse can be converted server-side.
            player.KillPlayer(Vector3.zero, spawnBody: true, CauseOfDeath.Suffocation);
        }

        if (IsServer)
            StartCoroutine(ConvertCorpseAfterDelay(playerId));
    }

    // --- Phase 2: copy the dead player into a new Still Life -----------------

    private IEnumerator ConvertCorpseAfterDelay(int playerId)
    {
        yield return new WaitForSeconds(conversionDelay);
        if (!Plugin.ConversionEnabled.Value) yield break;
        if (Plugin.LiveStillLives >= Plugin.MaxStillLives.Value) yield break;

        var player = StartOfRound.Instance.allPlayerScripts[playerId];
        Vector3 spawnPos = player.deadBody != null
            ? player.deadBody.transform.position
            : player.transform.position;

        // Reuse the same EnemyType the game spawned us from. This returns a
        // NetworkObjectReference; resolve it to reach the spawned AI component.
        var copyRef = RoundManager.Instance.SpawnEnemyGameObject(
            spawnPos, transform.eulerAngles.y, -1, enemyType);
        if (copyRef.TryGet(out NetworkObject netObj))
        {
            var ai = netObj.GetComponentInChildren<StillLifeAI>();
            if (ai != null)
            {
                ai.spawnedFromPlayer = true;
                ai.copiedFromPlayerId = playerId;
            }
        }

        // Hide the original corpse so it visually "becomes" the new entity.
        if (player.deadBody != null) player.deadBody.gameObject.SetActive(false);

        Plugin.Log.LogInfo($"Still Life copied player {playerId} into a new entity.");
    }

    // --- Light flicker & doors ----------------------------------------------

    private void FlickerNearbyLights(bool active)
    {
        if (Time.time < _nextFlickerToggle) return;
        _nextFlickerToggle = Time.time + Random.Range(0.05f, active ? 0.4f : 1.5f);

        // Resolve the "Room" layer once. If it doesn't exist, fall back to
        // Default so the call doesn't throw every frame.
        int roomLayer = LayerMask.NameToLayer("Room");
        int layerMask = (roomLayer < 0) ? ~0 : (1 << roomLayer);
        // Cache the resolved mask on first call so we don't do the
        // NameToLayer lookup every frame.
        if (_roomLayerMask == 0) _roomLayerMask = layerMask;

        foreach (var col in Physics.OverlapSphere(transform.position, 12f, _roomLayerMask))
        {
            var light = col.GetComponentInChildren<Light>();
            if (light != null)
                light.enabled = active ? (Random.value > 0.35f) : true;
        }
    }

    private int _roomLayerMask;

    /// <summary>
    /// Hook for the door-collision patch: knock first, then break.
    /// Called when the agent path is blocked by a closed door.
    /// </summary>
    public void OnBlockedByDoor(DoorLock door)
    {
        if (_doorRoutine != null) return;
        _doorRoutine = StartCoroutine(KnockThenBreak(door));
    }

    private IEnumerator KnockThenBreak(DoorLock door)
    {
        creatureAnimator?.SetTrigger("knock");
        for (int i = 0; i < 3; i++)
        {
            // play knock SFX via creatureSFX if assigned in the prefab
            creatureSFX?.PlayOneShot(creatureSFX.clip);
            yield return new WaitForSeconds(0.6f);
            if (door == null) break;
        }
        if (door != null && IsServer)
            door.OpenOrCloseDoor(_target);  // "tears it down"
        _doorRoutine = null;
    }

    // --- Helpers ------------------------------------------------------------

    private void Freeze(bool value)
    {
        if (_frozen == value) return;
        _frozen = value;
        agent.isStopped = value;
        creatureAnimator?.SetBool("frozen", value);
        if (value) agent.velocity = Vector3.zero;
    }

    private bool AnyPlayerSeesMe()
    {
        foreach (var p in StartOfRound.Instance.allPlayerScripts)
        {
            if (!p.isPlayerControlled || p.isPlayerDead) continue;
            if (p.HasLineOfSightToPosition(transform.position + Vector3.up,
                    width: 70f, range: 60, proximityAwareness: 2))
                return true;
        }
        return false;
    }

    private PlayerControllerB? FindNearestPlayer(float maxDist)
    {
        PlayerControllerB? best = null;
        float bestDist = maxDist;
        foreach (var p in StartOfRound.Instance.allPlayerScripts)
        {
            if (!p.isPlayerControlled || p.isPlayerDead) continue;
            float d = Vector3.Distance(transform.position, p.transform.position);
            if (d < bestDist) { bestDist = d; best = p; }
        }
        return best;
    }

    private void SwitchState(State s)
    {
        if (currentBehaviourStateIndex == (int)s) return;
        SwitchToBehaviourStateOnLocalClient((int)s);
        if (IsServer) SwitchToBehaviourClientRpc((int)s);
    }

    public override void HitEnemy(int force = 1, PlayerControllerB? playerWhoHit = null,
        bool playHitSFX = false, int hitID = -1)
    {
        base.HitEnemy(force, playerWhoHit, playHitSFX, hitID);
        if (isEnemyDead) return;
        // Lore: feels no pain, but the white foam body still ruptures eventually.
        enemyHP -= force;
        if (enemyHP <= 0 && IsOwner)
            KillEnemyOnOwnerClient();
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        Plugin.LiveStillLives = Mathf.Max(0, Plugin.LiveStillLives - 1);
    }
}
