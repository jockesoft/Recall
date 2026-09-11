using Microsoft.EntityFrameworkCore;
using Npgsql;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public sealed class RatingRepository(
    AppDbContext dbContext,
    ILogger<RatingRepository> logger)
    : IRatingRepository
{
    public Task<int?> GetRatingAsync(
        Guid userId,
        RatingTargetType targetType,
        int targetTvdbId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.UserRatings
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.TargetType == targetType && x.TargetTvdbId == targetTvdbId)
            .Select(x => (int?)x.Value)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task RateAsync(
        Guid userId,
        RatingTargetType targetType,
        int targetTvdbId,
        int seriesTvdbId,
        int value,
        CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.UserRatings
            .FirstOrDefaultAsync(
                x => x.UserId == userId
                     && x.TargetType == targetType
                     && x.TargetTvdbId == targetTvdbId,
                cancellationToken);

        if (existing is not null)
        {
            existing.Value = value;
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        dbContext.UserRatings.Add(new UserRatingEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TargetType = targetType,
            TargetTvdbId = targetTvdbId,
            SeriesTvdbId = seriesTvdbId,
            Value = value
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent request already inserted a rating for this target between
            // our lookup and insert — update that row to this request's value instead
            // of losing it silently.
            logger.LogInformation(
                "Rating race for user {UserId}, {TargetType} {TargetId}; updating the existing row instead.",
                userId, targetType, targetTvdbId);

            dbContext.ChangeTracker.Clear();

            var raceWinner = await dbContext.UserRatings.SingleAsync(
                x => x.UserId == userId && x.TargetType == targetType && x.TargetTvdbId == targetTvdbId,
                cancellationToken);
            raceWinner.Value = value;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task RemoveRatingAsync(
        Guid userId,
        RatingTargetType targetType,
        int targetTvdbId,
        CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.UserRatings
            .FirstOrDefaultAsync(
                x => x.UserId == userId
                     && x.TargetType == targetType
                     && x.TargetTvdbId == targetTvdbId,
                cancellationToken);

        if (existing is null)
            return;

        dbContext.UserRatings.Remove(existing);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<RatingSummary> GetSummaryAsync(
        RatingTargetType targetType,
        int targetTvdbId,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.UserRatings
            .AsNoTracking()
            .Where(x => x.TargetType == targetType && x.TargetTvdbId == targetTvdbId);

        var count = await query.CountAsync(cancellationToken);
        if (count == 0)
            return RatingSummary.Empty;

        var average = await query.AverageAsync(x => x.Value, cancellationToken);
        return new RatingSummary(average, count);
    }
}
