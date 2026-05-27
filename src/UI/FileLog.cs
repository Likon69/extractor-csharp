using System.IO;
using Microsoft.Extensions.Logging;

namespace MaNGOS.Extractor.UI;

public static class FileLog
{
    private static readonly object Sync = new();
    private static string? _logFilePath;

    public static string Initialize(string? baseDirectory = null)
    {
        lock (Sync)
        {
            if (!string.IsNullOrWhiteSpace(_logFilePath))
                return _logFilePath;

            var root = baseDirectory ?? AppContext.BaseDirectory;
            var logsDir = Path.Combine(root, "logs");
            Directory.CreateDirectory(logsDir);

            var fileName = $"extractor-{DateTime.Now:yyyyMMdd-HHmmss}.log";
            _logFilePath = Path.Combine(logsDir, fileName);

            WriteLineInternal($"[{DateTime.Now:HH:mm:ss}] [INFO] Log started");
            WriteLineInternal($"[{DateTime.Now:HH:mm:ss}] [INFO] App base directory: {root}");
            return _logFilePath;
        }
    }

    public static string GetPath()
    {
        return _logFilePath ?? Initialize();
    }

    public static void Write(string message, LogLevel level = LogLevel.Information)
    {
        lock (Sync)
        {
            Initialize();
            var line = $"[{DateTime.Now:HH:mm:ss}] [{level.ToString().ToUpperInvariant()}] {message}";
            WriteLineInternal(line);
        }
    }

    public static void WriteException(string context, Exception ex)
    {
        lock (Sync)
        {
            Initialize();
            WriteLineInternal($"[{DateTime.Now:HH:mm:ss}] [ERROR] {context}: {ex.GetType().Name}: {ex.Message}");
            if (!string.IsNullOrWhiteSpace(ex.StackTrace))
                WriteLineInternal(ex.StackTrace!);

            if (ex.InnerException != null)
            {
                WriteLineInternal($"[{DateTime.Now:HH:mm:ss}] [ERROR] InnerException: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                if (!string.IsNullOrWhiteSpace(ex.InnerException.StackTrace))
                    WriteLineInternal(ex.InnerException.StackTrace!);
            }
        }
    }

    private static void WriteLineInternal(string line)
    {
        File.AppendAllText(_logFilePath!, line + Environment.NewLine);
    }
}
