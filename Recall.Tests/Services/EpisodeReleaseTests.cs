using AwesomeAssertions;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Services;

/// <summary>When an episode counts as released (<see cref="EpisodeRelease"/>): late, never early.</summary>
[TestFixture]
public sealed class EpisodeReleaseTests
{
    private static DateTime Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    [Test]
    public void GoldRushS17E01_Should_BeReleasedAtMidnightUtc_OnOctoberThird()
    {
        // TheTVDB: aired 2026-10-02, airsTime 20:00, originalCountry usa (Discovery).
        var moment = EpisodeRelease.MomentUtc(new DateOnly(2026, 10, 2), "20:00", "usa")!;

        moment.Utc.Should().Be(Utc(2026, 10, 3, 0));
        moment.Utc.Kind.Should().Be(DateTimeKind.Utc);
        moment.IsTimeKnown.Should().BeTrue();
        moment.LocalTime.Should().Be(new TimeOnly(20, 0));
        moment.ZoneLabel.Should().Be("ET");
        moment.DateTimeAttribute.Should().Be("2026-10-03T00:00:00Z");
        moment.GroupDate.Should().Be(new DateOnly(2026, 10, 3), "grouped by the UTC date of the moment");
        moment.AirDate.Should().Be(new DateOnly(2026, 10, 2), "the date shown stays TheTVDB's");

        moment.IsReleasedBy(Utc(2026, 10, 2, 23, 59)).Should().BeFalse();
        moment.IsReleasedBy(Utc(2026, 10, 3, 0)).Should().BeTrue();
    }

    [Test]
    public void TheDaylightSavingChange_Should_MoveTheMomentAnHour()
    {
        // US clocks go back on 2026-11-01: 20:00 EST is 01:00 UTC, not 00:00.
        EpisodeRelease.MomentUtc(new DateOnly(2026, 10, 30), "20:00", "usa")!.Utc.Should().Be(Utc(2026, 10, 31, 0));
        EpisodeRelease.MomentUtc(new DateOnly(2026, 11, 6), "20:00", "usa")!.Utc.Should().Be(Utc(2026, 11, 7, 1));
    }

    [Test]
    public void AEuropeanSeries_Should_BeReleasedTheSameEvening()
    {
        // Bonde söker fru: 20:00 Stockholm.
        var summer = EpisodeRelease.MomentUtc(new DateOnly(2026, 9, 23), "20:00", "swe")!;
        summer.Utc.Should().Be(Utc(2026, 9, 23, 18));
        summer.ZoneLabel.Should().Be("CEST");

        var winter = EpisodeRelease.MomentUtc(new DateOnly(2026, 12, 16), "20:00", "swe")!;
        winter.Utc.Should().Be(Utc(2026, 12, 16, 19));
        winter.ZoneLabel.Should().Be("CET");
    }

    [Test]
    public void AStreamingSeries_Should_UseItsAirTime()
    {
        // Severance (Apple TV): Thursday 21:00 Eastern is Friday 01:00 UTC.
        EpisodeRelease.MomentUtc(new DateOnly(2025, 3, 20), "21:00", "usa")!.Utc.Should().Be(Utc(2025, 3, 21, 1));

        // The Gentlemen (Netflix, gbr): 08:00 London in March is 08:00 UTC.
        EpisodeRelease.MomentUtc(new DateOnly(2024, 3, 7), "08:00", "gbr")!.Utc.Should().Be(Utc(2024, 3, 7, 8));
    }

    [Test]
    public void AKnownCountryWithoutAnAirTime_Should_BeReleasedAtTheEndOfTheAirDateThere()
    {
        // Dark (Netflix, deu, no air time): midnight in Berlin, 22:00 UTC in summer.
        var moment = EpisodeRelease.MomentUtc(new DateOnly(2020, 6, 27), null, "deu")!;

        moment.Utc.Should().Be(Utc(2020, 6, 27, 22));
        moment.IsTimeKnown.Should().BeFalse();
        moment.LocalTime.Should().BeNull();
        moment.ZoneLabel.Should().BeNull();
        moment.DateTimeAttribute.Should().Be("2020-06-27", "a fallback is never given a time");
        moment.GroupDate.Should().Be(new DateOnly(2020, 6, 27));

        EpisodeRelease.MomentUtc(new DateOnly(2026, 10, 2), " ", "usa")!.Utc.Should().Be(Utc(2026, 10, 3, 4), "midnight Eastern");
        EpisodeRelease.MomentUtc(new DateOnly(2026, 10, 2), "TBA", "usa")!.IsTimeKnown.Should().BeFalse();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("xyz")]
    public void AnUnknownCountry_Should_BeReleasedAtNoonUtc_TheDayAfter(string? country)
    {
        // The end of 2026-10-02 everywhere on Earth: 2026-10-03 12:00 UTC, whatever the air time.
        var moment = EpisodeRelease.MomentUtc(new DateOnly(2026, 10, 2), "20:00", country)!;

        moment.Utc.Should().Be(Utc(2026, 10, 3, 12));
        moment.IsTimeKnown.Should().BeFalse();
        moment.DateTimeAttribute.Should().Be("2026-10-02");
        moment.GroupDate.Should().Be(new DateOnly(2026, 10, 2));
    }

    [Test]
    public void NoAirDate_Should_HaveNoMoment()
    {
        EpisodeRelease.MomentUtc(null, "20:00", "usa").Should().BeNull();
    }

    [Test]
    public void TheCountryCode_Should_BeMatchedWhateverItsCase()
    {
        EpisodeRelease.MomentUtc(new DateOnly(2026, 10, 2), "20:00", "USA")!.Utc.Should().Be(Utc(2026, 10, 3, 0));
    }

    [Test]
    public void ATimeSkippedByTheSpringChange_Should_StillGiveAMoment_NotAnEarlyOne()
    {
        // 02:30 does not exist in New York on 2026-03-08; it is taken as 03:30 EDT.
        EpisodeRelease.MomentUtc(new DateOnly(2026, 3, 8), "02:30", "usa")!.Utc.Should().Be(Utc(2026, 3, 8, 7, 30));
    }
}
