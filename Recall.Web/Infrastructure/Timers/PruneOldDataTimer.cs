using Microsoft.Extensions.Options;
using Quartz;
using Recall.Web.Infrastructure.Mail;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Infrastructure.Retention;

namespace Recall.Web.Infrastructure.Timers;

/// <summary>
/// Daily housekeeping: deletes rows that have served their purpose and would
/// otherwise accumulate forever — settled sign-in tokens, finished emails,
/// read notifications, the already-notified ledger, and completed imports.
/// How long each is kept comes from <see cref="RetentionOptions"/>
/// (<c>Retention</c> section); 0 or less skips that category.
///
/// Each category is its own statement and its own try/catch, so one failing
/// (a lock timeout, say) doesn't stop the others; it is simply retried on the
/// next run.
/// </summary>
[DisallowConcurrentExecution]
public sealed class PruneOldDataTimer(
    IDataRetentionRepository retentionRepository,
    IOptions<RetentionOptions> retentionOptions,
    IOptions<MailOptions> mailOptions,
    TimeProvider timeProvider,
    ILogger<PruneOldDataTimer> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        var retention = retentionOptions.Value;
        var maxSendAttempts = mailOptions.Value.MaxSendAttempts;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        await PruneAsync("login tokens", retention.LoginTokenDays, now,
            (cutoff, ct) => retentionRepository.DeleteLoginTokensAsync(cutoff, ct), cancellationToken);

        await PruneAsync("emails", retention.EmailDays, now,
            (cutoff, ct) => retentionRepository.DeleteFinishedEmailsAsync(cutoff, maxSendAttempts, ct), cancellationToken);

        await PruneAsync("read notifications", retention.ReadNotificationDays, now,
            (cutoff, ct) => retentionRepository.DeleteReadNotificationsAsync(cutoff, ct), cancellationToken);

        await PruneAsync("notified-episode ledger rows", retention.NotifiedEpisodeDays, now,
            (cutoff, ct) => retentionRepository.DeleteNotifiedEpisodesAsync(cutoff, ct), cancellationToken);

        await PruneAsync("weekly-digest ledger rows", retention.DigestLedgerDays, now,
            (cutoff, ct) => retentionRepository.DeleteDigestLedgerAsync(cutoff, ct), cancellationToken);

        await PruneAsync("completed import jobs", retention.ImportJobDays, now,
            (cutoff, ct) => retentionRepository.DeleteCompletedImportJobsAsync(cutoff, ct), cancellationToken);
    }

    private async Task PruneAsync(
        string what,
        int retentionDays,
        DateTime now,
        Func<DateTime, CancellationToken, Task<int>> delete,
        CancellationToken cancellationToken)
    {
        if (retentionDays <= 0)
        {
            logger.LogDebug("PruneOldDataTimer: {What} retention is disabled.", what);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var deleted = await delete(now.AddDays(-retentionDays), cancellationToken);

            if (deleted > 0)
                logger.LogInformation(
                    "PruneOldDataTimer: deleted {Count} {What} older than {Days} day(s).", deleted, what, retentionDays);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One category failing shouldn't stop the others — it's retried on the next run.
            logger.LogWarning(ex, "PruneOldDataTimer: failed to prune {What}.", what);
        }
    }
}
