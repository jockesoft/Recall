namespace Recall.Web.Services.Push;

/// <summary>
/// A short name for the browser that subscribed, from its user agent:
/// "Chrome on Android", "Safari on iPhone". Only for the device list on
/// Profile, so the user can tell their devices apart; nothing decides on it.
/// </summary>
public static class DeviceLabel
{
    public const string Unknown = "Unknown browser";

    public static string FromUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return Unknown;

        var browser = Browser(userAgent);
        var system = System(userAgent);

        return (browser, system) switch
        {
            (null, null) => Unknown,
            (null, { } s) => $"Browser on {s}",
            ({ } b, null) => b,
            ({ } b, { } s) => $"{b} on {s}"
        };
    }

    // Order matters: Edge, Opera and Samsung Internet also say "Chrome", and
    // Chrome also says "Safari".
    private static string? Browser(string ua) =>
        Has(ua, "Edg/") || Has(ua, "EdgA/") || Has(ua, "EdgiOS/") ? "Edge"
        : Has(ua, "OPR/") || Has(ua, "Opera") ? "Opera"
        : Has(ua, "SamsungBrowser/") ? "Samsung Internet"
        : Has(ua, "Firefox/") || Has(ua, "FxiOS/") ? "Firefox"
        : Has(ua, "Chrome/") || Has(ua, "CriOS/") || Has(ua, "Chromium/") ? "Chrome"
        : Has(ua, "Safari/") || Has(ua, "AppleWebKit/") ? "Safari"
        : null;

    // A Home Screen web app on an iPad says "Macintosh" like a Mac does.
    private static string? System(string ua) =>
        Has(ua, "Android") ? "Android"
        : Has(ua, "iPhone") ? "iPhone"
        : Has(ua, "iPad") ? "iPad"
        : Has(ua, "CrOS") ? "ChromeOS"
        : Has(ua, "Windows") ? "Windows"
        : Has(ua, "Macintosh") || Has(ua, "Mac OS X") ? "Mac"
        : Has(ua, "Linux") ? "Linux"
        : null;

    private static bool Has(string ua, string token) => ua.Contains(token, StringComparison.OrdinalIgnoreCase);
}
