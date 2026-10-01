namespace Recall.Web.Infrastructure.Hosting;

/// <summary>
/// Bound from the <c>TrustedProxies</c> configuration section. Decides whose
/// <c>X-Forwarded-*</c> headers the app believes — only a request whose
/// immediate sender is listed here gets its forwarded client IP, scheme and
/// host applied. Everything else keeps its real connection address, which is
/// what the per-IP rate limiters key on.
/// Prod: TrustedProxies__Networks__0=172.18.0.0/16  (in .env.prod)
/// </summary>
public sealed class TrustedProxyOptions
{
    public const string SectionName = "TrustedProxies";

    /// <summary>
    /// Used when neither <see cref="Addresses"/> nor <see cref="Networks"/> is
    /// configured: loopback plus the private ranges. That covers a reverse proxy
    /// on the same host or the same Docker network (the app sees it as the
    /// bridge gateway, a 172.16.0.0/12 address) without trusting a caller that
    /// reaches the app's port directly from a public address.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultNetworks =
    [
        "127.0.0.0/8",
        "::1/128",
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "fc00::/7"
    ];

    /// <summary>Individual proxy IP addresses to trust.</summary>
    public string[] Addresses { get; set; } = [];

    /// <summary>Proxy networks to trust, in CIDR notation.</summary>
    public string[] Networks { get; set; } = [];

    /// <summary>
    /// How many <c>X-Forwarded-For</c> entries to walk, right to left. 1 fits a
    /// single proxy; a chain (e.g. a CDN in front of the proxy) needs one per
    /// hop, and every hop but the first must itself be trusted above.
    /// </summary>
    public int ForwardLimit { get; set; } = 1;
}
