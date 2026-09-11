namespace Recall.Web.Infrastructure.Persistence.Entities;

/// <summary>
/// Whether a user has watched a movie. One row per (user, movie); the row's
/// presence is the watched flag. Unlike <see cref="EpisodeWatchEntity"/>, there's
/// no parent-series id to denormalize — a movie has no grouping concept.
/// </summary>
public sealed class UserMovieWatchEntity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public AppUserEntity User { get; set; } = null!;

    public int MovieTvdbId { get; set; }

    public DateTime WatchedUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
