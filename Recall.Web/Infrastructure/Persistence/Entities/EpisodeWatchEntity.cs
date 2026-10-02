namespace Recall.Web.Infrastructure.Persistence.Entities;

public sealed class EpisodeWatchEntity : IHasAuditTimestamps
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public AppUserEntity User { get; set; } = null!;

    public int SeriesTvdbId { get; set; }
    public int EpisodeTvdbId { get; set; }

    public DateTime WatchedUtc { get; set; }

    /// <summary>How the row was written; decides whether <see cref="WatchedUtc"/> counts as a watch date.</summary>
    public WatchSource Source { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
