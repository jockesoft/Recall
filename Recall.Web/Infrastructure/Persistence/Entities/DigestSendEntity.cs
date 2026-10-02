namespace Recall.Web.Infrastructure.Persistence.Entities;

public enum DigestSendStatus
{
    /// <summary>A digest email was queued for this user and week.</summary>
    Queued = 1,

    /// <summary>There was nothing to say that week, so no email was queued.</summary>
    Skipped = 2
}

/// <summary>
/// A ledger row: "user U has been dealt with for the digest of week W". The
/// unique <c>(user_id, period_start)</c> index is what stops the hourly digest
/// job from sending the same week's digest twice. Written in the same
/// transaction as the queued email.
/// </summary>
public sealed class DigestSendEntity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>The scheduled send date of the digest this row is about (UTC).</summary>
    public DateOnly PeriodStart { get; set; }

    public DigestSendStatus Status { get; set; }

    public DateTime CreatedUtc { get; set; }
}
