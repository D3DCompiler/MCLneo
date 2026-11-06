using System.Collections.Concurrent;
using System.Text;

namespace MCLneo.Core.Infrastructure;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error
}

/// <summary>Small thread-safe file logger that never throws into caller code.</summary>
public sealed class AppLog
{
    private readonly object _gate = new();
    private readonly string _file;
    private readonly ConcurrentQueue<string> _recent = new();

    public AppLog(AppPaths paths)
    {
        paths.EnsureCreated();
        _file = Path.Combine(paths.LogsDirectory, $"mclneo-{DateTime.Now:yyyyMMdd}.log");
    }

    public event Action<LogLevel, string>? Written;

    public IReadOnlyList<string> Recent => _recent.ToArray();

    public void Debug(string message) => Write(LogLevel.Debug, message);
    public void Info(string message) => Write(LogLevel.Info, message);
    public void Warn(string message, Exception? ex = null) => Write(LogLevel.Warn, message, ex);
    public void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);

    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level.ToString().ToUpperInvariant()}] {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (_gate)
        {
            try
            {
                File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Logging must never take the launcher down.
            }
        }

        _recent.Enqueue(line);
        while (_recent.Count > 300 && _recent.TryDequeue(out _))
        {
        }

        try
        {
            Written?.Invoke(level, line);
        }
        catch
        {
            // Ignore observer failures.
        }
    }
}
