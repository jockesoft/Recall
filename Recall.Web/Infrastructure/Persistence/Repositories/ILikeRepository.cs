using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public interface ILikeRepository
{
    /// <summary>True when the user has liked the given series or episode.</summary>
    Task<bool> IsLikedAsync(
        Guid userId,
        LikeTargetType targetType,
        int targetTvdbId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds the like if absent, removes it if present. Returns the resulting
    /// state: <c>true</c> when the target is now liked, <c>false</c> when it was
    /// just un-liked. Safe against a concurrent double-submit.
    /// </summary>
    /// <param name="seriesTvdbId">
    /// Parent series id, stored alongside the like. Pass the target id itself
    /// for a series like.
    /// </param>
    Task<bool> ToggleAsync(
        Guid userId,
        LikeTargetType targetType,
        int targetTvdbId,
        int seriesTvdbId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every like the user has of the given target type, newest first. Each row
    /// carries the target's TVDB id and (for episodes) the parent series id.
    /// </summary>
    Task<IReadOnlyList<UserLike>> GetLikesAsync(
        Guid userId,
        LikeTargetType targetType,
        CancellationToken cancellationToken = default);
}

/// <summary>A single "heart" the user has placed, flattened for read use.</summary>
/// <param name="TargetType">Whether <paramref name="TargetTvdbId"/> is a series or an episode.</param>
/// <param name="TargetTvdbId">TVDB id of the liked series or episode.</param>
/// <param name="SeriesTvdbId">
/// Parent series id — equals <paramref name="TargetTvdbId"/> for a series like.
/// </param>
/// <param name="CreatedUtc">When the like was placed.</param>
public sealed record UserLike(
    LikeTargetType TargetType,
    int TargetTvdbId,
    int SeriesTvdbId,
    DateTime CreatedUtc);
