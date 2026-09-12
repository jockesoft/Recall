using Microsoft.EntityFrameworkCore;
using Recall.Web.Infrastructure.Persistence;

namespace Recall.Web.Services.Sitemap;

public sealed class SitemapService(IDbContextFactory<AppDbContext> dbContextFactory) : ISitemapService
{
    // Each query opens its own DbContext via the factory, so the three run
    // safely in parallel (same rule as the TVDB/OMDb snapshot stores).
    public async Task<IReadOnlyList<SitemapEntry>> GetCachedSeriesAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await dbContext.CachedSeriesAggregates
            .AsNoTracking()
            .GroupBy(x => x.TvdbId)
            .Select(g => new SitemapEntry(g.Key, g.Max(x => x.RetrievedUtc)))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SitemapEntry>> GetCachedMoviesAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await dbContext.CachedMovieAggregates
            .AsNoTracking()
            .GroupBy(x => x.TvdbId)
            .Select(g => new SitemapEntry(g.Key, g.Max(x => x.RetrievedUtc)))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SitemapEntry>> GetCachedEpisodesAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await dbContext.CachedEpisodesExtended
            .AsNoTracking()
            .Select(x => new SitemapEntry(x.EpisodeTvdbId, x.RetrievedUtc))
            .ToListAsync(cancellationToken);
    }
}
