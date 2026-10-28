using Microsoft.EntityFrameworkCore;
using Npgsql;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Persistence.Repositories;

public sealed class PushSubscriptionRepository(
    AppDbContext dbContext,
    ILogger<PushSubscriptionRepository> logger) : IPushSubscriptionRepository
{
    public async Task<PushSubscribeResult> SaveAsync(
        Guid userId, NewPushSubscription subscription, CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.PushSubscriptions
            .FirstOrDefaultAsync(x => x.Endpoint == subscription.Endpoint, cancellationToken);

        if (existing is not null && existing.UserId == userId)
        {
            Refresh(existing, subscription);
            await dbContext.SaveChangesAsync(cancellationToken);
            return PushSubscribeResult.Updated;
        }

        // The browser was subscribed for another account: it is this user's
        // device now, with its own "added" date and nothing held for the other.
        if (existing is not null)
            dbContext.PushSubscriptions.Remove(existing);

        var entity = new PushSubscriptionEntity { Id = Guid.NewGuid(), UserId = userId };
        Refresh(entity, subscription);
        dbContext.PushSubscriptions.Add(entity);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return PushSubscribeResult.Added;
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A second request from the same browser saved the endpoint first
            // (a double tap). Take the row it wrote and bring it up to date.
            dbContext.ChangeTracker.Clear();
            logger.LogDebug("Push subscription for user {UserId} lost a race with a concurrent save.", userId);

            var winner = await dbContext.PushSubscriptions
                .FirstAsync(x => x.Endpoint == subscription.Endpoint, cancellationToken);
            winner.UserId = userId;
            Refresh(winner, subscription);
            await dbContext.SaveChangesAsync(cancellationToken);
            return PushSubscribeResult.Updated;
        }
    }

    public async Task<IReadOnlyList<PushDevice>> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await dbContext.PushSubscriptions
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.CreatedUtc)
            .ThenBy(x => x.Id)
            .Select(x => new PushDevice(
                x.Id, x.UserId, x.Endpoint, x.P256dh, x.Auth, x.DeviceLabel, x.TimeZone,
                x.CreatedUtc, x.LastSuccessUtc, x.HeldSinceUtc, x.HeldUntilUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> RemoveAsync(Guid userId, Guid subscriptionId, CancellationToken cancellationToken = default) =>
        await dbContext.PushSubscriptions
            .Where(x => x.UserId == userId && x.Id == subscriptionId)
            .ExecuteDeleteAsync(cancellationToken) > 0;

    public async Task<bool> RemoveByEndpointAsync(Guid userId, string endpoint, CancellationToken cancellationToken = default) =>
        await dbContext.PushSubscriptions
            .Where(x => x.UserId == userId && x.Endpoint == endpoint)
            .ExecuteDeleteAsync(cancellationToken) > 0;

    public Task DeleteAsync(Guid subscriptionId, CancellationToken cancellationToken = default) =>
        dbContext.PushSubscriptions
            .Where(x => x.Id == subscriptionId)
            .ExecuteDeleteAsync(cancellationToken);

    public Task MarkDeliveredAsync(Guid subscriptionId, DateTime deliveredUtc, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        // ExecuteUpdate bypasses the change tracker, so UpdatedUtc is set here.
        return dbContext.PushSubscriptions
            .Where(x => x.Id == subscriptionId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.LastSuccessUtc, deliveredUtc)
                      .SetProperty(x => x.HeldSinceUtc, (DateTime?)null)
                      .SetProperty(x => x.HeldUntilUtc, (DateTime?)null)
                      .SetProperty(x => x.UpdatedUtc, now),
                cancellationToken);
    }

    public Task HoldAsync(Guid subscriptionId, DateTime heldSinceUtc, DateTime releaseUtc, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        return dbContext.PushSubscriptions
            .Where(x => x.Id == subscriptionId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(
                          x => x.HeldSinceUtc,
                          x => x.HeldSinceUtc != null && x.HeldSinceUtc < heldSinceUtc ? x.HeldSinceUtc : heldSinceUtc)
                      .SetProperty(x => x.HeldUntilUtc, releaseUtc)
                      .SetProperty(x => x.UpdatedUtc, now),
                cancellationToken);
    }

    public Task ClearHoldAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        return dbContext.PushSubscriptions
            .Where(x => x.Id == subscriptionId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.HeldSinceUtc, (DateTime?)null)
                      .SetProperty(x => x.HeldUntilUtc, (DateTime?)null)
                      .SetProperty(x => x.UpdatedUtc, now),
                cancellationToken);
    }

    public async Task<IReadOnlyList<PushDevice>> GetDueHeldAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        return await dbContext.PushSubscriptions
            .AsNoTracking()
            .Where(x => x.HeldUntilUtc != null && (x.HeldUntilUtc <= nowUtc || !x.User.PushQuietHours))
            .OrderBy(x => x.UserId)
            .ThenBy(x => x.CreatedUtc)
            .Select(x => new PushDevice(
                x.Id, x.UserId, x.Endpoint, x.P256dh, x.Auth, x.DeviceLabel, x.TimeZone,
                x.CreatedUtc, x.LastSuccessUtc, x.HeldSinceUtc, x.HeldUntilUtc))
            .ToListAsync(cancellationToken);
    }

    private static void Refresh(PushSubscriptionEntity entity, NewPushSubscription subscription)
    {
        entity.Endpoint = subscription.Endpoint;
        entity.P256dh = subscription.P256dh;
        entity.Auth = subscription.Auth;
        entity.DeviceLabel = subscription.DeviceLabel;
        entity.TimeZone = subscription.TimeZone;
    }
}
