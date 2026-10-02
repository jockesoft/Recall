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
/// Marking an episode watched puts its series in the user's library, from
/// whatever page the mark came (the rule lives in
/// <see cref="WatchProgressService"/>); unmarking never takes it out.
/// </summary>
[TestFixture]
public sealed class MarkAddsToLibraryTests
{
    private const int SeriesId = 403245;
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 10, 2);

    private Mock<ITheTvDbService> _tvDb = null!;
    private Mock<IEpisodeWatchRepository> _watches = null!;
    private Mock<ITrackedSeriesRepository> _library = null!;
    private List<TrackedSeries> _tracked = null!;
    private HashSet<int> _watched = null!;
    private WatchProgressService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _tvDb = new Mock<ITheTvDbService>();
        _watches = new Mock<IEpisodeWatchRepository>();
        _library = new Mock<ITrackedSeriesRepository>();
        _tracked = [];
        _watched = [];

        EpisodeSummary[] episodes =
        [
            new() { Id = 1, SeasonNumber = 1, EpisodeNumber = 1, Name = "One", Aired = Today.AddDays(-30) },
            new() { Id = 2, SeasonNumber = 1, EpisodeNumber = 2, Name = "Two", Aired = Today.AddDays(-20) },
            new() { Id = 3, SeasonNumber = 1, EpisodeNumber = 3, Name = "Three", Aired = Today.AddDays(-10) },
            new() { Id = 4, SeasonNumber = 1, EpisodeNumber = 4, Name = "Four", Aired = Today.AddDays(7) }
        ];
        _tvDb.Setup(x => x.GetSeriesAggregateByIdAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesAggregate
            {
                TvdbId = SeriesId, Name = "Silo", Status = new SeriesStatus { Name = "Continuing" }, Episodes = episodes
            });
        _tvDb.Setup(x => x.GetSeriesByIdAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TvSeriesDetails(SeriesId, "Silo", "silo", "Overview", null, "2023-05-05", null, "Continuing"));

        // A small stand-in for the two tables.
        _library.Setup(x => x.ExistsAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _tracked.Any(t => t.TvdbId == SeriesId));
        _library.Setup(x => x.AddAsync(It.IsAny<TrackedSeries>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TrackedSeries series, CancellationToken _) =>
            {
                if (_tracked.Any(t => t.TvdbId == series.TvdbId)) return false;
                _tracked.Add(series);
                return true;
            });

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
                return added == 0 ? WatchedBatch.Empty : new WatchedBatch(added, DateTime.UtcNow);
            });

        _sut = new WatchProgressService(
            _tvDb.Object, _watches.Object, _library.Object, Mock.Of<IRatingRepository>(),
            new FixedTimeProvider(new DateTimeOffset(Today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero)),
            NullLogger<WatchProgressService>.Instance);
    }

    private void VerifyAdded(Times times) =>
        _library.Verify(x => x.AddAsync(It.IsAny<TrackedSeries>(), It.IsAny<CancellationToken>()), times);

    [Test]
    public async Task MarkingAnEpisode_Should_AddAnUntrackedSeries_ExactlyOnce()
    {
        var first = await _sut.ToggleEpisodeWatchedAsync(UserId, SeriesId, 1);
        var second = await _sut.ToggleEpisodeWatchedAsync(UserId, SeriesId, 2);

        first.Outcome.Should().Be(EpisodeWatchOutcome.MarkedWatched);
        first.AddedToLibrary.Should().Be("Silo", "the series was not in the library: the result names it for the toast");
        second.AddedToLibrary.Should().BeNull("it is in the library now");

        var tracked = _tracked.Should().ContainSingle().Subject;
        tracked.TvdbId.Should().Be(SeriesId);
        tracked.UserId.Should().Be(UserId);
        tracked.Name.Should().Be("Silo");
        VerifyAdded(Times.Once());
    }

    [Test]
    public async Task MarkingAnEpisode_Should_LeaveATrackedSeriesAlone()
    {
        _tracked.Add(new TrackedSeries { Id = Guid.NewGuid(), UserId = UserId, TvdbId = SeriesId, Name = "Silo" });

        var result = await _sut.MarkEpisodeWatchedAsync(UserId, SeriesId, 1);

        result.AddedToLibrary.Should().BeNull();
        VerifyAdded(Times.Never());
        _tvDb.Verify(x => x.GetSeriesByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Unmarking_Should_LeaveTheLibraryUnchanged_WhetherOrNotTheSeriesIsInIt()
    {
        // In the library: unmarking the only watched episode does not take the series out.
        await _sut.ToggleEpisodeWatchedAsync(UserId, SeriesId, 1);
        _library.Invocations.Clear();

        var unmarked = await _sut.ToggleEpisodeWatchedAsync(UserId, SeriesId, 1);

        unmarked.Outcome.Should().Be(EpisodeWatchOutcome.MarkedUnwatched);
        unmarked.AddedToLibrary.Should().BeNull();
        _tracked.Should().ContainSingle("unmarking never removes a series from the library");
        _library.VerifyNoOtherCalls();

        // Not in the library (the user removed it), with a watch left over: unmarking does not add it either.
        _tracked.Clear();
        _watched.Add(2);

        (await _sut.ToggleEpisodeWatchedAsync(UserId, SeriesId, 2)).AddedToLibrary.Should().BeNull();
        (await _sut.MarkSeasonUnwatchedAsync(UserId, SeriesId, 1)).Should().Be(0);
        _tracked.Should().BeEmpty();
        _library.VerifyNoOtherCalls();
    }

    private static IEnumerable<TestCaseData> MarkingPaths()
    {
        yield return new TestCaseData((Func<WatchProgressService, Task<string?>>)(async s =>
            (await s.MarkEpisodeWatchedAsync(UserId, SeriesId, 2)).AddedToLibrary)).SetName("AddsToLibrary_MarkEpisodeWatched");
        yield return new TestCaseData((Func<WatchProgressService, Task<string?>>)(async s =>
            (await s.ToggleEpisodeWatchedAsync(UserId, SeriesId, 2)).AddedToLibrary)).SetName("AddsToLibrary_ToggleEpisodeWatched");
        yield return new TestCaseData((Func<WatchProgressService, Task<string?>>)(async s =>
            (await s.MarkEpisodeWatchedUndoablyAsync(UserId, SeriesId, 2)).AddedToLibrary)).SetName("AddsToLibrary_OneTapMark");
        yield return new TestCaseData((Func<WatchProgressService, Task<string?>>)(async s =>
            (await s.MarkWatchedThroughAsync(UserId, SeriesId, 2)).AddedToLibrary)).SetName("AddsToLibrary_MarkThisAndEarlier");
        yield return new TestCaseData((Func<WatchProgressService, Task<string?>>)(async s =>
            (await s.MarkSeasonWatchedAsync(UserId, SeriesId, 1)).AddedToLibrary)).SetName("AddsToLibrary_MarkSeasonWatched");
    }

    [TestCaseSource(nameof(MarkingPaths))]
    public async Task EveryWayOfMarking_Should_AddTheSeries(Func<WatchProgressService, Task<string?>> mark)
    {
        (await mark(_sut)).Should().Be("Silo");
        _tracked.Should().ContainSingle();
    }

    [Test]
    public async Task ARefusedMark_AndASeasonWithNothingLeftToMark_Should_AddNothing()
    {
        (await _sut.MarkEpisodeWatchedAsync(UserId, SeriesId, 4)).Outcome.Should().Be(EpisodeWatchOutcome.NotAired);
        (await _sut.MarkWatchedThroughAsync(UserId, SeriesId, 999)).EpisodeFound.Should().BeFalse();
        _tracked.Should().BeEmpty();

        // Everything aired is watched already (and the user took the series out of the library since).
        _watched.UnionWith([1, 2, 3]);
        var season = await _sut.MarkSeasonWatchedAsync(UserId, SeriesId, 1);

        season.Batch.InsertedCount.Should().Be(0);
        season.AddedToLibrary.Should().BeNull();
        _tracked.Should().BeEmpty();
    }

    [Test]
    public async Task TheMark_Should_StillBeRecorded_WhenAddingToTheLibraryFails()
    {
        _library.Setup(x => x.AddAsync(It.IsAny<TrackedSeries>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is away"));

        var result = await _sut.MarkEpisodeWatchedAsync(UserId, SeriesId, 1);

        result.Outcome.Should().Be(EpisodeWatchOutcome.MarkedWatched);
        result.AddedToLibrary.Should().BeNull();
        _watched.Should().Contain(1);
    }

    [Test]
    public async Task AddingAndCatchingUp_Should_BothBeReported_ByTheSameMark()
    {
        _watched.UnionWith([1, 2]);

        var result = await _sut.MarkEpisodeWatchedAsync(UserId, SeriesId, 3);

        result.AddedToLibrary.Should().Be("Silo");
        result.CaughtUp!.Finished.Should().BeFalse();
        result.CaughtUp.NextEpisode!.Id.Should().Be(4);
    }
}
