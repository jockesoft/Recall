using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public interface IAppUserRepository
{
    /// <summary>How many accounts exist, and how many of them are admins. For the admin dashboard.</summary>
    Task<UserCounts> GetCountsAsync(CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Switches the weekly digest on (recording when, as the consent) or off.
    /// Switching on something already on keeps the original time. Returns false
    /// when no such user exists.
    /// </summary>
    Task<bool> SetDigestOptInAsync(Guid userId, bool optedIn, CancellationToken cancellationToken = default);

    /// <summary>Records that the user declined the Dashboard's one-time offer of the weekly digest.</summary>
    Task DismissDigestPromptAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the user is an admin and no other admin exists. Such an
    /// account cannot be deleted: nobody would be left to run the site.
    /// </summary>
    Task<bool> IsOnlyAdminAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the account and everything that belongs to it, in one
    /// transaction: tracked series and movies, episode and movie watches,
    /// likes, ratings, notifications and the notified-episode ledger, IMDb
    /// imports and their rows, sign-in tokens, the emails queued or sent to
    /// the account's address, and the user row itself. The shared metadata
    /// caches (TheTVDB and OMDb snapshots) are not user data and stay.
    /// Refuses, deleting nothing, when the user is the only admin.
    ///
    /// A table that holds rows belonging to a user must be deleted here. The
    /// PostgreSQL test for this method fails when a table with a
    /// <c>user_id</c> column still has rows for a deleted user.
    /// </summary>
    Task<AccountDeletionResult> DeleteAccountAsync(Guid userId, CancellationToken cancellationToken = default);
}

public enum AccountDeletionResult
{
    /// <summary>The account and all of its data are gone.</summary>
    Deleted,

    /// <summary>Nothing was deleted: the user is the only admin.</summary>
    OnlyAdmin,

    /// <summary>No user row exists for the supplied id (already deleted, for instance from another session).</summary>
    UserNotFound
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

/// <param name="Total">Every row in <c>app_user</c> — every account that has ever signed in.</param>
/// <param name="Admins">How many of those have the Admin role.</param>
public sealed record UserCounts(int Total, int Admins);
