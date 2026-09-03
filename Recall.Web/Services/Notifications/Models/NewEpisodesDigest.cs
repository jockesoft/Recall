namespace Recall.Web.Services.Notifications.Models;

/// <summary>
/// One newly aired episode the sweep is considering for a user.
/// </summary>
/// <param name="EpisodeTvdbId">Episode TVDB id — deep-link target and ledger key.</param>
/// <param name="SeasonNumber">Season number, when known.</param>
/// <param name="EpisodeNumber">Episode number, when known.</param>
/// <param name="EpisodeName">Episode title, when known and not a "TBA" placeholder.</param>
public sealed record NewEpisodeItem(
    int EpisodeTvdbId,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? EpisodeName)
{
    /// <summary>"S02E05", or <c>null</c> when either number is missing.</summary>
    public string? SlateCode =>
        SeasonNumber is { } s && EpisodeNumber is { } e ? $"S{s:D2}E{e:D2}" : null;
}

/// <summary>
/// Everything <see cref="INotificationService.NotifyNewEpisodesAsync"/> needs to
/// raise a single "new episode(s) of X" notification for one user — the series,
/// plus every recently aired episode that user hasn't watched. The service
/// drops any the user has already been told about and collapses the rest into
/// one alert.
/// </summary>
/// <param name="SeriesTvdbId">Parent series TVDB id.</param>
/// <param name="SeriesName">Series name, for the headline.</param>
/// <param name="Episodes">Candidate episodes (any order); may be empty.</param>
public sealed record NewEpisodesDigest(
    int SeriesTvdbId,
    string SeriesName,
    IReadOnlyList<NewEpisodeItem> Episodes);
