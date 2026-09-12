using Microsoft.EntityFrameworkCore;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public sealed class WatchlistImportRepository(AppDbContext dbContext) : IWatchlistImportRepository
{
    public async Task<WatchlistImportJob?> GetActiveJobForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var jobId = await dbContext.WatchlistImportJobs
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.Status == WatchlistImportJobStatus.Processing)
            .OrderByDescending(x => x.CreatedUtc)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return jobId is null ? null : await LoadJobAsync(jobId.Value, includeItems: false, cancellationToken);
    }

    public async Task<WatchlistImportJob?> GetLatestJobForUserAsync(
        Guid userId, bool includeItems = true, CancellationToken cancellationToken = default)
    {
        var jobId = await dbContext.WatchlistImportJobs
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedUtc)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return jobId is null ? null : await LoadJobAsync(jobId.Value, includeItems, cancellationToken);
    }

    public async Task<WatchlistImportJob> CreateJobAsync(
        Guid userId,
        string fileName,
        IReadOnlyList<NewWatchlistImportItem> items,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        var job = new WatchlistImportJobEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FileName = fileName,
            Status = WatchlistImportJobStatus.Processing,
            TotalCount = items.Count,
            CreatedUtc = now
        };

        job.Items = items.Select(item => new WatchlistImportItemEntity
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            RowNumber = item.RowNumber,
            ImdbId = item.ImdbId,
            Title = item.Title,
            TitleType = item.TitleType,
            YourRating = item.YourRating,
            Status = item.IsSupported ? WatchlistImportItemStatus.Pending : WatchlistImportItemStatus.Unsupported,
            ResultMessage = item.IsSupported ? null : $"\"{item.TitleType}\" isn't a supported title type.",
            ProcessedUtc = item.IsSupported ? null : now,
            CreatedUtc = now
        }).ToList();

        ApplyCounts(job, job.Items);

        dbContext.WatchlistImportJobs.Add(job);
        await dbContext.SaveChangesAsync(cancellationToken);

        return await LoadJobAsync(job.Id, includeItems: true, cancellationToken)
            ?? throw new InvalidOperationException("Import job vanished immediately after creation.");
    }

    public async Task<IReadOnlyList<WatchlistImportItem>> ClaimNextPendingBatchAsync(
        int batchSize, CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.WatchlistImportItems
            .AsNoTracking()
            .Where(x => x.Status == WatchlistImportItemStatus.Pending)
            .OrderBy(x => x.CreatedUtc)
            .ThenBy(x => x.RowNumber)
            .Take(batchSize)
            .Select(x => new { Item = x, x.Job.UserId })
            .ToListAsync(cancellationToken);

        return entities.Select(x => ToItemRecord(x.Item, x.UserId)).ToArray();
    }

    public async Task MarkItemResultAsync(
        Guid itemId,
        WatchlistImportItemStatus status,
        int? resolvedTvdbId,
        string? resultMessage,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.WatchlistImportItems
            .FirstOrDefaultAsync(x => x.Id == itemId, cancellationToken);

        if (entity is null)
            return;

        entity.Status = status;
        entity.ResolvedTvdbId = resolvedTvdbId;
        entity.ResultMessage = resultMessage;
        entity.ProcessedUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RecalculateJobProgressAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await dbContext.WatchlistImportJobs
            .FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);

        if (job is null)
            return;

        var items = await dbContext.WatchlistImportItems
            .AsNoTracking()
            .Where(x => x.JobId == jobId)
            .Select(x => x.Status)
            .ToListAsync(cancellationToken);

        ApplyCounts(job, items);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static void ApplyCounts(WatchlistImportJobEntity job, IEnumerable<WatchlistImportItemEntity> items)
        => ApplyCounts(job, items.Select(x => x.Status));

    private static void ApplyCounts(WatchlistImportJobEntity job, IEnumerable<WatchlistImportItemStatus> statuses)
    {
        var processed = 0;
        var imported = 0;
        var skipped = 0;
        var notFound = 0;
        var failed = 0;

        foreach (var status in statuses)
        {
            switch (status)
            {
                case WatchlistImportItemStatus.Pending:
                    continue;
                case WatchlistImportItemStatus.Imported:
                    imported++;
                    break;
                case WatchlistImportItemStatus.AlreadyInLibrary:
                case WatchlistImportItemStatus.Unsupported:
                    skipped++;
                    break;
                case WatchlistImportItemStatus.NotFound:
                    notFound++;
                    break;
                case WatchlistImportItemStatus.Failed:
                    failed++;
                    break;
            }

            processed++;
        }

        job.ProcessedCount = processed;
        job.ImportedCount = imported;
        job.SkippedCount = skipped;
        job.NotFoundCount = notFound;
        job.FailedCount = failed;

        if (job.ProcessedCount >= job.TotalCount && job.Status != WatchlistImportJobStatus.Completed)
        {
            job.Status = WatchlistImportJobStatus.Completed;
            job.CompletedUtc = DateTime.UtcNow;
        }
    }

    private async Task<WatchlistImportJob?> LoadJobAsync(Guid jobId, bool includeItems, CancellationToken cancellationToken)
    {
        var entity = await dbContext.WatchlistImportJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);

        if (entity is null)
            return null;

        IReadOnlyList<WatchlistImportItem> items = [];
        if (includeItems)
        {
            var itemEntities = await dbContext.WatchlistImportItems
                .AsNoTracking()
                .Where(x => x.JobId == jobId)
                .OrderBy(x => x.RowNumber)
                .ToListAsync(cancellationToken);

            items = itemEntities.Select(e => ToItemRecord(e, entity.UserId)).ToArray();
        }

        return ToJobRecord(entity, items);
    }

    private static WatchlistImportJob ToJobRecord(WatchlistImportJobEntity entity, IReadOnlyList<WatchlistImportItem> items)
        => new(
            entity.Id,
            entity.UserId,
            entity.FileName,
            entity.Status,
            entity.TotalCount,
            entity.ProcessedCount,
            entity.ImportedCount,
            entity.SkippedCount,
            entity.NotFoundCount,
            entity.FailedCount,
            entity.CreatedUtc,
            entity.CompletedUtc,
            items);

    private static WatchlistImportItem ToItemRecord(WatchlistImportItemEntity entity, Guid userId)
        => new(
            entity.Id,
            entity.JobId,
            userId,
            entity.RowNumber,
            entity.ImdbId,
            entity.Title,
            entity.TitleType,
            entity.YourRating,
            entity.Status,
            entity.ResolvedTvdbId,
            entity.ResultMessage,
            entity.CreatedUtc,
            entity.ProcessedUtc);
}
