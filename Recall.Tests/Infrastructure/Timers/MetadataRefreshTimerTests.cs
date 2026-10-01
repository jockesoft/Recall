using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;
using Recall.Web.Infrastructure.Persistence.TvdbCache;
using Recall.Web.Infrastructure.Timers;
using Recall.Web.Services;

namespace Recall.Tests.Infrastructure.Timers;

/// <summary>
/// The two TheTVDB refresh jobs: each asks the store for a capped batch of
/// stale rows and refreshes them one by one, never letting one bad item stop
/// the rest.
/// </summary>
[TestFixture]
public class MetadataRefreshTimerTests
{
    private Mock<ITvdbSnapshotStore> _store = null!;
    private Mock<ITheTvDbService> _tvDb = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new Mock<ITvdbSnapshotStore>();
        _tvDb = new Mock<ITheTvDbService>();

        SetUpSeriesCandidates();
        SetUpEpisodeCandidates();
        SetUpMovieCandidates();
    }

    private void SetUpSeriesCandidates(params int[] ids) =>
        _store
            .Setup(x => x.GetAggregatesNeedingRefreshAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ids.Select(id => new CachedAggregateKey(id, "eng")).ToList());

    private void SetUpEpisodeCandidates(params int[] ids) =>
        _store
            .Setup(x => x.GetEpisodesNeedingRefreshAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ids);

    private void SetUpMovieCandidates(params int[] ids) =>
        _store
            .Setup(x => x.GetMovieAggregatesNeedingRefreshAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ids.Select(id => new CachedAggregateKey(id, "eng")).ToList());

    private UpdateTvDbInfoTimer SeriesJob() => new(_store.Object, _tvDb.Object, NullLogger<UpdateTvDbInfoTimer>.Instance);

    private UpdateMovieInfoTimer MovieJob() => new(_store.Object, _tvDb.Object, NullLogger<UpdateMovieInfoTimer>.Instance);

    // ---- UpdateTvDbInfoTimer -----------------------------------------------------

    [Test]
    public async Task SeriesJob_Should_AskForAtMostTenSeries_AndTwentyFiveEpisodes_WithTheRightAgeCutoffs()
    {
        var before = DateTime.UtcNow;

        await SeriesJob().Execute(Mock.Of<IJobExecutionContext>());

        var after = DateTime.UtcNow;
        _store.Verify(x => x.GetAggregatesNeedingRefreshAsync(
            It.IsInRange(before.AddHours(-12), after.AddHours(-12), Moq.Range.Inclusive),
            It.IsInRange(before.AddDays(-30), after.AddDays(-30), Moq.Range.Inclusive),
            10,
            It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(x => x.GetEpisodesNeedingRefreshAsync(
            It.IsInRange(before.AddDays(-30), after.AddDays(-30), Moq.Range.Inclusive),
            It.IsInRange(before.AddHours(-12), after.AddHours(-12), Moq.Range.Inclusive),
            It.IsInRange(before.AddHours(-12), after.AddHours(-12), Moq.Range.Inclusive),
            DateOnly.FromDateTime(before),
            5,
            25,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task SeriesJob_Should_RefreshEveryCandidateItWasGiven()
    {
        SetUpSeriesCandidates(1, 2, 3);
        SetUpEpisodeCandidates(101, 102);

        await SeriesJob().Execute(Mock.Of<IJobExecutionContext>());

        foreach (var id in new[] { 1, 2, 3 })
            _tvDb.Verify(x => x.RefreshSeriesAggregateByIdAsync(id, It.IsAny<CancellationToken>()), Times.Once);
        foreach (var id in new[] { 101, 102 })
            _tvDb.Verify(x => x.RefreshEpisodeDetailsByIdAsync(id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task SeriesJob_Should_CarryOn_WhenOneSeriesAndOneEpisodeFail()
    {
        SetUpSeriesCandidates(1, 2, 3);
        SetUpEpisodeCandidates(101, 102, 103);
        _tvDb.Setup(x => x.RefreshSeriesAggregateByIdAsync(2, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("TheTVDB 500"));
        _tvDb.Setup(x => x.RefreshEpisodeDetailsByIdAsync(101, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("TheTVDB 500"));

        await SeriesJob().Execute(Mock.Of<IJobExecutionContext>());

        _tvDb.Verify(x => x.RefreshSeriesAggregateByIdAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        _tvDb.Verify(x => x.RefreshSeriesAggregateByIdAsync(3, It.IsAny<CancellationToken>()), Times.Once);
        _tvDb.Verify(x => x.RefreshEpisodeDetailsByIdAsync(102, It.IsAny<CancellationToken>()), Times.Once,
            "a failed series, and a failed first episode, must not stop the episode pass");
        _tvDb.Verify(x => x.RefreshEpisodeDetailsByIdAsync(103, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task SeriesJob_Should_CallNothing_WhenNothingIsDue()
    {
        await SeriesJob().Execute(Mock.Of<IJobExecutionContext>());

        _tvDb.VerifyNoOtherCalls();
    }

    // ---- UpdateMovieInfoTimer ----------------------------------------------------

    [Test]
    public async Task MovieJob_Should_AskForAtMostTenMovies_WithTheRightAgeCutoffs()
    {
        var before = DateTime.UtcNow;

        await MovieJob().Execute(Mock.Of<IJobExecutionContext>());

        var after = DateTime.UtcNow;
        _store.Verify(x => x.GetMovieAggregatesNeedingRefreshAsync(
            It.IsInRange(before.AddHours(-12), after.AddHours(-12), Moq.Range.Inclusive),
            It.IsInRange(before.AddDays(-30), after.AddDays(-30), Moq.Range.Inclusive),
            10,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task MovieJob_Should_CarryOn_WhenOneMovieFails()
    {
        SetUpMovieCandidates(1, 2, 3);
        _tvDb.Setup(x => x.RefreshMovieAggregateByIdAsync(1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("TheTVDB 500"));

        await MovieJob().Execute(Mock.Of<IJobExecutionContext>());

        _tvDb.Verify(x => x.RefreshMovieAggregateByIdAsync(2, It.IsAny<CancellationToken>()), Times.Once);
        _tvDb.Verify(x => x.RefreshMovieAggregateByIdAsync(3, It.IsAny<CancellationToken>()), Times.Once);
    }
}
