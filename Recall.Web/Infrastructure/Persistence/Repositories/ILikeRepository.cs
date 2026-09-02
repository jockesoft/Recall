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
}
