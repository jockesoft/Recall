using Microsoft.Extensions.Options;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Services.Digest;

/// <summary>One user's digest: what it says and, unless there is nothing to say, the email that says it.</summary>
/// <param name="Email">Null when <see cref="DigestContent.IsEmpty"/>: no email is sent.</param>
/// <param name="OneClickUnsubscribeUrl">The address for the <c>List-Unsubscribe</c> header.</param>
public sealed record ComposedDigest(DigestContent Content, DigestEmail? Email, string OneClickUnsubscribeUrl);

public interface IDigestComposer
{
    /// <summary>
    /// Builds a user's digest as of <paramref name="nowUtc"/>. Reads the user's
    /// library and watches from the database and series data from the caches
    /// only; it never calls TheTVDB or OMDb, and it sends and stores nothing.
    /// </summary>
    /// <param name="aggregates">
    /// Series already read during this run, by id (null for one that is not
    /// cached). Many users track the same series; the digest job passes one
    /// dictionary to every call. Optional.
    /// </param>
    Task<ComposedDigest> ComposeAsync(
        Guid userId,
        string username,
        DateTime nowUtc,
        string baseUrl,
        IDictionary<int, SeriesAggregate?>? aggregates = null,
        CancellationToken cancellationToken = default);
}

public sealed class DigestComposer(
    ITrackedSeriesRepository trackedSeriesRepository,
    IEpisodeWatchRepository episodeWatchRepository,
    ITheTvDbService theTvDbService,
    IDigestUnsubscribeTokens unsubscribeTokens,
    IOptions<LibraryOptions> libraryOptions,
    ILogger<DigestComposer> logger) : IDigestComposer
{
    public async Task<ComposedDigest> ComposeAsync(
        Guid userId,
        string username,
        DateTime nowUtc,
        string baseUrl,
        IDictionary<int, SeriesAggregate?>? aggregates = null,
        CancellationToken cancellationToken = default)
    {
        aggregates ??= new Dictionary<int, SeriesAggregate?>();

        // The repositories share the scoped DbContext, so these stay sequential.
        // A series the user stopped watching is in none of the three sections,
        // so it is not read at all (SeriesLibraryStateRule has the rule).
        var tracked = SeriesLibraryStateRule.Followed(
            await trackedSeriesRepository.GetByUserAsync(userId, cancellationToken));

        var cached = new List<SeriesAggregate>(tracked.Count);
        foreach (var series in tracked)
        {
            if (!aggregates.TryGetValue(series.TvdbId, out var aggregate))
            {
                aggregate = await ReadCachedAsync(series.TvdbId, cancellationToken);
                aggregates[series.TvdbId] = aggregate;
            }

            if (aggregate is not null)
                cached.Add(aggregate);
        }

        var seriesIds = cached.Select(a => a.TvdbId).ToList();
        var watchedIds = seriesIds.Count > 0
            ? await episodeWatchRepository.GetWatchedEpisodeIdsAsync(userId, seriesIds, cancellationToken)
            : new HashSet<int>();
        var lastWatched = await episodeWatchRepository.GetLastWatchedUtcBySeriesAsync(userId, cancellationToken);

        var content = DigestBuilder.Build(
            cached, watchedIds, lastWatched, ContinueWatchingOrder.AddedUtc(tracked), nowUtc, libraryOptions.Value);

        var token = unsubscribeTokens.Create(userId);
        var oneClickUrl = $"{baseUrl}/Digest/OneClick?token={token}";

        if (content.IsEmpty)
            return new ComposedDigest(content, null, oneClickUrl);

        var links = new DigestLinks(baseUrl, $"{baseUrl}/Digest/Unsubscribe?token={token}");
        return new ComposedDigest(content, DigestEmailRenderer.Render(content, username, DateOnly.FromDateTime(nowUtc), links), oneClickUrl);
    }

    /// <summary>A series that cannot be read from the caches is left out of this digest; it must not cost an API call or the whole email.</summary>
    private async Task<SeriesAggregate?> ReadCachedAsync(int seriesId, CancellationToken cancellationToken)
    {
        try
        {
            return await theTvDbService.GetCachedSeriesAggregateAsync(seriesId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Digest: could not read cached series {SeriesId}; leaving it out.", seriesId);
            return null;
        }
    }
}
