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
    public void Build_Should_GiveTheFunnel_WithEachStepsShareOfTheOneBefore()
    {
        var report = SiteMetricsReport.Build(
        [
            C(0, "view.series.anon", 600), C(10, "view.episode.anon", 400),
            C(0, "view.series.user", 999),               // signed in: not part of the funnel
            C(0, "action.clicked.add-series", 40), C(10, "action.clicked.like-movie", 10),
            C(0, "view.login.anon", 25),
            C(0, "signin.link-sent", 20),
            C(0, "signin.link-redeemed", 12), C(0, "signin.code-redeemed", 3),
            C(0, "signin.new-account", 9)
        ], Today);

        report.Funnel.Select(s => s.Last30).Should().Equal(1000, 50, 25, 20, 15, 9);
        report.Funnel.Select(s => s.Last7).Should().Equal(600, 40, 25, 20, 15, 9);

        report.Funnel[0].RateOfPrevious30.Should().BeNull("the first step has nothing before it");
        report.Funnel[1].RateOfPrevious30.Should().BeApproximately(0.05, 1e-9);
        report.Funnel[2].RateOfPrevious30.Should().BeApproximately(0.5, 1e-9);
        report.Funnel[5].RateOfPrevious30.Should().BeApproximately(0.6, 1e-9);
        report.Funnel[1].RateOfPrevious7.Should().BeApproximately(40.0 / 600, 1e-9);
    }

    [Test]
    public void Build_Should_GiveNoRate_WhereTheStepBeforeHadNothing()
    {
        var report = SiteMetricsReport.Build([C(0, "view.login.anon", 5)], Today);

        report.Funnel[2].Last30.Should().Be(5);
        report.Funnel[2].RateOfPrevious30.Should().BeNull("nobody pressed an action, so there is no share to give");
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
