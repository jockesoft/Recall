using AwesomeAssertions;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Services;

[TestFixture]
public sealed class WatchTimeSummaryTests
{
    [TestCase(0, "0 minutes")]
    [TestCase(1, "1 minute")]
    [TestCase(59, "59 minutes")]
    [TestCase(60, "1 hour")]
    [TestCase(90, "1 hour, 30 minutes")]
    [TestCase(60 * 24, "1 day")]
    [TestCase(60 * (24 * 5 + 7) + 12, "5 days, 7 hours")]          // minutes dropped: two units only
    [TestCase(60 * 24 * 2 + 30, "2 days")]                         // not "2 days, 30 minutes": the next unit down is hours
    [TestCase(60 * 24 * 33, "1 month, 3 days")]
    [TestCase(60 * 24 * 360, "1 year")]
    // 1y 9mo 26d 20h  = ((1*12+9)*30 + 26) * 24 + 20  hours  -> *60 minutes
    [TestCase((((1 * 12 + 9) * 30 + 26) * 24 + 20) * 60, "1 year, 9 months")]
    public void Readable_Should_UseTheTwoLargestUnits(int totalMinutes, string expected)
    {
        new WatchTimeSummary(totalMinutes, EpisodeCount: 1).Readable.Should().Be(expected);
    }

    [TestCase(815, 3, "815 episodes and 3 movies")]
    [TestCase(1, 1, "1 episode and 1 movie")]
    [TestCase(12, 0, "12 episodes")]
    [TestCase(0, 2, "2 movies")]
    [TestCase(0, 0, "")]
    public void Across_Should_NameWhatWasWatched_LeavingOutAKindAtZero(int episodes, int movies, string expected)
    {
        new WatchTimeSummary(100, episodes, movies).Across.Should().Be(expected);
    }

    [Test]
    public void HasData_IsFalse_WhenZero()
    {
        WatchTimeSummary.Empty.HasData.Should().BeFalse();
        new WatchTimeSummary(1, 1).HasData.Should().BeTrue();
    }
}
