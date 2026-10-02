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
    string Name)
{
    /// <summary>True when the episode has an air date on or before <paramref name="date"/>.</summary>
    public bool HasAiredBy(DateOnly date) => Aired is { } aired && aired <= date;

    /// <summary>
    /// A special (TheTVDB's season 0): listed and markable, but never part of
    /// watch progress. An episode without a season number is not a special.
    /// </summary>
    public bool IsSpecial => SeasonNumber == 0;

    /// <summary>"S02E06"-style slate code; missing numbers render as "??".</summary>
    public string SlateCode() => $"S{SeasonNumber?.ToString("D2") ?? "??"}E{EpisodeNumber?.ToString("D2") ?? "??"}";
}
