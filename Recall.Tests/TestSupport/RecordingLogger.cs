using Microsoft.Extensions.Logging;

namespace Recall.Tests.TestSupport;

/// <summary>
/// A logger that keeps what it was given, formatted, for tests that assert on a log line.
/// Safe to log to from several threads at once (two requests timing out together);
/// read <see cref="Entries"/> once the code under test has finished.
/// </summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var entry = (logLevel, formatter(state, exception));

        lock (Entries)
            Entries.Add(entry);
    }
}
