using Microsoft.EntityFrameworkCore;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public sealed class DataRetentionRepository(AppDbContext dbContext) : IDataRetentionRepository
{
    public Task<int> DeleteLoginTokensAsync(DateTime settledBeforeUtc, CancellationToken cancellationToken = default)
    {
        return dbContext.LoginTokens
            .Where(x => x.ExpiresUtc < settledBeforeUtc
                        || (x.ConsumedUtc != null && x.ConsumedUtc < settledBeforeUtc))
            .ExecuteDeleteAsync(cancellationToken);
    }

    public Task<int> DeleteFinishedEmailsAsync(
        DateTime settledBeforeUtc, int maxSendAttempts, CancellationToken cancellationToken = default)
    {
        return dbContext.Emails
            .Where(x => (x.SentUtc != null && x.SentUtc < settledBeforeUtc)
                        // Gave up: UpdatedUtc is when the last attempt was recorded.
                        || (x.SentUtc == null && x.SendAttempts >= maxSendAttempts && x.UpdatedUtc < settledBeforeUtc))
            .ExecuteDeleteAsync(cancellationToken);
    }

    public Task<int> DeleteReadNotificationsAsync(DateTime readBeforeUtc, CancellationToken cancellationToken = default)
    {
        return dbContext.Notifications
            // ReadUtc can be missing on a row marked read before that column was kept; fall back to its age.
            .Where(x => x.IsRead && (x.ReadUtc ?? x.CreatedUtc) < readBeforeUtc)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public Task<int> DeleteNotifiedEpisodesAsync(DateTime createdBeforeUtc, CancellationToken cancellationToken = default)
    {
        return dbContext.NotifiedEpisodes
            .Where(x => x.CreatedUtc < createdBeforeUtc)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public Task<int> DeleteDigestLedgerAsync(DateTime createdBeforeUtc, CancellationToken cancellationToken = default)
    {
        return dbContext.DigestSends
            .Where(x => x.CreatedUtc < createdBeforeUtc)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<int> DeleteCompletedImportJobsAsync(DateTime completedBeforeUtc, CancellationToken cancellationToken = default)
    {
        var expiredJobs = dbContext.WatchlistImportJobs
            .Where(x => x.Status == WatchlistImportJobStatus.Completed
                        && x.CompletedUtc != null
                        && x.CompletedUtc < completedBeforeUtc);

        // The foreign key cascades, but deleting the rows explicitly keeps this
        // from depending on it (ExecuteDelete bypasses EF's own cascade).
        await dbContext.WatchlistImportItems
            .Where(item => expiredJobs.Any(job => job.Id == item.JobId))
            .ExecuteDeleteAsync(cancellationToken);

        return await expiredJobs.ExecuteDeleteAsync(cancellationToken);
    }
}
