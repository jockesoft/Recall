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

    /// <summary>
    /// How many titles the user has given each rating, keyed by the rating
    /// (1–10) and counting series, episodes and movies together; a value nobody
    /// used is absent. One grouped query, for the Stats page.
    /// </summary>
    Task<IReadOnlyDictionary<int, int>> GetValueCountsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>The community aggregate — average and count — of everyone's rating of a target.</summary>
    Task<RatingSummary> GetSummaryAsync(
        RatingTargetType targetType,
        int targetTvdbId,
        CancellationToken cancellationToken = default);
}

/// <summary>Aggregate of every rating placed on a single series or episode.</summary>
public sealed record RatingSummary(double? Average, int Count)
{
    public static readonly RatingSummary Empty = new(null, 0);
}
