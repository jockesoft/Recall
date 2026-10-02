using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Recall.Tests.TestSupport;
using Recall.Web.Infrastructure.External.TheTvDb;
using Recall.Web.Services.External.TheTvDb;
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
        _options = new TheTvDbOptions();

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
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<StillRecheck>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ids);

    private void SetUpMovieCandidates(params int[] ids) =>
        _store
            .Setup(x => x.GetMovieAggregatesNeedingRefreshAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ids.Select(id => new CachedAggregateKey(id, "eng")).ToList());

    private TheTvDbOptions _options = new();

    private UpdateTvDbInfoTimer SeriesJob() =>
        new(_store.Object, _tvDb.Object, Options.Create(_options), NullLogger<UpdateTvDbInfoTimer>.Instance);

    private UpdateMovieInfoTimer MovieJob() => new(_store.Object, _tvDb.Object, NullLogger<UpdateMovieInfoTimer>.Instance);

    // ---- UpdateTvDbInfoTimer -----------------------------------------------------

    [Test]
    public async Task SeriesJob_Should_LogHowManyTheTvDbRequestsTheRunMade_AtInformationLevel()
    {
        // Three requests per series and two per episode: what the real client sends.
        SetUpSeriesCandidates(1, 2);
        SetUpEpisodeCandidates(11, 12, 13);
        _tvDb.Setup(x => x.RefreshSeriesAggregateByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                for (var i = 0; i < 3; i++) TheTvDbRequestMeter.Record();
                return true;
            });
        _tvDb.Setup(x => x.RefreshEpisodeDetailsByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                for (var i = 0; i < 2; i++) TheTvDbRequestMeter.Record();
                return true;
            });
        var log = new RecordingLogger<UpdateTvDbInfoTimer>();

        // A request made outside the job, while it happens to run, is not its cost.
        TheTvDbRequestMeter.Record();
        await new UpdateTvDbInfoTimer(_store.Object, _tvDb.Object, Options.Create(_options), log).Execute(Mock.Of<IJobExecutionContext>());

        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information && e.Message.Contains("this run"))
            .Which.Message.Should().Be(
                "UpdateTvDbInfoTimer: 12 TheTVDB request(s) this run: 6 for 2 series, 6 for 3 cached episode(s).");
    }

    [Test]
    public async Task SeriesJob_Should_LogZeroRequests_WhenNothingIsDue()
    {
        var log = new RecordingLogger<UpdateTvDbInfoTimer>();

        await new UpdateTvDbInfoTimer(_store.Object, _tvDb.Object, Options.Create(_options), log).Execute(Mock.Of<IJobExecutionContext>());

        log.Entries.Should().Contain(e => e.Level == LogLevel.Information
            && e.Message == "UpdateTvDbInfoTimer: 0 TheTVDB request(s) this run: 0 for 0 series, 0 for 0 cached episode(s).");
    }

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
            // The still rechecks are scheduled from the time of the run and share the cap of 25.
            // ...and skip series that rarely have stills, by the configured defaults.
            It.Is<StillRecheck>(r => r.NowUtc >= before && r.NowUtc <= after && r.MinStillPercent == 10 && r.MinAiredEpisodes == 10),
            25,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task SeriesJob_Should_TakeTheRarelyHasStillsThresholds_FromConfiguration()
    {
        _options = new TheTvDbOptions { StillRecheckMinStillPercent = 25, StillRecheckMinAiredEpisodes = 40 };

        await SeriesJob().Execute(Mock.Of<IJobExecutionContext>());

        _store.Verify(x => x.GetEpisodesNeedingRefreshAsync(
            It.IsAny<DateTime>(), It.IsAny<DateTime>(),
            It.Is<StillRecheck>(r => r.MinStillPercent == 25 && r.MinAiredEpisodes == 40),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
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
