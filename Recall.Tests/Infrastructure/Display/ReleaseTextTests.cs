using AwesomeAssertions;
using Recall.Web.Infrastructure.Display;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Infrastructure.Display;

/// <summary>
/// The text the server writes inside a release time's &lt;time&gt; element (what
/// shows without script); the browser rewrites only a timed one.
/// </summary>
[TestFixture]
public class ReleaseTextTests
{
    private static readonly DateOnly Today = new(2026, 10, 2);

    [Test]
    public void DateAndTime_Should_BeTheAirDate_AndTheSeriesOwnTime()
    {
        var goldRush = EpisodeRelease.MomentUtc(new DateOnly(2026, 10, 2), "20:00", "usa")!;

        ReleaseText.DateAndTime(goldRush, Today).Should().Be("Fri, Oct 2 · 8:00 PM ET");
        ReleaseText.TimeOnly(goldRush).Should().Be("8:00 PM ET");
    }

    [Test]
    public void DateAndTime_Should_NameTheZoneInEffectThatDay()
    {
        var summer = EpisodeRelease.MomentUtc(new DateOnly(2026, 6, 5), "20:00", "swe")!;
        var winter = EpisodeRelease.MomentUtc(new DateOnly(2026, 12, 4), "20:00", "swe")!;

        ReleaseText.TimeOnly(summer).Should().Be("8:00 PM CEST");
        ReleaseText.TimeOnly(winter).Should().Be("8:00 PM CET");
    }

    [Test]
    public void DateAndTime_Should_IncludeTheYear_OutsideTheCurrentOne()
    {
        var release = EpisodeRelease.MomentUtc(new DateOnly(2027, 1, 8), "21:00", "gbr")!;

        ReleaseText.DateAndTime(release, Today).Should().Be("Jan 8, 2027 · 9:00 PM GMT");
    }

    [TestCase("usa", null)]
    [TestCase(null, "20:00")]
    [TestCase(null, null)]
    public void AFallbackMoment_Should_ShowItsDateOnly_NeverATime(string? country, string? airsTime)
    {
        var release = EpisodeRelease.MomentUtc(new DateOnly(2026, 10, 2), airsTime, country)!;

        release.IsTimeKnown.Should().BeFalse();
        ReleaseText.DateAndTime(release, Today).Should().Be("Fri, Oct 2");
        ReleaseText.TimeOnly(release).Should().BeNull("under Today or Tomorrow a date-only card shows nothing");
        release.DateTimeAttribute.Should().Be("2026-10-02", "the late fallback moment is never put in the markup");
    }
}
