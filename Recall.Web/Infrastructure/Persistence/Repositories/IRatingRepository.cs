using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public interface IRatingRepository
{
    /// <summary>The current user's rating of the given target, or null if unrated.</summary>
    Task<int?> GetRatingAsync(
        Guid userId,
        RatingTargetType targetType,
        int targetTvdbId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the user's rating for the given target, overwriting any previous value.
    /// </summary>
    /// <param name="value">1 (worst) to 10 (best).</param>
    /// <param name="seriesTvdbId">
    /// Parent series id, stored alongside the rating. Pass the target id itself
    /// for a series rating.
    /// </param>
    Task RateAsync(
        Guid userId,
        RatingTargetType targetType,
        int targetTvdbId,
        int seriesTvdbId,
        int value,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the user's rating of the given target, if any.</summary>
    Task RemoveRatingAsync(
        Guid userId,
        RatingTargetType targetType,
        int targetTvdbId,
        CancellationToken cancellationToken = default);
}
