namespace Recall.Web.Infrastructure.Persistence.Entities;

public sealed class AppUserEntity : IHasAuditTimestamps
{
    public Guid Id { get; set; }

    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    /// <summary>Access level. Defaults to <see cref="UserRole.User"/> on creation.</summary>
    public UserRole Role { get; set; } = UserRole.User;

    /// <summary>
    /// When the user switched the weekly email digest on; null while it is off.
    /// The digest is opt-in, and this is the record of the consent.
    /// </summary>
    public DateTime? DigestOptedInUtc { get; set; }

    /// <summary>
    /// When the user answered "No thanks" to the Dashboard's one-time offer of
    /// the weekly digest. Set once; the offer is not shown again.
    /// </summary>
    public DateTime? DigestPromptDismissedUtc { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public ICollection<TrackedSeriesEntity> TrackedSeries { get; set; } = new List<TrackedSeriesEntity>();
    public ICollection<EpisodeWatchEntity> EpisodeWatches { get; set; } = new List<EpisodeWatchEntity>();
    public ICollection<LoginTokenEntity> LoginTokens { get; set; } = new List<LoginTokenEntity>();
    public ICollection<UserLikeEntity> Likes { get; set; } = new List<UserLikeEntity>();
    public ICollection<UserMovieWatchEntity> MovieWatches { get; set; } = new List<UserMovieWatchEntity>();
    public ICollection<TrackedMovieEntity> TrackedMovies { get; set; } = new List<TrackedMovieEntity>();
    public ICollection<UserRatingEntity> Ratings { get; set; } = new List<UserRatingEntity>();
    public ICollection<NotificationEntity> Notifications { get; set; } = new List<NotificationEntity>();
    public ICollection<WatchlistImportJobEntity> WatchlistImportJobs { get; set; } = new List<WatchlistImportJobEntity>();
}
