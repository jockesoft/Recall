using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recall.Web.Domain.Omdb;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.OmdbCache;

public sealed class MovieOmdbSnapshotStore(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ILogger<MovieOmdbSnapshotStore> logger)
    : IMovieOmdbSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = RecallJsonOptions.Web;

    public async Task<OmdbSeries?> GetAsync(int tvdbId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var row = await dbContext.CachedMoviesOmdb
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TvdbId == tvdbId, cancellationToken);

        if (string.IsNullOrEmpty(row?.Payload))
            return null;

        try
        {
            return JsonSerializer.Deserialize<OmdbSeries>(row.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Corrupt OMDb snapshot for movie {TvdbId}; ignoring.", tvdbId);
            return null;
        }
    }

    public async Task UpsertAsync(
        int tvdbId, string? imdbId, OmdbSeries? data, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var row = await dbContext.CachedMoviesOmdb
            .FirstOrDefaultAsync(x => x.TvdbId == tvdbId, cancellationToken);

        if (row is null)
        {
            row = new CachedMovieOmdbEntity { TvdbId = tvdbId };
            dbContext.CachedMoviesOmdb.Add(row);
        }

        row.ImdbId = imdbId;
        row.Name = data?.Title;
        row.Payload = data is null ? null : JsonSerializer.Serialize(data, JsonOptions);
        row.RetrievedUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<int>> GetMoviesNeedingOmdbAsync(
        DateTime staleBeforeUtc, int limit, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Cached movies with no OMDb row at all, or whose row is older than the
        // cutoff. Anti-join (NOT EXISTS) — provider-safe and no large IN list.
        return await dbContext.CachedMovieAggregates
            .AsNoTracking()
            .Where(agg => !dbContext.CachedMoviesOmdb
                .Any(omdb => omdb.TvdbId == agg.TvdbId && omdb.RetrievedUtc >= staleBeforeUtc))
            .Select(agg => agg.TvdbId)
            .Distinct()
            .OrderBy(tvdbId => tvdbId)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }
}
