namespace Recall.Web.Domain.TheTvDb;

/// <summary>
/// The slice of a <see cref="SeriesAggregate"/> that list pages need: enough to
/// draw a card and work out watch progress, and nothing else. It leaves out the
/// series overview, characters, seasons, aliases, remote ids and every episode's
/// overview — most of an aggregate's bulk.
///
/// A summary is never fetched or stored on its own. It is only ever a
/// projection of the aggregate (<see cref="FromAggregate"/>), cached beside it,
/// so the aggregate stays the single source of a series' episode list. Pages
/// that show one series in full (Series/Details) keep using the aggregate.
/// </summary>
public sealed record SeriesSummary
{
    public int TvdbId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? ImageUrl { get; init; }
    public DateOnly? FirstAired { get; init; }

    /// <summary>TheTVDB's status text, e.g. "Ended" or "Continuing".</summary>
    public string? StatusName { get; init; }

    public int? AverageRuntimeMinutes { get; init; }

    public IReadOnlyList<SeriesSummaryEpisode> Episodes { get; init; } = [];

    public static SeriesSummary FromAggregate(SeriesAggregate aggregate) => new()
    {
        TvdbId = aggregate.TvdbId,
        Name = aggregate.Name,
        ImageUrl = aggregate.ImageUrl,
        FirstAired = aggregate.FirstAired,
        StatusName = aggregate.Status?.Name,
        AverageRuntimeMinutes = aggregate.AverageRuntimeMinutes,
        Episodes = aggregate.Episodes
            .Select(e => new SeriesSummaryEpisode
            {
                Id = e.Id,
                SeasonNumber = e.SeasonNumber,
                EpisodeNumber = e.EpisodeNumber,
                Name = e.Name,
                Image = e.Image,
                Aired = e.Aired,
                RuntimeMinutes = e.RuntimeMinutes,
                IsMovie = e.IsMovie,
                FinaleType = e.FinaleType
            })
            .ToList()
    };
}

/// <summary>One episode in a <see cref="SeriesSummary"/> — an <see cref="EpisodeSummary"/> without its overview.</summary>
public sealed record SeriesSummaryEpisode
{
    public int Id { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Image { get; init; }
    public DateOnly? Aired { get; init; }
    public int? RuntimeMinutes { get; init; }
    public bool? IsMovie { get; init; }
    public string? FinaleType { get; init; }
}
