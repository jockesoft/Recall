using AwesomeAssertions;
using Recall.Web.Services.Metrics;

namespace Recall.Tests.Services.Metrics;

[TestFixture]
public sealed class SiteMetricsReportTests
{
    private static readonly DateOnly Today = new(2026, 11, 27);

    private static SiteMetricCount C(int daysAgo, string key, long count) => new(Today.AddDays(-daysAgo), key, count);

    [Test]
    public void Build_Should_ListThirtyDays_NewestFirst_WithEachDaysFigures()
    {
        var report = SiteMetricsReport.Build(
        [
            C(0, "view.series.anon", 10), C(0, "view.movie.anon", 4), C(0, "view.login.anon", 3), C(0, "view.dashboard.user", 0),
            C(0, "view.search.user", 7), C(0, "view.crawler", 50), C(0, "signin.link-sent", 2),
            C(0, "signin.link-redeemed", 1), C(0, "signin.code-redeemed", 1), C(0, "signin.new-account", 1),
            C(3, "view.landing.anon", 9)
        ], Today);

        report.Days.Should().HaveCount(30);
        report.Days[0].Date.Should().Be(Today);
        report.Days[^1].Date.Should().Be(Today.AddDays(-29));

        var today = report.Days[0];
        today.Visitors.Should().Be(17, "series, movie and sign-in views by visitors; crawlers are not visitors");
        today.SignedInViews.Should().Be(7);
        today.Crawlers.Should().Be(50);
        today.DetailsViews.Should().Be(14);
        today.SignInPage.Should().Be(3);
        today.LinksSent.Should().Be(2);
        today.SignIns.Should().Be(2, "by the link and by the code");
        today.NewAccounts.Should().Be(1);
        report.Days[3].Visitors.Should().Be(9);
    }

    [Test]
    public void Build_Should_TotalTheLast7AndTheLast30Days_AndLeaveOutOlderOnes()
    {
        var report = SiteMetricsReport.Build(
        [
            C(0, "signin.link-sent", 1), C(6, "signin.link-sent", 2), C(7, "signin.link-sent", 4),
            C(29, "signin.link-sent", 8), C(30, "signin.link-sent", 16), C(-1, "signin.link-sent", 32)
        ], Today);

        var sent = report.Groups.Single(g => g.Heading == "Signing in").Rows.Single(r => r.Label == "Sign-in emails sent");
        sent.Last7.Should().Be(3, "today and the six days before it");
        sent.Last30.Should().Be(15, "today and the 29 days before it; day 30 and tomorrow are outside");
    }

    [Test]
    public void Build_Should_GiveTheActionPath_FromDetailsViewsToActionsDone()
    {
        var report = SiteMetricsReport.Build(
        [
            C(0, "view.series.anon", 600), C(10, "view.episode.anon", 400),
            C(0, "view.series.user", 999),               // signed in: not part of the funnel
            C(0, "action.clicked.add-series", 40), C(10, "action.clicked.like-movie", 10),
            C(0, "signin.page.with-action", 25),
            C(0, "signin.link-sent.with-action", 20),
            C(0, "signin.signed-in.with-action", 15),
            C(0, "signin.action-done", 9),
            C(0, "signin.page.direct", 500), C(0, "signin.link-sent.direct", 100)   // the other path
        ], Today);

        report.ActionFunnel.Select(s => s.Last30).Should().Equal(1000, 50, 25, 20, 15, 9);
        report.ActionFunnel.Select(s => s.Last7).Should().Equal(600, 40, 25, 20, 15, 9);

        report.ActionFunnel[0].RateOfPrevious30.Should().BeNull("the first step has nothing before it");
        report.ActionFunnel[1].RateOfPrevious30.Should().BeApproximately(0.05, 1e-9);
        report.ActionFunnel[2].RateOfPrevious30.Should().BeApproximately(0.5, 1e-9);
        report.ActionFunnel[5].RateOfPrevious30.Should().BeApproximately(0.6, 1e-9);
        report.ActionFunnel[1].RateOfPrevious7.Should().BeApproximately(40.0 / 600, 1e-9);
    }

    [Test]
    public void Build_Should_GiveTheDirectPath_FromTheSignInPageToNewAccounts()
    {
        var report = SiteMetricsReport.Build(
        [
            C(0, "signin.page.direct", 200), C(0, "signin.link-sent.direct", 50),
            C(0, "signin.signed-in.direct", 40), C(0, "signin.new-account.direct", 10),
            C(0, "signin.new-account.with-action", 99)   // the other path
        ], Today);

        report.DirectFunnel.Select(s => (s.Label, s.Last30)).Should().Equal(
            ("Sign-in page without a pending action", 200L), ("Sign-in emails sent", 50L), ("Signed in", 40L), ("New accounts", 10L));
        report.DirectFunnel.Select(s => s.RateOfPrevious30).Should().Equal(null, 0.25, 0.8, 0.25);
    }

    [Test]
    public void NoStep_Should_ShowMoreThanAllOfTheStepBefore()
    {
        // A sign-in just after midnight UTC from an email sent just before it, and a
        // second email after the first one expired: a window can catch more of a step.
        var report = SiteMetricsReport.Build(
        [
            C(6, "signin.page.direct", 2), C(6, "signin.link-sent.direct", 3), C(6, "signin.signed-in.direct", 3),
            C(0, "action.clicked.add-series", 1), C(0, "signin.page.with-action", 1), C(0, "signin.link-sent.with-action", 1),
            C(0, "signin.signed-in.with-action", 2), C(0, "signin.action-done", 2)
        ], Today);

        report.DirectFunnel.Concat(report.ActionFunnel)
            .SelectMany(s => new[] { s.RateOfPrevious7, s.RateOfPrevious30 })
            .OfType<double>().Should().OnlyContain(rate => rate <= 1.0);
        report.DirectFunnel[1].RateOfPrevious7.Should().Be(1.0);
        report.DirectFunnel[1].Last7.Should().Be(3, "the counts themselves are as counted");
    }

    [Test]
    public void Build_Should_GiveNoRate_WhereTheStepBeforeHadNothing()
    {
        var report = SiteMetricsReport.Build([C(0, "signin.page.with-action", 5)], Today);

        report.ActionFunnel[2].Last30.Should().Be(5);
        report.ActionFunnel[2].RateOfPrevious30.Should().BeNull("nobody pressed an action, so there is no share to give");
    }

    [Test]
    public void Build_Should_ListEveryAction_OnceWithItsPressesAndCompletions()
    {
        var report = SiteMetricsReport.Build(
        [
            C(0, "action.clicked.add-series", 4), C(20, "action.clicked.add-series", 6),
            C(0, "action.completed.add-series", 1), C(20, "action.completed.add-series", 2),
            C(3, "action.clicked.rate-episode", 1)
        ], Today);

        report.Actions.Should().HaveCount(Enum.GetValues<Recall.Web.Services.PendingActions.PendingActionKind>().Length);
        report.Actions[0].Should().Be(new SiteActionTotal("Add a series to the library", 4, 1, 10, 3));
        report.Actions.Single(a => a.Label == "Rate an episode").Should().Be(new SiteActionTotal("Rate an episode", 1, 0, 1, 0));
        report.Groups.Should().NotContain(g => g.Heading == "Actions by signed-out visitors", "the actions are a table of their own");
    }

    [Test]
    public void Build_Should_TotalEveryPageType_ForVisitorsAndSignedInUsersApart()
    {
        var report = SiteMetricsReport.Build([C(1, "view.discover.user", 4), C(2, "view.discover.anon", 1)], Today);

        report.Groups.Single(g => g.Heading == "Page views by signed-in users").Rows.Single(r => r.Label == "Discover").Last7.Should().Be(4);
        report.Groups.Single(g => g.Heading == "Page views by visitors").Rows.Single(r => r.Label == "Discover").Last7.Should().Be(1);
        report.Groups.Single(g => g.Heading == "Page views by visitors").Rows.Should().HaveCount(SiteMetricKeys.Pages.Count);
    }
}
