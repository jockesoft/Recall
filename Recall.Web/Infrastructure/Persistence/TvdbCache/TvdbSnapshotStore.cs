using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Mappings;

namespace Recall.Web.Infrastructure.Persistence.TvdbCache;

public sealed class TvdbSnapshotStore(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ILogger<TvdbSnapshotStore> logger)
    : ITvdbSnapshotStore
{
    // Same instance Redis/DistributedCacheJson uses — SeriesAggregate already
    // round-trips through this, so the JSON shapes stay in lockstep.
    private static readonly JsonSerializerOptions JsonOptions = RecallJsonOptions.Web;

    // Each operation takes its own context. Callers fan this store out in
    // parallel within a single request, so a shared (scoped) context would
    // throw "a second operation was started on this context instance".

    public async Task<SeriesAggregate?> GetSeriesAggregateAsync(
        int tvdbId, string language, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var row = await dbContext.CachedSeriesAggregates
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TvdbId == tvdbId && x.Language == language, cancellationToken);

        return Deserialize<SeriesAggregate>(row?.Payload, tvdbId);
    }

    public async Task SaveSeriesAggregateAsync(
        SeriesAggregate aggregate, string language, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var exists = await dbContext.CachedSeriesAggregates
            .AsNoTracking()
            .AnyAsync(x => x.TvdbId == aggregate.TvdbId && x.Language == language, cancellationToken);
        if (exists)
            return;

        var entity = new CachedSeriesAggregateEntity
        {
            TvdbId = aggregate.TvdbId,
            Language = language,
            Name = aggregate.Name,
            StatusName = aggregate.Status?.Name,
            KeepUpdated = aggregate.Status?.KeepUpdated,
            Payload = JsonSerializer.Serialize(aggregate, JsonOptions),
            MappingVersion = SeriesDataDtoMappings.AggregateVersion,
            RetrievedUtc = DateTime.UtcNow
        };
        (entity.AiredEpisodeCount, entity.AiredStillCount) = StillCoverage(aggregate);

        await InsertAsync(dbContext, entity, aggregate.TvdbId, cancellationToken);
    }

    public async Task<MovieAggregate?> GetMovieAggregateAsync(
        int tvdbId, string language, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var row = await dbContext.CachedMovieAggregates
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TvdbId == tvdbId && x.Language == language, cancellationToken);

        return Deserialize<MovieAggregate>(row?.Payload, tvdbId);
    }

    public async Task SaveMovieAggregateAsync(
        MovieAggregate aggregate, string language, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var exists = await dbContext.CachedMovieAggregates
            .AsNoTracking()
            .AnyAsync(x => x.TvdbId == aggregate.TvdbId && x.Language == language, cancellationToken);
        if (exists)
            return;

        var entity = new CachedMovieAggregateEntity
        {
            TvdbId = aggregate.TvdbId,
            Language = language,
            Name = aggregate.Name,
            StatusName = aggregate.Status?.Name,
            KeepUpdated = aggregate.Status?.KeepUpdated,
            Payload = JsonSerializer.Serialize(aggregate, JsonOptions),
            RetrievedUtc = DateTime.UtcNow
        };

        await InsertAsync(dbContext, entity, aggregate.TvdbId, cancellationToken);
    }

    public async Task<IReadOnlyList<CachedAggregateKey>> GetMovieAggregatesNeedingRefreshAsync(
        DateTime staleBeforeUtc, DateTime settledStaleBeforeUtc, int limit, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await dbContext.CachedMovieAggregates
            .AsNoTracking()
            .Where(x => (x.KeepUpdated == true && x.RetrievedUtc < staleBeforeUtc)
                        || (x.KeepUpdated != true && x.RetrievedUtc < settledStaleBeforeUtc))
            // A movie a user actually has on their pages outranks one that was
            // only ever opened once (or by a crawler).
            .OrderByDescending(x =>
                dbContext.TrackedMovies.Any(t => t.TvdbId == x.TvdbId)
                || dbContext.UserMovieWatches.Any(w => w.MovieTvdbId == x.TvdbId)
                || dbContext.UserLikes.Any(l => l.TargetType == LikeTargetType.Movie && l.TargetTvdbId == x.TvdbId))
            .ThenBy(x => x.RetrievedUtc)
            .Take(limit)
            .Select(x => new CachedAggregateKey(x.TvdbId, x.Language))
            .ToListAsync(cancellationToken);
    }

    public async Task UpsertMovieAggregateAsync(
        MovieAggregate aggregate, string language, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var row = await dbContext.CachedMovieAggregates
            .FirstOrDefaultAsync(x => x.TvdbId == aggregate.TvdbId && x.Language == language, cancellationToken);

        if (row is null)
        {
            row = new CachedMovieAggregateEntity { TvdbId = aggregate.TvdbId, Language = language };
            dbContext.CachedMovieAggregates.Add(row);
        }

        row.Name = aggregate.Name;
        row.StatusName = aggregate.Status?.Name;
        row.KeepUpdated = aggregate.Status?.KeepUpdated;
        row.Payload = JsonSerializer.Serialize(aggregate, JsonOptions);
        row.RetrievedUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CachedAggregateKey>> GetAggregatesNeedingRefreshAsync(
        DateTime staleBeforeUtc, DateTime settledStaleBeforeUtc, int limit, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        const int currentVersion = SeriesDataDtoMappings.AggregateVersion;

        return await dbContext.CachedSeriesAggregates
            .AsNoTracking()
            .Where(x => x.MappingVersion < currentVersion
                        || (x.KeepUpdated == true && x.RetrievedUtc < staleBeforeUtc)
                        || (x.KeepUpdated != true && x.RetrievedUtc < settledStaleBeforeUtc))
            // Rows written by an older mapping (today: without TheTVDB genres)
            // come first, whatever their age, so a new field reaches the cache
            // in hours. A refresh brings a row to the current version, so this
            // group empties and the order below is all that is left.
            .OrderByDescending(x => x.MappingVersion < currentVersion)
            // A series in someone's library outranks one that was only ever
            // opened once (or by a crawler), so the per-run cap is spent on
            // data users actually see first.
            .ThenByDescending(x => dbContext.TrackedSeries.Any(t => t.TvdbId == x.TvdbId))
            .ThenBy(x => x.RetrievedUtc)
            .Take(limit)
            .Select(x => new CachedAggregateKey(x.TvdbId, x.Language))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// How many regular episodes of the series have aired as of now (UTC), and
    /// how many of those have a still: the two numbers the still recheck judges
    /// "this series rarely has stills" by. Specials and movie-flagged entries
    /// are left out, like everywhere progress is counted.
    /// </summary>
    internal static (int Aired, int WithStill) StillCoverage(SeriesAggregate aggregate)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var aired = aggregate.Episodes
            .Where(e => e.SeasonNumber != 0 && e.IsMovie != true && e.Aired is { } date && date <= today)
            .ToList();

        return (aired.Count, aired.Count(e => !string.IsNullOrWhiteSpace(e.Image)));
    }

    public async Task<IReadOnlyList<int>> GetEpisodesNeedingRefreshAsync(
        DateTime staleBeforeUtc,
        DateTime tbaStaleBeforeUtc,
        StillRecheck stillRecheck,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Plain values for the query: see StillRecheck for the schedule.
        var today = stillRecheck.Today;
        var dailyFrom = stillRecheck.DailyFrom;
        var weeklyFrom = stillRecheck.WeeklyFrom;
        var dailyBeforeUtc = stillRecheck.DailyBeforeUtc;
        var weeklyBeforeUtc = stillRecheck.WeeklyBeforeUtc;
        var minStillPercent = stillRecheck.MinStillPercent;
        var minAiredEpisodes = stillRecheck.MinAiredEpisodes;

        return await dbContext.CachedEpisodesExtended
            .AsNoTracking()
            .Where(x => x.RetrievedUtc < staleBeforeUtc
                        || (x.Name != null && x.Name.ToUpper() == "TBA" && x.RetrievedUtc < tbaStaleBeforeUtc)
                        // Aired without a still: daily while it is recent, weekly
                        // for a while longer, then no more.
                        || (!x.HasImage && x.Aired != null && x.Aired <= today
                            && ((x.Aired >= dailyFrom && x.RetrievedUtc < dailyBeforeUtc)
                                || (x.Aired < dailyFrom && x.Aired >= weeklyFrom && x.RetrievedUtc < weeklyBeforeUtc))
                            // ...unless its series rarely has stills at all: enough
                            // aired episodes to judge, and too few of them with one.
                            && !dbContext.CachedSeriesAggregates.Any(a =>
                                a.TvdbId == x.SeriesTvdbId
                                && a.AiredEpisodeCount >= minAiredEpisodes
                                && a.AiredStillCount * 100 < a.AiredEpisodeCount * minStillPercent)))
            .OrderBy(x => x.RetrievedUtc)
            .Take(limit)
            .Select(x => x.EpisodeTvdbId)
            .ToListAsync(cancellationToken);
    }

    public async Task UpsertSeriesAggregateAsync(
        SeriesAggregate aggregate, string language, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var row = await dbContext.CachedSeriesAggregates
            .FirstOrDefaultAsync(x => x.TvdbId == aggregate.TvdbId && x.Language == language, cancellationToken);

        if (row is null)
        {
            row = new CachedSeriesAggregateEntity { TvdbId = aggregate.TvdbId, Language = language };
            dbContext.CachedSeriesAggregates.Add(row);
        }

        row.Name = aggregate.Name;
        row.StatusName = aggregate.Status?.Name;
        row.KeepUpdated = aggregate.Status?.KeepUpdated;
        row.Payload = JsonSerializer.Serialize(aggregate, JsonOptions);
        row.MappingVersion = SeriesDataDtoMappings.AggregateVersion;
        (row.AiredEpisodeCount, row.AiredStillCount) = StillCoverage(aggregate);
        row.RetrievedUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<Series?> GetSeriesExtendedAsync(int tvdbId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var row = await dbContext.CachedSeriesExtended
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TvdbId == tvdbId, cancellationToken);

        return Deserialize<Series>(row?.Payload, tvdbId);
    }

    public async Task SaveSeriesExtendedAsync(Series series, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var exists = await dbContext.CachedSeriesExtended
            .AsNoTracking()
            .AnyAsync(x => x.TvdbId == series.Id, cancellationToken);
        if (exists)
            return;

        var entity = new CachedSeriesExtendedEntity
        {
            TvdbId = series.Id,
            Name = series.Name,
            Payload = JsonSerializer.Serialize(series, JsonOptions),
            RetrievedUtc = DateTime.UtcNow
        };

        await InsertAsync(dbContext, entity, series.Id, cancellationToken);
    }

    public async Task<Episode?> GetEpisodeExtendedAsync(int episodeTvdbId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var row = await dbContext.CachedEpisodesExtended
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.EpisodeTvdbId == episodeTvdbId, cancellationToken);

        return Deserialize<Episode>(row?.Payload, episodeTvdbId);
    }

    public async Task SaveEpisodeExtendedAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        if (episode.Id is not { } episodeTvdbId)
        {
            logger.LogWarning("Skipping episode snapshot save — episode has no id.");
            return;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var exists = await dbContext.CachedEpisodesExtended
            .AsNoTracking()
            .AnyAsync(x => x.EpisodeTvdbId == episodeTvdbId, cancellationToken);
        if (exists)
            return;

        var entity = new CachedEpisodeExtendedEntity
        {
            EpisodeTvdbId = episodeTvdbId,
            SeriesTvdbId = episode.SeriesId,
            Name = episode.Name,
            Aired = ParseAired(episode.Aired),
            HasImage = !string.IsNullOrEmpty(episode.Image),
            Payload = JsonSerializer.Serialize(episode, JsonOptions),
            RetrievedUtc = DateTime.UtcNow
        };

        await InsertAsync(dbContext, entity, episodeTvdbId, cancellationToken);
    }

    public async Task UpsertEpisodeExtendedAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        if (episode.Id is not { } episodeTvdbId)
        {
            logger.LogWarning("Skipping episode snapshot upsert — episode has no id.");
            return;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var row = await dbContext.CachedEpisodesExtended
            .FirstOrDefaultAsync(x => x.EpisodeTvdbId == episodeTvdbId, cancellationToken);

        if (row is null)
        {
            row = new CachedEpisodeExtendedEntity { EpisodeTvdbId = episodeTvdbId };
            dbContext.CachedEpisodesExtended.Add(row);
        }

        var hasImage = !string.IsNullOrEmpty(episode.Image);

        row.SeriesTvdbId = episode.SeriesId;
        row.Name = episode.Name;
        row.Aired = ParseAired(episode.Aired);
        row.HasImage = hasImage;
        row.Payload = JsonSerializer.Serialize(episode, JsonOptions);
        row.RetrievedUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Episode>> BackfillEpisodeImagesFromAggregateAsync(
        SeriesAggregate aggregate, CancellationToken cancellationToken = default)
    {
        var imagesByEpisodeId = aggregate.Episodes
            .Where(e => !string.IsNullOrEmpty(e.Image))
            .ToDictionary(e => e.Id, e => e.Image!);

        if (imagesByEpisodeId.Count == 0)
            return [];

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var episodeIds = imagesByEpisodeId.Keys.ToList();

        var candidates = await dbContext.CachedEpisodesExtended
            .Where(x => !x.HasImage && episodeIds.Contains(x.EpisodeTvdbId))
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
            return [];

        var patched = new List<Episode>(candidates.Count);

        foreach (var row in candidates)
        {
            var episode = Deserialize<Episode>(row.Payload, row.EpisodeTvdbId);
            if (episode is null)
                continue;

            var updated = episode with { Image = imagesByEpisodeId[row.EpisodeTvdbId] };

            row.Payload = JsonSerializer.Serialize(updated, JsonOptions);
            row.HasImage = true;

            patched.Add(updated);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return patched;
    }

    private async Task InsertAsync(AppDbContext dbContext, object entity, int id, CancellationToken cancellationToken)
    {
        dbContext.Add(entity);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Concurrent request already wrote this snapshot — fine, it's insert-only.
            dbContext.Entry(entity).State = EntityState.Detached;
            logger.LogInformation("Snapshot {EntityType} for {Id} already stored by a concurrent request.", entity.GetType().Name, id);
        }
    }

    private T? Deserialize<T>(string? payload, int id) where T : class
    {
        if (string.IsNullOrEmpty(payload))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            // Treat a corrupt/outdated row as a miss so the caller falls through to the API.
            logger.LogWarning(ex, "Corrupt {Type} snapshot for {Id}; ignoring.", typeof(T).Name, id);
            return null;
        }
    }

    private static DateOnly? ParseAired(string? aired) =>
        DateOnly.TryParse(aired, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
}
