namespace Recall.Tests.TestSupport;

/// <summary>
/// A <see cref="TimeProvider"/> stuck at one instant. Pass a
/// <paramref name="localTimeZone"/> to stand in for a machine whose local date
/// differs from the UTC date at that instant.
/// </summary>
public sealed class FixedTimeProvider(DateTimeOffset now, TimeZoneInfo? localTimeZone = null) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public override TimeZoneInfo LocalTimeZone => localTimeZone ?? TimeZoneInfo.Utc;
}
