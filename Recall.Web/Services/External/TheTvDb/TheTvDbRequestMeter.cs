namespace Recall.Web.Services.External.TheTvDb;

/// <summary>
/// Counts the requests sent to TheTVDB by the code running inside it, so a
/// background job can log what one run cost:
/// <code>
/// using var meter = TheTvDbRequestMeter.Start();
/// await DoTheWorkAsync();
/// logger.LogInformation("{Requests} TheTVDB requests", meter.Count);
/// </code>
/// It follows the async flow (an <see cref="AsyncLocal{T}"/>), so it sees the
/// requests of everything the caller awaits, in parallel or not, and nothing
/// from requests other work happens to make at the same time. Every request
/// that leaves the process is counted: retries, the resend after a 401, and
/// the login itself. Meters nest; an inner one's requests count in the outer.
/// </summary>
public sealed class TheTvDbRequestMeter : IDisposable
{
    private static readonly AsyncLocal<TheTvDbRequestMeter?> Current = new();

    private readonly TheTvDbRequestMeter? _parent;
    private int _count;

    private TheTvDbRequestMeter(TheTvDbRequestMeter? parent) => _parent = parent;

    /// <summary>Requests sent since <see cref="Start"/>.</summary>
    public int Count => Volatile.Read(ref _count);

    public static TheTvDbRequestMeter Start()
    {
        var meter = new TheTvDbRequestMeter(Current.Value);
        Current.Value = meter;
        return meter;
    }

    /// <summary>Called by the transport for each request it is about to send.</summary>
    public static void Record()
    {
        for (var meter = Current.Value; meter is not null; meter = meter._parent)
            Interlocked.Increment(ref meter._count);
    }

    public void Dispose() => Current.Value = _parent;
}
