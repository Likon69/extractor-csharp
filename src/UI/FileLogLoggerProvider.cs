using Microsoft.Extensions.Logging;

namespace MaNGOS.Extractor.UI;

/// <summary>
/// Bridges Microsoft.Extensions.Logging to FileLog so all service log messages
/// appear in the log file and (optionally) in the UI via a callback.
/// </summary>
public sealed class FileLogLoggerProvider : ILoggerProvider
{
    private readonly Action<string, LogLevel>? _uiCallback;

    public FileLogLoggerProvider(Action<string, LogLevel>? uiCallback = null)
    {
        _uiCallback = uiCallback;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogLogger(categoryName, _uiCallback);

    public void Dispose() { }

    private sealed class FileLogLogger : ILogger
    {
        private readonly string _category;
        private readonly Action<string, LogLevel>? _uiCallback;

        public FileLogLogger(string category, Action<string, LogLevel>? uiCallback)
        {
            _category = category;
            _uiCallback = uiCallback;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            string message = formatter(state, exception);

            if (_uiCallback != null)
            {
                // AddLog handles FileLog.Write — avoid writing twice
                _uiCallback(message, logLevel);
            }
            else
            {
                FileLog.Write(message, logLevel);
            }

            if (exception != null)
                FileLog.WriteException(_category, exception);
        }
    }
}
