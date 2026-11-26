using Microsoft.Extensions.Options;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services.Stats;
using Recall.Web.Services.WatchTracking;
using Recall.Web.Extensions;

namespace Recall.Web.Services.Discover;

public interface IDiscoverService
{
    /// <summary>
    /// The user's Discover rows (<see cref="DiscoverBuilder"/>), from the
    /// stored lists, what the user knows, and their Stats genres. Reads the
    /// database and the metadata caches only: a page view never calls TheTVDB.
    /// </summary>
    Task<DiscoverPage> GetPageAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The title as it stands in one of the lists the user's page reads today,
    /// or null when it is in none: what a POST from the page (add, hide) checks
    /// before it acts, so it never acts on an id that was not offered.
    /// </summary>
    Task<DiscoverTitle?> FindAsync(Guid userId, DiscoverKind kind, int tvdbId, CancellationToken cancellationToken = default);
}

public sealed class DiscoverService(
    IDiscoverListRepository listRepository,
    IDiscoverUserRepository userRepository,
    IStatsService statsService,
    IOptions<DiscoverOptions> options,
    TimeProvider timeProvider,
    ILogger<DiscoverService> logger)
    : IDiscoverService
{
    public async Task<DiscoverPage> GetPageAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // One after another: they share the request's DbContext.
        var known = await userRepository.GetKnownTitlesAsync(userId, cancellationToken);
        var genres = await GenresAsync(userId, cancellationToken);
        var today = AirDate.Today(timeProvider);
        var lists = await listRepository.GetAsync(DiscoverBuilder.KeysFor(today, genres, options.Value), cancellationToken);

        return DiscoverBuilder.Build(new DiscoverInput(lists, known, genres), today, options.Value);
    }

    public async Task<DiscoverTitle?> FindAsync(Guid userId, DiscoverKind kind, int tvdbId, CancellationToken cancellationToken = default)
    {
        if (tvdbId <= 0)
            return null;

        var genres = await GenresAsync(userId, cancellationToken);
        var lists = await listRepository.GetAsync(
            DiscoverBuilder.KeysFor(AirDate.Today(timeProvider), genres, options.Value), cancellationToken);

        return lists.Values
            .SelectMany(list => list.Titles)
            .FirstOrDefault(t => t.Kind == kind && t.TvdbId == tvdbId);
    }

    /// <summary>The user's genre rows; none (so "All-time favourites") when their stats cannot be read.</summary>
    private async Task<IReadOnlyList<DiscoverGenre>> GenresAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var stats = await statsService.GetAsync(userId, cancellationToken);
            return DiscoverGenres.ForUser(stats, options.Value.MinGenreTitles);
        }
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
        {
            logger.LogWarning(ex, "Discover: could not read the genres of user {UserId}; showing all-time favourites.", userId);
            return [];
        }
    }
}
