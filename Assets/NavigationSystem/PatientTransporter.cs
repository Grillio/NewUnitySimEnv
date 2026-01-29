// PatientTransporter.cs
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(SphereCollider))]
public class PatientTransporter : MonoBehaviour
{
    public enum TransporterState { Charging, Idle, MovingToTask, InTask, MovingToIdle }

    [Serializable]
    public struct Status
    {
        public TransporterState State;
        public float TimeToCompleteCurrentStateSeconds; // SIM seconds remaining (best-effort)
        public Status(TransporterState state, float timeRemainingSeconds)
        {
            State = state;
            TimeToCompleteCurrentStateSeconds = Mathf.Max(0f, timeRemainingSeconds);
        }
    }

    private enum TaskPhase { None, Mounting, Traveling, Unmounting }

    [Header("Movement (meters per SIM second)")]
    [SerializeField] private float baseMoveSpeed = 1.5f;

    [Header("Task Timing (SIM seconds)")]
    [SerializeField] private float mountSpeed = 5f;
    [SerializeField] private float unmountSpeed = 5f;

    [Header("Energy")]
    [SerializeField] private bool isRobotic = true;
    [SerializeField] private float rechargeRate = 1f;
    [SerializeField] private float dischargeRate = 1f;

    [Header("Status")]
    [SerializeField] private Status status = new Status(TransporterState.Idle, 0f);

    [Header("Task Queue (Max 2)")]
    [SerializeField] private TaskData[] tasks = new TaskData[2];
    [SerializeField, Range(0, 2)] private int taskCount = 0;

    [Header("Nav Defaults")]
    [SerializeField] private float stepMeters = 2f;
    [SerializeField] private bool sampleToNavMesh = true;
    [SerializeField] private float sampleRadius = 2f;
    [SerializeField] private int areaMask = NavMesh.AllAreas;

    [Header("Idle Roam Areas")]
    [Tooltip("When Idle with no tasks, transporter wanders to random NavMesh points near these transforms.")]
    [SerializeField] private Transform[] idleAreas;

    [Tooltip("Radius around chosen idle area transform to pick a random point (meters).")]
    [SerializeField] private float idleAreaPickRadius = 4f;

    [Tooltip("SIM seconds to wait after reaching an idle roam destination before choosing a new one.")]
    [SerializeField] private float idleRoamWaitSimSeconds = 3f;

    // --------------------------------------------------
    // CROWD / PROXIMITY SLOWDOWN (trigger-sphere based)
    // --------------------------------------------------
    [Header("Crowd Slowdown (Trigger Sphere)")]
    [Tooltip("Radius of the detection sphere (meters).")]
    [SerializeField] private float crowdRadius = 2.5f;

    [Tooltip("Only objects with this tag count toward crowding.")]
    [SerializeField] private string crowdTag = "Other";

    [Tooltip("Only objects on these layers count. IMPORTANT: do NOT include Default.")]
    [SerializeField] private LayerMask crowdDetectableLayers = ~0;

    [Tooltip("No effect when there is only 1 other object nearby. Slowdown starts at this count.")]
    [SerializeField] private int minPeopleForLerp = 2;

    [Tooltip("Max slowdown reached at this count (and above).")]
    [SerializeField] private int maxPeopleForLerp = 6;

    [Tooltip("Stacking slowdown weight per person (0.10 = 10% per person, before capping).")]
    [SerializeField] private float slowdownPerPersonWeight = 0.08f;

    [Tooltip("Maximum total slowdown percent cap (0.6 = max 60% slowdown).")]
    [SerializeField, Range(0f, 0.95f)] private float maxTotalSlowdownPercent = 0.6f;

    private SphereCollider crowdSphere;
    private readonly HashSet<Collider> nearby = new HashSet<Collider>();

    [Header("Debug")]
    [SerializeField] private bool logMoves = false;

    // Cached components
    private NavMeshAgent agent;
    private Rigidbody rb;

    // Runtime nav
    private NavMeshPathUtility nav;

    // Task movement runtime
    private Vector3[] pathToStart;
    private int pathToStartIndex;

    private Vector3[] pathTask;
    private int pathTaskIndex;

    private TaskPhase phase = TaskPhase.None;
    private float phaseTimer = 0f; // SIM seconds remaining for Mount/Unmount phases

    // Idle roam runtime
    private Vector3[] idleRoamPath;
    private int idleRoamIndex;
    private float idleWaitTimerSim = 0f;

    [Header("Legacy (Manager may read)")]
    [Tooltip("SIM seconds to complete current queued tasks (not real seconds).")]
    [SerializeField] private float timeToCompleteCurrentTasks = 0f;

    public float TransportationSpeed => baseMoveSpeed;
    public float MountDurationSeconds => mountSpeed;
    public float UnmountDurationSeconds => unmountSpeed;
    public float TimeToCompleteCurrentTasks => timeToCompleteCurrentTasks;

    public bool IsRobotic => isRobotic;
    public float RechargeRate => rechargeRate;
    public float DischargeRate => dischargeRate;
    public Status CurrentStatus => status;

    // ============================================================
    // Unity
    // ============================================================

    private void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        rb = GetComponent<Rigidbody>();

        // We drive movement ourselves. Disable agent so it cannot override.
        if (agent != null && agent.enabled)
            agent.enabled = false;

        // Setup crowd sphere trigger
        crowdSphere = GetComponent<SphereCollider>();
        crowdSphere.isTrigger = true;
        crowdSphere.radius = Mathf.Max(0.01f, crowdRadius);

        // IMPORTANT per your request:
        // - Keep this collider on Default layer + Default tag (do not change here).
        // - We ignore Default-layer objects in OnTriggerEnter, so spheres won't count spheres.
    }

    private void Start()
    {
#if UNITY_2022_2_OR_NEWER
        ToyFleetManager mgr = FindFirstObjectByType<ToyFleetManager>();
#else
        ToyFleetManager mgr = FindObjectOfType<ToyFleetManager>();
#endif
        if (mgr == null)
        {
            Debug.LogWarning($"[PatientTransporter] No ToyFleetManager found in scene for '{name}'.");
            return;
        }

        if (isRobotic) mgr.AddRoboticTransporter(this);
        else mgr.AddHumanTransporter(this);

        nav = NavMeshPathUtility.I;
    }

    private void OnValidate()
    {
        // keep collider radius synced in editor
        if (crowdSphere == null) crowdSphere = GetComponent<SphereCollider>();
        if (crowdSphere != null)
        {
            crowdSphere.isTrigger = true;
            crowdSphere.radius = Mathf.Max(0.01f, crowdRadius);
        }

        minPeopleForLerp = Mathf.Max(2, minPeopleForLerp);
        maxPeopleForLerp = Mathf.Max(minPeopleForLerp, maxPeopleForLerp);
        slowdownPerPersonWeight = Mathf.Max(0f, slowdownPerPersonWeight);
        maxTotalSlowdownPercent = Mathf.Clamp(maxTotalSlowdownPercent, 0f, 0.95f);
    }

    // Tick ONCE PER RENDERED FRAME for determinism with Pause+Step.
    private void Update()
    {
        TickFrame();
    }

    // ============================================================
    // Trigger-based crowd detection
    // ============================================================

    private void OnTriggerEnter(Collider other)
    {
        if (other == null) return;
        if (other.attachedRigidbody == rb) return; // ignore self

        // IMPORTANT: ignore anything on Default layer (this prevents sphere-sphere counting)
        if (other.gameObject.layer == LayerMask.NameToLayer("Default"))
            return;

        // LayerMask filter (and you should exclude Default from this mask too)
        if ((crowdDetectableLayers.value & (1 << other.gameObject.layer)) == 0)
            return;

        if (!other.CompareTag(crowdTag))
            return;

        nearby.Add(other);
    }

    private void OnTriggerExit(Collider other)
    {
        if (other == null) return;
        nearby.Remove(other);
    }

    private int GetNearbyCountPruned()
    {
        // Remove destroyed colliders safely
        if (nearby.Count == 0) return 0;

        // Copy to avoid modifying while iterating set
        // (small N expected; this is fine)
        var toRemove = (List<Collider>)null;
        foreach (var c in nearby)
        {
            if (c == null)
            {
                toRemove ??= new List<Collider>(4);
                toRemove.Add(c);
            }
        }
        if (toRemove != null)
            for (int i = 0; i < toRemove.Count; i++)
                nearby.Remove(toRemove[i]);

        return nearby.Count;
    }

    private float GetCrowdSpeedMultiplier()
    {
        int count = GetNearbyCountPruned();

        // Your requirement: only 1 other => no effect.
        // So 0 or 1 => multiplier = 1
        if (count <= 1) return 1f;

        // Start lerp at minPeopleForLerp (>=2)
        if (count < minPeopleForLerp) return 1f;

        float t = Mathf.InverseLerp(minPeopleForLerp, maxPeopleForLerp, count);

        // stacking slowdown: count * weight, then scaled by lerp, then capped
        float stacked = count * slowdownPerPersonWeight;

        // Lerp makes it ramp from 0..stacked (but only within min..max band)
        float slowdown = Mathf.Lerp(0f, stacked, t);

        // Cap total slowdown
        slowdown = Mathf.Min(slowdown, maxTotalSlowdownPercent);

        return Mathf.Clamp01(1f - slowdown);
    }

    private float EffectiveMoveSpeed()
    {
        return Mathf.Max(0f, baseMoveSpeed) * GetCrowdSpeedMultiplier();
    }

    // ============================================================
    // Simulation time source (robust: property or field)
    // ============================================================

    private float GetSimDtPerFrame()
    {
        var seq = SequencerSubSys.I;
        if (seq == null) return 1f;

        try
        {
            Type t = seq.GetType();

            var p = t.GetProperty("SecondsPerFrame", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.PropertyType == typeof(float))
            {
                float v = (float)p.GetValue(seq, null);
                return Mathf.Max(0.000001f, v);
            }

            var f = t.GetField("secondsPerFrame", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null && f.FieldType == typeof(float))
            {
                float v = (float)f.GetValue(seq);
                return Mathf.Max(0.000001f, v);
            }
        }
        catch { /* ignore */ }

        return 1f;
    }

    // --------------------------------------------------------------------
    // BACK-COMPAT WRAPPER (ToyFleetManager expects this exact method name)
    // Returns SIM seconds (not real seconds).
    // --------------------------------------------------------------------
    public float CalculateEstimatedTimeToCompleteWithReposition(
        Vector3 newStartWorld,
        Vector3 newEndWorld,
        NavMeshPathUtility nav,
        int areaMask = NavMesh.AllAreas,
        float stepMeters = 2f,
        bool sampleToNavMesh = true,
        float sampleRadius = 2f)
    {
        return CalculateEstimatedTimeToCompleteWithReposition_SimSeconds(
            newStartWorld,
            newEndWorld,
            nav,
            areaMask,
            stepMeters,
            sampleToNavMesh,
            sampleRadius
        );
    }

    // ============================================================
    // Public API (ToyFleetManager should call this)
    // ============================================================

    public bool TryAddTask(
        TaskData newTask,
        NavMeshPathUtility nav,
        int areaMask = NavMesh.AllAreas,
        float stepMeters = 2f,
        bool sampleToNavMesh = true,
        float sampleRadius = 2f)
    {
        if (nav == null) return false;
        if (baseMoveSpeed <= 0f) return false;
        if (status.State == TransporterState.Charging) return false;
        if (newTask.TargetA == null || newTask.TargetB == null) return false;

        this.nav = nav;
        this.areaMask = areaMask;
        this.stepMeters = Mathf.Max(0.1f, stepMeters);
        this.sampleToNavMesh = sampleToNavMesh;
        this.sampleRadius = Mathf.Max(0.01f, sampleRadius);

        // Compute task path TargetA -> TargetB
        if (!nav.TryGetSegmentedPath(
                newTask.TargetA,
                newTask.TargetB,
                out Vector3[] taskWaypoints,
                out float taskDist,
                out _,
                this.stepMeters,
                this.areaMask,
                this.sampleToNavMesh,
                this.sampleRadius))
            return false;

        newTask.Path = taskWaypoints;

        // NOTE: ETA planning uses baseMoveSpeed (stable). Actual movement uses EffectiveMoveSpeed (crowd-adjusted).
        float effPlanSpeed = Mathf.Max(0.0001f, baseMoveSpeed);
        float travelSimSeconds = taskDist / effPlanSpeed;
        newTask.TimeToComplete = Mathf.Max(0f, mountSpeed) + travelSimSeconds + Mathf.Max(0f, unmountSpeed);

        // Cancel idle roaming immediately if we get a real task
        CancelIdleRoam();

        if (taskCount == 0)
        {
            tasks[0] = newTask;
            taskCount = 1;
            EnsureExecuting();
            RecomputeLegacyETA_SimSeconds();
            return true;
        }

        int currentPriority = tasks[0].Priority;

        // Preempt if higher priority than current
        if (newTask.Priority > currentPriority)
        {
            if (taskCount >= 2) return false; // no room to push old current

            tasks[1] = tasks[0];
            taskCount = 2;

            tasks[0] = newTask;

            BeginRepositionToCurrentStart();
            RecomputeLegacyETA_SimSeconds();
            return true;
        }

        // Otherwise enqueue if space
        if (taskCount >= 2) return false;
        tasks[1] = newTask;
        taskCount = 2;

        EnsureExecuting();
        RecomputeLegacyETA_SimSeconds();
        return true;
    }

    /// <summary>
    /// Estimate SIM seconds to finish current queue then do (newStart->newEnd).
    /// Planning estimate uses baseMoveSpeed (stable). Actual movement is crowd-adjusted.
    /// </summary>
    public float CalculateEstimatedTimeToCompleteWithReposition_SimSeconds(
        Vector3 newStartWorld,
        Vector3 newEndWorld,
        NavMeshPathUtility nav,
        int areaMask = NavMesh.AllAreas,
        float stepMeters = 2f,
        bool sampleToNavMesh = true,
        float sampleRadius = 2f)
    {
        if (nav == null || baseMoveSpeed <= 0f)
            return float.PositiveInfinity;

        float effPlanSpeed = Mathf.Max(0.0001f, baseMoveSpeed);

        float totalSim = 0f;
        Vector3 pos = GetPos();

        for (int i = 0; i < taskCount; i++)
        {
            if (tasks[i].TargetA == null || tasks[i].TargetB == null)
                return float.PositiveInfinity;

            Vector3 start = tasks[i].TargetA.transform.position;
            Vector3 end = tasks[i].TargetB.transform.position;

            if (!nav.TryGetSegmentedPath(pos, start, out _, out float repositionDist, out _, stepMeters, areaMask, sampleToNavMesh, sampleRadius))
                return float.PositiveInfinity;

            totalSim += repositionDist / effPlanSpeed;

            float taskSim = tasks[i].TimeToComplete;
            if (taskSim <= 0f)
            {
                if (!nav.TryGetSegmentedPath(start, end, out _, out float taskDist, out _, stepMeters, areaMask, sampleToNavMesh, sampleRadius))
                    return float.PositiveInfinity;

                taskSim = Mathf.Max(0f, mountSpeed) + (taskDist / effPlanSpeed) + Mathf.Max(0f, unmountSpeed);
            }

            totalSim += taskSim;
            pos = end;
        }

        if (!nav.TryGetSegmentedPath(pos, newStartWorld, out _, out float newRepositionDist, out _, stepMeters, areaMask, sampleToNavMesh, sampleRadius))
            return float.PositiveInfinity;

        totalSim += newRepositionDist / effPlanSpeed;

        if (!nav.TryGetSegmentedPath(newStartWorld, newEndWorld, out _, out float newTaskDist, out _, stepMeters, areaMask, sampleToNavMesh, sampleRadius))
            return float.PositiveInfinity;

        totalSim += Mathf.Max(0f, mountSpeed) + (newTaskDist / effPlanSpeed) + Mathf.Max(0f, unmountSpeed);

        return totalSim;
    }

    // ============================================================
    // Execution loop (frame-driven)
    // ============================================================

    private void TickFrame()
    {
        float dtSim = GetSimDtPerFrame();
        float effMove = EffectiveMoveSpeed();

        // best-effort countdown
        if (status.State != TransporterState.Idle)
            status.TimeToCompleteCurrentStateSeconds = Mathf.Max(0f, status.TimeToCompleteCurrentStateSeconds - dtSim);

        if (status.State == TransporterState.Charging)
            return;

        // TASKS TAKE PRIORITY OVER IDLE ROAM
        if (taskCount > 0)
        {
            if (status.State == TransporterState.MovingToIdle)
                CancelIdleRoam();

            TickTasks(dtSim, effMove);
            return;
        }

        TickIdleRoam(dtSim, effMove);
    }

    private void TickTasks(float dtSim, float effMove)
    {
        if (status.State == TransporterState.Idle)
            BeginRepositionToCurrentStart();

        if (status.State == TransporterState.MovingToTask)
        {
            FollowPolylineContinuous(pathToStart, ref pathToStartIndex, dtSim, effMove, OnArrivedAtTaskStart);
            return;
        }

        if (status.State == TransporterState.InTask)
        {
            TickInTask(dtSim, effMove);
            return;
        }

        if (status.State == TransporterState.MovingToIdle)
        {
            CancelIdleRoam();
            BeginRepositionToCurrentStart();
        }
    }

    private void EnsureExecuting()
    {
        if (status.State == TransporterState.Idle && taskCount > 0)
            BeginRepositionToCurrentStart();
    }

    private void BeginRepositionToCurrentStart()
    {
        if (nav == null) nav = NavMeshPathUtility.I;
        if (nav == null)
        {
            Debug.LogWarning($"[PatientTransporter] No NavMeshPathUtility in scene for '{name}'.");
            status.State = TransporterState.Idle;
            return;
        }

        if (taskCount <= 0)
        {
            status.State = TransporterState.Idle;
            return;
        }

        Vector3 from = GetPos();
        Vector3 to = tasks[0].TargetA.transform.position;

        if (!nav.TryGetSegmentedPath(
                from, to,
                out pathToStart,
                out float dist,
                out _,
                stepMeters,
                areaMask,
                sampleToNavMesh,
                sampleRadius))
        {
            Debug.LogWarning($"[PatientTransporter] No reposition path '{name}' -> start.");
            status.State = TransporterState.Idle;
            return;
        }

        pathToStartIndex = (pathToStart.Length >= 2) ? 1 : 0;

        status.State = TransporterState.MovingToTask;

        // NOTE: uses current effective speed for momentary ETA; crowd can change later
        float effMove = Mathf.Max(0.0001f, EffectiveMoveSpeed());
        status.TimeToCompleteCurrentStateSeconds = dist / effMove;

        phase = TaskPhase.None;
        phaseTimer = 0f;
        pathTask = null;
        pathTaskIndex = 0;

        if (logMoves)
            Debug.Log($"[PatientTransporter] '{name}' MovingToTask. waypoints={pathToStart.Length} dist={dist:0.0}m simETA≈{status.TimeToCompleteCurrentStateSeconds:0.00}s");
    }

    private void OnArrivedAtTaskStart()
    {
        pathTask = tasks[0].Path;
        pathTaskIndex = (pathTask != null && pathTask.Length >= 2) ? 1 : 0;

        status.State = TransporterState.InTask;
        phase = TaskPhase.Mounting;

        phaseTimer = Mathf.Max(0f, mountSpeed);

        // best-effort total remaining
        status.TimeToCompleteCurrentStateSeconds = ComputeTaskSimSeconds_Planning(tasks[0]);

        if (logMoves)
            Debug.Log($"[PatientTransporter] '{name}' Arrived at start. Mounting {phaseTimer:0.00}s(sim)");
    }

    private void TickInTask(float dtSim, float effMove)
    {
        if (phase == TaskPhase.Mounting)
        {
            phaseTimer -= dtSim;
            if (phaseTimer <= 0f)
            {
                phase = TaskPhase.Traveling;
                if (logMoves) Debug.Log($"[PatientTransporter] '{name}' Traveling.");
            }
            return;
        }

        if (phase == TaskPhase.Traveling)
        {
            if (pathTask == null || pathTask.Length < 2)
            {
                SetPos(tasks[0].TargetB.transform.position);
                phase = TaskPhase.Unmounting;
                phaseTimer = Mathf.Max(0f, unmountSpeed);
                return;
            }

            FollowPolylineContinuous(pathTask, ref pathTaskIndex, dtSim, effMove, () =>
            {
                phase = TaskPhase.Unmounting;
                phaseTimer = Mathf.Max(0f, unmountSpeed);
                if (logMoves) Debug.Log($"[PatientTransporter] '{name}' Reached destination. Unmounting {phaseTimer:0.00}s(sim)");
            });

            return;
        }

        if (phase == TaskPhase.Unmounting)
        {
            phaseTimer -= dtSim;
            if (phaseTimer <= 0f)
            {
                if (logMoves) Debug.Log($"[PatientTransporter] '{name}' Task complete.");
                RemoveCurrentTaskAndAdvance();
            }
        }
    }

    // Planning-time task duration (stable): uses baseMoveSpeed
    private float ComputeTaskSimSeconds_Planning(TaskData t)
    {
        if (nav == null) nav = NavMeshPathUtility.I;
        if (nav == null || t.TargetA == null || t.TargetB == null || baseMoveSpeed <= 0f)
            return 0f;

        if (!nav.TryGetSegmentedPath(
                t.TargetA, t.TargetB,
                out _, out float dist, out _,
                stepMeters, areaMask, sampleToNavMesh, sampleRadius))
            return 0f;

        float effPlanSpeed = Mathf.Max(0.0001f, baseMoveSpeed);
        return Mathf.Max(0f, mountSpeed) + (dist / effPlanSpeed) + Mathf.Max(0f, unmountSpeed);
    }

    private void RemoveCurrentTaskAndAdvance()
    {
        if (taskCount == 2)
        {
            tasks[0] = tasks[1];
            tasks[1] = default;
            taskCount = 1;
        }
        else
        {
            tasks[0] = default;
            taskCount = 0;
        }

        pathToStart = null;
        pathTask = null;
        pathToStartIndex = 0;
        pathTaskIndex = 0;
        phase = TaskPhase.None;
        phaseTimer = 0f;

        RecomputeLegacyETA_SimSeconds();

        if (taskCount > 0)
        {
            BeginRepositionToCurrentStart();
        }
        else
        {
            status.State = TransporterState.Idle;
            status.TimeToCompleteCurrentStateSeconds = 0f;
            idleWaitTimerSim = 0f;
        }
    }

    // ============================================================
    // Idle roaming
    // ============================================================

    private void TickIdleRoam(float dtSim, float effMove)
    {
        if (idleAreas == null || idleAreas.Length == 0 || effMove <= 0f)
        {
            status.State = TransporterState.Idle;
            status.TimeToCompleteCurrentStateSeconds = 0f;
            idleWaitTimerSim = 0f;
            idleRoamPath = null;
            idleRoamIndex = 0;
            return;
        }

        if (status.State == TransporterState.MovingToIdle)
        {
            FollowPolylineContinuous(idleRoamPath, ref idleRoamIndex, dtSim, effMove, OnArrivedAtIdleRoamDestination);
            return;
        }

        status.State = TransporterState.Idle;

        if (idleWaitTimerSim > 0f)
        {
            idleWaitTimerSim = Mathf.Max(0f, idleWaitTimerSim - dtSim);
            status.TimeToCompleteCurrentStateSeconds = idleWaitTimerSim;
            return;
        }

        BeginIdleRoamMove(effMove);
    }

    private void BeginIdleRoamMove(float effMove)
    {
        if (nav == null) nav = NavMeshPathUtility.I;
        if (nav == null) return;

        if (!TryPickIdleRoamDestination(out Vector3 dest))
        {
            status.State = TransporterState.Idle;
            status.TimeToCompleteCurrentStateSeconds = 0f;
            return;
        }

        Vector3 from = GetPos();

        if (!nav.TryGetSegmentedPath(
                from, dest,
                out idleRoamPath,
                out float dist,
                out _,
                stepMeters,
                areaMask,
                sampleToNavMesh,
                sampleRadius))
        {
            status.State = TransporterState.Idle;
            status.TimeToCompleteCurrentStateSeconds = 0f;
            idleRoamPath = null;
            idleRoamIndex = 0;
            return;
        }

        idleRoamIndex = (idleRoamPath != null && idleRoamPath.Length >= 2) ? 1 : 0;

        status.State = TransporterState.MovingToIdle;
        status.TimeToCompleteCurrentStateSeconds = dist / Mathf.Max(0.0001f, effMove);

        if (logMoves)
            Debug.Log($"[PatientTransporter] '{name}' MovingToIdle. dist={dist:0.0}m simETA≈{status.TimeToCompleteCurrentStateSeconds:0.00}s");
    }

    private void OnArrivedAtIdleRoamDestination()
    {
        idleRoamPath = null;
        idleRoamIndex = 0;

        status.State = TransporterState.Idle;
        idleWaitTimerSim = Mathf.Max(0f, idleRoamWaitSimSeconds);
        status.TimeToCompleteCurrentStateSeconds = idleWaitTimerSim;

        if (logMoves)
            Debug.Log($"[PatientTransporter] '{name}' Arrived at idle roam point. Waiting {idleWaitTimerSim:0.00}s(sim).");
    }

    private void CancelIdleRoam()
    {
        idleRoamPath = null;
        idleRoamIndex = 0;
        idleWaitTimerSim = 0f;
    }

    private bool TryPickIdleRoamDestination(out Vector3 dest)
    {
        dest = default;

        if (idleAreas == null || idleAreas.Length == 0) return false;

        for (int attempt = 0; attempt < 10; attempt++)
        {
            Transform area = idleAreas[UnityEngine.Random.Range(0, idleAreas.Length)];
            if (area == null) continue;

            Vector3 center = area.position;

            Vector2 r = UnityEngine.Random.insideUnitCircle * Mathf.Max(0.1f, idleAreaPickRadius);
            Vector3 candidate = new Vector3(center.x + r.x, center.y, center.z + r.y);

            if (sampleToNavMesh)
            {
                if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, Mathf.Max(sampleRadius, 0.1f), areaMask))
                {
                    dest = hit.position;
                    return true;
                }
            }
            else
            {
                dest = candidate;
                return true;
            }
        }

        return false;
    }

    // ============================================================
    // Continuous polyline follower (no stop/go)
    // ============================================================

    private void FollowPolylineContinuous(Vector3[] pts, ref int index, float dtSim, float moveMetersPerSimSecond, Action onArrive)
    {
        if (pts == null || pts.Length == 0)
        {
            onArrive?.Invoke();
            return;
        }

        if (index >= pts.Length)
        {
            onArrive?.Invoke();
            return;
        }

        float remainingMeters = Mathf.Max(0f, moveMetersPerSimSecond) * Mathf.Max(0f, dtSim);

        while (remainingMeters > 0f && index < pts.Length)
        {
            Vector3 pos = GetPos();
            Vector3 target = pts[index];

            Vector3 to = target - pos;
            float dist = to.magnitude;

            if (dist <= 0.05f)
            {
                index++;
                continue;
            }

            float step = Mathf.Min(remainingMeters, dist);
            Vector3 next = pos + (to / dist) * step;
            SetPos(next);

            remainingMeters -= step;

            if (step >= dist - 0.0001f)
                index++;
        }

        if (index >= pts.Length)
            onArrive?.Invoke();
    }

    // ============================================================
    // Position helpers
    // ============================================================

    private Vector3 GetPos()
    {
        if (rb != null) return rb.position;
        return transform.position;
    }

    private void SetPos(Vector3 p)
    {
        if (rb != null)
            rb.MovePosition(p);
        else
            transform.position = p;
    }

    // ============================================================
    // Legacy ETA (SIM seconds) — planning estimate uses baseMoveSpeed
    // ============================================================

    private void RecomputeLegacyETA_SimSeconds()
    {
        if (nav == null) nav = NavMeshPathUtility.I;
        if (nav == null || baseMoveSpeed <= 0f)
        {
            timeToCompleteCurrentTasks = 0f;
            return;
        }

        if (taskCount == 0)
        {
            timeToCompleteCurrentTasks = 0f;
            return;
        }

        float effPlanSpeed = Mathf.Max(0.0001f, baseMoveSpeed);

        float totalSim = 0f;
        Vector3 pos = GetPos();

        for (int i = 0; i < taskCount; i++)
        {
            Vector3 start = tasks[i].TargetA.transform.position;
            Vector3 end = tasks[i].TargetB.transform.position;

            if (!nav.TryGetSegmentedPath(pos, start, out _, out float repositionDist, out _, stepMeters, areaMask, sampleToNavMesh, sampleRadius))
            {
                timeToCompleteCurrentTasks = float.PositiveInfinity;
                return;
            }

            totalSim += repositionDist / effPlanSpeed;

            if (!nav.TryGetSegmentedPath(start, end, out _, out float taskDist, out _, stepMeters, areaMask, sampleToNavMesh, sampleRadius))
            {
                timeToCompleteCurrentTasks = float.PositiveInfinity;
                return;
            }

            totalSim += Mathf.Max(0f, mountSpeed) + (taskDist / effPlanSpeed) + Mathf.Max(0f, unmountSpeed);
            pos = end;
        }

        timeToCompleteCurrentTasks = totalSim;
    }

    // ============================================================
    // Accessors
    // ============================================================

    public int GetTaskCount() => taskCount;
    public TaskData GetCurrentTask() => taskCount > 0 ? tasks[0] : default;
}
