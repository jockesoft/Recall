using Microsoft.EntityFrameworkCore;
using Recall.Web.Domain.Internal;
using Recall.Web.Mappings;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public sealed class LoginTokenRepository(AppDbContext dbContext) : ILoginTokenRepository
{
    public async Task AddAsync(LoginToken token, CancellationToken cancellationToken = default)
    {
        dbContext.LoginTokens.Add(token.ToEntity());
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<LoginToken?> GetActiveByHashAsync(
        string tokenHash,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.LoginTokens
            .AsNoTracking()
            .Where(x => x.TokenHash == tokenHash
                        && x.ConsumedUtc == null
                        && x.ExpiresUtc > nowUtc)
            .Select(x => x.ToDomain())
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> MarkConsumedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var affected = await dbContext.LoginTokens
            .Where(x => x.Id == id && x.ConsumedUtc == null)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.ConsumedUtc, DateTime.UtcNow)
                    .SetProperty(x => x.UpdatedUtc, DateTime.UtcNow),
                cancellationToken);

        return affected > 0;
    }

    public async Task<LoginToken?> GetNewestActiveWithCodeAsync(
        Guid userId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.LoginTokens
            .AsNoTracking()
            .Where(x => x.UserId == userId
                        && x.CodeHash != null
                        && x.ConsumedUtc == null
                        && x.ExpiresUtc > nowUtc)
            .OrderByDescending(x => x.CreatedUtc)
            .Select(x => x.ToDomain())
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<int> RecordWrongCodeAsync(Guid id, int maxAttempts, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        // One statement: two wrong guesses arriving together both count, and
        // the one that reaches the limit consumes the token in the same update.
        var affected = await dbContext.LoginTokens
            .Where(x => x.Id == id && x.ConsumedUtc == null)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.CodeAttempts, x => x.CodeAttempts + 1)
                    .SetProperty(x => x.ConsumedUtc, x => x.CodeAttempts + 1 >= maxAttempts ? (DateTime?)now : x.ConsumedUtc)
                    .SetProperty(x => x.UpdatedUtc, now),
                cancellationToken);

        if (affected == 0)
            return 0;

        return await dbContext.LoginTokens
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => x.CodeAttempts)
            .FirstAsync(cancellationToken);
    }

    public async Task InvalidateActiveForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await dbContext.LoginTokens
            .Where(x => x.UserId == userId && x.ConsumedUtc == null)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.ConsumedUtc, DateTime.UtcNow)
                    .SetProperty(x => x.UpdatedUtc, DateTime.UtcNow),
                cancellationToken);
    }
}
