using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// SequencerSubSys
/// - Loads ProjectRoot/Sequences/<fileName>.csv
/// - CSV rows: time, fromArea, toArea, priority
///   time supports HH:MM or HH:MM:SS
/// - Runs a fixed-step simulation clock driven by realtime * timingScale
/// - Fires NewTask when events are due
///
/// Inspector is intentionally minimal:
///   ✅ fileName
///   ✅ autoBegin
///   ✅ timingScale
///   ✅ live clock (read-only display fields)
///
/// CHANGE REQUEST:
/// - Do NOT append Day/Hour/Minute to log output
/// - Emit: "[Sequencer] New Task, id_000, C-33, CT, Stat"
/// - Assign each row a stable identity when loading (id_000, id_001, ...)
/// </summary>
public sealed class SequencerSubSys : MonoBehaviour
{
    public static SequencerSubSys I { get; private set; }

    // -----------------------------
    // Inspector: ONLY these show
    // -----------------------------

    [Header("Sequence File (ProjectRoot/Sequences/)")]
    [Tooltip("Base name WITHOUT extension. Example: 'HospitalRunA' -> loads HospitalRunA.csv")]
    [SerializeField] public string fileName = "Sequence1";

    [Header("Playback")]
    [SerializeField] private bool autoBegin = true;

    [Header("Timing")]
    [Tooltip("Sim seconds per real second. Example: 4000 means 1 real second = 4000 sim seconds.")]
    [Min(0.0001f)]
    [SerializeField] private float timingScale = 1.0f;

    [Header("Time Clock (read-only display)")]
    [SerializeField] private int currentDay = 1;     // 1-based
    [SerializeField] private int currentHour = 0;    // 0-23
    [SerializeField] private int currentMinute = 0;  // 0-59
    [SerializeField] private string clockText = "Day 1 00:00";

    // -----------------------------
    // Hidden knobs (still serialized)
    // -----------------------------

    [HideInInspector] [Min(0f)]
    [Tooltip("Optional real-time delay before simulation starts (seconds).")]
    [SerializeField] private float startOffsetSeconds = 0f;

    [HideInInspector] [Min(0.000001f)]
    [Tooltip("Fixed simulation step in SIM seconds. Smaller = smoother sim driver but more CPU.")]
    [SerializeField] private float simStepSeconds = 0.1f;

    [HideInInspector] [Min(1)]
    [Tooltip("Safety cap: maximum fixed-steps processed per Unity frame (prevents lock-ups).")]
    [SerializeField] private int maxStepsPerFrame = 20000;

    [HideInInspector]
    [Tooltip("If true, clock starts at the first row's HH:MM time-of-day.")]
    [SerializeField] private bool anchorClockToFirstRowTime = true;

    // -----------------------------
    // Events
    // -----------------------------

    /// <summary>
    /// NewTask(taskId, fromArea, toArea, priority)
    /// </summary>
    public event Action<string, string, string, string> NewTask;

    // -----------------------------
    // Internals
    // -----------------------------

    private struct SeqEvent
    {
        public float t; // seconds since sequence start (RELATIVE)
        public string id;
        public string fromArea;
        public string toArea;
        public string priority;
    }

    private readonly List<SeqEvent> _events = new();
    private int _eventIndex;

    private bool _loaded;
    private bool _running;

    private double _simTime;
    private double _simBudget;

    private int _anchorTodSeconds;
    private bool _hasAnchor;

    private float _lastRealtime;

    private const int SecondsPerMinute = 60;
    private const int SecondsPerHour = 3600;
    private const int SecondsPerDay = 86400;

    // -----------------------------
    // Unity lifecycle
    // -----------------------------

    private void Awake()
    {
        if (I != null && I != this)
        {
            Destroy(gameObject);
            return;
        }
        I = this;

        LoadSequence();
        UpdateInspectorClock(0.0);
    }

    private void Start()
    {
        if (autoBegin) Begin();
    }

    private void OnDestroy()
    {
        if (I == this) I = null;
    }

    // -----------------------------
    // Public API
    // -----------------------------

    public void Begin()
    {
        if (!_loaded)
        {
            Debug.LogError("[SequencerSubSys] Cannot Begin(): sequence not loaded.");
            return;
        }

        _running = true;
        _eventIndex = 0;

        _simTime = 0.0;
        _simBudget = 0.0;

        _lastRealtime = Time.realtimeSinceStartup;

        // Implement start offset by moving lastRealtime forward.
        if (startOffsetSeconds > 0f)
            _lastRealtime += startOffsetSeconds;

        UpdateInspectorClock(_simTime);
    }

    public void Stop() => _running = false;

    public void Reload(bool restartIfRunning = true)
    {
        bool wasRunning = _running;
        _running = false;

        _events.Clear();
        _loaded = false;
        _hasAnchor = false;

        LoadSequence();

        if (restartIfRunning && wasRunning && _loaded)
            Begin();
    }

    // -----------------------------
    // Main loop
    // -----------------------------

    private void Update()
    {
        if (!_running) return;
        if (_eventIndex >= _events.Count) { _running = false; return; }

        float now = Time.realtimeSinceStartup;
        float realDt = now - _lastRealtime;
        _lastRealtime = now;

        // Waiting for startOffsetSeconds
        if (realDt <= 0f)
        {
            UpdateInspectorClock(_simTime);
            return;
        }

        // Avoid hitch explosions
        realDt = Mathf.Clamp(realDt, 0f, 0.25f);

        _simBudget += (double)realDt * (double)Mathf.Max(0.0001f, timingScale);

        int steps = 0;
        double step = Math.Max(0.000001, (double)simStepSeconds);

        while (_simBudget >= step && steps < maxStepsPerFrame)
        {
            _simBudget -= step;
            _simTime += step;
            steps++;

            StepSimulation();
        }

        if (steps >= maxStepsPerFrame)
        {
            Debug.LogWarning(
                $"[SequencerSubSys] Hit maxStepsPerFrame={maxStepsPerFrame}. Sim is falling behind. " +
                $"Consider increasing simStepSeconds or lowering timingScale."
            );
        }

        UpdateInspectorClock(_simTime);
    }

    private void StepSimulation()
    {
        while (_eventIndex < _events.Count && _simTime >= _events[_eventIndex].t)
        {
            var e = _events[_eventIndex];
            _eventIndex++;

            try
            {
                NewTask?.Invoke(e.id, e.fromArea, e.toArea, e.priority);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SequencerSubSys] Task subscriber threw: {ex}");
            }

            // Log format requested (NO day/hour/minute)
            // "[Sequencer] New Task, id_000, C-33, CT, Stat"
            if (LoggerSubSys.I != null)
            {
                LoggerSubSys.I.LogMsg($"[Sequencer] New Task, {e.id}, {e.fromArea}, {e.toArea}, {e.priority}");
            }
            else
            {
                Debug.Log($"[Sequencer] New Task, {e.id}, {e.fromArea}, {e.toArea}, {e.priority}");
            }
        }
    }

    // -----------------------------
    // Loading
    // -----------------------------

    private void LoadSequence()
    {
        string path = GetSequencePath(fileName);

        if (!File.Exists(path))
        {
            Debug.LogError($"[SequencerSubSys] Sequence file not found: {path}");
            _loaded = false;
            return;
        }

        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception ex)
        {
            Debug.LogError($"[SequencerSubSys] Failed to read {path}: {ex}");
            _loaded = false;
            return;
        }

        // Parse absolute time-of-day seconds first, then convert to relative seconds from first row (with day rollover).
        var parsedAbs = new List<(int todSec, string from, string to, string priority)>(lines.Length);

        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];
            if (string.IsNullOrWhiteSpace(raw)) continue;

            raw = raw.Trim();
            if (raw.StartsWith("#")) continue;

            string[] parts = raw.Split(',');
            if (parts.Length < 4) continue;

            string tStr = parts[0].Trim();
            string fromArea = parts[1].Trim();
            string toArea = parts[2].Trim();
            string priority = parts[3].Trim();

            if (!TryParseTimeOfDayToSeconds(tStr, out int todSeconds))
            {
                Debug.LogWarning($"[SequencerSubSys] Skipping bad time '{tStr}' on line {i + 1}");
                continue;
            }

            parsedAbs.Add((todSeconds, fromArea, toArea, priority));
        }

        if (parsedAbs.Count == 0)
        {
            Debug.LogError("[SequencerSubSys] No valid rows found.");
            _loaded = false;
            return;
        }

        // Keep time ordering; handle midnight rollover after sorting.
        parsedAbs.Sort((a, b) => a.todSec.CompareTo(b.todSec));

        _anchorTodSeconds = parsedAbs[0].todSec;
        _hasAnchor = true;

        _events.Clear();

        int prevAbs = _anchorTodSeconds;
        int dayOffset = 0;

        for (int i = 0; i < parsedAbs.Count; i++)
        {
            int abs = parsedAbs[i].todSec;

            // Rollover: 23:59 then 00:05 => next day
            if (i > 0 && abs < prevAbs)
                dayOffset += SecondsPerDay;

            float rel = (abs - _anchorTodSeconds) + dayOffset;

            string id = $"id_{i:000}";

            _events.Add(new SeqEvent
            {
                t = rel,
                id = id,
                fromArea = parsedAbs[i].from,
                toArea = parsedAbs[i].to,
                priority = parsedAbs[i].priority
            });

            prevAbs = abs;
        }

        _eventIndex = 0;
        _loaded = true;

        Debug.Log($"[SequencerSubSys] Loaded {_events.Count} events. Anchor TOD={SecondsToHHMM(_anchorTodSeconds)}");
    }

    private static string GetSequencePath(string baseName)
    {
        string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
        string seqDir = Path.Combine(projectRoot, "Sequences");
        if (!Directory.Exists(seqDir)) Directory.CreateDirectory(seqDir);
        return Path.Combine(seqDir, $"{baseName}.csv");
    }

    private static bool TryParseTimeOfDayToSeconds(string s, out int todSeconds)
    {
        todSeconds = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;

        s = s.Trim();
        var tokens = s.Split(':');

        if (tokens.Length == 2)
        {
            if (!int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hh)) return false;
            if (!int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mm)) return false;
            if (hh < 0 || hh > 23) return false;
            if (mm < 0 || mm > 59) return false;

            todSeconds = hh * SecondsPerHour + mm * SecondsPerMinute;
            return true;
        }

        if (tokens.Length == 3)
        {
            if (!int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hh)) return false;
            if (!int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mm)) return false;
            if (!int.TryParse(tokens[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ss)) return false;
            if (hh < 0 || hh > 23) return false;
            if (mm < 0 || mm > 59) return false;
            if (ss < 0 || ss > 59) return false;

            todSeconds = hh * SecondsPerHour + mm * SecondsPerMinute + ss;
            return true;
        }

        return false;
    }

    // -----------------------------
    // Clock display (Inspector)
    // -----------------------------

    private void UpdateInspectorClock(double simSeconds)
    {
        GetClockFromSimSeconds(simSeconds, out currentDay, out currentHour, out currentMinute);
        clockText = $"Day {currentDay} {currentHour}:{currentMinute:00}";
    }

    private void GetClockFromSimSeconds(double simSeconds, out int day, out int hour, out int minute)
    {
        int total = Mathf.FloorToInt((float)Math.Max(0.0, simSeconds));

        day = (total / SecondsPerDay) + 1;
        int intoDay = total % SecondsPerDay;

        int tod = (anchorClockToFirstRowTime && _hasAnchor)
            ? (_anchorTodSeconds + intoDay) % SecondsPerDay
            : intoDay;

        hour = tod / SecondsPerHour;
        minute = (tod % SecondsPerHour) / SecondsPerMinute;
    }

    private static string SecondsToHHMM(int todSeconds)
    {
        todSeconds = ((todSeconds % SecondsPerDay) + SecondsPerDay) % SecondsPerDay;
        int hh = todSeconds / SecondsPerHour;
        int mm = (todSeconds % SecondsPerHour) / SecondsPerMinute;
        return $"{hh}:{mm:00}";
    }
}
