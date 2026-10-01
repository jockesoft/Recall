namespace Recall.Web.Infrastructure.Persistence.Entities;

/// <summary>
/// A movie on a user's watchlist ("want to watch") — the movie counterpart of
/// <see cref="TrackedSeriesEntity"/>. A movie leaves the watchlist when it is
/// marked watched (<see cref="UserMovieWatchEntity"/>); it is never both.
/// </summary>
public sealed class TrackedMovieEntity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public AppUserEntity User { get; set; } = null!;

    public int TvdbId { get; set; }

    /// <summary>
    /// The title as known when it was added. Display normally comes from the
    /// cached TheTVDB aggregate; this is the fallback when that can't be loaded.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
