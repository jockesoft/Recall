namespace Recall.Web.Services.Push;

/// <summary>
/// The time zone a device reported (<c>Intl.DateTimeFormat().resolvedOptions().timeZone</c>),
/// checked against the server's own zone data. Quiet hours are worked out in it.
/// </summary>
public static class DeviceTimeZone
{
    public const string Fallback = "UTC";

    /// <summary>The zone id when the server knows it, otherwise <see cref="Fallback"/>.</summary>
    public static string Normalize(string? id)
    {
        var trimmed = id?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed.Length <= 64 && TimeZoneInfo.TryFindSystemTimeZoneById(trimmed, out _)
            ? trimmed
            : Fallback;
    }

    /// <summary>The zone for <paramref name="id"/>, or UTC when it is not known (any more).</summary>
    public static TimeZoneInfo Find(string? id) =>
        !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;
}
