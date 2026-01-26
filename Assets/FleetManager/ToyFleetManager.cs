using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class ToyFleetManager : MonoBehaviour
{
    // Singleton-style instance reference (normal class, static reference to the live instance)
    public static ToyFleetManager I { get; private set; }

    [Header("Wiring")]
    [Tooltip("If null, will use SequencerSubSys.I")]
    [SerializeField] private SequencerSubSys sequencer;

    [Tooltip("Reference to your NavigationMapper component (NavMesh path + distance).")]
    [SerializeField] private NavMeshPathUtility navigationMapper;

    [Header("Locations (optional pre-fill)")]
    [Tooltip("Optional: you can pre-assign LocationData objects here, or they can self-register at runtime.")]
    [SerializeField] private List<LocationData> locations = new();

    [Header("Transporters (optional pre-fill)")]
    [Tooltip("Optional: you can pre-assign PatientTransporter objects here, or they can self-register at runtime.")]
    [SerializeField] private List<PatientTransporter> transporters = new();

    private readonly Dictionary<string, LocationData> _locationByCode = new();
    private readonly List<TaskRecord> _pending = new();

    private float _printTimer;

    private struct TaskRecord
    {
        public string id;
        public string from;
        public string to;
        public string priority;
    }

    private void Start()
    {
        if (I != null && I != this)
        {
            Destroy(gameObject);
            return;
        }
        I = this;

        if (sequencer == null)
            sequencer = SequencerSubSys.I;

        if (navigationMapper == null)
            navigationMapper = NavMeshPathUtility.I;

        // If you pre-filled locations/transporters in inspector, register them.
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
        _printTimer += Time.deltaTime;
        if (_printTimer >= 5f)
        {
            _printTimer = 0f;
            Debug.Log($"[ToyFleetManager] Pending (loaded but undistributed) tasks: {_pending.Count}");
        }
    }

    // -----------------------------
    // Public Registration API (instance methods)
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
            if (locations[i] == null) continue;
            AddLocation(locations[i]);
        }
    }

    private void RegisterInspectorTransporters()
    {
        if (transporters == null) transporters = new List<PatientTransporter>();

        // De-dup nulls / duplicates
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
        _pending.Add(new TaskRecord
        {
            id = taskId,
            from = fromArea,
            to = toArea,
            priority = priority
        });

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

        // World targets to navigate between
        GameObject fromTarget = fromLoc.GetTargetObject();
        GameObject toTarget = toLoc.GetTargetObject();

        if (fromTarget == null || toTarget == null)
        {
            Debug.LogWarning($"[ToyFleetManager] Task {taskId}: Missing targetObject on from/to LocationData.");
            return;
        }

        if (navigationMapper == null)
        {
            Debug.LogWarning($"[ToyFleetManager] Task {taskId}: NavigationMapper not assigned/found.");
            return;
        }

        // Path + distance
        if (!navigationMapper.TryGetPath(
                fromTarget.transform.position,
                toTarget.transform.position,
                out NavMeshPath path,
                out float distanceMeters))
        {
            Debug.LogWarning($"[ToyFleetManager] Task {taskId}: No complete NavMesh path from '{fromArea}' to '{toArea}'.");
            return;
        }

        if (transporters == null || transporters.Count == 0)
        {
            Debug.LogWarning($"[ToyFleetManager] Task {taskId}: No transporters registered.");
            return;
        }

        // For now, call estimate on every transporter (distance only), and log it.
        for (int i = 0; i < transporters.Count; i++)
        {
            var t = transporters[i];
            if (t == null) continue;

            float etaSeconds = t.CalculateEstimatedTimeToComplete(distanceMeters);

            Debug.Log(
                $"[ToyFleetManager] Task {taskId}: Transporter '{t.name}' ETA={etaSeconds:0.00}s " +
                $"(dist={distanceMeters:0.00}m, from={fromArea}, to={toArea}, priority={priority})"
            );
        }

        // NOTE: 'path' is computed and available here if you later want to store it per task.
        // Right now we only compute distance+path to support ETA calculations.
        _ = path;
    }
}
