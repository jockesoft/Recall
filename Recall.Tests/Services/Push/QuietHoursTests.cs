using AwesomeAssertions;
using Recall.Web.Services.Push;

namespace Recall.Tests.Services.Push;

/// <summary>22:00 to 08:00 on each device's own clock, daylight saving included.</summary>
[TestFixture]
public class QuietHoursTests
{
    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeZoneInfo Kolkata = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    private static DateTime Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    [TestCase(19, 59, false, Description = "21:59 in Stockholm (CEST)")]
    [TestCase(20, 0, true, Description = "22:00")]
    [TestCase(1, 0, true, Description = "03:00")]
    [TestCase(5, 59, true, Description = "07:59")]
    [TestCase(6, 0, false, Description = "08:00")]
    public void IsQuiet_Should_GoByTheDevicesClock(int utcHour, int utcMinute, bool quiet)
    {
        QuietHours.IsQuiet(Utc(9, 15, utcHour, utcMinute), Stockholm).Should().Be(quiet);
    }

    [Test]
    public void TheSameMoment_Should_BeQuietInOneZone_AndNotInAnother()
    {
        var moment = Utc(9, 15, 21); // 23:00 in Stockholm, 17:00 in New York

        QuietHours.IsQuiet(moment, Stockholm).Should().BeTrue();
        QuietHours.IsQuiet(moment, NewYork).Should().BeFalse();
    }

    [Test]
    public void BeforeMidnight_Should_BeReleasedAtEightTheNextMorning()
    {
        // 23:00 Sep 15 in Stockholm (CEST, +2) -> 08:00 Sep 16 = 06:00 UTC.
        QuietHours.NextRelease(Utc(9, 15, 21), Stockholm).Should().Be(Utc(9, 16, 6));
    }

    [Test]
    public void AfterMidnight_Should_BeReleasedAtEightTheSameMorning()
    {
        // 03:00 Sep 16 in New York (EDT, -4) = 07:00 UTC -> 08:00 = 12:00 UTC.
        QuietHours.NextRelease(Utc(9, 16, 7), NewYork).Should().Be(Utc(9, 16, 12));
    }

    [Test]
    public void ANightWhenTheClocksGoBack_Should_StillReleaseAtEightLocalTime()
    {
        // Europe leaves summer time at 03:00 on Oct 25 2026. 23:00 on Oct 24 is
        // CEST (+2) = 21:00 UTC; 08:00 on Oct 25 is CET (+1) = 07:00 UTC.
        QuietHours.NextRelease(Utc(10, 24, 21), Stockholm).Should().Be(Utc(10, 25, 7));
    }

    [Test]
    public void AHalfHourZone_Should_BeHonoured()
    {
        // 23:30 in Kolkata (+5:30) = 18:00 UTC -> 08:00 = 02:30 UTC.
        QuietHours.IsQuiet(Utc(9, 15, 18), Kolkata).Should().BeTrue();
        QuietHours.NextRelease(Utc(9, 15, 18), Kolkata).Should().Be(Utc(9, 16, 2, 30));
    }
}
