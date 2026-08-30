namespace Recall.Web.Infrastructure.Persistence.Entities;

public sealed class AppUserEntity
{
    public Guid Id { get; set; }

    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public ICollection<TrackedSeriesEntity> TrackedSeries { get; set; } = new List<TrackedSeriesEntity>();
    public ICollection<EpisodeWatchEntity> EpisodeWatches { get; set; } = new List<EpisodeWatchEntity>();
    public ICollection<LoginTokenEntity> LoginTokens { get; set; } = new List<LoginTokenEntity>();
}
