namespace Recall.Web.Infrastructure.Persistence.Repositories;

/// <summary>What a browser hands over when it subscribes, plus what the server worked out about it.</summary>
public sealed record NewPushSubscription(string Endpoint, string P256dh, string Auth, string DeviceLabel, string TimeZone);

/// <summary>One of a user's push subscriptions ("devices").</summary>
public sealed record PushDevice(
    Guid Id,
    Guid UserId,
    string Endpoint,
    string P256dh,
    string Auth,
    string DeviceLabel,
    string TimeZone,
    DateTime CreatedUtc,
    DateTime? LastSuccessUtc,
    DateTime? HeldSinceUtc,
    DateTime? HeldUntilUtc);

public enum PushSubscribeResult
{
    /// <summary>A new device for the user.</summary>
    Added,

    /// <summary>The browser was already subscribed for the user; its keys, label and time zone were refreshed.</summary>
    Updated
}

/// <summary>Push subscriptions and their quiet-hours hold. Every method that takes a user id touches only that user's rows.</summary>
public interface IPushSubscriptionRepository
{
    /// <summary>
    /// Stores the browser's subscription for the user. The endpoint is unique:
    /// a browser that subscribes again is updated, and one that was subscribed
    /// for another account (someone else signed in on the same browser before)
    /// moves to this user as a new device.
    /// </summary>
    Task<PushSubscribeResult> SaveAsync(Guid userId, NewPushSubscription subscription, CancellationToken cancellationToken = default);

    /// <summary>The user's devices, oldest first.</summary>
    Task<IReadOnlyList<PushDevice>> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Removes one of the user's devices. False when it is not theirs or already gone.</summary>
    Task<bool> RemoveAsync(Guid userId, Guid subscriptionId, CancellationToken cancellationToken = default);

    /// <summary>Removes the user's subscription with this endpoint (the browser unsubscribing itself). False when there was none.</summary>
    Task<bool> RemoveByEndpointAsync(Guid userId, string endpoint, CancellationToken cancellationToken = default);

    /// <summary>Deletes a subscription the push service says no longer exists (404 or 410).</summary>
    Task DeleteAsync(Guid subscriptionId, CancellationToken cancellationToken = default);

    /// <summary>Records a successful push: the last-success time, and no push held any more.</summary>
    Task MarkDeliveredAsync(Guid subscriptionId, DateTime deliveredUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Holds a push for quiet hours until <paramref name="releaseUtc"/>. A hold
    /// already in place keeps its earlier start, so the released push covers
    /// everything held since then.
    /// </summary>
    Task HoldAsync(Guid subscriptionId, DateTime heldSinceUtc, DateTime releaseUtc, CancellationToken cancellationToken = default);

    /// <summary>Drops a held push without sending it.</summary>
    Task ClearHoldAsync(Guid subscriptionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscriptions whose held push is due at <paramref name="nowUtc"/>: the
    /// release time has come, or the user has turned quiet hours off since.
    /// </summary>
    Task<IReadOnlyList<PushDevice>> GetDueHeldAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
}
