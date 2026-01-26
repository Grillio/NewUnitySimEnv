// LoggerSubSys.cs
// Writes logs to:
// <ProjectRoot>/SimulationRuns/<SequenceFileName>/<run#>.txt
//
// Log format: timestamp,message
// timestamp is seconds since StartLogging(), beginning at 0.00
//
// This version expects SequencerSubSys (not SequenceSubSys) and uses its fileName
// to name the folder.

using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

public sealed class LoggerSubSys : MonoBehaviour
{
    public static LoggerSubSys I { get; private set; }

    [Header("Wiring")]
    [Tooltip("If null, LoggerSubSys will try GetComponent<SequencerSubSys>() then FindObjectOfType.")]
    [SerializeField] private SequencerSubSys sequencerSubSys;

    [Header("Options")]
    [Tooltip("Also echo logs to Unity Console.")]
    [SerializeField] private bool echoToConsole = false;

    private readonly object _ioLock = new object();

    private float _startTimeRealtime;
    private bool _started;

    private string _logFilePath;
    private StreamWriter _writer;

    private void Awake()
    {
        if (I != null && I != this)
        {
            Destroy(gameObject);
            return;
        }

        I = this;

        // If SequencerSubSys already awakened, grab it. Otherwise ResolveSequencerSubSys() will find it later.
        if (sequencerSubSys == null)
            sequencerSubSys = SequencerSubSys.I;
    }

    private void Start()
    {
        StartLogging();
    }

    private void OnApplicationQuit()
    {
        End();
    }

    private void OnDestroy()
    {
        if (I == this) I = null;
        CloseWriter();
    }

    /// <summary>
    /// Initializes the log file and writes the start line at 0.00 seconds.
    /// Called automatically from Unity Start().
    /// </summary>
    public void StartLogging()
    {
        if (_started) return;

        ResolveSequencerSubSys();
        string sequenceName = GetSequenceFolderName();

        string projectRoot = GetProjectRoot();
        string baseDir = Path.Combine(projectRoot, "SimulationRuns", sequenceName);
        Directory.CreateDirectory(baseDir);

        int runNumber = GetNextRunNumber(baseDir);
        _logFilePath = Path.Combine(baseDir, $"{runNumber}.txt");

        OpenWriter(_logFilePath);

        _startTimeRealtime = Time.realtimeSinceStartup;
        _started = true;

        WriteLine(0f, "SIM_START");
    }

    /// <summary>
    /// Log a message with a timestamp offset from simulation start.
    /// Output: "timestamp,message"
    /// </summary>
    public void LogMsg(string message)
    {
        if (!_started)
            StartLogging();

        float t = Time.realtimeSinceStartup - _startTimeRealtime;
        WriteLine(t, message);

        if (echoToConsole)
            Debug.Log($"[LoggerSubSys] {t.ToString("0.00", CultureInfo.InvariantCulture)},{message}");
    }

    /// <summary>
    /// Ends logging and writes a final end line.
    /// Safe to call multiple times.
    /// </summary>
    public void End()
    {
        if (!_started) return;

        float t = Time.realtimeSinceStartup - _startTimeRealtime;
        WriteLine(t, "SIM_END");

        _started = false;
        CloseWriter();
    }

    // -----------------------------
    // Internals
    // -----------------------------

    private void ResolveSequencerSubSys()
    {
        if (sequencerSubSys != null) return;

        sequencerSubSys = GetComponent<SequencerSubSys>();
        if (sequencerSubSys != null) return;

        sequencerSubSys = FindObjectOfType<SequencerSubSys>();
    }

    private string GetSequenceFolderName()
    {
        // Use SequencerSubSys fileName (base name) as folder name.
        // Fall back to something safe if missing.
        string name = (sequencerSubSys != null) ? sequencerSubSys.fileName : null;
        

        if (!string.IsNullOrWhiteSpace(name))
            return SanitizeForFolderName(name);

        return "UnknownSequence";
    }

    private static string GetProjectRoot()
    {
        // Application.dataPath => <ProjectRoot>/Assets
        return Directory.GetParent(Application.dataPath)!.FullName;
    }

    private static int GetNextRunNumber(string dir)
    {
        int max = 0;

        if (!Directory.Exists(dir))
            return 1;

        foreach (string path in Directory.GetFiles(dir, "*.txt", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                if (n > max) max = n;
        }

        return max + 1;
    }

    private void OpenWriter(string path)
    {
        lock (_ioLock)
        {
            CloseWriter();

            _writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };

            _writer.WriteLine("timestamp,message");
        }
    }

    private void CloseWriter()
    {
        lock (_ioLock)
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch { }
            finally
            {
                _writer = null;
            }
        }
    }

    private void WriteLine(float tSeconds, string message)
    {
        string ts = tSeconds.ToString("0.00", CultureInfo.InvariantCulture);
        string sanitizedMsg = SanitizeCsv(message);

        lock (_ioLock)
        {
            if (_writer == null)
            {
                if (!string.IsNullOrEmpty(_logFilePath))
                    OpenWriter(_logFilePath);
                else
                    return;
            }

            _writer.WriteLine($"{ts},{sanitizedMsg}");
        }
    }

    private static string SanitizeCsv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        s = s.Replace("\r", " ").Replace("\n", " ");

        bool needsQuotes = s.Contains(",") || s.Contains("\"");
        if (!needsQuotes) return s;

        s = s.Replace("\"", "\"\"");
        return $"\"{s}\"";
    }

    private static string SanitizeForFolderName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        name = name.Trim();
        if (string.IsNullOrEmpty(name)) name = "UnnamedSequence";
        return name;
    }
}
