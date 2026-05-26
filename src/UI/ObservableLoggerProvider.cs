using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;

namespace MaNGOS.Extractor.UI;

public sealed class ObservableLoggerProvider : ILoggerFactory, ILoggerProvider
{
    public static readonly ObservableLoggerProvider Instance = new();

    private readonly List<ILogger> _loggers = new();
    public ObservableCollection<LogEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new ObservableLogger(this, categoryName);

    public ILoggerProvider CreateLoggerProvider(string categoryName) => this;

    public void Dispose() { }

    void ILoggerFactory.AddProvider(ILoggerProvider provider) { }
}

public sealed class ObservableLogger : ILogger
{
    private readonly ObservableLoggerProvider _provider;
    private readonly string _categoryName;

    public ObservableLogger(ObservableLoggerProvider provider, string categoryName)
    {
        _provider = provider;
        _categoryName = categoryName;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = exception != null
            ? $"{formatter(state, null)}: {exception.Message}"
            : formatter(state, null);

        var entry = new LogEntry(DateTime.Now, logLevel, $"[{_categoryName}] {message}");
        
        _provider.Entries.Add(entry);

        while (_provider.Entries.Count > 1000)
            _provider.Entries.RemoveAt(0);
    }
}

public class LogEntry
{
    public DateTime Timestamp { get; }
    public LogLevel Level { get; }
    public string Message { get; }

    public System.Windows.Media.Brush Color => Level switch
    {
        LogLevel.Warning => System.Windows.Media.Brushes.Orange,
        LogLevel.Error => System.Windows.Media.Brushes.Red,
        LogLevel.Critical => System.Windows.Media.Brushes.DarkRed,
        _ => System.Windows.Media.Brushes.White
    };

    public LogEntry(DateTime timestamp, LogLevel level, string message)
    {
        Timestamp = timestamp;
        Level = level;
        Message = message;
    }
}