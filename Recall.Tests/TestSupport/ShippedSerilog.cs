using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Recall.Tests.TestSupport;

/// <summary>
/// A Serilog logger with the levels the app ships with: the
/// <c>Serilog:MinimumLevel</c> section of the real appsettings files (copied to
/// the test output), writing to an <see cref="InMemorySink"/> instead of the
/// console and log file the files name.
/// </summary>
public static class ShippedSerilog
{
    /// <summary>The configurations the app runs with: the base file alone, and with each environment's file on top.</summary>
    public static readonly string?[] Environments = [null, "Development", "Production"];

    public static Logger CreateLogger(InMemorySink sink, string? environment)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false);
        if (environment is not null)
            builder.AddJsonFile($"appsettings.{environment}.json", optional: false);
        var appConfiguration = builder.Build();

        // Only the levels: the sinks the files name (console, a log file) stay out of a test run.
        var levels = appConfiguration.GetSection("Serilog:MinimumLevel").AsEnumerable()
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        if (levels.Count == 0)
            throw new InvalidOperationException("appsettings.json has no Serilog:MinimumLevel section.");
        var levelsOnly = new ConfigurationBuilder().AddInMemoryCollection(levels).Build();

        return new LoggerConfiguration()
            .ReadFrom.Configuration(levelsOnly)
            .WriteTo.Sink(sink)
            .CreateLogger();
    }
}

/// <summary>Keeps every event it is given.</summary>
public sealed class InMemorySink : ILogEventSink
{
    private readonly List<LogEvent> _events = [];

    public IReadOnlyList<LogEvent> Events
    {
        get
        {
            lock (_events)
                return _events.ToList();
        }
    }

    public void Emit(LogEvent logEvent)
    {
        lock (_events)
            _events.Add(logEvent);
    }

    public static string? Scalar(LogEvent logEvent, string property) =>
        logEvent.Properties.TryGetValue(property, out var value) ? (value as ScalarValue)?.Value?.ToString() : null;

    public static string? SourceContext(LogEvent logEvent) => Scalar(logEvent, Constants.SourceContextPropertyName);
}
