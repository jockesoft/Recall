namespace Recall.Web.Services.Metrics;

/// <summary>
/// Every key a site metric can be counted under. A key names what happened
/// and nothing about who it happened to: a page type and whether the visitor
/// was signed in, a step of signing in. <see cref="SiteMetricsBuffer"/>
/// refuses any other key, so nothing taken from a request (an address, a
/// user agent, a path) can reach <c>site_metric_daily</c>.
/// </summary>
public static class SiteMetricKeys
{
    /// <summary>A page view from a known crawler (<see cref="CrawlerUserAgents"/>), whatever the page. Not counted under any page type.</summary>
    public const string CrawlerView = "view.crawler";

    /// <summary>A sign-in email that was actually queued: not a tripped bot trap, a throttled address or one off the allowlist.</summary>
    public const string LinkSent = "signin.link-sent";

    /// <summary>A sign-in by the link in the email.</summary>
    public const string LinkRedeemed = "signin.link-redeemed";

    /// <summary>A sign-in by the code in the email.</summary>
    public const string CodeRedeemed = "signin.code-redeemed";

    /// <summary>An account's first sign-in (<c>app_user.first_sign_in_utc</c> was empty): a new account that is really used.</summary>
    public const string NewAccount = "signin.new-account";

    /// <summary>"view.series.anon", "view.login.user".</summary>
    public static string PageView(SitePage page, bool signedIn) =>
        $"view.{Slug(page)}.{(signedIn ? "user" : "anon")}";

    /// <summary>The page types a view is counted under, in the order the admin page lists them.</summary>
    public static IReadOnlyList<SitePage> Pages { get; } = Enum.GetValues<SitePage>();

    /// <summary>Every key there is.</summary>
    public static IReadOnlySet<string> All { get; } = BuildAll();

    public static bool IsKnown(string key) => All.Contains(key);

    private static HashSet<string> BuildAll()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal)
        {
            CrawlerView, LinkSent, LinkRedeemed, CodeRedeemed, NewAccount
        };

        foreach (var page in Pages)
        {
            keys.Add(PageView(page, signedIn: false));
            keys.Add(PageView(page, signedIn: true));
        }

        return keys;
    }

    private static string Slug(SitePage page) => page.ToString().ToLowerInvariant();
}

/// <summary>What kind of page a view was of, for <see cref="SiteMetricKeys.PageView"/>.</summary>
public enum SitePage
{
    Landing,
    Series,
    Movie,
    Episode,
    Discover,
    Search,
    Login,
    Other
}
