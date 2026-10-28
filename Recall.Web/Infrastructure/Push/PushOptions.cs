namespace Recall.Web.Infrastructure.Push;

/// <summary>
/// Bound from the <c>Push</c> configuration section: Web Push notifications
/// and the VAPID key pair that signs them. Off by default; switching it on
/// without a valid key pair and contact address fails startup
/// (<c>AddPush</c>). The keys are never in the repository: user secrets in
/// development, <c>Push__*</c> variables in <c>.env.prod</c>.
/// </summary>
public sealed class PushOptions
{
    public const string SectionName = "Push";

    /// <summary>Whether push exists on this installation. While false, Profile and Admin show nothing about it and nothing is sent.</summary>
    public bool Enabled { get; set; }

    /// <summary>The VAPID public key: an uncompressed P-256 point, base64url (87 characters). Browsers get it to subscribe.</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>The VAPID private key: the P-256 scalar, base64url (43 characters). Signs every push; never leaves the server.</summary>
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>Who the push services can contact about this sender: a <c>mailto:</c> address.</summary>
    public string Subject { get; set; } = string.Empty;
}
