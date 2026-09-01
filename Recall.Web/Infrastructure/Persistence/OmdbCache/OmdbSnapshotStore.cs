using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recall.Web.Domain.Omdb;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.OmdbCache;

public sealed class OmdbSnapshotStore(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ILogger<OmdbSnapshotStore> logger)
    : IOmdbSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<OmdbSeries?> GetAsync(int tvdbId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var row = await dbContext.CachedSeriesOmdb
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
            logger.LogWarning(ex, "Corrupt OMDb snapshot for series {TvdbId}; ignoring.", tvdbId);
            return null;
        }
    }

    public async Task UpsertAsync(
        int tvdbId, string? imdbId, OmdbSeries? data, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var row = await dbContext.CachedSeriesOmdb
            .FirstOrDefaultAsync(x => x.TvdbId == tvdbId, cancellationToken);

        if (row is null)
        {
            row = new CachedSeriesOmdbEntity { TvdbId = tvdbId };
            dbContext.CachedSeriesOmdb.Add(row);
        }

        row.ImdbId = imdbId;
        row.Name = data?.Title;
        row.Payload = data is null ? null : JsonSerializer.Serialize(data, JsonOptions);
        row.RetrievedUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<int>> GetSeriesNeedingOmdbAsync(
        DateTime staleBeforeUtc, int limit, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Cached series with no OMDb row at all, or whose row is older than the
        // cutoff. Anti-join (NOT EXISTS) — provider-safe and no large IN list.
        return await dbContext.CachedSeriesAggregates
            .AsNoTracking()
            .Where(agg => !dbContext.CachedSeriesOmdb
                .Any(omdb => omdb.TvdbId == agg.TvdbId && omdb.RetrievedUtc >= staleBeforeUtc))
            .Select(agg => agg.TvdbId)
            .Distinct()
            .OrderBy(tvdbId => tvdbId)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }
}
