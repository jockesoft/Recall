using System.Globalization;
using Recall.Web.Domain.TheTvDb;

namespace Recall.Web.Services.WatchTracking;

internal static class WatchTrackingMappings
{
    /// <summary>Non-movie episodes from an extended series, projected for watch tracking.</summary>
    public static IEnumerable<WatchableEpisode> ToWatchableEpisodes(this Series series) =>
        series.Episodes
            .Where(e => e is { Id: not null, IsMovie: false })
            .Select(e => new WatchableEpisode(
                e.Id!.Value,
                e.SeasonNumber,
                e.Number,
                ParseDate(e.Aired),
                e.Name ?? string.Empty));

    /// <summary>Non-movie episodes from a series aggregate, projected for watch tracking.</summary>
    public static IEnumerable<WatchableEpisode> ToWatchableEpisodes(this SeriesAggregate aggregate) =>
        aggregate.Episodes
            .Where(e => e.IsMovie != true)
            .Select(e => new WatchableEpisode(
                e.Id,
                e.SeasonNumber,
                e.EpisodeNumber,
                e.Aired,
                e.Name));

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
}
