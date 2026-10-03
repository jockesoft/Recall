using AwesomeAssertions;
using Recall.Tests.TestSupport;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Services;

[TestFixture]
public class AirDateTests
{
    private static readonly DateOnly Oct1 = new(2026, 10, 1);

    [TestCase(0)]
    [TestCase(14)]
    [TestCase(-12)]
    public void Today_Should_BeTheUtcDate_WhateverTheLocalTimeZone(int localOffsetHours)
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(localOffsetHours), "test", "test");
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 10, 1, 23, 30, 0, TimeSpan.Zero), zone);

        AirDate.Today(clock).Should().Be(Oct1);
    }

    [Test]
    public void Today_Should_UseTheInstant_NotTheOffsetItWasExpressedIn()
    {
        // 01:00 on Oct 2 at UTC+2 is 23:00 on Oct 1 in UTC.
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 10, 2, 1, 0, 0, TimeSpan.FromHours(2)));

        AirDate.Today(clock).Should().Be(Oct1);
    }

    [Test]
    public void Now_Should_BeTheClocksMomentInUtc()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 10, 2, 1, 0, 0, TimeSpan.FromHours(2)));

        var now = AirDate.Now(clock);

        now.Should().Be(new DateTime(2026, 10, 1, 23, 0, 0, DateTimeKind.Utc));
        now.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Test]
    public void MayBeMarked_Should_AllowAnEpisode_OnceItsAirDateHasBegunAnywhereOnEarth()
    {
        // UTC+14 reaches Oct 2 at 10:00 UTC on Oct 1.
        var oct2 = new DateOnly(2026, 10, 2);

        AirDate.MayBeMarked(oct2, new DateTime(2026, 10, 1, 9, 59, 0, DateTimeKind.Utc)).Should().BeFalse();
        AirDate.MayBeMarked(oct2, new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc)).Should().BeTrue();
        AirDate.MayBeMarked(oct2.AddDays(-30), new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)).Should().BeTrue();
        AirDate.MayBeMarked(oct2.AddDays(2), new DateTime(2026, 10, 1, 23, 0, 0, DateTimeKind.Utc)).Should().BeFalse();
        AirDate.MayBeMarked(null, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc))
            .Should().BeTrue("an unknown air date may always be marked");
    }
}
