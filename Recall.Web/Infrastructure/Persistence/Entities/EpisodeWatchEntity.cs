namespace Recall.Web.Infrastructure.Persistence.Entities;

public sealed class EpisodeWatchEntity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public AppUserEntity User { get; set; } = null!;

    public int SeriesTvdbId { get; set; }
    public int EpisodeTvdbId { get; set; }

    public DateTime WatchedUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
