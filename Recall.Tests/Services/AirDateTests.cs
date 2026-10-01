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
    public void IsInFuture_Should_BeTrue_OnlyForAKnownDateAfterToday()
    {
        AirDate.IsInFuture(Oct1.AddDays(1), Oct1).Should().BeTrue();
        AirDate.IsInFuture(Oct1, Oct1).Should().BeFalse("an episode airing today counts as aired");
        AirDate.IsInFuture(Oct1.AddDays(-1), Oct1).Should().BeFalse();
        AirDate.IsInFuture(null, Oct1).Should().BeFalse("an unknown air date is not treated as unaired");
    }
}
