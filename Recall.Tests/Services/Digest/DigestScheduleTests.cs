using AwesomeAssertions;
using Recall.Web.Services.Digest;

namespace Recall.Tests.Services.Digest;

[TestFixture]
public sealed class DigestScheduleTests
{
    // Friday 15:00 UTC, 48 hours to catch up: the defaults. 2026-10-02 is a Friday.
    private static readonly DigestOptions Options = new();

    private static DateTimeOffset Utc(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    [Test]
    public void BeforeTheScheduledHour_NothingShouldBeDue()
    {
        DigestSchedule.DuePeriod(Utc(2, 14, 59), Options).Should().BeNull("Friday, a minute early; last week's window closed long ago");
    }

    [Test]
    public void FromTheScheduledHour_TheWeekShouldBeDue_NamedByItsSendDate()
    {
        DigestSchedule.DuePeriod(Utc(2, 15), Options).Should().Be(new DateOnly(2026, 10, 2));
        DigestSchedule.DuePeriod(Utc(2, 23, 30), Options).Should().Be(new DateOnly(2026, 10, 2));
    }

    [Test]
    public void EveryRunInTheCatchUpWindow_Should_WorkOnTheSameWeek()
    {
        // A restart at 15:00, or more recipients than one run takes: later runs pick the same week up.
        DigestSchedule.DuePeriod(Utc(3, 9), Options).Should().Be(new DateOnly(2026, 10, 2));
        DigestSchedule.DuePeriod(Utc(4, 15), Options).Should().Be(new DateOnly(2026, 10, 2), "exactly 48 hours later is still inside");
    }

    [Test]
    public void AfterTheCatchUpWindow_NothingShouldBeDue_UntilNextWeek()
    {
        DigestSchedule.DuePeriod(Utc(4, 15, 1), Options).Should().BeNull();
        DigestSchedule.DuePeriod(Utc(7, 12), Options).Should().BeNull("a Wednesday");
        DigestSchedule.DuePeriod(Utc(9, 15), Options).Should().Be(new DateOnly(2026, 10, 9), "the following Friday");
    }

    [Test]
    public void TheDayAndHour_Should_ComeFromConfiguration()
    {
        var mondayMorning = new DigestOptions { DayOfWeek = DayOfWeek.Monday, HourUtc = 6, CatchUpHours = 12 };

        DigestSchedule.DuePeriod(Utc(5, 6), mondayMorning).Should().Be(new DateOnly(2026, 10, 5));
        DigestSchedule.DuePeriod(Utc(5, 18, 1), mondayMorning).Should().BeNull();
        DigestSchedule.DuePeriod(Utc(2, 15), mondayMorning).Should().BeNull("Friday is not the day any more");
    }
}
