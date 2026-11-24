using Recall.Web.Services;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Pages.Shared;

/// <summary>
/// The sentence of the toast after a one-tap mark (a Dashboard card or row, a
/// Library Watching card): "Marked Severance S02E06 as watched." Read from the
/// cached series aggregate; a plain sentence when it can't be read.
/// </summary>
public static class OneTapMarkToast
{
    /// <param name="resumedWatching">
    /// The series a mark resumed, when the user had stopped watching it (the
    /// watch service says so): "… and resumed watching Silo."
    /// </param>
    public static async Task<string> DescribeAsync(
        ITheTvDbService theTvDbService,
        ILogger logger,
        string caller,
        int seriesId,
        int episodeId,
        string? resumedWatching,
        CancellationToken cancellationToken)
    {
        var aggregate = await theTvDbService.TryGetSeriesAggregateAsync(seriesId, logger, caller, cancellationToken);
        var episode = aggregate?.ToWatchableEpisodes().FirstOrDefault(e => e.Id == episodeId);
        var clause = MarkLibraryEffect.Clause(null, resumedWatching);

        return aggregate is null || episode is null
            ? $"Marked as watched{clause}."
            : $"Marked {aggregate.Name} {episode.SlateCode()} as watched{clause}.";
    }
}
