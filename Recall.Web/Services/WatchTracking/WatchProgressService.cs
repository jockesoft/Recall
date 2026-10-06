using System.Globalization;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Mappings;

namespace Recall.Web.Services.WatchTracking;

public sealed class WatchProgressService(
    ITheTvDbService theTvDbService,
    IEpisodeWatchRepository episodeWatchRepository,
    ITrackedSeriesRepository trackedSeriesRepository,
    IRatingRepository ratingRepository,
    TimeProvider timeProvider,
    ILogger<WatchProgressService> logger)
    : IWatchProgressService
{
    private DateTime Now => AirDate.Now(timeProvider);

    public SeriesWatchProgress BuildProgress(
        int seriesTvdbId,
        IEnumerable<WatchableEpisode> episodes,
        IReadOnlySet<int> watchedEpisodeIds)
        => WatchProgressCalculator.Build(seriesTvdbId, episodes, watchedEpisodeIds, Now);

    public async Task<SeriesWatchProgress> GetSeriesProgressAsync(
        Guid userId,
        int seriesTvdbId,
        CancellationToken cancellationToken = default)
    {
        var (episodes, watched) = await LoadEpisodesAndWatchedAsync(userId, seriesTvdbId, cancellationToken);

        return WatchProgressCalculator.Build(seriesTvdbId, episodes, watched, Now);
    }

    /// <summary>
    /// Loads a series' ordered episodes (TheTVDB, via the layered cache) and the
    /// user's watched ids (the DB) concurrently — the two calls are independent
    /// and use separate DbContext instances, so there's no benefit to awaiting
    /// them one after another.
    /// </summary>
    private async Task<(IReadOnlyList<WatchableEpisode> Ordered, IReadOnlySet<int> Watched)> LoadEpisodesAndWatchedAsync(
        Guid userId,
        int seriesTvdbId,
        CancellationToken cancellationToken)
    {
        var orderedTask = GetOrderedEpisodesAsync(seriesTvdbId, cancellationToken);
        var watchedTask = episodeWatchRepository.GetWatchedEpisodeIdsAsync(userId, seriesTvdbId, cancellationToken);

        await Task.WhenAll(orderedTask, watchedTask);

        return (orderedTask.Result, watchedTask.Result);
    }

    public async Task<IReadOnlyList<WatchableEpisode>> GetOrderedEpisodesAsync(
        int seriesTvdbId,
        CancellationToken cancellationToken = default)
    {
        // The aggregate, not GetSeriesByIdExtendedAsync: it's what Series/Details,
        // the dashboard and the library render and count from, and the only one
        // of the two the refresh job keeps current — so "mark watched through"
        // can't disagree with the page about which episodes exist.
        var aggregate = await theTvDbService.GetSeriesAggregateByIdAsync(seriesTvdbId, cancellationToken);

        return aggregate is null
            ? []
            : WatchProgressCalculator.Order(aggregate.ToWatchableEpisodes());
    }

    public async Task<int> GetPriorUnwatchedCountAsync(
        Guid userId,
        int seriesTvdbId,
        int episodeTvdbId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (ordered, watched) = await LoadEpisodesAndWatchedAsync(userId, seriesTvdbId, cancellationToken);

            // Same set MarkWatchedThroughAsync would write, so the "also mark N
            // earlier episodes?" prompt never promises more than it does.
            return WatchProgressCalculator.CountPriorUnwatched(WithoutUnaired(ordered), watched, episodeTvdbId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nice-to-have prompt for a "catch up?" modal — never worth a broken page.
            logger.LogWarning(ex, "Could not compute prior-unwatched count for series {SeriesId}, episode {EpisodeId}.", seriesTvdbId, episodeTvdbId);
            return 0;
        }
    }

    public async Task<MarkWatchedThroughResult> MarkWatchedThroughAsync(
        Guid userId,
        int seriesTvdbId,
        int episodeTvdbId,
        CancellationToken cancellationToken = default)
    {
        var ordered = await GetOrderedEpisodesAsync(seriesTvdbId, cancellationToken);
        var target = ordered.FirstOrDefault(e => e.Id == episodeTvdbId);

        if (target is null)
        {
            // The episode isn't part of this series' known episode list (a stale
            // cache, a renumbered/removed episode, or route/form values that
            // don't actually match) — don't record a watch against the wrong
            // series just because IdsThrough would otherwise fall back to it.
            return new MarkWatchedThroughResult(EpisodeFound: false, MarkedCount: 0);
        }

        if (!AirDate.MayBeMarked(target.Aired, Now))
            return new MarkWatchedThroughResult(EpisodeFound: true, MarkedCount: 0, HasAired: false);

        // "Everything earlier" can include an episode that hasn't aired yet (a
        // listed but unaired special, a gap in the schedule) — leave those out.
        var idsToMark = WatchProgressCalculator.IdsThrough(WithoutUnaired(ordered), episodeTvdbId);
        var before = await BeforeMarkAsync(userId, seriesTvdbId, cancellationToken);

        // The episode that was clicked is a Single watch; the earlier ones
        // marked along with it are Bulk (their date is today's catch-up).
        var batch = await episodeWatchRepository.MarkWatchedRangeAsync(
            userId, seriesTvdbId, idsToMark, WatchSource.Bulk, clickedEpisodeTvdbId: episodeTvdbId, cancellationToken);

        var caughtUp = await CaughtUpByAsync(before, idsToMark, cancellationToken);
        var library = await EnsureWatchingAsync(userId, seriesTvdbId, cancellationToken);

        return new MarkWatchedThroughResult(
            EpisodeFound: true, idsToMark.Count, Batch: batch,
            CaughtUp: caughtUp, AddedToLibrary: library.AddedToLibrary, ResumedWatching: library.ResumedWatching);
    }

    public async Task<SeasonWatchResult> MarkSeasonWatchedAsync(
        Guid userId,
        int seriesTvdbId,
        int seasonNumber,
        CancellationToken cancellationToken = default)
    {
        var season = await GetSeasonEpisodesAsync(seriesTvdbId, seasonNumber, cancellationToken);
        if (season.Count == 0)
            return new SeasonWatchResult(SeasonFound: false, WatchedBatch.Empty);

        var now = Now;
        var idsToMark = season
            .Where(e => AirDate.MayBeMarked(e.Aired, now))
            .Select(e => e.Id)
            .ToList();

        var before = await BeforeMarkAsync(userId, seriesTvdbId, cancellationToken);

        // All Bulk, even a season with one episode left: nobody pointed at an episode.
        var batch = await episodeWatchRepository.MarkWatchedRangeAsync(
            userId, seriesTvdbId, idsToMark, WatchSource.Bulk, cancellationToken: cancellationToken);

        var caughtUp = await CaughtUpByAsync(before, idsToMark, cancellationToken);

        // A season with nothing left to mark wrote nothing, so it neither adds nor resumes.
        var library = batch.InsertedCount > 0
            ? await EnsureWatchingAsync(userId, seriesTvdbId, cancellationToken)
            : MarkLibraryEffect.None;

        return new SeasonWatchResult(
            SeasonFound: true, batch, caughtUp, library.AddedToLibrary, library.ResumedWatching);
    }

    public async Task<int> MarkSeasonUnwatchedAsync(
        Guid userId,
        int seriesTvdbId,
        int seasonNumber,
        CancellationToken cancellationToken = default)
    {
        var season = await GetSeasonEpisodesAsync(seriesTvdbId, seasonNumber, cancellationToken);

        return await episodeWatchRepository.MarkUnwatchedRangeAsync(
            userId, season.Select(e => e.Id).ToList(), cancellationToken);
    }

    public Task<int> UndoWatchedBatchAsync(
        Guid userId,
        int seriesTvdbId,
        DateTime batchWatchedUtc,
        CancellationToken cancellationToken = default)
        => episodeWatchRepository.UndoWatchedBatchAsync(userId, seriesTvdbId, batchWatchedUtc, cancellationToken);

    /// <summary>
    /// Every aggregate entry in the season — including movie-flagged ones, since
    /// the season list on Series/Details shows (and lets you tick) those too.
    /// </summary>
    private async Task<IReadOnlyList<Domain.TheTvDb.EpisodeSummary>> GetSeasonEpisodesAsync(
        int seriesTvdbId,
        int seasonNumber,
        CancellationToken cancellationToken)
    {
        var aggregate = await theTvDbService.GetSeriesAggregateByIdAsync(seriesTvdbId, cancellationToken);

        return aggregate is null
            ? []
            : aggregate.Episodes.Where(e => e.SeasonNumber == seasonNumber).ToList();
    }

    public async Task<EpisodeWatchResult> MarkEpisodeWatchedAsync(
        Guid userId,
        int seriesTvdbId,
        int episodeTvdbId,
        CancellationToken cancellationToken = default)
    {
        if (await RefusalAsync(seriesTvdbId, episodeTvdbId, cancellationToken) is { } refusal)
            return refusal;

        var before = await BeforeMarkAsync(userId, seriesTvdbId, cancellationToken);
        await episodeWatchRepository.MarkWatchedAsync(userId, seriesTvdbId, episodeTvdbId, cancellationToken);

        var caughtUp = await CaughtUpByAsync(before, [episodeTvdbId], cancellationToken);
        var library = await EnsureWatchingAsync(userId, seriesTvdbId, cancellationToken);

        return new EpisodeWatchResult(
            EpisodeWatchOutcome.MarkedWatched, caughtUp, library.AddedToLibrary, library.ResumedWatching);
    }

    public async Task<UndoableEpisodeWatch> MarkEpisodeWatchedUndoablyAsync(
        Guid userId,
        int seriesTvdbId,
        int episodeTvdbId,
        CancellationToken cancellationToken = default)
    {
        if (await RefusalAsync(seriesTvdbId, episodeTvdbId, cancellationToken) is { } refusal)
            return new UndoableEpisodeWatch(refusal);

        var before = await BeforeMarkAsync(userId, seriesTvdbId, cancellationToken);

        // A range of one: it gets the batch timestamp the undo looks for.
        var batch = await episodeWatchRepository.MarkWatchedRangeAsync(
            userId, seriesTvdbId, [episodeTvdbId], WatchSource.Single, cancellationToken: cancellationToken);
        var caughtUp = await CaughtUpByAsync(before, [episodeTvdbId], cancellationToken);
        var library = await EnsureWatchingAsync(userId, seriesTvdbId, cancellationToken);

        return new UndoableEpisodeWatch(
            EpisodeWatchOutcome.MarkedWatched, batch, caughtUp, library.AddedToLibrary, library.ResumedWatching);
    }

    // ---- the library --------------------------------------------------------------

    /// <summary>
    /// After a mark, makes the series one the user is watching: puts it in the
    /// library if it is not there, and resumes it if they had stopped watching
    /// it. Says which of the two this call did, if either (a concurrent request
    /// that got there first says so itself). Best-effort: the watch is recorded
    /// either way, and the next mark tries again. It only ever adds and
    /// resumes: nothing here removes a series from the library or stops it.
    /// </summary>
    private async Task<MarkLibraryEffect> EnsureWatchingAsync(Guid userId, int seriesTvdbId, CancellationToken cancellationToken)
    {
        try
        {
            if (await trackedSeriesRepository.ExistsAsync(userId, seriesTvdbId, cancellationToken))
            {
                return new MarkLibraryEffect(
                    ResumedWatching: await trackedSeriesRepository.ResumeAsync(userId, seriesTvdbId, cancellationToken));
            }

            var details = await theTvDbService.GetSeriesByIdAsync(seriesTvdbId, cancellationToken);
            if (details is null)
                return MarkLibraryEffect.None;

            return await trackedSeriesRepository.AddAsync(TrackedSeriesMappings.FromTvDbDetails(userId, details), cancellationToken)
                ? new MarkLibraryEffect(AddedToLibrary: details.Name)
                : MarkLibraryEffect.None;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not add series {SeriesId} to the library, or resume it, after a mark.", seriesTvdbId);
            return MarkLibraryEffect.None;
        }
    }

    // ---- stopped watching ----------------------------------------------------------

    public async Task<StopWatchingResult> StopWatchingAsync(
        Guid userId,
        int seriesTvdbId,
        CancellationToken cancellationToken = default)
    {
        var tracked = await trackedSeriesRepository.GetByUserAndTvdbIdAsync(userId, seriesTvdbId, cancellationToken);
        if (tracked is null)
            return new StopWatchingResult(StopWatchingOutcome.NotInLibrary);

        if (SeriesLibraryStateRule.IsStopped(tracked))
            return new StopWatchingResult(StopWatchingOutcome.AlreadyStopped, tracked.Name);

        if (await IsFinishedAsync(userId, seriesTvdbId, cancellationToken))
            return new StopWatchingResult(StopWatchingOutcome.Finished, tracked.Name);

        // False here means a concurrent request stopped it in between.
        return await trackedSeriesRepository.StopAsync(userId, seriesTvdbId, Now, cancellationToken)
            ? new StopWatchingResult(StopWatchingOutcome.Stopped, tracked.Name)
            : new StopWatchingResult(StopWatchingOutcome.AlreadyStopped, tracked.Name);
    }

    public Task<string?> ResumeWatchingAsync(
        Guid userId,
        int seriesTvdbId,
        CancellationToken cancellationToken = default)
        => trackedSeriesRepository.ResumeAsync(userId, seriesTvdbId, cancellationToken);

    /// <summary>
    /// Whether the series is finished by the Library's rule, which is when
    /// "Stop watching" is not offered. A series whose state cannot be read is
    /// not called finished: the user asked to stop, and stopping loses nothing.
    /// </summary>
    private async Task<bool> IsFinishedAsync(Guid userId, int seriesTvdbId, CancellationToken cancellationToken)
    {
        try
        {
            var series = await theTvDbService.GetSeriesAggregateByIdAsync(seriesTvdbId, cancellationToken);
            if (series is null)
                return false;

            var watched = await episodeWatchRepository.GetWatchedEpisodeIdsAsync(userId, seriesTvdbId, cancellationToken);
            var progress = WatchProgressCalculator.Build(seriesTvdbId, series.ToWatchableEpisodes(), watched, Now);

            return !SeriesLibraryStateRule.CanStop(SeriesLibraryStateRule.Of(series, progress));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the state of series {SeriesId} before stopping it.", seriesTvdbId);
            return false;
        }
    }

    // ---- "You're up to date" -------------------------------------------------------

    /// <summary>Where the user stood in a series just before a mark: the series, and what they had watched of it.</summary>
    private sealed record BeforeMark(Guid UserId, Domain.TheTvDb.SeriesAggregate Series, IReadOnlySet<int> WatchedIds);

    /// <summary>
    /// The state before a mark, kept only when it matters: the series has aired
    /// regular episodes the user has not watched. Null when it is already up to
    /// date (or nothing has aired), so a mark there can never "catch up". The
    /// toast is a nicety: if this fails the mark still goes ahead.
    /// </summary>
    private async Task<BeforeMark?> BeforeMarkAsync(Guid userId, int seriesTvdbId, CancellationToken cancellationToken)
    {
        try
        {
            var series = await theTvDbService.GetSeriesAggregateByIdAsync(seriesTvdbId, cancellationToken);
            if (series is null)
                return null;

            var watched = await episodeWatchRepository.GetWatchedEpisodeIdsAsync(userId, seriesTvdbId, cancellationToken);
            var progress = WatchProgressCalculator.Build(seriesTvdbId, series.ToWatchableEpisodes(), watched, Now);

            return progress.IsUpToDate ? null : new BeforeMark(userId, series, watched);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the state of series {SeriesId} before a mark; no caught-up message.", seriesTvdbId);
            return null;
        }
    }

    /// <summary>
    /// Whether marking <paramref name="markedIds"/> took the series from having
    /// unwatched aired regular episodes to having none, judged by the rule the
    /// Library's sections use (<see cref="SeriesLibraryStateRule"/>). Specials
    /// never count either way, so marking one changes nothing here; marking
    /// the last regular episode does, in whatever order the others were marked.
    /// </summary>
    private async Task<SeriesCaughtUp?> CaughtUpByAsync(
        BeforeMark? before, IReadOnlyCollection<int> markedIds, CancellationToken cancellationToken)
    {
        if (before is null || markedIds.Count == 0)
            return null;

        try
        {
            var series = before.Series;
            var now = Now;

            var watchedAfter = new HashSet<int>(before.WatchedIds);
            watchedAfter.UnionWith(markedIds);

            var after = WatchProgressCalculator.Build(series.TvdbId, series.ToWatchableEpisodes(), watchedAfter, now);
            var state = SeriesLibraryStateRule.Of(series, after);

            if (state == SeriesLibraryState.Watching)
                return null;

            if (state == SeriesLibraryState.Finished)
            {
                var rating = await ratingRepository.GetRatingAsync(
                    before.UserId, RatingTargetType.Series, series.TvdbId, cancellationToken);

                return new SeriesCaughtUp(series.TvdbId, series.Name, Finished: true, UserHasRated: rating is not null);
            }

            // Up to date with a series that continues: when is the next one?
            var next = after.OrderedEpisodes
                .Where(e => !e.IsSpecial && e.Release is { } release && !release.IsReleasedBy(now))
                .OrderBy(e => e.Release!.Utc)
                .FirstOrDefault();

            return new SeriesCaughtUp(series.TvdbId, series.Name, Finished: false, NextEpisode: next);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not tell whether series {SeriesId} is now caught up; no caught-up message.", before.Series.TvdbId);
            return null;
        }
    }

    /// <summary>
    /// Why a watch must not be recorded, or null when it may: the episode has
    /// to belong to the series it was submitted with, and to have aired.
    /// </summary>
    private async Task<EpisodeWatchOutcome?> RefusalAsync(
        int seriesTvdbId,
        int episodeTvdbId,
        CancellationToken cancellationToken)
    {
        var lookup = await FindEpisodeInSeriesAsync(seriesTvdbId, episodeTvdbId, cancellationToken);

        if (!lookup.Found)
        {
            logger.LogWarning(
                "Watch rejected: episode {EpisodeId} is not part of series {SeriesId}.", episodeTvdbId, seriesTvdbId);
            return EpisodeWatchOutcome.EpisodeNotInSeries;
        }

        return AirDate.MayBeMarked(lookup.Aired, Now) ? null : EpisodeWatchOutcome.NotAired;
    }

    public async Task<EpisodeWatchResult> ToggleEpisodeWatchedAsync(
        Guid userId,
        int seriesTvdbId,
        int episodeTvdbId,
        CancellationToken cancellationToken = default)
    {
        if (await episodeWatchRepository.IsWatchedAsync(userId, episodeTvdbId, cancellationToken))
        {
            await episodeWatchRepository.MarkUnwatchedAsync(userId, episodeTvdbId, cancellationToken);
            return EpisodeWatchOutcome.MarkedUnwatched;
        }

        return await MarkEpisodeWatchedAsync(userId, seriesTvdbId, episodeTvdbId, cancellationToken);
    }

    /// <summary>
    /// Confirms an episode belongs to a series. The aggregate is checked first
    /// (already cached for any page that offers a watch button, and it includes
    /// the movie-flagged entries the watchable list drops). An episode missing
    /// from it — a special the aggregate omits, or one added since its last
    /// refresh — gets a second chance against its own cached record, which
    /// names its parent series.
    /// </summary>
    private async Task<(bool Found, DateOnly? Aired)> FindEpisodeInSeriesAsync(
        int seriesTvdbId,
        int episodeTvdbId,
        CancellationToken cancellationToken)
    {
        var aggregate = await theTvDbService.GetSeriesAggregateByIdAsync(seriesTvdbId, cancellationToken);
        if (aggregate?.Episodes.FirstOrDefault(e => e.Id == episodeTvdbId) is { } summary)
            return (true, summary.Aired);

        var episode = await theTvDbService.GetEpisodeDetailsAsync(episodeTvdbId, cancellationToken);
        if (episode?.SeriesId != seriesTvdbId)
            return (false, null);

        var aired = DateOnly.TryParse(episode.Aired, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : (DateOnly?)null;

        return (true, aired);
    }

    private IReadOnlyList<WatchableEpisode> WithoutUnaired(IReadOnlyList<WatchableEpisode> ordered)
    {
        var now = Now;
        return ordered.Where(e => AirDate.MayBeMarked(e.Aired, now)).ToList();
    }
}
