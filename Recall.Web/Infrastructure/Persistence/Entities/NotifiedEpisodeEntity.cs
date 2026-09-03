namespace Recall.Web.Infrastructure.Persistence.Entities;

/// <summary>
/// A ledger row: "user U has already been told about episode E". Written
/// alongside the <see cref="NotificationEntity"/> that first covered the episode.
/// One notification can cover several of these (a full-season drop), and the
/// unique <c>(user_id, episode_tvdb_id)</c> index is what stops a re-run of the
/// sweep — or late-arriving TVDB air dates — from notifying twice.
/// </summary>
public sealed class NotifiedEpisodeEntity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public int SeriesTvdbId { get; set; }
    public int EpisodeTvdbId { get; set; }

    public DateTime CreatedUtc { get; set; }
}
