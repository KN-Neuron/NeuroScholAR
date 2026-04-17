using System.IO;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class LogExporter : MonoBehaviour
{
    [SerializeField] private Button exportButton;
    [SerializeField] private TMP_Text statusText;

    private void Awake()
    {
        exportButton?.onClick.AddListener(Export);
    }

    public void Export()
    {
        string logDir = Path.Combine(Application.persistentDataPath, "Logs");
        if (!Directory.Exists(logDir))
        {
            SetStatus("No log files");
            return;
        }

        var files = Directory.GetFiles(logDir, "session_*.log");
        if (files.Length == 0) { SetStatus("Bo logs"); return; }

        System.Array.Sort(files);
        string latest = files[files.Length - 1];

#if UNITY_ANDROID && !UNITY_EDITOR
        new NativeShare()
            .AddFile(latest)
            .SetSubject("AR Session Log")
            .SetText("Logi sesji AR")
            .Share();
#elif UNITY_EDITOR
        UnityEditor.EditorUtility.RevealInFinder(latest);
#else
        SetStatus($"Log: {latest}");
#endif
        ARLogger.Log(LogLevel.Info, LogCategory.UI, $"Log exported: {Path.GetFileName(latest)}");
    }

    private void SetStatus(string msg)
    {
        if (statusText) statusText.text = msg;
        ARLogger.Log(LogLevel.Info, LogCategory.UI, $"Export status: {msg}");
    }
}