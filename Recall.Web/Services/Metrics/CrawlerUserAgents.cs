namespace Recall.Web.Services.Metrics;

/// <summary>
/// Known crawlers, by a word in their user agent. A view from one is counted
/// as <see cref="SiteMetricKeys.CrawlerView"/> and under no page type, so the
/// funnel counts people. The user agent is only looked at, never kept.
/// Deliberately short: the search engines and the big link-preview and AI
/// crawlers that visit public pages. Something not on the list counts as a
/// visitor.
/// </summary>
public static class CrawlerUserAgents
{
    public static IReadOnlyList<string> Tokens { get; } =
    [
        "Googlebot", "Google-InspectionTool", "Storebot-Google", "AdsBot-Google", "Mediapartners-Google",
        "bingbot", "BingPreview", "DuckDuckBot", "YandexBot", "Baiduspider", "Applebot", "Slurp",
        "facebookexternalhit", "Twitterbot", "LinkedInBot", "Slackbot", "Discordbot", "WhatsApp", "TelegramBot",
        "AhrefsBot", "SemrushBot", "MJ12bot", "DotBot", "PetalBot", "SeznamBot", "Bytespider",
        "GPTBot", "ChatGPT-User", "OAI-SearchBot", "ClaudeBot", "Claude-User", "PerplexityBot", "CCBot", "Amazonbot"
    ];

    public static bool IsCrawler(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return false;

        foreach (var token in Tokens)
        {
            if (userAgent.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
