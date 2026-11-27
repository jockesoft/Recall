namespace Recall.Web.Services.Metrics;

/// <summary>
/// What /Admin/Metrics shows, built from the daily counts by the pure
/// <see cref="Build"/>: the two paths of the sign-up funnel with their
/// conversion rates, the last 30 days one by one, the 7- and 30-day totals
/// of every key, and each signed-out action's presses and completions.
/// </summary>
public sealed record SiteMetricsReport(
    DateOnly Today,
    IReadOnlyList<SiteMetricsDay> Days,
    IReadOnlyList<SiteMetricsTotalGroup> Groups,
    IReadOnlyList<SiteFunnelStep> ActionFunnel,
    IReadOnlyList<SiteFunnelStep> DirectFunnel,
    IReadOnlyList<SiteActionTotal> Actions)
{
    /// <summary>How many days the table and the longer totals cover, today included.</summary>
    public const int LongWindow = 30;

    /// <summary>The shorter totals, today included.</summary>
    public const int ShortWindow = 7;

    /// <summary>A key's prefix for "a signed-out visitor pressed an action" (Part B's pending actions), by kind.</summary>
    public const string ActionClickedPrefix = "action.clicked.";

    /// <summary>A key's prefix for "a pending action was done after sign-in", by kind.</summary>
    public const string ActionCompletedPrefix = "action.completed.";

    public static SiteMetricsReport Build(IEnumerable<SiteMetricCount> counts, DateOnly today)
    {
        var first = today.AddDays(-(LongWindow - 1));
        var byDay = new Dictionary<DateOnly, Dictionary<string, long>>();

        foreach (var count in counts)
        {
            if (count.Date < first || count.Date > today)
                continue;

            if (!byDay.TryGetValue(count.Date, out var keys))
                byDay[count.Date] = keys = new Dictionary<string, long>(StringComparer.Ordinal);

            keys[count.Key] = keys.GetValueOrDefault(count.Key) + count.Count;
        }

        var days = Enumerable.Range(0, LongWindow)
            .Select(offset => today.AddDays(-offset))
            .Select(date => Day(date, byDay.GetValueOrDefault(date) ?? Empty))
            .ToList();

        long Total(int window, Func<IReadOnlyDictionary<string, long>, long> select) =>
            Enumerable.Range(0, window)
                .Select(offset => byDay.GetValueOrDefault(today.AddDays(-offset)) ?? Empty)
                .Sum(keys => select(keys));

        SiteMetricsTotal Row(string label, Func<IReadOnlyDictionary<string, long>, long> select) =>
            new(label, Total(ShortWindow, select), Total(LongWindow, select));

        SiteMetricsTotal KeyRow(string label, string key) => Row(label, keys => keys.GetValueOrDefault(key));

        var groups = new List<SiteMetricsTotalGroup>
        {
            new("Page views by visitors", SiteMetricKeys.Pages
                .Select(page => KeyRow(PageLabel(page), SiteMetricKeys.PageView(page, signedIn: false))).ToList()),
            new("Page views by signed-in users", SiteMetricKeys.Pages
                .Select(page => KeyRow(PageLabel(page), SiteMetricKeys.PageView(page, signedIn: true))).ToList()),
            new("Crawlers", [KeyRow("Page views by known crawlers", SiteMetricKeys.CrawlerView)]),
            new("Signing in",
            [
                KeyRow("Sign-in emails sent", SiteMetricKeys.LinkSent),
                KeyRow("Signed in with the link", SiteMetricKeys.LinkRedeemed),
                KeyRow("Signed in with the code", SiteMetricKeys.CodeRedeemed),
                KeyRow("New accounts (first sign-in)", SiteMetricKeys.NewAccount)
            ])
        };

        groups.Add(new SiteMetricsTotalGroup("Sign-up paths",
        [
            KeyRow("Sign-in page views with a pending action (once per press)", SiteMetricKeys.SignInPageWithAction),
            KeyRow("Sign-in page views without one", SiteMetricKeys.SignInPageDirect),
            KeyRow("Emails sent with a pending action (once per press)", SiteMetricKeys.LinkSentWithAction),
            KeyRow("Emails sent without one", SiteMetricKeys.LinkSentDirect),
            KeyRow("Sign-ins with a pending action", SiteMetricKeys.SignedInWithAction),
            KeyRow("Sign-ins without one", SiteMetricKeys.SignedInDirect),
            KeyRow("New accounts with a pending action", SiteMetricKeys.NewAccountWithAction),
            KeyRow("New accounts without one", SiteMetricKeys.NewAccountDirect),
            KeyRow("Pending actions done after an action-path sign-in", SiteMetricKeys.ActionDone)
        ]));

        long Key(IReadOnlyDictionary<string, long> keys, string key) => keys.GetValueOrDefault(key);

        var actionFunnel = Funnel(Total,
        [
            ("Visitors' views of Series, Movie and Episode pages", DetailsViews),
            ("Actions pressed", ActionClicks),
            ("Sign-in page saying what the press will do", keys => Key(keys, SiteMetricKeys.SignInPageWithAction)),
            ("Sign-in emails sent", keys => Key(keys, SiteMetricKeys.LinkSentWithAction)),
            ("Signed in", keys => Key(keys, SiteMetricKeys.SignedInWithAction)),
            ("Action done", keys => Key(keys, SiteMetricKeys.ActionDone))
        ]);

        var directFunnel = Funnel(Total,
        [
            ("Sign-in page without a pending action", keys => Key(keys, SiteMetricKeys.SignInPageDirect)),
            ("Sign-in emails sent", keys => Key(keys, SiteMetricKeys.LinkSentDirect)),
            ("Signed in", keys => Key(keys, SiteMetricKeys.SignedInDirect)),
            ("New accounts", keys => Key(keys, SiteMetricKeys.NewAccountDirect))
        ]);

        var actions = ActionKinds(byDay.Values)
            .Select(kind => new SiteActionTotal(
                ActionLabels.For(kind),
                Total(ShortWindow, keys => Key(keys, ActionClickedPrefix + kind)),
                Total(ShortWindow, keys => Key(keys, ActionCompletedPrefix + kind)),
                Total(LongWindow, keys => Key(keys, ActionClickedPrefix + kind)),
                Total(LongWindow, keys => Key(keys, ActionCompletedPrefix + kind))))
            .ToList();

        return new SiteMetricsReport(today, days, groups, actionFunnel, directFunnel, actions);
    }

    /// <summary>
    /// The steps with each one's share of the step before it, never above 1:
    /// the keys are counted so that a step follows from the one before, but a
    /// day ends at midnight UTC between them (and a press, a view and an email
    /// can come from different days), so a window can still catch a few more of
    /// a later step than of the one before it.
    /// </summary>
    private static List<SiteFunnelStep> Funnel(
        Func<int, Func<IReadOnlyDictionary<string, long>, long>, long> total,
        (string Label, Func<IReadOnlyDictionary<string, long>, long> Select)[] steps)
    {
        var funnel = new List<SiteFunnelStep>();
        foreach (var (label, select) in steps)
        {
            var step = new SiteFunnelStep(label, total(ShortWindow, select), total(LongWindow, select));
            if (funnel.Count > 0)
            {
                var previous = funnel[^1];
                step = step with
                {
                    RateOfPrevious7 = Rate(step.Last7, previous.Last7),
                    RateOfPrevious30 = Rate(step.Last30, previous.Last30)
                };
            }

            funnel.Add(step);
        }

        return funnel;
    }

    /// <summary>A share as a fraction (0.25) and never more than 1, or null when there was nothing to take a share of.</summary>
    public static double? Rate(long part, long whole) => whole > 0 ? Math.Min(1.0, (double)part / whole) : null;


    public static string PageLabel(SitePage page) => page switch
    {
        SitePage.Landing => "Landing page",
        SitePage.Series => "Series pages",
        SitePage.Movie => "Movie pages",
        SitePage.Episode => "Episode pages",
        SitePage.Discover => "Discover",
        SitePage.Search => "Search",
        SitePage.Login => "Sign-in page",
        _ => "Other pages"
    };

    private static readonly IReadOnlyDictionary<string, long> Empty = new Dictionary<string, long>();

    private static SiteMetricsDay Day(DateOnly date, IReadOnlyDictionary<string, long> keys) => new(
        date,
        Visitors: SiteMetricKeys.Pages.Sum(page => keys.GetValueOrDefault(SiteMetricKeys.PageView(page, signedIn: false))),
        SignedInViews: SiteMetricKeys.Pages.Sum(page => keys.GetValueOrDefault(SiteMetricKeys.PageView(page, signedIn: true))),
        Crawlers: keys.GetValueOrDefault(SiteMetricKeys.CrawlerView),
        DetailsViews: DetailsViews(keys),
        ActionClicks: ActionClicks(keys),
        SignInPage: keys.GetValueOrDefault(SiteMetricKeys.PageView(SitePage.Login, signedIn: false)),
        LinksSent: keys.GetValueOrDefault(SiteMetricKeys.LinkSent),
        SignIns: SignIns(keys),
        NewAccounts: keys.GetValueOrDefault(SiteMetricKeys.NewAccount),
        ActionsDone: Prefixed(keys, ActionCompletedPrefix));

    private static long DetailsViews(IReadOnlyDictionary<string, long> keys) =>
        keys.GetValueOrDefault(SiteMetricKeys.PageView(SitePage.Series, signedIn: false))
        + keys.GetValueOrDefault(SiteMetricKeys.PageView(SitePage.Movie, signedIn: false))
        + keys.GetValueOrDefault(SiteMetricKeys.PageView(SitePage.Episode, signedIn: false));

    private static long ActionClicks(IReadOnlyDictionary<string, long> keys) => Prefixed(keys, ActionClickedPrefix);

    private static long SignIns(IReadOnlyDictionary<string, long> keys) =>
        keys.GetValueOrDefault(SiteMetricKeys.LinkRedeemed) + keys.GetValueOrDefault(SiteMetricKeys.CodeRedeemed);

    private static long Prefixed(IReadOnlyDictionary<string, long> keys, string prefix) =>
        keys.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(entry => entry.Value);

    /// <summary>The action kinds known to the keys, then any other that was counted, in a stable order.</summary>
    private static IEnumerable<string> ActionKinds(IEnumerable<Dictionary<string, long>> days)
    {
        var known = ActionLabels.Kinds;
        var counted = days.SelectMany(keys => keys.Keys)
            .Select(key => key.StartsWith(ActionClickedPrefix, StringComparison.Ordinal) ? key[ActionClickedPrefix.Length..]
                : key.StartsWith(ActionCompletedPrefix, StringComparison.Ordinal) ? key[ActionCompletedPrefix.Length..]
                : null)
            .OfType<string>()
            .Where(kind => !known.Contains(kind))
            .Distinct()
            .Order(StringComparer.Ordinal);

        return known.Concat(counted);
    }
}

/// <summary>One day of the table: views, then each step of signing up.</summary>
public sealed record SiteMetricsDay(
    DateOnly Date,
    long Visitors,
    long SignedInViews,
    long Crawlers,
    long DetailsViews,
    long ActionClicks,
    long SignInPage,
    long LinksSent,
    long SignIns,
    long NewAccounts,
    long ActionsDone);

/// <summary>One signed-out action: how often it was pressed, and done after sign-in, over 7 and 30 days.</summary>
public sealed record SiteActionTotal(string Label, long Pressed7, long Done7, long Pressed30, long Done30);

/// <summary>A heading and its rows of totals.</summary>
public sealed record SiteMetricsTotalGroup(string Heading, IReadOnlyList<SiteMetricsTotal> Rows);

/// <summary>One key's (or one sum's) totals over the last 7 and 30 days.</summary>
public sealed record SiteMetricsTotal(string Label, long Last7, long Last30);

/// <summary>A step of the funnel, with its share of the step before (null for the first, or when the step before had none).</summary>
public sealed record SiteFunnelStep(string Label, long Last7, long Last30)
{
    public double? RateOfPrevious7 { get; init; }
    public double? RateOfPrevious30 { get; init; }
}
