namespace Recall.Web.Infrastructure.Persistence.Repositories;

/// <summary>
/// Deletes rows that have served their purpose. Each method takes the cutoff
/// instant (everything settled before it goes) and returns how many rows were
/// removed. Called only by <c>PruneOldDataTimer</c>, which works the cutoffs
/// out from <c>RetentionOptions</c>.
/// </summary>
public interface IDataRetentionRepository
{
    /// <summary>Sign-in tokens that expired, or were consumed, before <paramref name="settledBeforeUtc"/>.</summary>
    Task<int> DeleteLoginTokensAsync(DateTime settledBeforeUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Emails sent before <paramref name="settledBeforeUtc"/>, and emails that
    /// used up <paramref name="maxSendAttempts"/> and were last tried before
    /// it. A message that can still be sent is never touched.
    /// </summary>
    Task<int> DeleteFinishedEmailsAsync(DateTime settledBeforeUtc, int maxSendAttempts, CancellationToken cancellationToken = default);

    /// <summary>Notifications read before <paramref name="readBeforeUtc"/>. Unread ones are never touched.</summary>
    Task<int> DeleteReadNotificationsAsync(DateTime readBeforeUtc, CancellationToken cancellationToken = default);

    /// <summary>"Already notified" ledger rows written before <paramref name="createdBeforeUtc"/>.</summary>
    Task<int> DeleteNotifiedEpisodesAsync(DateTime createdBeforeUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Import jobs completed before <paramref name="completedBeforeUtc"/>, with
    /// their rows. Returns the number of jobs removed. A job still processing
    /// is never touched.
    /// </summary>
    Task<int> DeleteCompletedImportJobsAsync(DateTime completedBeforeUtc, CancellationToken cancellationToken = default);
}
