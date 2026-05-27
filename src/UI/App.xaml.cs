using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using MaNGOS.Extractor.Core.Constants;
using Microsoft.Extensions.Logging;

namespace MaNGOS.Extractor.UI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var logPath = FileLog.Initialize();
        Debug.WriteLine($"MaNGOS Extractor v1.0 — WotLK build {WowConstants.TargetBuild}");
        Debug.WriteLine($"Format: map=v1.5 | vmap=VMAPt07 | mmap=t06");
        FileLog.Write($"MaNGOS Extractor v1.0 — WotLK build {WowConstants.TargetBuild}", LogLevel.Information);
        FileLog.Write($"Format: map=v1.5 | vmap=VMAPt07 | mmap=t06", LogLevel.Information);
        FileLog.Write($"Log file: {logPath}", LogLevel.Information);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnTaskSchedulerUnobservedTaskException;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        FileLog.Write($"Application exit code: {e.ApplicationExitCode}", LogLevel.Information);
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        FileLog.WriteException("DispatcherUnhandledException", e.Exception);
    }

    private static void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            FileLog.WriteException("AppDomain.CurrentDomain.UnhandledException", ex);
        else
            FileLog.Write($"Unhandled exception object: {e.ExceptionObject}", LogLevel.Error);
    }

    private static void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        FileLog.WriteException("TaskScheduler.UnobservedTaskException", e.Exception);
    }
}
