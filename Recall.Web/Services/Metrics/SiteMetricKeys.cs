using Recall.Web.Services.PendingActions;

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

    // ---- the two paths of the sign-up funnel ----------------------------------------
    // The action path follows one press (the pending-action cookie's stage counts each
    // step once per press); the direct path is everything without a press. The keys
    // above still count every view, email and sign-in, whichever path.

    /// <summary>A visitor's view of the sign-in page that showed what their press will do: once per press.</summary>
    public const string SignInPageWithAction = "signin.page.with-action";

    /// <summary>A visitor's view of the sign-in page without a pending action.</summary>
    public const string SignInPageDirect = "signin.page.direct";

    /// <summary>The first sign-in email sent for a press.</summary>
    public const string LinkSentWithAction = "signin.link-sent.with-action";

    /// <summary>A sign-in email sent without a pending action.</summary>
    public const string LinkSentDirect = "signin.link-sent.direct";

    /// <summary>A sign-in by an email that was asked for with a pending action (<c>login_token.with_pending_action</c>).</summary>
    public const string SignedInWithAction = "signin.signed-in.with-action";

    /// <summary>A sign-in by an email that was asked for without one.</summary>
    public const string SignedInDirect = "signin.signed-in.direct";

    /// <summary>A new account's first sign-in, on the action path.</summary>
    public const string NewAccountWithAction = "signin.new-account.with-action";

    /// <summary>A new account's first sign-in, on the direct path.</summary>
    public const string NewAccountDirect = "signin.new-account.direct";

    /// <summary>The pending action done (or found already done) after an action-path sign-in.</summary>
    public const string ActionDone = "signin.action-done";

    /// <summary>"action.clicked.add-series": a signed-out visitor pressed an action on a Details page.</summary>
    public static string ActionClicked(PendingActionKind kind) => "action.clicked." + PendingAction.Slug(kind);

    /// <summary>"action.completed.add-series": the pending action was done after the visitor signed in.</summary>
    public static string ActionCompleted(PendingActionKind kind) => "action.completed." + PendingAction.Slug(kind);

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
            CrawlerView, LinkSent, LinkRedeemed, CodeRedeemed, NewAccount,
            SignInPageWithAction, SignInPageDirect, LinkSentWithAction, LinkSentDirect,
            SignedInWithAction, SignedInDirect, NewAccountWithAction, NewAccountDirect, ActionDone
        };

        foreach (var kind in Enum.GetValues<PendingActionKind>())
        {
            keys.Add(ActionClicked(kind));
            keys.Add(ActionCompleted(kind));
        }

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
