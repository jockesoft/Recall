namespace Recall.Web.Services.Push;

/// <summary>Where one push goes: a browser's subscription.</summary>
public sealed record PushTarget(string Endpoint, string P256dh, string Auth);

/// <summary>What the push service said.</summary>
public enum PushDelivery
{
    /// <summary>Accepted for delivery.</summary>
    Delivered,

    /// <summary>The subscription no longer exists (404 or 410): delete it.</summary>
    Gone,

    /// <summary>Anything else: an error status, a timeout, no connection. The subscription is kept.</summary>
    Failed
}

/// <summary>
/// Sends one encrypted, VAPID-signed push to one browser. Never throws for a
/// failed delivery: the outcome is the return value. Mocked in tests; no test
/// sends a real push.
/// </summary>
public interface IPushSender
{
    Task<PushDelivery> SendAsync(PushTarget target, PushPayload payload, string topic, CancellationToken cancellationToken = default);
}
