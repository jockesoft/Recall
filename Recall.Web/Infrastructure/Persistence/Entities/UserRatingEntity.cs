namespace Recall.Web.Infrastructure.Persistence.Entities;

/// <summary>What a <see cref="UserRatingEntity"/> points at.</summary>
public enum RatingTargetType
{
    Series = 1,
    Episode = 2
}

/// <summary>
/// A user's 1-10 rating of a series or a single episode. One row per
/// (user, target type, target id); rating again overwrites the previous value.
/// </summary>
public sealed class UserRatingEntity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public AppUserEntity User { get; set; } = null!;

    /// <summary>Whether <see cref="TargetTvdbId"/> is a series or an episode.</summary>
    public RatingTargetType TargetType { get; set; }

    /// <summary>TVDB id of the rated series or episode.</summary>
    public int TargetTvdbId { get; set; }

    /// <summary>
    /// TVDB id of the series this rating belongs to — equals
    /// <see cref="TargetTvdbId"/> for a series rating, the parent series for an
    /// episode rating. Lets "everything this user rated in series X" be answered
    /// without going back to TheTVDB.
    /// </summary>
    public int SeriesTvdbId { get; set; }

    /// <summary>1 (worst) to 10 (best).</summary>
    public int Value { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
