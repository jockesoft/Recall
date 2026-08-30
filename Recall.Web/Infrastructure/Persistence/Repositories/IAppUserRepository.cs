using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public interface IAppUserRepository
{
    Task<AppUserEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<AppUserEntity?> GetByEmailAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the user for <paramref name="email"/>, provisioning a passwordless
    /// account on first sign-in. The email is matched and stored lower-cased.
    /// </summary>
    Task<AppUserEntity> GetOrCreateByEmailAsync(string email, CancellationToken cancellationToken = default);
}
