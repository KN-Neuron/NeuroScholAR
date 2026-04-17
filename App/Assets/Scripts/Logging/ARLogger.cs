using UnityEngine;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading.Tasks;

public enum LogLevel { Trace = 0, Debug = 1, Info = 2, Warning = 3, Error = 4 }
public enum LogCategory { AR, EEG, Sync, UI, Perf, Auth }

public class ARLogger : MonoBehaviour
{
    public static ARLogger Instance { get; private set; }

    [Header("Settings")]
    [SerializeField] private LogLevel minimumLevel = LogLevel.Debug;
    [SerializeField] private bool captureUnityLogs = true;
    [SerializeField] private float fpsLogInterval = 20f;

    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB
    private const int MaxLogFiles = 5;

    private string _currentLogPath;
    private readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
    private bool _isRunning;
    private StreamWriter _writer;
    private float _fpsTimer;
    private int _frameCount;


    private void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        InitLogFile();
        WriteSessionMetadata();
        StartFlushLoop();

        if (captureUnityLogs)
            Application.logMessageReceivedThreaded += OnUnityLog;

        Application.quitting += OnAppQuit;
    }

    private void Update()
    {
        _frameCount++;
        _fpsTimer += Time.unscaledDeltaTime;
        if (_fpsTimer >= fpsLogInterval)
        {
            float fps = _frameCount / _fpsTimer;
            Log(LogLevel.Info, LogCategory.Perf, $"FPS={fps:F1} over {fpsLogInterval}s");
            _frameCount = 0;
            _fpsTimer = 0f;
        }
    }

    private void OnDestroy()
    {
        _isRunning = false;
        Application.logMessageReceivedThreaded -= OnUnityLog;
        FlushAllSync();
        _writer?.Close();
    }


    public static void Log(LogLevel level, LogCategory category, string message)
        => Instance?.EnqueueLine(level, category, message);

    public static void LogAR(string msg, LogLevel level = LogLevel.Info)
        => Log(level, LogCategory.AR, msg);
    public static void LogEEG(string msg, LogLevel level = LogLevel.Info)
        => Log(level, LogCategory.EEG, msg);
    public static void LogPerf(string msg)
        => Log(LogLevel.Info, LogCategory.Perf, msg);


    private void EnqueueLine(LogLevel level, LogCategory category, string message)
    {
        if (level < minimumLevel) return;

        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}]" +
                      $"[{level.ToString().ToUpper()}]" +
                      $"[{category}] {message}";
        _queue.Enqueue(line);
    }

    private void OnUnityLog(string msg, string stackTrace, LogType type)
    {
        var level = type switch
        {
            LogType.Warning => LogLevel.Warning,
            LogType.Error or LogType.Exception => LogLevel.Error,
            _ => LogLevel.Debug
        };
   
        var category = LogCategory.AR;

        string body = (type == LogType.Exception || type == LogType.Error) && !string.IsNullOrEmpty(stackTrace)
            ? $"{msg}\nSTACK:\n{stackTrace}"
            : msg;

        EnqueueLine(level, category, body);
    }

    private void InitLogFile()
    {
        string dir = Path.Combine(Application.persistentDataPath, "Logs");
        Directory.CreateDirectory(dir);
        RotateLogs(dir);

        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        _currentLogPath = Path.Combine(dir, $"session_{timestamp}.log");
        _writer = new StreamWriter(_currentLogPath, append: false, Encoding.UTF8) { AutoFlush = false };
    }

    private void RotateLogs(string dir)
    {
        var files = new DirectoryInfo(dir).GetFiles("session_*.log");
        Array.Sort(files, (a, b) => a.CreationTime.CompareTo(b.CreationTime));

        // delete oldest if over limit
        while (files.Length >= MaxLogFiles)
        {
            files[0].Delete();
            var tmp = new DirectoryInfo(dir).GetFiles("session_*.log");
            Array.Sort(tmp, (a, b) => a.CreationTime.CompareTo(b.CreationTime));
            files = tmp;
        }
    }

    private void WriteSessionMetadata()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== SESSION START ===");
        sb.AppendLine($"Time:        {DateTime.Now:O}");
        sb.AppendLine($"Device:      {SystemInfo.deviceModel}");
        sb.AppendLine($"OS:          {SystemInfo.operatingSystem}");
        sb.AppendLine($"App:         {Application.productName} v{Application.version}");
        sb.AppendLine($"Unity:       {Application.unityVersion}");
        sb.AppendLine($"GPU:         {SystemInfo.graphicsDeviceName}");
        sb.AppendLine($"RAM (MB):    {SystemInfo.systemMemorySize}");
        sb.AppendLine("===================");
        _queue.Enqueue(sb.ToString());
    }

    private void StartFlushLoop()
    {
        _isRunning = true;
        _ = FlushLoopAsync();
    }

    private async Task FlushLoopAsync()
    {
        while (_isRunning)
        {
            await Task.Delay(500); // flush every 500ms
            await FlushAsync();
        }
    }

    private async Task FlushAsync()
    {
        if (_writer == null || _queue.IsEmpty) return;

        var sb = new StringBuilder();
        while (_queue.TryDequeue(out string line))
            sb.AppendLine(line);

        try
        {
            await _writer.WriteAsync(sb.ToString());
            await _writer.FlushAsync();
            CheckRotation();
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"[ARLogger] Flush failed: {e.Message}");
        }
    }

    private void FlushAllSync()
    {
        if (_writer == null) return;
        while (_queue.TryDequeue(out string line))
            _writer.WriteLine(line);
        _writer.Flush();
    }

    private void CheckRotation()
    {
        var info = new FileInfo(_currentLogPath);
        if (info.Exists && info.Length > MaxFileSizeBytes)
        {
            _writer?.Close();
            string dir = Path.GetDirectoryName(_currentLogPath);
            RotateLogs(dir);
            string ts = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            _currentLogPath = Path.Combine(dir, $"session_{ts}.log");
            _writer = new StreamWriter(_currentLogPath, false, Encoding.UTF8) { AutoFlush = false };
            Log(LogLevel.Info, LogCategory.AR, "Log rotated — new file started.");
        }
    }

    private void OnAppQuit()
    {
        Log(LogLevel.Info, LogCategory.AR, "=== SESSION END ===");
        FlushAllSync();
    }
}