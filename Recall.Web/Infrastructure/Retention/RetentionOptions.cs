namespace Recall.Web.Infrastructure.Retention;

/// <summary>
/// Bound from the <c>Retention</c> configuration section: how long
/// <c>PruneOldDataTimer</c> keeps rows that have served their purpose. Each
/// value is in days; 0 or less switches that category off.
/// </summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>Sign-in tokens, counted from when they expired or were consumed.</summary>
    public int LoginTokenDays { get; set; } = 7;

    /// <summary>Emails that were sent, or that gave up after <c>Mail:MaxSendAttempts</c>. A message still queued is never deleted.</summary>
    public int EmailDays { get; set; } = 30;

    /// <summary>Notifications the user has read, counted from when they read them. Unread ones are kept.</summary>
    public int ReadNotificationDays { get; set; } = 90;

    /// <summary>
    /// The "already notified about this episode" ledger. It only has to outlive
    /// <c>NewEpisodeNotificationTimer</c>'s look-back window (3 days); keep this
    /// comfortably above that or a user gets notified twice.
    /// </summary>
    public int NotifiedEpisodeDays { get; set; } = 30;

    /// <summary>Completed IMDb import jobs and their rows, counted from completion. A job still processing is never deleted.</summary>
    public int ImportJobDays { get; set; } = 90;
}
