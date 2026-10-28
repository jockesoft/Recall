using System.Buffers.Text;

namespace Recall.Web.Services.Push;

/// <summary>
/// Checks what a browser posts when it subscribes, before it is stored. The
/// endpoint is a URL the server will later send requests to, so only the push
/// services of the browsers people use are accepted: anything else could make
/// the server call an address of the sender's choosing.
/// </summary>
public static class PushSubscriptionInput
{
    /// <summary>
    /// Hosts (and their subdomains) of the push services: Google's (Chrome,
    /// Android, Samsung Internet, Opera, Brave), Apple's (Safari on iPhone, iPad
    /// and Mac), Mozilla's (Firefox) and Microsoft's (Edge).
    /// </summary>
    public static readonly IReadOnlyList<string> KnownServices =
    [
        "fcm.googleapis.com",
        "push.apple.com",
        "push.services.mozilla.com",
        "notify.windows.com"
    ];

    public enum Problem
    {
        None,

        /// <summary>The endpoint is not an https address at a known push service.</summary>
        UnknownService,

        /// <summary>The keys are missing or not the sizes Web Push uses.</summary>
        BadKeys
    }

    public static Problem Check(string? endpoint, string? p256dh, string? auth)
    {
        if (!IsKnownService(endpoint))
            return Problem.UnknownService;

        // An uncompressed P-256 point and a 16-byte secret, as the browser's
        // PushSubscription.toJSON() gives them.
        return DecodesTo(p256dh, 65) && DecodesTo(auth, 16) ? Problem.None : Problem.BadKeys;
    }

    public static bool IsKnownService(string? endpoint) =>
        endpoint is { Length: > 0 and <= 2048 }
        && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && KnownServices.Any(host =>
            uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));

    private static bool DecodesTo(string? value, int length)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            return false;

        try
        {
            return Base64Url.DecodeFromChars(value.Trim().TrimEnd('=')).Length == length;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
