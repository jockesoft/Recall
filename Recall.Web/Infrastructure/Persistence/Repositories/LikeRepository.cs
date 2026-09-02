using Microsoft.EntityFrameworkCore;
using Npgsql;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public sealed class LikeRepository(
    AppDbContext dbContext,
    ILogger<LikeRepository> logger)
    : ILikeRepository
{
    public Task<bool> IsLikedAsync(
        Guid userId,
        LikeTargetType targetType,
        int targetTvdbId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.UserLikes
            .AsNoTracking()
            .AnyAsync(
                x => x.UserId == userId
                     && x.TargetType == targetType
                     && x.TargetTvdbId == targetTvdbId,
                cancellationToken);
    }

    public async Task<bool> ToggleAsync(
        Guid userId,
        LikeTargetType targetType,
        int targetTvdbId,
        int seriesTvdbId,
        CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.UserLikes
            .FirstOrDefaultAsync(
                x => x.UserId == userId
                     && x.TargetType == targetType
                     && x.TargetTvdbId == targetTvdbId,
                cancellationToken);

        if (existing is not null)
        {
            dbContext.UserLikes.Remove(existing);
            await dbContext.SaveChangesAsync(cancellationToken);
            return false;
        }

        dbContext.UserLikes.Add(new UserLikeEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TargetType = targetType,
            TargetTvdbId = targetTvdbId,
            SeriesTvdbId = seriesTvdbId
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent request already inserted the same like — that's fine,
            // the end state is still "liked".
            logger.LogInformation(
                "Like already exists for user {UserId}, {TargetType} {TargetId}.",
                userId, targetType, targetTvdbId);
        }

        return true;
    }
}
