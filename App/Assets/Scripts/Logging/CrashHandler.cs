using System;
using UnityEngine;

public class CrashHandler : MonoBehaviour
{
    private void Awake()
    {
 
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    private void OnDestroy()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        var ex = args.ExceptionObject as Exception;
        string msg = ex != null
            ? $"UNHANDLED EXCEPTION: {ex.Message}\n{ex.StackTrace}"
            : $"UNHANDLED EXCEPTION: {args.ExceptionObject}";


        ARLogger.Log(LogLevel.Error, LogCategory.AR, msg);
    }
}