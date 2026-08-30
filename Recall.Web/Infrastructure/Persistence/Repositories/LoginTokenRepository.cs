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

    public async Task<LoginToken?> GetMostRecentActiveForUserAsync(
        Guid userId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.LoginTokens
            .AsNoTracking()
            .Where(x => x.UserId == userId
                        && x.ConsumedUtc == null
                        && x.ExpiresUtc > nowUtc)
            .OrderByDescending(x => x.CreatedUtc)
            .Select(x => x.ToDomain())
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task MarkConsumedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await dbContext.LoginTokens
            .Where(x => x.Id == id && x.ConsumedUtc == null)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.ConsumedUtc, DateTime.UtcNow)
                    .SetProperty(x => x.UpdatedUtc, DateTime.UtcNow),
                cancellationToken);
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
