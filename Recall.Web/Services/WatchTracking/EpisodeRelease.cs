using System.Globalization;

namespace Recall.Web.Services.WatchTracking;

/// <summary>
/// When an episode is released: a moment in UTC.
/// </summary>
/// <param name="Utc">The moment, as a UTC <see cref="DateTime"/>.</param>
/// <param name="IsTimeKnown">
/// True when it comes from the series' air time in its country's zone. False
/// for a fallback (no air time, or no known zone): then <see cref="Utc"/> is a
/// deliberately late bound, and only the air date may be shown, never a time.
/// </param>
/// <param name="AirDate">TheTVDB's air date, the date shown when no time is known.</param>
/// <param name="LocalTime">The air time as TheTVDB gives it, in the zone of <see cref="ZoneLabel"/>; null for a fallback.</param>
/// <param name="ZoneLabel">"ET", "CEST", "UK"...: what the server writes after the time; null for a fallback.</param>
public sealed record ReleaseMoment(DateTime Utc, bool IsTimeKnown, DateOnly AirDate, TimeOnly? LocalTime = null, string? ZoneLabel = null)
{
    /// <summary>Released by <paramref name="nowUtc"/>.</summary>
    public bool IsReleasedBy(DateTime nowUtc) => Utc <= nowUtc;

    /// <summary>
    /// The date the Upcoming groups (Today / Tomorrow / This week) file it
    /// under: the UTC date of the moment when the time is known, else the air
    /// date itself, which is the only date a date-only card shows.
    /// </summary>
    public DateOnly GroupDate => IsTimeKnown ? DateOnly.FromDateTime(Utc) : AirDate;

    /// <summary>
    /// The <c>datetime</c> attribute of the <c>&lt;time&gt;</c> element: the
    /// moment in UTC, ISO 8601 ("2026-10-03T00:00:00Z"), which the browser
    /// rewrites into the viewer's own date and time; just the date
    /// ("2026-10-05") for a fallback, which is never given a time.
    /// </summary>
    public string DateTimeAttribute => IsTimeKnown
        ? Utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
        : AirDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// The one rule for when an episode counts as released. TheTVDB gives an air
/// date with no time zone, plus the series' air time and its country of origin;
/// the release moment is the air date at the air time in that country's zone.
/// Gold Rush S17E01: 2026-10-02 at 20:00 in New York is 2026-10-03 00:00 UTC.
/// <para>
/// When something is missing the moment is late, never early: a notification
/// may arrive a few hours late, but never before the episode exists.
/// </para>
/// <list type="bullet">
/// <item>Air time and a known country: that moment (daylight saving included).</item>
/// <item>A known country but no air time: the end of the air date in that
/// country (midnight local), when anything released that day has been.</item>
/// <item>No known country: 12:00 UTC the day after the air date, the end of that
/// date everywhere on Earth (UTC−12).</item>
/// <item>No air date: no moment (never released for notifications and
/// "upcoming"; progress leaves it out, and it may always be marked watched).</item>
/// </list>
/// Pure: everything comes from data already in the cached series aggregate,
/// so it costs no TheTVDB request.
/// </summary>
public static class EpisodeRelease
{
    private sealed record Zone(string Id, string StandardLabel, string DaylightLabel);

    /// <summary>
    /// TheTVDB's country codes (ISO 3166-1 alpha-3, lower case) to the zone its
    /// broadcasters schedule by. A country with several zones gets the one its
    /// networks announce times in: the US and Canada Eastern, Australia
    /// Sydney, Brazil São Paulo, Russia Moscow, Mexico Mexico City, Indonesia
    /// Jakarta. A country missing here takes the no-country fallback.
    /// </summary>
    private static readonly Dictionary<string, Zone> Zones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["usa"] = new("America/New_York", "ET", "ET"),
        ["can"] = new("America/Toronto", "ET", "ET"),
        ["mex"] = new("America/Mexico_City", "CST", "CST"),
        ["bra"] = new("America/Sao_Paulo", "BRT", "BRT"),
        ["arg"] = new("America/Argentina/Buenos_Aires", "ART", "ART"),
        ["chl"] = new("America/Santiago", "CLT", "CLST"),
        ["col"] = new("America/Bogota", "COT", "COT"),
        ["per"] = new("America/Lima", "PET", "PET"),
        ["gbr"] = new("Europe/London", "GMT", "BST"),
        ["irl"] = new("Europe/Dublin", "GMT", "IST"),
        ["prt"] = new("Europe/Lisbon", "WET", "WEST"),
        ["isl"] = new("Atlantic/Reykjavik", "GMT", "GMT"),
        ["swe"] = new("Europe/Stockholm", "CET", "CEST"),
        ["nor"] = new("Europe/Oslo", "CET", "CEST"),
        ["dnk"] = new("Europe/Copenhagen", "CET", "CEST"),
        ["deu"] = new("Europe/Berlin", "CET", "CEST"),
        ["fra"] = new("Europe/Paris", "CET", "CEST"),
        ["nld"] = new("Europe/Amsterdam", "CET", "CEST"),
        ["bel"] = new("Europe/Brussels", "CET", "CEST"),
        ["lux"] = new("Europe/Luxembourg", "CET", "CEST"),
        ["che"] = new("Europe/Zurich", "CET", "CEST"),
        ["aut"] = new("Europe/Vienna", "CET", "CEST"),
        ["ita"] = new("Europe/Rome", "CET", "CEST"),
        ["esp"] = new("Europe/Madrid", "CET", "CEST"),
        ["pol"] = new("Europe/Warsaw", "CET", "CEST"),
        ["cze"] = new("Europe/Prague", "CET", "CEST"),
        ["svk"] = new("Europe/Bratislava", "CET", "CEST"),
        ["hun"] = new("Europe/Budapest", "CET", "CEST"),
        ["svn"] = new("Europe/Ljubljana", "CET", "CEST"),
        ["hrv"] = new("Europe/Zagreb", "CET", "CEST"),
        ["srb"] = new("Europe/Belgrade", "CET", "CEST"),
        ["fin"] = new("Europe/Helsinki", "EET", "EEST"),
        ["est"] = new("Europe/Tallinn", "EET", "EEST"),
        ["lva"] = new("Europe/Riga", "EET", "EEST"),
        ["ltu"] = new("Europe/Vilnius", "EET", "EEST"),
        ["grc"] = new("Europe/Athens", "EET", "EEST"),
        ["rou"] = new("Europe/Bucharest", "EET", "EEST"),
        ["bgr"] = new("Europe/Sofia", "EET", "EEST"),
        ["ukr"] = new("Europe/Kyiv", "EET", "EEST"),
        ["isr"] = new("Asia/Jerusalem", "IST", "IDT"),
        ["tur"] = new("Europe/Istanbul", "TRT", "TRT"),
        ["rus"] = new("Europe/Moscow", "MSK", "MSK"),
        ["egy"] = new("Africa/Cairo", "EET", "EEST"),
        ["zaf"] = new("Africa/Johannesburg", "SAST", "SAST"),
        ["nga"] = new("Africa/Lagos", "WAT", "WAT"),
        ["ind"] = new("Asia/Kolkata", "IST", "IST"),
        ["chn"] = new("Asia/Shanghai", "CST", "CST"),
        ["hkg"] = new("Asia/Hong_Kong", "HKT", "HKT"),
        ["twn"] = new("Asia/Taipei", "CST", "CST"),
        ["jpn"] = new("Asia/Tokyo", "JST", "JST"),
        ["kor"] = new("Asia/Seoul", "KST", "KST"),
        ["tha"] = new("Asia/Bangkok", "ICT", "ICT"),
        ["sgp"] = new("Asia/Singapore", "SGT", "SGT"),
        ["mys"] = new("Asia/Kuala_Lumpur", "MYT", "MYT"),
        ["phl"] = new("Asia/Manila", "PHT", "PHT"),
        ["idn"] = new("Asia/Jakarta", "WIB", "WIB"),
        ["aus"] = new("Australia/Sydney", "AEST", "AEDT"),
        ["nzl"] = new("Pacific/Auckland", "NZST", "NZDT"),
    };

    /// <summary>The release moment of an episode aired on <paramref name="airDate"/> by a series with the given air time and country.</summary>
    public static ReleaseMoment? MomentUtc(DateOnly? airDate, string? airsTime, string? country)
    {
        if (airDate is not { } date)
            return null;

        var zone = ZoneOf(country);
        if (zone is null)
        {
            // No country we know: the end of the air date everywhere on Earth.
            return new ReleaseMoment(date.AddDays(1).ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc), IsTimeKnown: false, date);
        }

        var (info, labels) = zone.Value;

        if (TryParseTime(airsTime) is not { } time)
        {
            // No air time: the end of the air date in that country.
            var midnight = date.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            return new ReleaseMoment(ToUtc(midnight, info), IsTimeKnown: false, date);
        }

        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        var label = info.IsDaylightSavingTime(local) ? labels.DaylightLabel : labels.StandardLabel;
        return new ReleaseMoment(ToUtc(local, info), IsTimeKnown: true, date, time, label);
    }

    /// <summary>The zone a country's broadcasters schedule by, or null when it is not in the table (or the machine lacks it).</summary>
    private static (TimeZoneInfo Info, Zone Labels)? ZoneOf(string? country)
    {
        if (string.IsNullOrWhiteSpace(country) || !Zones.TryGetValue(country.Trim(), out var zone))
            return null;

        try
        {
            return (TimeZoneInfo.FindSystemTimeZoneById(zone.Id), zone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>TheTVDB writes "20:00"; anything that is not a time is treated as no time.</summary>
    private static TimeOnly? TryParseTime(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && TimeOnly.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : null;

    /// <summary>
    /// Local wall time to UTC. A time skipped by a daylight-saving change (02:30
    /// on the spring-forward night) is moved an hour on, so it still exists.
    /// </summary>
    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(local))
            local = local.AddHours(1);

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
