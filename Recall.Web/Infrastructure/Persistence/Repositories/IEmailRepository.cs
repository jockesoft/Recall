using Recall.Web.Domain.Internal;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public interface IEmailRepository
{
    /// <summary>Queues a new message for later delivery by the mail timer.</summary>
    Task AddAsync(OutboundEmail email, CancellationToken cancellationToken = default);

    /// <summary>
    /// The next batch of messages that still need sending: never sent, and not
    /// yet past <paramref name="maxAttempts"/>. Ordered by priority then age.
    /// </summary>
    Task<IReadOnlyList<OutboundEmail>> GetPendingAsync(
        int maxCount,
        int maxAttempts,
        CancellationToken cancellationToken = default);

    /// <summary>Marks a message delivered (sets <c>sent_utc</c>).</summary>
    Task MarkSentAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Records a failed delivery attempt (increments <c>send_attempts</c>).</summary>
    Task RecordFailedAttemptAsync(Guid id, CancellationToken cancellationToken = default);
}
