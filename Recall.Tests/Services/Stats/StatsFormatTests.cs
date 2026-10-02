using AwesomeAssertions;
using Recall.Web.Infrastructure.Display;
using Recall.Web.Services.Stats;

namespace Recall.Tests.Services.Stats;

[TestFixture]
public sealed class StatsFormatTests
{
    [TestCase(0, "0 min")]
    [TestCase(45, "45 min")]
    [TestCase(60, "1 h")]
    [TestCase(580, "9 h 40 min")]
    [TestCase(99 * 60 + 59, "99 h 59 min")]
    [TestCase(100 * 60 + 30, "100 h")]
    [TestCase(1234 * 60, "1,234 h")]
    public void Duration_Should_ReadAsHoursAndMinutes(int minutes, string expected)
    {
        StatsFormat.Duration(minutes).Should().Be(expected);
    }

    [TestCase(0, "")]
    [TestCase(20, "<1 h")]
    [TestCase(89, "1 h")]
    [TestCase(90, "2 h")]
    [TestCase(600, "10 h")]
    public void Hours_Should_RoundToWholeHours(int minutes, string expected)
    {
        StatsFormat.Hours(minutes).Should().Be(expected);
    }

    [TestCase(1, "episode", "1 episode")]
    [TestCase(0, "movie", "0 movies")]
    [TestCase(1500, "episode", "1,500 episodes")]
    public void Count_Should_PluraliseTheNoun(int count, string noun, string expected)
    {
        StatsFormat.Count(count, noun).Should().Be(expected);
    }

    [TestCase(0, 100, 0)]
    [TestCase(50, 100, 50)]
    [TestCase(100, 100, 100)]
    [TestCase(1, 1000, 2, Description = "a small value still shows")]
    [TestCase(5, 0, 0)]
    public void Percent_Should_ScaleToTheLargestValue(int value, int largest, int expected)
    {
        StatsFormat.Percent(value, largest).Should().Be(expected);
    }

    [Test]
    public void Months_Should_BeWrittenInEnglish()
    {
        DisplayDate.Month(new DateOnly(2026, 10, 1)).Should().Be("October 2026");
        DisplayDate.MonthShort(new DateOnly(2026, 10, 1)).Should().Be("Oct");
    }
}
