namespace Recall.Web.Services.Digest;

/// <summary>
/// Bound from the <c>Digest</c> configuration section: the weekly email digest.
/// It is off unless <see cref="Enabled"/> is set, and enabling it requires
/// <see cref="SiteOptions.BaseUrl"/> (checked at startup).
/// </summary>
public sealed class DigestOptions
{
    public const string SectionName = "Digest";

    /// <summary>
    /// Master switch. While false no digest is sent, and the Profile switch and
    /// the Dashboard card that offer it are hidden.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>The day the digest goes out, in UTC.</summary>
    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Friday;

    /// <summary>The hour (0–23, UTC) from which the digest goes out on that day.</summary>
    public int HourUtc { get; set; } = 15;

    /// <summary>
    /// How long after the scheduled moment a digest may still be sent. The job
    /// runs hourly, so this is what lets it catch up after a restart, or work
    /// through more recipients than one run takes.
    /// </summary>
    public int CatchUpHours { get; set; } = 48;

    /// <summary>
    /// The most users one hourly run handles. Set it to fit the mail provider's
    /// sending limits; the rest are picked up by the following runs.
    /// </summary>
    public int MaxPerRun { get; set; } = 100;
}

/// <summary>Bound from the <c>Site</c> configuration section.</summary>
public sealed class SiteOptions
{
    public const string SectionName = "Site";

    /// <summary>
    /// The site's public address, e.g. <c>https://recall.nu</c>. Used for links
    /// in emails sent by a background job, which has no request to take the
    /// scheme and host from. No default: it must be set before the digest is
    /// enabled.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary><see cref="BaseUrl"/> as an absolute http(s) address without a trailing slash, or null when it is not one.</summary>
    public string? NormalizedBaseUrl =>
        Uri.TryCreate(BaseUrl?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/')
            : null;
}
