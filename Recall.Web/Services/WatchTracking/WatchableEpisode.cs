namespace Recall.Web.Services.WatchTracking;

/// <summary>
/// A minimal, source-agnostic projection of a TV episode — just enough to reason
/// about watch order and release state, without caring which TheTVDB call produced it
/// (series aggregate, extended series, …).
/// </summary>
public sealed record WatchableEpisode(
    int Id,
    int? SeasonNumber,
    int? EpisodeNumber,
    DateOnly? Aired,
    string Name,
    ReleaseMoment? KnownRelease = null)
{
    /// <summary>
    /// When the episode is released (<see cref="EpisodeRelease"/>): the moment
    /// the projection from the series worked out from its air time and country,
    /// or, for an episode built without them, the no-country fallback (noon UTC
    /// the day after the air date). Null without an air date.
    /// </summary>
    public ReleaseMoment? Release => KnownRelease ?? EpisodeRelease.MomentUtc(Aired, airsTime: null, country: null);

    /// <summary>
    /// True when the episode has been released by <paramref name="nowUtc"/>.
    /// An episode with no air date is never "released": progress leaves it out,
    /// though it may still be marked watched (<see cref="AirDate.MayBeMarked"/>).
    /// </summary>
    public bool IsReleasedBy(DateTime nowUtc) => Release?.IsReleasedBy(nowUtc) == true;

    /// <summary>
    /// A special (TheTVDB's season 0): listed and markable, but never part of
    /// watch progress. An episode without a season number is not a special.
    /// </summary>
    public bool IsSpecial => SeasonNumber == 0;

    /// <summary>"S02E06"-style slate code; missing numbers render as "??".</summary>
    public string SlateCode() => $"S{SeasonNumber?.ToString("D2") ?? "??"}E{EpisodeNumber?.ToString("D2") ?? "??"}";
}
