using Microsoft.EntityFrameworkCore;
using Npgsql;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public sealed class AppUserRepository(AppDbContext dbContext) : IAppUserRepository
{
    public async Task<UserCounts> GetCountsAsync(CancellationToken cancellationToken = default)
    {
        var total = await dbContext.AppUsers.CountAsync(cancellationToken);
        var admins = await dbContext.AppUsers.CountAsync(x => x.Role == UserRole.Admin, cancellationToken);

        return new UserCounts(total, admins);
    }

    public Task<AppUserEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.AppUsers.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<bool> IsOnlyAdminAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var isAdmin = await dbContext.AppUsers
            .AnyAsync(x => x.Id == userId && x.Role == UserRole.Admin, cancellationToken);

        return isAdmin
               && !await dbContext.AppUsers.AnyAsync(x => x.Id != userId && x.Role == UserRole.Admin, cancellationToken);
    }

    public async Task<AccountDeletionResult> DeleteAccountAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // Serializable, so two admins deleting their accounts at the same moment
        // cannot both pass the "is there another admin?" check: one of the two
        // transactions fails and its caller reports an error.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);

        var user = await dbContext.AppUsers
            .AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new { x.Email, x.Role })
            .FirstOrDefaultAsync(cancellationToken);

        if (user is null)
            return AccountDeletionResult.UserNotFound;

        if (user.Role == UserRole.Admin
            && !await dbContext.AppUsers.AnyAsync(x => x.Id != userId && x.Role == UserRole.Admin, cancellationToken))
        {
            return AccountDeletionResult.OnlyAdmin;
        }

        // Every table that holds rows belonging to a user, children first. The
        // foreign keys cascade from app_user as well, but spelling the tables
        // out keeps the list of what an account owns in one readable place,
        // and covers what no foreign key reaches (the mail queue).
        await dbContext.WatchlistImportItems.Where(x => x.Job.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.WatchlistImportJobs.Where(x => x.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.NotifiedEpisodes.Where(x => x.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.Notifications.Where(x => x.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.UserRatings.Where(x => x.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.UserLikes.Where(x => x.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.UserMovieWatches.Where(x => x.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.EpisodeWatches.Where(x => x.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.TrackedMovies.Where(x => x.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.TrackedSeries.Where(x => x.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.LoginTokens.Where(x => x.UserId == userId).ExecuteDeleteAsync(cancellationToken);

        // The mail queue has no user id: its rows are found by the address
        // (stored lower-cased on the user).
        await dbContext.Emails.Where(x => x.ToAddress.ToLower() == user.Email).ExecuteDeleteAsync(cancellationToken);

        await dbContext.AppUsers.Where(x => x.Id == userId).ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return AccountDeletionResult.Deleted;
    }

    public async Task<bool> IsUsernameAvailableAsync(
        string username,
        Guid excludingUserId,
        CancellationToken cancellationToken = default)
    {
        var normalized = username.Trim().ToLowerInvariant();

        return !await dbContext.AppUsers.AnyAsync(
            x => x.Id != excludingUserId && x.Username.ToLower() == normalized,
            cancellationToken);
    }

    public async Task<UsernameUpdateResult> UpdateUsernameAsync(
        Guid userId,
        string username,
        CancellationToken cancellationToken = default)
    {
        var trimmed = username.Trim();

        var user = await dbContext.AppUsers.FirstOrDefaultAsync(x => x.Id == userId, cancellationToken);
        if (user is null)
            return UsernameUpdateResult.UserNotFound;

        if (string.Equals(user.Username, trimmed, StringComparison.Ordinal))
            return UsernameUpdateResult.Updated;

        var taken = await dbContext.AppUsers.AnyAsync(
            x => x.Id != userId && x.Username.ToLower() == trimmed.ToLower(),
            cancellationToken);
        if (taken)
            return UsernameUpdateResult.Taken;

        user.Username = trimmed;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return UsernameUpdateResult.Updated;
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Lost a race to another request claiming the same name.
            dbContext.Entry(user).State = EntityState.Unchanged;
            return UsernameUpdateResult.Taken;
        }
    }

    public Task<AppUserEntity?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalized = email.Trim().ToLowerInvariant();
        return dbContext.AppUsers.FirstOrDefaultAsync(x => x.Email == normalized, cancellationToken);
    }

    public async Task<AppUserEntity> GetOrCreateByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalized = email.Trim().ToLowerInvariant();

        var existing = await dbContext.AppUsers
            .FirstOrDefaultAsync(x => x.Email == normalized, cancellationToken);

        if (existing is not null)
            return existing;

        var user = new AppUserEntity
        {
            Id = Guid.NewGuid(),
            Email = normalized,
            Username = await BuildUniqueUsernameAsync(normalized, cancellationToken)
        };

        dbContext.AppUsers.Add(user);
        await dbContext.SaveChangesAsync(cancellationToken);

        return user;
    }

    /// <summary>
    /// Derives a display name from the email's local part, appending a short
    /// random suffix if that name is already taken (Username is unique).
    /// </summary>
    private async Task<string> BuildUniqueUsernameAsync(string email, CancellationToken cancellationToken)
    {
        var atIndex = email.IndexOf('@');
        var baseName = (atIndex > 0 ? email[..atIndex] : email).Trim();
        if (baseName.Length == 0)
            baseName = "user";
        if (baseName.Length > 180)
            baseName = baseName[..180];

        var candidate = baseName;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var taken = await dbContext.AppUsers
                .AnyAsync(x => x.Username == candidate, cancellationToken);

            if (!taken)
                return candidate;

            candidate = $"{baseName}-{Guid.NewGuid():N}"[..(baseName.Length + 9)];
        }

        return $"{baseName}-{Guid.NewGuid():N}";
    }
}
