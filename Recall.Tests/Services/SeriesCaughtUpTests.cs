using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Services;

/// <summary>
/// "You're up to date" / "You've finished": when a mark counts as catching up
/// (decided in <see cref="WatchProgressService"/> by comparing the series'
/// state before and after the write), on every way of marking, and what the
/// toast then says.
/// </summary>
[TestFixture]
public sealed class SeriesCaughtUpTests
{
    private const int SeriesId = 42;
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 10, 2);
    private static readonly DateTime BatchStamp = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    private Mock<ITheTvDbService> _tvDb = null!;
    private Mock<IEpisodeWatchRepository> _watches = null!;
    private Mock<IRatingRepository> _ratings = null!;
    private HashSet<int> _watched = null!;
    private WatchProgressService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _tvDb = new Mock<ITheTvDbService>();
        _watches = new Mock<IEpisodeWatchRepository>();
        _ratings = new Mock<IRatingRepository>();
        _watched = [];

        // A small stand-in for the table: what is watched, and marks and unmarks changing it.
        _watches.Setup(x => x.GetWatchedEpisodeIdsAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _watched.ToHashSet());
        _watches.Setup(x => x.IsWatchedAsync(UserId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, int id, CancellationToken _) => _watched.Contains(id));
        _watches.Setup(x => x.MarkWatchedAsync(UserId, SeriesId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, int _, int id, CancellationToken _) => _watched.Add(id))
            .Returns(Task.CompletedTask);
        _watches.Setup(x => x.MarkUnwatchedAsync(UserId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, int id, CancellationToken _) => _watched.Remove(id))
            .Returns(Task.CompletedTask);
        _watches.Setup(x => x.MarkWatchedRangeAsync(
                UserId, SeriesId, It.IsAny<IEnumerable<int>>(), It.IsAny<WatchSource>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, int _, IEnumerable<int> ids, WatchSource _, int? _, CancellationToken _) =>
            {
                var added = ids.Count(id => _watched.Add(id));
                return added == 0 ? WatchedBatch.Empty : new WatchedBatch(added, BatchStamp);
            });

        _sut = new WatchProgressService(
            _tvDb.Object, _watches.Object,
            // Already in the library: adding it is MarkAddsToLibraryTests' subject.
            Mock.Of<ITrackedSeriesRepository>(r => r.ExistsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()) == Task.FromResult(true)),
            _ratings.Object,
            new FixedTimeProvider(new DateTimeOffset(Today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero)),
            NullLogger<WatchProgressService>.Instance);
    }

    private static EpisodeSummary Ep(int id, int season, int number, int daysFromToday) => new()
    {
        Id = id, SeasonNumber = season, EpisodeNumber = number, Name = $"S{season}E{number}", Aired = Today.AddDays(daysFromToday)
    };

    private void Series(string status, params EpisodeSummary[] episodes) =>
        _tvDb.Setup(x => x.GetSeriesAggregateByIdAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesAggregate
            {
                TvdbId = SeriesId, Name = "Chernobyl", Status = new SeriesStatus { Name = status }, Episodes = episodes
            });

    private void Watched(params int[] ids) => _watched.UnionWith(ids);

    private void Rated(int? value) =>
        _ratings.Setup(x => x.GetRatingAsync(UserId, RatingTargetType.Series, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(value);

    // A special, three aired regular episodes, and one that airs next week.
    private static readonly EpisodeSummary[] ThreeAired =
        [Ep(5, 0, 1, -50), Ep(1, 1, 1, -30), Ep(2, 1, 2, -20), Ep(3, 1, 3, -10)];

    // ---- the transition rule -------------------------------------------------------

    [Test]
    public async Task MarkingTheLastUnwatchedEpisode_Should_FinishAnEndedSeries_WhateverTheOrder()
    {
        // Out of order: the first and the last were watched earlier; the middle one closes the gap.
        Series("Ended", ThreeAired);
        Watched(1, 3);
        Rated(null);

        var result = await _sut.MarkEpisodeWatchedAsync(UserId, SeriesId, 2);

        result.Outcome.Should().Be(EpisodeWatchOutcome.MarkedWatched);
        result.CaughtUp.Should().Be(new SeriesCaughtUp(SeriesId, "Chernobyl", Finished: true, UserHasRated: false));
    }

    [Test]
    public async Task AMark_Should_NotCatchUp_WhileAnotherAiredRegularEpisodeIsUnwatched()
    {
        Series("Ended", ThreeAired);
        Watched(1);

        (await _sut.MarkEpisodeWatchedAsync(UserId, SeriesId, 3)).CaughtUp.Should().BeNull("episode 2 is still unwatched");
        (await _sut.MarkEpisodeWatchedAsync(UserId, SeriesId, 2)).CaughtUp.Should().NotBeNull("now it is the last one");
    }

    [Test]
    public async Task UnwatchedSpecials_Should_NotStandInTheWay_AndMarkingASpecialNeverCatchesUp()
    {
        Series("Ended", ThreeAired);
        Watched(1, 2);

        (await _sut.MarkEpisodeWatchedAsync(UserId, SeriesId, 5)).CaughtUp
            .Should().BeNull("a special changes nothing about being caught up: episode 3 is still unwatched");

        _watched.Remove(5);
        (await _sut.MarkEpisodeWatchedAsync(UserId, SeriesId, 3)).CaughtUp
            .Should().NotBeNull("the unwatched special does not count");

        (await _sut.MarkEpisodeWatchedAsync(UserId, SeriesId, 5)).CaughtUp
            .Should().BeNull("marking the special afterwards: the series was already finished");
    }

    [Test]
    public async Task AMark_Should_NotCatchUp_WhenTheSeriesWasAlreadyUpToDate()
    {
        Series("Continuing", [.. ThreeAired, Ep(4, 1, 4, 7)]);
        Watched(1, 2, 3);

        // Marking a watched episode again, and the one case that can still be marked: an episode with no air date.
        (await _sut.MarkEpisodeWatchedAsync(UserId, SeriesId, 3)).CaughtUp.Should().BeNull();
        (await _sut.MarkSeasonWatchedAsync(UserId, SeriesId, 1)).CaughtUp.Should().BeNull();
    }

    [Test]
    public async Task Unmarking_Should_NeverCatchUp()
    {
        Series("Ended", ThreeAired);
        Watched(1, 2, 3);

        var result = await _sut.ToggleEpisodeWatchedAsync(UserId, SeriesId, 3);

        result.Outcome.Should().Be(EpisodeWatchOutcome.MarkedUnwatched);
        result.CaughtUp.Should().BeNull();
    }

    [Test]
    public async Task ARefusedMark_Should_NotCatchUp()
    {
        Series("Continuing", Ep(1, 1, 1, -30), Ep(2, 1, 2, 5));

        var result = await _sut.MarkEpisodeWatchedAsync(UserId, SeriesId, 2);

        result.Outcome.Should().Be(EpisodeWatchOutcome.NotAired);
        result.CaughtUp.Should().BeNull();
    }

    [Test]
    public async Task AContinuingSeries_Should_BeUpToDate_WithItsNextAirDate_WhenThereIsOne()
    {
        Series("Continuing", Ep(1, 1, 1, -30), Ep(2, 1, 2, -1), Ep(9, 0, 2, 3), Ep(4, 1, 4, 14), Ep(3, 1, 3, 7));
        Watched(1);

        var caughtUp = (await _sut.MarkEpisodeWatchedAsync(UserId, SeriesId, 2)).CaughtUp;

        caughtUp!.Finished.Should().BeFalse();
        caughtUp.NextEpisode!.Id.Should().Be(3, "the soonest regular episode; the special airing before it does not count");
        caughtUp.NextEpisode.Aired.Should().Be(Today.AddDays(7));
        caughtUp.OfferRating.Should().BeFalse("only a finished series asks for a rating");
        _ratings.VerifyNoOtherCalls();
    }

    [Test]
    public async Task AContinuingSeries_Should_BeUpToDate_WithoutADate_WhenNothingIsScheduled()
    {
        Series("Continuing", Ep(1, 1, 1, -30), Ep(2, 1, 2, -1));
        Watched(1);

        var caughtUp = (await _sut.MarkEpisodeWatchedAsync(UserId, SeriesId, 2)).CaughtUp;

        caughtUp.Should().Be(new SeriesCaughtUp(SeriesId, "Chernobyl", Finished: false, NextEpisode: null));
    }

    [Test]
    public async Task AFinishedSeries_Should_OfferARating_OnlyWhenTheUserHasNotRatedIt()
    {
        Series("Ended", ThreeAired);
        Watched(1, 2);

        Rated(8);
        (await _sut.MarkEpisodeWatchedAsync(UserId, SeriesId, 3)).CaughtUp!.OfferRating.Should().BeFalse();

        _watched.Remove(3);
        Rated(null);
        (await _sut.MarkEpisodeWatchedAsync(UserId, SeriesId, 3)).CaughtUp!.OfferRating.Should().BeTrue();
    }

    [Test]
    public async Task TheMark_Should_StillBeRecorded_WhenTheCaughtUpCheckFails()
    {
        // The toast is a nicety; a failure reading the rating must not fail the mark.
        Series("Ended", ThreeAired);
        Watched(1, 2);
        _ratings.Setup(x => x.GetRatingAsync(UserId, RatingTargetType.Series, SeriesId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is away"));

        var result = await _sut.MarkEpisodeWatchedAsync(UserId, SeriesId, 3);

        result.Outcome.Should().Be(EpisodeWatchOutcome.MarkedWatched);
        result.CaughtUp.Should().BeNull();
        _watched.Should().Contain(3);
    }

    // ---- every way of marking ------------------------------------------------------

    private static IEnumerable<TestCaseData> MarkingPaths()
    {
        yield return new TestCaseData((Func<WatchProgressService, Task<SeriesCaughtUp?>>)(async s =>
            (await s.MarkEpisodeWatchedAsync(UserId, SeriesId, 3)).CaughtUp)).SetName("CaughtUp_MarkEpisodeWatched");
        yield return new TestCaseData((Func<WatchProgressService, Task<SeriesCaughtUp?>>)(async s =>
            (await s.ToggleEpisodeWatchedAsync(UserId, SeriesId, 3)).CaughtUp)).SetName("CaughtUp_ToggleEpisodeWatched_AndTheHeaderButton");
        yield return new TestCaseData((Func<WatchProgressService, Task<SeriesCaughtUp?>>)(async s =>
            (await s.MarkEpisodeWatchedUndoablyAsync(UserId, SeriesId, 3)).CaughtUp)).SetName("CaughtUp_DashboardOneTap");
        yield return new TestCaseData((Func<WatchProgressService, Task<SeriesCaughtUp?>>)(async s =>
            (await s.MarkWatchedThroughAsync(UserId, SeriesId, 3)).CaughtUp)).SetName("CaughtUp_MarkThisAndEarlier");
        yield return new TestCaseData((Func<WatchProgressService, Task<SeriesCaughtUp?>>)(async s =>
            (await s.MarkSeasonWatchedAsync(UserId, SeriesId, 1)).CaughtUp)).SetName("CaughtUp_MarkSeasonWatched");
    }

    [TestCaseSource(nameof(MarkingPaths))]
    public async Task EveryWayOfMarking_Should_ReportCatchingUp(Func<WatchProgressService, Task<SeriesCaughtUp?>> mark)
    {
        // Episodes 1 and 2 are watched for the single marks; the bulk ones sweep them in themselves.
        Series("Ended", ThreeAired);
        Watched(1, 2);
        Rated(null);

        var caughtUp = await mark(_sut);

        caughtUp.Should().Be(new SeriesCaughtUp(SeriesId, "Chernobyl", Finished: true, UserHasRated: false));
        _watched.Should().Contain(3);
    }

    [Test]
    public async Task ABulkMark_Should_CatchUp_FromNothingWatched()
    {
        Series("Ended", ThreeAired);
        Rated(null);

        var result = await _sut.MarkWatchedThroughAsync(UserId, SeriesId, 3);

        result.Batch!.InsertedCount.Should().Be(3);
        result.CaughtUp!.Finished.Should().BeTrue();
    }

    [Test]
    public async Task MarkingOneSeason_Should_NotCatchUp_WhileAnotherSeasonHasUnwatchedEpisodes()
    {
        Series("Ended", Ep(1, 1, 1, -60), Ep(2, 1, 2, -50), Ep(11, 2, 1, -20));

        (await _sut.MarkSeasonWatchedAsync(UserId, SeriesId, 1)).CaughtUp.Should().BeNull();
        (await _sut.MarkSeasonWatchedAsync(UserId, SeriesId, 2)).CaughtUp.Should().NotBeNull();
    }

    // ---- wording ---------------------------------------------------------------------

    [Test]
    public void Sentence_Should_SayFinished_ForAnEndedSeries()
    {
        new SeriesCaughtUp(SeriesId, "Chernobyl", Finished: true, UserHasRated: false).Sentence(Today)
            .Should().Be("You've finished Chernobyl.");
    }

    [Test]
    public void Sentence_Should_NameTheNextEpisodeAndItsDate_ThroughDisplayDate()
    {
        var thisYear = new WatchableEpisode(31, 3, 4, new DateOnly(2026, 10, 9), "Next");
        var nextYear = new WatchableEpisode(32, 3, 5, new DateOnly(2027, 1, 8), "Later");

        new SeriesCaughtUp(SeriesId, "Silo", Finished: false, NextEpisode: thisYear).Sentence(Today)
            .Should().Be("You're up to date with Silo. Next episode S03E04 on Fri, Oct 9.");
        new SeriesCaughtUp(SeriesId, "Silo", Finished: false, NextEpisode: nextYear).Sentence(Today)
            .Should().Be("You're up to date with Silo. Next episode S03E05 on Jan 8, 2027.");
    }

    [Test]
    public void Sentence_Should_PromiseANotification_WhenNoNextAirDateIsKnown()
    {
        new SeriesCaughtUp(SeriesId, "Silo", Finished: false).Sentence(Today)
            .Should().Be("You're up to date with Silo. We'll let you know when a new episode airs.");
        new SeriesCaughtUp(SeriesId, "Silo", Finished: false, NextEpisode: new WatchableEpisode(31, 3, 4, null, "Undated")).Sentence(Today)
            .Should().Be("You're up to date with Silo. We'll let you know when a new episode airs.");
    }
}
