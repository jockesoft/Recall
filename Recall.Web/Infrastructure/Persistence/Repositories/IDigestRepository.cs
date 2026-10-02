using Recall.Web.Domain.Internal;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

/// <summary>Someone the weekly digest may go to: they have switched it on.</summary>
public sealed record DigestRecipient(Guid UserId, string Email, string Username);

/// <summary>The weekly digest's recipients and its ledger of who has been dealt with for which week.</summary>
public interface IDigestRepository
{
    /// <summary>
    /// Up to <paramref name="max"/> users who have the digest switched on and
    /// have no ledger row for the week of <paramref name="periodStart"/> yet,
    /// earliest opt-in first.
    /// </summary>
    Task<IReadOnlyList<DigestRecipient>> GetDueRecipientsAsync(
        DateOnly periodStart,
        int max,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that a user has been dealt with for a week and, when there is an
    /// email, queues it: both in one transaction, so there is never a ledger row
    /// without its email or an email without its ledger row. Returns false, and
    /// writes nothing, when the week was already recorded for that user (the
    /// unique index is the guard against a second digest).
    /// </summary>
    Task<bool> RecordAsync(
        Guid userId,
        DateOnly periodStart,
        DigestSendStatus status,
        OutboundEmail? email,
        CancellationToken cancellationToken = default);
}
