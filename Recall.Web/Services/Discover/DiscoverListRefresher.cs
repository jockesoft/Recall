using Microsoft.Extensions.Options;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Mappings;
using Recall.Web.Services.External.TheTvDb;
using Recall.Web.Services.Stats;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Services.Discover;

/// <summary>What one run of the Discover job did.</summary>
/// <param name="Due">False when no list was old enough to fetch: the run did nothing else.</param>
public sealed record DiscoverRefreshResult(bool Due, int Requests, int Refreshed, int Failed, int Users, int Genres, int Removed)
{
    public static DiscoverRefreshResult NotDue { get; } = new(false, 0, 0, 0, 0, 0, 0);
}

public interface IDiscoverListRefresher
{
    /// <summary>
    /// Fetches the Discover lists that are due: once a day (and at once while a
    /// list has never been fetched), the fixed lists and the genre lists for the
    /// genres in some user's top two. A failed or empty fetch keeps the list as
    /// it was. Never throws for one list's failure.
    /// </summary>
    Task<DiscoverRefreshResult> RunAsync(CancellationToken cancellationToken = default);
}

public sealed class DiscoverListRefresher(
    ITheTvDbApiClient apiClient,
    IDiscoverListRepository listRepository,
    IAppUserRepository userRepository,
    IStatsService statsService,
    IOptions<DiscoverOptions> options,
    TimeProvider timeProvider,
    ILogger<DiscoverListRefresher> logger)
    : IDiscoverListRefresher
{
    /// <summary>A list no page needs any more (last year's, a genre nobody has now) is deleted once it is this old.</summary>
    public static readonly TimeSpan UnusedListLifetime = TimeSpan.FromDays(30);

    private DiscoverOptions Options => options.Value;

    public async Task<DiscoverRefreshResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var today = AirDate.Today(timeProvider);

        var fetched = await listRepository.GetFetchedUtcAsync(cancellationToken);
        var dueBefore = now.AddHours(-Math.Max(1, Options.RefreshAfterHours));
        bool IsDue(DiscoverListKey key) => !fetched.TryGetValue(key.Key, out var at) || at <= dueBefore;

        // The fixed lists decide whether this is the day's run: the genre lists
        // (and the per-user work of finding them) come along with them.
        var fixedKeys = DiscoverListKey.Fixed(today, Options);
        if (!fixedKeys.Any(IsDue))
            return DiscoverRefreshResult.NotDue;

        using var meter = TheTvDbRequestMeter.Start();

        var (users, genreIds) = await GenresInUseAsync(cancellationToken);
        var keys = fixedKeys.Concat(genreIds.SelectMany(DiscoverListKey.ForGenre)).ToList();

        int refreshed = 0, failed = 0;
        foreach (var key in keys.Where(IsDue))
        {
            if (await RefreshAsync(key, today, now, cancellationToken))
                refreshed++;
            else
                failed++;
        }

        var removed = await RemoveUnusedAsync(keys, now, cancellationToken);

        var result = new DiscoverRefreshResult(true, meter.Count, refreshed, failed, users, genreIds.Count, removed);
        logger.LogInformation(
            "DiscoverListsTimer: {Requests} TheTVDB request(s) this run: {Refreshed} list(s) refreshed, {Failed} failed, " +
            "{Genres} genre(s) from {Users} user(s), {Removed} unused list(s) removed.",
            result.Requests, result.Refreshed, result.Failed, result.Genres, result.Users, result.Removed);

        return result;
    }

    /// <summary>The genres in some user's top two (<see cref="DiscoverGenres.ForUser"/>), from Stats, which reads the caches only.</summary>
    private async Task<(int Users, IReadOnlyList<int> GenreIds)> GenresInUseAsync(CancellationToken cancellationToken)
    {
        var genreIds = new SortedSet<int>();
        IReadOnlyList<Guid> userIds;

        try
        {
            userIds = await userRepository.GetIdsWithWatchesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without users the fixed lists are still worth fetching.
            logger.LogWarning(ex, "DiscoverListsTimer: could not list the users; fetching the fixed lists only.");
            return (0, []);
        }

        foreach (var userId in userIds)
        {
            try
            {
                var stats = await statsService.GetAsync(userId, cancellationToken);
                foreach (var genre in DiscoverGenres.ForUser(stats, Options.MinGenreTitles))
                    genreIds.Add(genre.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "DiscoverListsTimer: could not read the genres of user {UserId}.", userId);
            }
        }

        return (userIds.Count, genreIds.ToList());
    }

    /// <summary>One list: fetched, filtered and stored; on any failure, or nothing usable, the stored list stays.</summary>
    private async Task<bool> RefreshAsync(DiscoverListKey key, DateOnly today, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            var attemptTimeout = TimeSpan.FromSeconds(Math.Max(1, Options.FilterAttemptTimeoutSeconds));
            var records = key.Kind == DiscoverKind.Movie
                ? await apiClient.FilterMoviesAsync(key.Filter, attemptTimeout, cancellationToken)
                : await apiClient.FilterSeriesAsync(key.Filter, attemptTimeout, cancellationToken);

            var titles = records
                .Select(record => record.ToDiscoverTitle(key.Kind))
                .OfType<DiscoverTitle>()
                .Where(title => DiscoverQuality.Passes(title, today, Options.MinScore))
                .DistinctBy(title => title.TvdbId)
                .Take(key.Keep)
                .ToList();

            if (titles.Count == 0)
            {
                logger.LogWarning(
                    "DiscoverListsTimer: {Key} came back with nothing usable ({Records} record(s)); the stored list is kept.",
                    key.Key, records.Count);
                return false;
            }

            await listRepository.SaveAsync(key.Key, titles, now, cancellationToken);
            return true;
        }
        // A timeout is a cancellation too (TaskCanceledException), but not the
        // job's: only the job's own cancellation ends the run.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "DiscoverListsTimer: could not refresh {Key}; the stored list is kept.", key.Key);
            return false;
        }
    }

    private async Task<int> RemoveUnusedAsync(IReadOnlyList<DiscoverListKey> keys, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            return await listRepository.DeleteUnusedAsync(keys.Select(k => k.Key).ToList(), now - UnusedListLifetime, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "DiscoverListsTimer: could not remove unused lists.");
            return 0;
        }
    }
}
