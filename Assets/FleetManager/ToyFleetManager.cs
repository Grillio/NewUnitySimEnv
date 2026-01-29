// ToyFleetManager.cs
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Represents a navigation task between two world targets,
/// including the computed path and task metadata.
/// </summary>
[Serializable]
public struct TaskData
{
    public Component TargetA;          // start (uses transform.position)
    public Component TargetB;          // end (uses transform.position)
    public Vector3[] Path;             // segmented task path A->B
    public int Priority;               // higher = more important
    public float TimeToComplete;       // seconds (filled after assignment / estimation)

    public TaskData(Component targetA, Component targetB, int priority = 0)
    {
        TargetA = targetA;
        TargetB = targetB;
        Path = null;
        Priority = priority;
        TimeToComplete = 0f;
    }
}

/// <summary>
/// ToyFleetManager:
/// - Registers LocationData by code
/// - Registers PatientTransporters into Human / Robotic lists
/// - Subscribes to SequencerSubSys.NewTask
/// - Builds segmented NavMesh path (A->B) via NavMeshPathUtility
/// - Chooses best transporter by ETA:
///     * If task is robotic-compatible:
///         - Robot ETA = normal
///         - Human ETA = ETA * humanPenaltyMultiplierWhenRobotAllowed
///     * If task is NOT robotic-compatible:
///         - Humans only (normal)
/// - Assigns by calling transporter.TryAddTask(TaskData,...)
/// </summary>
public sealed class ToyFleetManager : MonoBehaviour
{
    public static ToyFleetManager I { get; private set; }

    [Header("Wiring")]
    [Tooltip("If null, uses SequencerSubSys.I")]
    [SerializeField] private SequencerSubSys sequencer;

    [Tooltip("If null, uses NavMeshPathUtility.I")]
    [SerializeField] private NavMeshPathUtility nav;

    [Header("Transporters")]
    [SerializeField] public List<PatientTransporter> RoboticTransporters = new();
    [SerializeField] public List<PatientTransporter> HumanTransporters = new();

    [Header("Selection Weights")]
    [Tooltip("When a task is robotic-compatible, human ETAs are multiplied by this to bias robot selection.")]
    [Min(1f)]
    [SerializeField] private float humanPenaltyMultiplierWhenRobotAllowed = 1.25f;

    [Header("Locations")]
    [SerializeField] private bool alsoPopulateFromInspectorList = true;

    [Tooltip("Optional: prefill these in Inspector. They will be registered into Locations on Start.")]
    [SerializeField] private List<LocationData> inspectorLocations = new();

    /// <summary>Runtime registry: location code -> LocationData</summary>
    public readonly Dictionary<string, LocationData> Locations = new();

    [Header("Robotic Compatibility Rules")]
    [Tooltip("If priority EXACTLY matches any entry here (case-sensitive), robots are disallowed.")]
    [SerializeField] private List<string> roboticDisallowedPrioritiesExact = new() { "Critical", "STAT", "Stat" };

    [Tooltip("If priority CONTAINS any of these tags (case-insensitive), robots are disallowed.")]
    [SerializeField] private List<string> roboticDisallowedPriorityTags = new() { "ICU", "ISO", "BARI" };

    [Header("NavMesh Segmentation")]
    [Min(0.1f)]
    [SerializeField] private float stepMeters = 2f;

    [SerializeField] private bool sampleToNavMesh = true;
    [SerializeField] private float sampleRadius = 2f;
    [SerializeField] private int areaMask = NavMesh.AllAreas;

    [Header("Debug")]
    [SerializeField] private bool logAssignments = true;
    [SerializeField] private bool logRejectedTasks = true;

    // ------------------------------------------------------------
    // Collected tasks (everything that fires off)
    // ------------------------------------------------------------

    [Serializable]
    public struct TaskRecord
    {
        public string TaskId;
        public string FromCode;
        public string ToCode;
        public string PriorityText;

        public bool RoboticCompatible;

        public TaskData Data;

        public PatientTransporter AssignedTransporter; // null if unassigned
        public bool Assigned;

        // Debug: the "score" used for selection (after penalties), not the raw ETA.
        public float SelectionScoreSeconds;
    }

    [SerializeField] private List<TaskRecord> receivedTasks = new();
    public IReadOnlyList<TaskRecord> ReceivedTasks => receivedTasks;

    // ------------------------------------------------------------
    // Unity lifecycle
    // ------------------------------------------------------------

    private void Awake()
    {
        if (I != null && I != this)
        {
            Destroy(gameObject);
            return;
        }
        I = this;
    }

    private void Start()
    {
        if (sequencer == null) sequencer = SequencerSubSys.I;
        if (nav == null) nav = NavMeshPathUtility.I;

        DedupTransporters(RoboticTransporters);
        DedupTransporters(HumanTransporters);

        if (alsoPopulateFromInspectorList)
            RegisterInspectorLocations();
    }

    private void OnEnable()
    {
        if (sequencer == null) sequencer = SequencerSubSys.I;

        if (sequencer != null)
            sequencer.NewTask += OnNewTask;
        else if (logRejectedTasks)
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

    // ------------------------------------------------------------
    // Registration API (called by LocationData, spawners, etc.)
    // ------------------------------------------------------------

    public void AddLocation(LocationData location)
    {
        if (location == null) return;

        string code = location.GetLocationCodeName();
        if (string.IsNullOrWhiteSpace(code))
        {
            if (logRejectedTasks)
                Debug.LogWarning($"[ToyFleetManager] Location '{location.name}' has empty locationCodeName.");
            return;
        }

        if (Locations.TryGetValue(code, out var existing) && existing != null && existing != location)
        {
            if (logRejectedTasks)
                Debug.LogWarning($"[ToyFleetManager] Duplicate location code '{code}'. Keeping '{existing.name}', ignoring '{location.name}'.");
            return;
        }

        Locations[code] = location;
    }

    public void AddRoboticTransporter(PatientTransporter transporter)
    {
        if (transporter == null) return;
        if (!RoboticTransporters.Contains(transporter))
            RoboticTransporters.Add(transporter);
    }

    public void AddHumanTransporter(PatientTransporter transporter)
    {
        if (transporter == null) return;
        if (!HumanTransporters.Contains(transporter))
            HumanTransporters.Add(transporter);
    }

    // ------------------------------------------------------------
    // Core: receive tasks
    // ------------------------------------------------------------

    private void OnNewTask(string taskId, string fromArea, string toArea, string priorityText)
    {
        if (nav == null) nav = NavMeshPathUtility.I;

        TaskRecord record = new TaskRecord
        {
            TaskId = taskId,
            FromCode = fromArea,
            ToCode = toArea,
            PriorityText = priorityText,
            RoboticCompatible = IsRoboticCompatible(priorityText),
            Data = default,
            AssignedTransporter = null,
            Assigned = false,
            SelectionScoreSeconds = float.PositiveInfinity
        };

        // Nav check
        if (nav == null)
        {
            if (logRejectedTasks)
                Debug.LogWarning($"[ToyFleetManager] Task {taskId}: NavMeshPathUtility not found.");
            receivedTasks.Add(record);
            return;
        }

        // Resolve locations
        if (!Locations.TryGetValue(fromArea, out var fromLoc) || fromLoc == null)
        {
            if (logRejectedTasks)
                Debug.LogWarning($"[ToyFleetManager] Task {taskId}: Unknown FROM location '{fromArea}'.");
            receivedTasks.Add(record);
            return;
        }

        if (!Locations.TryGetValue(toArea, out var toLoc) || toLoc == null)
        {
            if (logRejectedTasks)
                Debug.LogWarning($"[ToyFleetManager] Task {taskId}: Unknown TO location '{toArea}'.");
            receivedTasks.Add(record);
            return;
        }

        // Resolve targets
        GameObject fromTarget = fromLoc.GetTargetObject();
        GameObject toTarget = toLoc.GetTargetObject();
        if (fromTarget == null || toTarget == null)
        {
            if (logRejectedTasks)
                Debug.LogWarning($"[ToyFleetManager] Task {taskId}: Missing targetObject on LocationData (from/to).");
            receivedTasks.Add(record);
            return;
        }

        Vector3 startPos = fromTarget.transform.position;
        Vector3 endPos = toTarget.transform.position;

        // Build segmented path A->B and distance (primarily for record/debug; transporter also uses its own)
        if (!nav.TryGetSegmentedPath(
                startPos, endPos,
                out Vector3[] waypoints,
                out float totalDistanceMeters,
                out float floorDiffMeters,
                stepMeters, areaMask,
                sampleToNavMesh, sampleRadius))
        {
            if (logRejectedTasks)
                Debug.LogWarning($"[ToyFleetManager] Task {taskId}: No complete segmented NavMesh path '{fromArea}' -> '{toArea}'.");
            receivedTasks.Add(record);
            return;
        }

        // Priority mapping (text -> int)
        int priorityInt = ParsePriorityToInt(priorityText);

        // Build TaskData using LocationData components so transporter can read transforms
        TaskData data = new TaskData(fromLoc, toLoc, priorityInt)
        {
            Path = waypoints,         // segmented task path start->end (optional; transporter recomputes too)
            TimeToComplete = 0f
        };
        record.Data = data;

        // Select transporter
        bool robotOk = record.RoboticCompatible;

        PatientTransporter best = null;
        float bestScore = float.PositiveInfinity; // score used for selection (after penalties)
        float bestRawEta = float.PositiveInfinity; // raw ETA before penalty (stored into TimeToComplete)

        if (robotOk)
        {
            // Robots: no penalty
            EvaluateBestTransporter(
                RoboticTransporters,
                isHuman: false,
                newStartWorld: startPos,
                newEndWorld: endPos,
                ref best,
                ref bestScore,
                ref bestRawEta);

            // Humans: apply penalty multiplier
            EvaluateBestTransporter(
                HumanTransporters,
                isHuman: true,
                newStartWorld: startPos,
                newEndWorld: endPos,
                ref best,
                ref bestScore,
                ref bestRawEta);
        }
        else
        {
            // Humans only: no penalty
            EvaluateBestTransporter(
                HumanTransporters,
                isHuman: false,
                newStartWorld: startPos,
                newEndWorld: endPos,
                ref best,
                ref bestScore,
                ref bestRawEta);
        }

        if (best == null || float.IsInfinity(bestScore) || float.IsNaN(bestScore))
        {
            receivedTasks.Add(record);
            if (logRejectedTasks)
                Debug.LogWarning($"[ToyFleetManager] Task {taskId}: No valid transporter ETA found (roboticCompatible={robotOk}).");
            return;
        }

        // Assign (this starts movement inside PatientTransporter)
        bool queued = best.TryAddTask(data, nav, areaMask, stepMeters, sampleToNavMesh, sampleRadius);
        if (!queued)
        {
            receivedTasks.Add(record);
            if (logRejectedTasks)
                Debug.LogWarning($"[ToyFleetManager] Task {taskId}: Best transporter '{best.name}' could not queue task.");
            return;
        }

        // Store results
        data.TimeToComplete = bestRawEta; // store RAW ETA, not the penalized score
        record.Data = data;
        record.AssignedTransporter = best;
        record.Assigned = true;
        record.SelectionScoreSeconds = bestScore;

        receivedTasks.Add(record);

        if (logAssignments)
        {
            Debug.Log(
                $"[ToyFleetManager] Assigned Task {taskId} ({fromArea}->{toArea}, '{priorityText}', robotOK={robotOk}) " +
                $"to '{best.name}' rawETA={bestRawEta:0.00}s score={bestScore:0.00}s " +
                $"| dist={totalDistanceMeters:0.0}m floorΔ={floorDiffMeters:0.0}m waypoints={waypoints.Length}"
            );
        }
    }

    // ------------------------------------------------------------
    // Selection helpers
    // ------------------------------------------------------------

    /// <summary>
    /// Evaluates all candidates; chooses the one with the smallest "selection score".
    /// If isHuman==true AND task is robot-compatible, we multiply the score by the configured penalty.
    /// We also track bestRawEta separately so you can store true ETA in TaskData.
    /// </summary>
    private void EvaluateBestTransporter(
        List<PatientTransporter> candidates,
        bool isHuman,
        Vector3 newStartWorld,
        Vector3 newEndWorld,
        ref PatientTransporter best,
        ref float bestScore,
        ref float bestRawEta)
    {
        if (candidates == null || candidates.Count == 0) return;

        for (int i = 0; i < candidates.Count; i++)
        {
            PatientTransporter t = candidates[i];
            if (t == null) continue;

            float rawEta = t.CalculateEstimatedTimeToCompleteWithReposition(
                newStartWorld, newEndWorld,
                nav, areaMask,
                stepMeters, sampleToNavMesh, sampleRadius);

            if (float.IsNaN(rawEta) || float.IsInfinity(rawEta)) continue;

            float score = rawEta;

            // Only bias humans when robots are allowed (we only call isHuman=true in that case above)
            if (isHuman)
                score *= Mathf.Max(1f, humanPenaltyMultiplierWhenRobotAllowed);

            if (score < bestScore)
            {
                bestScore = score;
                bestRawEta = rawEta;
                best = t;
            }
        }
    }

    private bool IsRoboticCompatible(string priorityText)
    {
        // Exact disallow list (case-sensitive)
        if (!string.IsNullOrEmpty(priorityText) && roboticDisallowedPrioritiesExact != null)
        {
            for (int i = 0; i < roboticDisallowedPrioritiesExact.Count; i++)
            {
                if (string.Equals(priorityText, roboticDisallowedPrioritiesExact[i], StringComparison.Ordinal))
                    return false;
            }
        }

        // Tag-based disallow (case-insensitive contains)
        if (!string.IsNullOrEmpty(priorityText) && roboticDisallowedPriorityTags != null)
        {
            string p = priorityText.ToLowerInvariant();
            for (int i = 0; i < roboticDisallowedPriorityTags.Count; i++)
            {
                string tag = roboticDisallowedPriorityTags[i];
                if (string.IsNullOrWhiteSpace(tag)) continue;

                if (p.Contains(tag.ToLowerInvariant()))
                    return false;
            }
        }

        return true;
    }

    private int ParsePriorityToInt(string priorityText)
    {
        if (string.IsNullOrWhiteSpace(priorityText))
            return 0;

        if (int.TryParse(priorityText.Trim(), out int n))
            return n;

        string p = priorityText.ToLowerInvariant();
        if (p.Contains("stat")) return 100;
        if (p.Contains("critical")) return 100;
        if (p.Contains("high")) return 50;
        if (p.Contains("normal")) return 10;
        if (p.Contains("low")) return 1;

        return 0;
    }

    private void RegisterInspectorLocations()
    {
        if (inspectorLocations == null) return;

        for (int i = 0; i < inspectorLocations.Count; i++)
        {
            LocationData loc = inspectorLocations[i];
            if (loc == null) continue;
            AddLocation(loc);
        }
    }

    private static void DedupTransporters(List<PatientTransporter> list)
    {
        if (list == null) return;

        var set = new HashSet<PatientTransporter>();
        for (int i = list.Count - 1; i >= 0; i--)
        {
            PatientTransporter t = list[i];
            if (t == null || !set.Add(t))
                list.RemoveAt(i);
        }
    }
}
