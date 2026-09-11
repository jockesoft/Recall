using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recall.Web.Domain.Omdb;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.OmdbCache;

public sealed class EpisodeOmdbSnapshotStore(
    AppDbContext dbContext,
    ILogger<EpisodeOmdbSnapshotStore> logger)
    : IEpisodeOmdbSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = RecallJsonOptions.Web;

    public async Task<OmdbSeries?> GetAsync(int episodeTvdbId, CancellationToken cancellationToken = default)
    {
        var row = await dbContext.CachedEpisodesOmdb
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TvdbId == episodeTvdbId, cancellationToken);

        if (string.IsNullOrEmpty(row?.Payload))
            return null;

        try
        {
            return JsonSerializer.Deserialize<OmdbSeries>(row.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Corrupt OMDb snapshot for episode {TvdbId}; ignoring.", episodeTvdbId);
            return null;
        }
    }

    public Task<DateTime?> GetRetrievedUtcAsync(int episodeTvdbId, CancellationToken cancellationToken = default)
    {
        return dbContext.CachedEpisodesOmdb
            .AsNoTracking()
            .Where(x => x.TvdbId == episodeTvdbId)
            .Select(x => (DateTime?)x.RetrievedUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task UpsertAsync(
        int episodeTvdbId, string? imdbId, OmdbSeries? data, CancellationToken cancellationToken = default)
    {
        var row = await dbContext.CachedEpisodesOmdb
            .FirstOrDefaultAsync(x => x.TvdbId == episodeTvdbId, cancellationToken);

        if (row is null)
        {
            row = new CachedEpisodeOmdbEntity { TvdbId = episodeTvdbId };
            dbContext.CachedEpisodesOmdb.Add(row);
        }

        row.ImdbId = imdbId;
        row.Name = data?.Title;
        row.Payload = data is null ? null : JsonSerializer.Serialize(data, JsonOptions);
        row.RetrievedUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
