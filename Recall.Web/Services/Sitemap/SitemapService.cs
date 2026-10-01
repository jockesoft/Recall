using Microsoft.EntityFrameworkCore;
using Recall.Web.Infrastructure.Persistence;

namespace Recall.Web.Services.Sitemap;

public sealed class SitemapService(IDbContextFactory<AppDbContext> dbContextFactory) : ISitemapService
{
    // Each query opens its own DbContext via the factory, so queries can run
    // in parallel (same rule as the TVDB/OMDb snapshot stores).

    public async Task<SitemapContent> GetCachedContentAsync(int maxEntries, CancellationToken cancellationToken = default)
    {
        var room = Math.Max(0, maxEntries);

        // Both asked for the full allowance up front so they can run together;
        // movies are trimmed afterwards to what series left over.
        var seriesTask = GetCachedSeriesAsync(room, cancellationToken);
        var moviesTask = GetCachedMoviesAsync(room, cancellationToken);
        await Task.WhenAll(seriesTask, moviesTask);

        var series = seriesTask.Result;
        room -= series.Count;

        var movies = moviesTask.Result.Count <= room
            ? moviesTask.Result
            : moviesTask.Result.Take(room).ToList();
        room -= movies.Count;

        var episodes = await GetCachedEpisodesAsync(room, cancellationToken);

        return new SitemapContent(series, movies, episodes);
    }

    public async Task<IReadOnlyList<SitemapEntry>> GetCachedSeriesAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
            return [];

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var rows = await dbContext.CachedSeriesAggregates
            .AsNoTracking()
            .GroupBy(x => x.TvdbId)
            .Select(g => new { TvdbId = g.Key, LastModifiedUtc = g.Max(x => x.RetrievedUtc) })
            .OrderByDescending(x => x.LastModifiedUtc)
            .ThenBy(x => x.TvdbId)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return rows.Select(x => new SitemapEntry(x.TvdbId, x.LastModifiedUtc)).ToList();
    }

    public async Task<IReadOnlyList<SitemapEntry>> GetCachedMoviesAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
            return [];

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var rows = await dbContext.CachedMovieAggregates
            .AsNoTracking()
            .GroupBy(x => x.TvdbId)
            .Select(g => new { TvdbId = g.Key, LastModifiedUtc = g.Max(x => x.RetrievedUtc) })
            .OrderByDescending(x => x.LastModifiedUtc)
            .ThenBy(x => x.TvdbId)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return rows.Select(x => new SitemapEntry(x.TvdbId, x.LastModifiedUtc)).ToList();
    }

    public async Task<IReadOnlyList<SitemapEntry>> GetCachedEpisodesAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
            return [];

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var rows = await dbContext.CachedEpisodesExtended
            .AsNoTracking()
            .OrderByDescending(x => x.RetrievedUtc)
            .ThenBy(x => x.EpisodeTvdbId)
            .Take(limit)
            .Select(x => new { x.EpisodeTvdbId, x.RetrievedUtc })
            .ToListAsync(cancellationToken);

        return rows.Select(x => new SitemapEntry(x.EpisodeTvdbId, x.RetrievedUtc)).ToList();
    }
}
