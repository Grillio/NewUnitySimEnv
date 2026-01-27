using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// ToyFleetManager
/// - Listens to SequencerSubSys.NewTask(taskId, from, to, priority)
/// - Resolves LocationData codes to world positions
/// - Uses NavMeshPathUtility to compute NavMeshPath + length
/// - Builds TaskInformation (start/end/path/priority/roviAllowed + BEST transporter selection)
/// - Evaluates every transporter ETA with patient/task timing rules, then stores the best
/// </summary>
public sealed class ToyFleetManager : MonoBehaviour
{
    // Singleton instance reference
    public static ToyFleetManager I { get; private set; }

    [Header("Wiring")]
    [Tooltip("If null, will use SequencerSubSys.I")]
    [SerializeField] private SequencerSubSys sequencer;

    [Tooltip("If null, will use NavMeshPathUtility.I")]
    [SerializeField] private NavMeshPathUtility navigationMapper;

    [Header("Locations (optional pre-fill)")]
    [Tooltip("Optional: you can pre-assign LocationData objects here, or they can self-register at runtime.")]
    [SerializeField] private List<LocationData> locations = new();

    [Header("Transporters (optional pre-fill)")]
    [Tooltip("Optional: you can pre-assign PatientTransporter objects here, or they can self-register at runtime.")]
    [SerializeField] private List<PatientTransporter> transporters = new();

    [Header("Rovi Rules")]
    [Tooltip("If true, tasks with priority matching any entry in 'roviDisallowedPriorities' will set roviAllowed=false.")]
    [SerializeField] private bool useRoviPriorityFilter = true;

    [Tooltip("Priority strings that disallow Rovi robots (exact match). Example: Critical, STAT")]
    [SerializeField] private List<string> roviDisallowedPriorities = new() { "Critical", "STAT" };

    [Header("Patient/Task Timing (for ETA)")]
    [Tooltip("Base seconds to mount/load patient before movement.")]
    [SerializeField] private float baseMountSeconds = 3f;

    [Tooltip("Base seconds to unmount/unload patient after movement.")]
    [SerializeField] private float baseUnmountSeconds = 3f;

    [Tooltip("Rules applied by matching tags in priority (case-insensitive). First match wins.")]
    [SerializeField] private List<PatientTimingRule> patientTimingRules = new()
    {
        new PatientTimingRule { containsTag = "ICU",   extraMountSeconds = 10f, extraUnmountSeconds = 10f, travelTimeMultiplier = 1.15f },
        new PatientTimingRule { containsTag = "BARI",  extraMountSeconds = 15f, extraUnmountSeconds = 15f, travelTimeMultiplier = 1.25f },
        new PatientTimingRule { containsTag = "ISO",   extraMountSeconds = 8f,  extraUnmountSeconds = 8f,  travelTimeMultiplier = 1.10f },
        new PatientTimingRule { containsTag = "WHEEL", extraMountSeconds = 5f,  extraUnmountSeconds = 5f,  travelTimeMultiplier = 1.05f },
    };

    [Header("Debug")]
    [SerializeField] private float pendingPrintIntervalSeconds = 5f;
    [SerializeField] private bool logAllTransporterOptions = true;

    // Code -> location lookup
    private readonly Dictionary<string, LocationData> _locationByCode = new();

    // Tasks received but not yet assigned/consumed elsewhere
    private readonly List<TaskInformation> _pendingTasks = new();

    private float _printTimer;

    /// <summary>
    /// Optional rules to adjust ETA based on patient/task characteristics.
    /// By default this matches against the priority string (easy to drive from CSV).
    /// </summary>
    [Serializable]
    private struct PatientTimingRule
    {
        [Tooltip("If 'priority' contains this tag (case-insensitive), apply these adjustments.")]
        public string containsTag;

        [Tooltip("Extra seconds added for mounting/loading the patient.")]
        public float extraMountSeconds;

        [Tooltip("Extra seconds added for unmounting/unloading the patient.")]
        public float extraUnmountSeconds;

        [Tooltip("Multiply travel time (e.g., 1.25 = slower).")]
        public float travelTimeMultiplier;
    }

    /// <summary>
    /// TaskInformation:
    /// Contains start/end positions, computed NavMeshPath, cached path length, priority, roviAllowed,
    /// and the BEST transporter choice after evaluating every option.
    /// </summary>
    private struct TaskInformation
    {
        public string taskId;

        public Vector3 startPosition;
        public Vector3 endPosition;

        public NavMeshPath path;
        public float pathLengthMeters;

        public string priority;
        public bool roviAllowed;

        public PatientTransporter bestTransporter;
        public float bestEtaSeconds;

        // Optional debug fields (helpful when tuning timing rules)
        public float bestMountSeconds;
        public float bestTravelSeconds;
        public float bestUnmountSeconds;
        public string bestRuleTag;

        public bool HasBest => bestTransporter != null && !float.IsInfinity(bestEtaSeconds) && !float.IsNaN(bestEtaSeconds);

        public override string ToString()
        {
            string bt = bestTransporter != null ? bestTransporter.name : "None";
            return $"TaskId={taskId}, Priority={priority}, RoviAllowed={roviAllowed}, Dist={pathLengthMeters:0.00}m, Best={bt}, BestETA={bestEtaSeconds:0.00}s";
        }
    }

    private void Awake()
    {
        // Singleton enforcement
        if (I != null && I != this)
        {
            Destroy(gameObject);
            return;
        }
        I = this;
    }

    private void Start()
    {
        if (sequencer == null)
            sequencer = SequencerSubSys.I;

        if (navigationMapper == null)
            navigationMapper = NavMeshPathUtility.I;

        RegisterInspectorLocations();
        RegisterInspectorTransporters();
    }

    private void OnEnable()
    {
        if (sequencer == null)
            sequencer = SequencerSubSys.I;

        if (sequencer != null)
            sequencer.NewTask += OnNewTask;
        else
            Debug.LogWarning("[ToyFleetManager] SequencerSubSys not found; cannot subscribe to NewTask.");
    }

    private void OnDisable()
    {
        if (sequencer != null)
            sequencer.NewTask -= OnNewTask;
    }

    private void OnDestroy()
    {
        if (I == this) I = null;
    }

    private void Update()
    {
        if (pendingPrintIntervalSeconds <= 0f) return;

        _printTimer += Time.deltaTime;
        if (_printTimer >= pendingPrintIntervalSeconds)
        {
            _printTimer = 0f;
            Debug.Log($"[ToyFleetManager] Pending (loaded but undistributed) tasks: {_pendingTasks.Count}");
        }
    }

    // -----------------------------
    // Public Registration API
    // -----------------------------

    public void AddLocation(LocationData location)
    {
        if (location == null) return;

        if (!locations.Contains(location))
            locations.Add(location);

        string code = location.GetLocationCodeName();
        if (string.IsNullOrWhiteSpace(code))
        {
            Debug.LogWarning($"[ToyFleetManager] Location '{location.name}' has empty locationCodeName.");
            return;
        }

        if (_locationByCode.TryGetValue(code, out var existing) && existing != null && existing != location)
        {
            Debug.LogWarning($"[ToyFleetManager] Duplicate locationCodeName '{code}'. Keeping '{existing.name}', ignoring '{location.name}'.");
            return;
        }

        _locationByCode[code] = location;
    }

    public void AddTransporter(PatientTransporter transporter)
    {
        if (transporter == null) return;

        if (!transporters.Contains(transporter))
            transporters.Add(transporter);
    }

    // -----------------------------
    // Internals
    // -----------------------------

    private void RegisterInspectorLocations()
    {
        _locationByCode.Clear();

        if (locations == null) locations = new List<LocationData>();

        for (int i = 0; i < locations.Count; i++)
        {
            var loc = locations[i];
            if (loc == null) continue;
            AddLocation(loc);
        }
    }

    private void RegisterInspectorTransporters()
    {
        if (transporters == null) transporters = new List<PatientTransporter>();

        var dedup = new HashSet<PatientTransporter>();
        var cleaned = new List<PatientTransporter>(transporters.Count);

        for (int i = 0; i < transporters.Count; i++)
        {
            var t = transporters[i];
            if (t == null) continue;
            if (dedup.Add(t)) cleaned.Add(t);
        }

        transporters = cleaned;
    }

    private void OnNewTask(string taskId, string fromArea, string toArea, string priority)
    {
        // Validate wiring
        if (navigationMapper == null)
            navigationMapper = NavMeshPathUtility.I;

        if (navigationMapper == null)
        {
            Debug.LogWarning($"[ToyFleetManager] Task {taskId}: NavMeshPathUtility not assigned/found.");
            return;
        }

        // Resolve locations
        if (!_locationByCode.TryGetValue(fromArea, out var fromLoc) || fromLoc == null)
        {
            Debug.LogWarning($"[ToyFleetManager] Task {taskId}: Unknown FROM location '{fromArea}'.");
            return;
        }

        if (!_locationByCode.TryGetValue(toArea, out var toLoc) || toLoc == null)
        {
            Debug.LogWarning($"[ToyFleetManager] Task {taskId}: Unknown TO location '{toArea}'.");
            return;
        }

        // Resolve target objects
        GameObject fromTarget = fromLoc.GetTargetObject();
        GameObject toTarget = toLoc.GetTargetObject();

        if (fromTarget == null || toTarget == null)
        {
            Debug.LogWarning($"[ToyFleetManager] Task {taskId}: Missing targetObject on from/to LocationData.");
            return;
        }

        Vector3 startPos = fromTarget.transform.position;
        Vector3 endPos = toTarget.transform.position;

        // Compute path + distance from NavMeshPathUtility
        if (!navigationMapper.TryGetPath(startPos, endPos, out NavMeshPath path, out float distanceMeters))
        {
            Debug.LogWarning($"[ToyFleetManager] Task {taskId}: No complete NavMesh path from '{fromArea}' to '{toArea}'.");
            return;
        }

        // Decide roviAllowed
        bool roviAllowed = IsRoviAllowed(priority);

        // Build task info (best transporter will be filled below)
        TaskInformation info = new TaskInformation
        {
            taskId = taskId,
            startPosition = startPos,
            endPosition = endPos,
            path = path,
            pathLengthMeters = distanceMeters,
            priority = priority,
            roviAllowed = roviAllowed,

            bestTransporter = null,
            bestEtaSeconds = float.PositiveInfinity,
            bestMountSeconds = 0f,
            bestTravelSeconds = 0f,
            bestUnmountSeconds = 0f,
            bestRuleTag = ""
        };

        // Need transporters to evaluate
        if (transporters == null || transporters.Count == 0)
        {
            _pendingTasks.Add(info);
            Debug.LogWarning($"[ToyFleetManager] Task {taskId}: No transporters registered. Task stored pending (no best choice).");
            return;
        }

        // Select timing rule based on patient/task type (default: match priority tags)
        PatientTimingRule timingRule = GetTimingRuleForPriority(priority);

        // Evaluate every transporter and keep best
        EvaluateAndStoreBestTransporter(
            ref info,
            timingRule,
            fromArea,
            toArea
        );

        // Store task with best choice
        _pendingTasks.Add(info);

        // Log summary
        if (!info.HasBest)
        {
            Debug.LogWarning($"[ToyFleetManager] Task {taskId}: No valid transporter ETA could be computed. Task stored pending.");
            return;
        }

        Debug.Log(
            $"[ToyFleetManager] NEW TASK STORED: {info} " +
            $"| Breakdown: mount={info.bestMountSeconds:0.0}s travel={info.bestTravelSeconds:0.0}s unmount={info.bestUnmountSeconds:0.0}s ruleTag='{info.bestRuleTag}' " +
            $"| from={fromArea} to={toArea}"
        );
    }

    private void EvaluateAndStoreBestTransporter(
        ref TaskInformation info,
        PatientTimingRule timingRule,
        string fromArea,
        string toArea)
    {
        float bestEta = float.PositiveInfinity;
        PatientTransporter best = null;

        float bestMount = 0f;
        float bestTravel = 0f;
        float bestUnmount = 0f;

        float mountSeconds = Mathf.Max(0f, baseMountSeconds + timingRule.extraMountSeconds);
        float unmountSeconds = Mathf.Max(0f, baseUnmountSeconds + timingRule.extraUnmountSeconds);
        float travelMult = Mathf.Max(0.01f, timingRule.travelTimeMultiplier);

        for (int i = 0; i < transporters.Count; i++)
        {
            var t = transporters[i];
            if (t == null) continue;

            // Your existing transporter method: returns travel seconds for given meters
            float baseTravelSeconds = t.CalculateEstimatedTimeToComplete(info.pathLengthMeters);

            if (float.IsNaN(baseTravelSeconds) || float.IsInfinity(baseTravelSeconds) || baseTravelSeconds <= 0f)
                continue;

            float adjustedTravel = baseTravelSeconds * travelMult;
            float totalEta = mountSeconds + adjustedTravel + unmountSeconds;

            if (logAllTransporterOptions)
            {
                Debug.Log(
                    $"[ToyFleetManager] Task {info.taskId}: Option '{t.name}' ETA={totalEta:0.00}s " +
                    $"(mount={mountSeconds:0.0}s, travel={adjustedTravel:0.0}s, unmount={unmountSeconds:0.0}s, tag='{timingRule.containsTag}', roviAllowed={info.roviAllowed}) " +
                    $"from={fromArea} to={toArea}"
                );
            }

            if (totalEta < bestEta)
            {
                bestEta = totalEta;
                best = t;

                bestMount = mountSeconds;
                bestTravel = adjustedTravel;
                bestUnmount = unmountSeconds;
            }
        }

        info.bestTransporter = best;
        info.bestEtaSeconds = bestEta;

        info.bestMountSeconds = bestMount;
        info.bestTravelSeconds = bestTravel;
        info.bestUnmountSeconds = bestUnmount;
        info.bestRuleTag = timingRule.containsTag ?? "";
    }

    private bool IsRoviAllowed(string priority)
    {
        if (!useRoviPriorityFilter)
            return true;

        if (roviDisallowedPriorities == null || roviDisallowedPriorities.Count == 0)
            return true;

        for (int i = 0; i < roviDisallowedPriorities.Count; i++)
        {
            if (string.Equals(priority, roviDisallowedPriorities[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private PatientTimingRule GetTimingRuleForPriority(string priority)
    {
        // Defaults: no extras, multiplier 1
        var rule = new PatientTimingRule
        {
            containsTag = "",
            extraMountSeconds = 0f,
            extraUnmountSeconds = 0f,
            travelTimeMultiplier = 1f
        };

        if (string.IsNullOrWhiteSpace(priority) || patientTimingRules == null)
            return rule;

        string p = priority.ToLowerInvariant();

        for (int i = 0; i < patientTimingRules.Count; i++)
        {
            var r = patientTimingRules[i];
            if (string.IsNullOrWhiteSpace(r.containsTag)) continue;

            if (p.Contains(r.containsTag.ToLowerInvariant()))
                return r;
        }

        return rule;
    }

    // Optional helper if you want external systems to read pending tasks later
    public IReadOnlyList<(string taskId, string priority, bool roviAllowed, PatientTransporter best, float bestEtaSeconds)> GetPendingTaskSummaries()
    {
        var list = new List<(string, string, bool, PatientTransporter, float)>(_pendingTasks.Count);
        for (int i = 0; i < _pendingTasks.Count; i++)
        {
            var t = _pendingTasks[i];
            list.Add((t.taskId, t.priority, t.roviAllowed, t.bestTransporter, t.bestEtaSeconds));
        }
        return list;
    }
}
