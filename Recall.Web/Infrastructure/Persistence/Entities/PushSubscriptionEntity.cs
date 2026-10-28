namespace Recall.Web.Infrastructure.Persistence.Entities;

/// <summary>
/// One browser on one device that has said yes to push notifications: where to
/// send them (<see cref="Endpoint"/>, an address at the browser's push
/// service) and the keys that encrypt them for that browser alone. Belongs to
/// a user; a browser that subscribes again for another user moves to them.
/// </summary>
public sealed class PushSubscriptionEntity : IHasAuditTimestamps
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public AppUserEntity User { get; set; } = null!;

    /// <summary>The push service URL for this browser. Unique: it identifies the subscription.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The browser's P-256 public key (base64url), for encrypting the payload.</summary>
    public string P256dh { get; set; } = string.Empty;

    /// <summary>The browser's authentication secret (base64url), for encrypting the payload.</summary>
    public string Auth { get; set; } = string.Empty;

    /// <summary>"Chrome on Android", "Safari on iPhone": from the user agent when it subscribed, for the device list.</summary>
    public string DeviceLabel { get; set; } = string.Empty;

    /// <summary>The device's IANA time zone as the browser reported it ("Europe/Stockholm"), for quiet hours.</summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>When a push to it last succeeded; null until one has. Subscriptions silent for long are pruned.</summary>
    public DateTime? LastSuccessUtc { get; set; }

    /// <summary>
    /// A push held back by quiet hours: notifications created from this moment
    /// on are summarised when it is released. Null when nothing is held.
    /// </summary>
    public DateTime? HeldSinceUtc { get; set; }

    /// <summary>When the held push is due: 08:00 on the device. Null when nothing is held.</summary>
    public DateTime? HeldUntilUtc { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
