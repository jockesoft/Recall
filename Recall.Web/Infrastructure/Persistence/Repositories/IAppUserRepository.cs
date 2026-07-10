using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public interface IAppUserRepository
{
    Task<AppUserEntity> GetOrCreateByExternalIdAsync(
        string externalId,
        string? email,
        string? displayName,
        CancellationToken cancellationToken = default);
}