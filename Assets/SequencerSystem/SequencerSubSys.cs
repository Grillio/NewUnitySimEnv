using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// SequencerSubSys (Deterministic / Frame-Driven)
///
/// Loads: ProjectRoot/Sequences/<fileName>.csv
/// CSV columns: time, fromArea, toArea, priority
///
/// TIME FORMAT (CSV):
/// - If ClockDisplayMode == TimeOfDay_HHMM:
///     time MUST be "HH:MM" or "HH:MM:SS" (time-of-day)
///     Events are scheduled relative to the first row (supports midnight rollover).
///
/// - If ClockDisplayMode == Elapsed_MMSS:
///     time MUST be "MM:SS" (elapsed)
///     Events are scheduled as absolute elapsed seconds since sequence start.
///
/// KEY BEHAVIOR (YOUR REQUEST):
/// - Simulation time is NOT based on realtime or FPS.
/// - Each rendered Unity frame advances the simulation by 'secondsPerFrame'.
///   Examples:
///     secondsPerFrame = 1.0   -> 1 frame = 1 sim second
///     secondsPerFrame = 0.1   -> 10 frames = 1 sim second
///     secondsPerFrame = 0.005 -> 200 frames = 1 sim second
/// - Works perfectly with Pause + Step (each Step advances exactly one frame).
///
/// LOG REQUIREMENT:
/// - Emit exactly: "[Sequencer] New Task, id_000, C-33, CT, Stat"
/// - No Day/Hour/Minute appended in the log.
/// - IDs are stable based on load order after sorting: id_000, id_001, ...
/// </summary>
public sealed class SequencerSubSys : MonoBehaviour
{
    public static SequencerSubSys I { get; private set; }

    // -----------------------------
    // Inspector
    // -----------------------------

    public enum ClockDisplayMode
    {
        TimeOfDay_HHMM, // CSV expects HH:MM or HH:MM:SS (time-of-day)
        Elapsed_MMSS    // CSV expects MM:SS (elapsed)
    }

    [Header("Sequence File (ProjectRoot/Sequences/)")]
    [Tooltip("Base name WITHOUT extension. Example: 'Sequence1' -> loads Sequence1.csv")]
    [SerializeField] public string fileName = "Sequence1";

    [Header("Playback")]
    [SerializeField] private bool autoBegin = true;

    [Header("Deterministic Simulation (Frame-Driven)")]
    [Tooltip("How many SIM seconds pass per rendered frame.\n" +
             "1.0 => 1 frame = 1 sec\n" +
             "0.1 => 10 frames = 1 sec\n" +
             "0.005 => 200 frames = 1 sec")]
    [Min(0.000001f)]
    [SerializeField] public float secondsPerFrame = 1f;

    [Header("CSV Time Mode / Clock Display")]
    [SerializeField] private ClockDisplayMode clockDisplayMode = ClockDisplayMode.TimeOfDay_HHMM;

    [Header("Time Clock (read-only display)")]
    [SerializeField] private int currentDay = 1;            // meaningful in TimeOfDay mode
    [SerializeField] private int currentHourOrMinute = 0;   // TOD: hour, Elapsed: minutes
    [SerializeField] private int currentMinuteOrSecond = 0; // TOD: minute, Elapsed: seconds (integer)
    [SerializeField] private float currentSecondFloat = 0f; // seconds-with-fraction within the minute (or elapsed seconds component)
    [SerializeField] private string clockText = "Day 1 00:00";

    // -----------------------------
    // Hidden knobs (still serialized)
    // -----------------------------

    [HideInInspector] [Min(0f)]
    [Tooltip("Optional delay BEFORE simulation begins, measured in SIM seconds (not real seconds).")]
    [SerializeField] private float startOffsetSeconds = 0f;

    [HideInInspector] [Min(0.000001f)]
    [Tooltip("Internal simulation step (SIM seconds). Smaller = finer stepping but more CPU.\n" +
             "If you want EXACTLY one step per frame, set simStepSeconds == secondsPerFrame.\n" +
             "Otherwise, sim will accumulate budget and step multiple times per frame if needed.")]
    [SerializeField] private float simStepSeconds = 0.1f;

    [HideInInspector] [Min(1)]
    [Tooltip("Safety cap: maximum sim steps processed per Unity frame (prevents lock-ups).")]
    [SerializeField] private int maxStepsPerFrame = 20000;

    [HideInInspector]
    [Tooltip("TimeOfDay mode only: if true, displayed clock starts at the first row's time-of-day.")]
    [SerializeField] private bool anchorClockToFirstRowTime = true;

    /// <summary>
    /// Back-compat for other scripts that referenced SequencerSubSys.TimingScale.
    /// In this deterministic model, there is no realtime scaling; return 1.
    /// </summary>
    public float TimingScale => 1f;

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
        public float t; // seconds since sequence start (RELATIVE for TOD mode, ABS elapsed for Elapsed mode)
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

    private double _startDelayBudget; // SIM seconds remaining before simulation begins

    private int _anchorTodSeconds;
    private bool _hasAnchor;

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

    /// <summary>
    /// Returns a display string based on the selected clock display mode.
    /// - TimeOfDay_HHMM -> "HH:MM"
    /// - Elapsed_MMSS   -> "MM:SS"
    /// </summary>
    public string GetCurrentTimeString()
    {
        if (clockDisplayMode == ClockDisplayMode.Elapsed_MMSS)
        {
            int whole = Mathf.FloorToInt((float)Math.Max(0.0, _simTime));
            int mm = whole / SecondsPerMinute;
            int ss = whole % SecondsPerMinute;
            return $"{mm:00}:{ss:00}";
        }

        return $"{currentHourOrMinute:00}:{currentMinuteOrSecond:00}";
    }

    /// <summary>Fractional seconds component for display/debug.</summary>
    public float CurrentSecondFloat => currentSecondFloat;

    /// <summary>Current simulation time in seconds since Begin().</summary>
    public double SimTimeSeconds => _simTime;

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

        _startDelayBudget = Math.Max(0.0, startOffsetSeconds);

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
    // Main loop (deterministic)
    // -----------------------------

    private void Update()
    {
        if (!_running) return;
        if (_eventIndex >= _events.Count) { _running = false; return; }

        double dtSim = Math.Max(0.0, (double)secondsPerFrame);
        if (dtSim <= 0.0)
        {
            UpdateInspectorClock(_simTime);
            return;
        }

        // Start delay (SIM seconds)
        if (_startDelayBudget > 0.0)
        {
            _startDelayBudget -= dtSim;
            if (_startDelayBudget > 0.0)
            {
                UpdateInspectorClock(_simTime);
                return;
            }

            // carry remainder into budget if we overshot the delay
            dtSim = -_startDelayBudget;
            _startDelayBudget = 0.0;
        }

        _simBudget += dtSim;

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
            Debug.LogWarning($"[SequencerSubSys] Hit maxStepsPerFrame={maxStepsPerFrame}. Sim is falling behind.");

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

            // EXACT log format required
            if (LoggerSubSys.I != null)
                LoggerSubSys.I.LogMsg($"[Sequencer] New Task, {e.id}, {e.fromArea}, {e.toArea}, {e.priority}");
            else
                Debug.Log($"[Sequencer] New Task, {e.id}, {e.fromArea}, {e.toArea}, {e.priority}");
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

        _events.Clear();

        if (clockDisplayMode == ClockDisplayMode.Elapsed_MMSS)
            LoadElapsed_MMSS(lines);
        else
            LoadTimeOfDay_HHMM(lines);

        _eventIndex = 0;
        _loaded = _events.Count > 0;

        if (!_loaded)
            Debug.LogError("[SequencerSubSys] No valid rows found.");
    }

    private void LoadElapsed_MMSS(string[] lines)
    {
        var parsed = new List<(int elapsedSec, string from, string to, string priority)>(lines.Length);

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

            if (!TryParseElapsed_MMSS(tStr, out int elapsed))
            {
                Debug.LogWarning($"[SequencerSubSys] (Elapsed) Skipping bad time '{tStr}' on line {i + 1} (expected MM:SS).");
                continue;
            }

            parsed.Add((elapsed, fromArea, toArea, priority));
        }

        if (parsed.Count == 0) return;

        parsed.Sort((a, b) => a.elapsedSec.CompareTo(b.elapsedSec));

        _anchorTodSeconds = 0;
        _hasAnchor = false;

        for (int i = 0; i < parsed.Count; i++)
        {
            string id = $"id_{i:000}";
            _events.Add(new SeqEvent
            {
                t = parsed[i].elapsedSec,
                id = id,
                fromArea = parsed[i].from,
                toArea = parsed[i].to,
                priority = parsed[i].priority
            });
        }

        Debug.Log($"[SequencerSubSys] Loaded {_events.Count} events (Elapsed MM:SS). MaxT={parsed[^1].elapsedSec}s");
    }

    private void LoadTimeOfDay_HHMM(string[] lines)
    {
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

            if (!TryParseTimeOfDay_HHMMSS(tStr, out int todSeconds))
            {
                Debug.LogWarning($"[SequencerSubSys] (TimeOfDay) Skipping bad time '{tStr}' on line {i + 1} (expected HH:MM or HH:MM:SS).");
                continue;
            }

            parsedAbs.Add((todSeconds, fromArea, toArea, priority));
        }

        if (parsedAbs.Count == 0) return;

        parsedAbs.Sort((a, b) => a.todSec.CompareTo(b.todSec));

        _anchorTodSeconds = parsedAbs[0].todSec;
        _hasAnchor = true;

        int prevAbs = _anchorTodSeconds;
        int dayOffset = 0;

        for (int i = 0; i < parsedAbs.Count; i++)
        {
            int abs = parsedAbs[i].todSec;

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

        Debug.Log($"[SequencerSubSys] Loaded {_events.Count} events. Anchor TOD={SecondsToHHMM(_anchorTodSeconds)}");
    }

    private static string GetSequencePath(string baseName)
    {
        string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
        string seqDir = Path.Combine(projectRoot, "Sequences");
        if (!Directory.Exists(seqDir)) Directory.CreateDirectory(seqDir);
        return Path.Combine(seqDir, $"{baseName}.csv");
    }

    // -----------------------------
    // Time Parsing
    // -----------------------------

    private static bool TryParseElapsed_MMSS(string s, out int elapsedSeconds)
    {
        elapsedSeconds = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;

        s = s.Trim();
        var tokens = s.Split(':');
        if (tokens.Length != 2) return false;

        if (!int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mm)) return false;
        if (!int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ss)) return false;

        if (mm < 0) return false;
        if (ss < 0 || ss > 59) return false;

        elapsedSeconds = mm * SecondsPerMinute + ss;
        return true;
    }

    private static bool TryParseTimeOfDay_HHMMSS(string s, out int todSeconds)
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
        double s = Math.Max(0.0, simSeconds);
        int whole = (int)Math.Floor(s);
        double frac = s - whole;

        if (clockDisplayMode == ClockDisplayMode.Elapsed_MMSS)
        {
            int mm = whole / SecondsPerMinute;
            int ss = whole % SecondsPerMinute;

            currentDay = 0;
            currentHourOrMinute = mm;
            currentMinuteOrSecond = ss;

            currentSecondFloat = (float)(ss + frac); // 0..59.999

            clockText = $"{mm:00}:{ss:00} ({currentSecondFloat:0.000}s)";
            return;
        }

        // Time-of-day display
        int day = (whole / SecondsPerDay) + 1;
        int intoDay = whole % SecondsPerDay;

        int tod = (anchorClockToFirstRowTime && _hasAnchor)
            ? (_anchorTodSeconds + intoDay) % SecondsPerDay
            : intoDay;

        int hh = tod / SecondsPerHour;
        int mm2 = (tod % SecondsPerHour) / SecondsPerMinute;
        int ss2 = tod % SecondsPerMinute;

        currentDay = day;
        currentHourOrMinute = hh;
        currentMinuteOrSecond = mm2;

        currentSecondFloat = (float)(ss2 + frac); // 0..59.999

        clockText = $"Day {day} {hh:00}:{mm2:00} ({currentSecondFloat:0.000}s)";
    }

    private static string SecondsToHHMM(int todSeconds)
    {
        todSeconds = ((todSeconds % SecondsPerDay) + SecondsPerDay) % SecondsPerDay;
        int hh = todSeconds / SecondsPerHour;
        int mm = (todSeconds % SecondsPerHour) / SecondsPerMinute;
        return $"{hh:00}:{mm:00}";
    }
}
