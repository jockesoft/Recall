using Microsoft.EntityFrameworkCore;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public sealed class AppUserRepository(AppDbContext dbContext) : IAppUserRepository
{
    public async Task<AppUserEntity> GetOrCreateByExternalIdAsync(
        string externalId,
        string? email,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.AppUsers
            .FirstOrDefaultAsync(x => x.UserId == externalId, cancellationToken);

        if (existing is not null)
            return existing;

        var user = new AppUserEntity
        {
            Id = Guid.NewGuid(),
            UserId = externalId,
            Email = string.IsNullOrWhiteSpace(email) ? "unknown@local" : email.Trim(),
            Username = string.IsNullOrWhiteSpace(displayName) ? "Unknown user" : displayName.Trim()
        };

        dbContext.AppUsers.Add(user);
        await dbContext.SaveChangesAsync(cancellationToken);

        return user;
    }
}