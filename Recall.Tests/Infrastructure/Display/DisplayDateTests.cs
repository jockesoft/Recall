using AwesomeAssertions;
using Recall.Web.Infrastructure.Display;

namespace Recall.Tests.Infrastructure.Display;

[TestFixture]
public sealed class DisplayDateTests
{
    private static readonly DateOnly Today = new(2026, 10, 1);

    [Test]
    public void Format_Should_ShowWeekdayAndNoYear_ForADateInTheCurrentYear()
    {
        DisplayDate.Format(new DateOnly(2026, 9, 4), Today).Should().Be("Fri, Sep 4");
        DisplayDate.Format(new DateOnly(2026, 12, 31), Today).Should().Be("Thu, Dec 31");
    }

    [Test]
    public void Format_Should_ShowTheYear_ForAnyOtherYear()
    {
        DisplayDate.Format(new DateOnly(2008, 1, 20), Today).Should().Be("Jan 20, 2008");
        DisplayDate.Format(new DateOnly(2027, 7, 9), Today).Should().Be("Jul 9, 2027");
    }

    [Test]
    public void Short_Should_DropTheWeekday_AndKeepTheYearRule()
    {
        DisplayDate.Short(new DateOnly(2026, 9, 25), Today).Should().Be("Sep 25");
        DisplayDate.Short(new DateOnly(1995, 12, 15), Today).Should().Be("Dec 15, 1995");
        DisplayDate.Short(new DateTime(2026, 9, 25, 23, 59, 0, DateTimeKind.Utc), Today).Should().Be("Sep 25");
    }

    [Test]
    public void Time_Should_UseTheTwelveHourClock()
    {
        DisplayDate.Time(new TimeOnly(20, 0)).Should().Be("8:00 PM");
        DisplayDate.Time(new TimeOnly(9, 5)).Should().Be("9:05 AM");
    }

    [Test]
    public void Format_Should_UseTheDatePartOfATimestamp()
    {
        DisplayDate.Format(new DateTime(2026, 9, 4, 23, 59, 0, DateTimeKind.Utc), Today).Should().Be("Fri, Sep 4");
    }

    [TestCase("2008-01-20", "Jan 20, 2008")]
    [TestCase("2024-12-23 18:07:56", "Dec 23, 2024")]
    [TestCase("2026-06-27", "Sat, Jun 27")]
    public void Format_Should_ParseTheDatesTheTvDbSendsAsText(string value, string expected)
    {
        DisplayDate.Format(value, Today).Should().Be(expected);
    }

    [Test]
    public void Format_Should_LeaveTextThatIsNotADateAlone()
    {
        DisplayDate.Format("TBA", Today).Should().Be("TBA");
        DisplayDate.Format("", Today).Should().BeNull();
        DisplayDate.Format((string?)null, Today).Should().BeNull();
    }

    [Test]
    public void Format_Should_NotDependOnTheCurrentCulture()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("sv-SE");
            DisplayDate.Format(new DateOnly(2026, 10, 1), Today).Should().Be("Thu, Oct 1");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [TestCase(30, "just now")]
    [TestCase(5 * 60, "5m ago")]
    [TestCase(3 * 3600, "3h ago")]
    [TestCase(2 * 86400, "2d ago")]
    [TestCase(28 * 86400, "4w ago")]
    public void Relative_Should_UseTheLargestWholeUnit(int seconds, string expected)
    {
        DisplayDate.Relative(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
    }
}
