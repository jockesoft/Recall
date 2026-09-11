namespace Recall.Web.Infrastructure.Persistence.Entities;

/// <summary>What a <see cref="UserLikeEntity"/> points at.</summary>
public enum LikeTargetType
{
    Series = 1,
    Episode = 2,
    Movie = 3
}

/// <summary>
/// A user's "heart" on a series, a single episode, or a movie. One row per
/// (user, target type, target id); the row's presence is the like.
/// </summary>
public sealed class UserLikeEntity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public AppUserEntity User { get; set; } = null!;

    /// <summary>Whether <see cref="TargetTvdbId"/> is a series, an episode, or a movie.</summary>
    public LikeTargetType TargetType { get; set; }

    /// <summary>TVDB id of the liked series, episode, or movie.</summary>
    public int TargetTvdbId { get; set; }

    /// <summary>
    /// TVDB id of the series this like belongs to — equals
    /// <see cref="TargetTvdbId"/> for a series or movie like, the parent series for
    /// an episode like. Lets "everything this user liked in series X" be answered
    /// without going back to TheTVDB.
    /// </summary>
    public int SeriesTvdbId { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
