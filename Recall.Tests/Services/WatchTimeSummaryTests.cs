using AwesomeAssertions;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Services;

[TestFixture]
public sealed class WatchTimeSummaryTests
{
    [TestCase(0, "0h")]
    [TestCase(59, "0h")]           // sub-hour rounds down
    [TestCase(60, "1h")]
    [TestCase(90, "1h")]
    [TestCase(60 * 24, "1d 0h")]
    [TestCase(60 * 24 * 30, "1mo 0d 0h")]
    [TestCase(60 * 24 * 360, "1y 0mo 0d 0h")]
    // 1y 9mo 26d 20h  = ((1*12+9)*30 + 26) * 24 + 20  hours  -> *60 minutes
    [TestCase((((1 * 12 + 9) * 30 + 26) * 24 + 20) * 60, "1y 9mo 26d 20h")]
    public void Formatted_BreaksDownAsExpected(int totalMinutes, string expected)
    {
        new WatchTimeSummary(totalMinutes, EpisodeCount: 1).Formatted.Should().Be(expected);
    }

    [Test]
    public void HasData_IsFalse_WhenZero()
    {
        WatchTimeSummary.Empty.HasData.Should().BeFalse();
        new WatchTimeSummary(1, 1).HasData.Should().BeTrue();
    }
}
