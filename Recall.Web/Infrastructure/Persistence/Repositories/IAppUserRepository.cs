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

    /// <summary>
    /// True when <paramref name="username"/> is not used by any user other than
    /// <paramref name="excludingUserId"/>. Comparison is case-insensitive and
    /// trims surrounding whitespace.
    /// </summary>
    Task<bool> IsUsernameAvailableAsync(
        string username,
        Guid excludingUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the given user's <see cref="AppUserEntity.Username"/>. The value is
    /// trimmed. Returns <see cref="UsernameUpdateResult.Taken"/> when another
    /// account already uses it (checked up-front and again on the unique-index
    /// violation, so it is race-safe).
    /// </summary>
    Task<UsernameUpdateResult> UpdateUsernameAsync(
        Guid userId,
        string username,
        CancellationToken cancellationToken = default);
}

public enum UsernameUpdateResult
{
    /// <summary>The username was saved (or already matched — a no-op).</summary>
    Updated,

    /// <summary>Another account already uses that username.</summary>
    Taken,

    /// <summary>No user row exists for the supplied id.</summary>
    UserNotFound
}
