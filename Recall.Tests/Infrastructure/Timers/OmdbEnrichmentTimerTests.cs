using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Quartz;
using Recall.Web.Domain.Omdb;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.External.Omdb;
using Recall.Web.Infrastructure.Persistence.OmdbCache;
using Recall.Web.Infrastructure.Timers;
using Recall.Web.Services;
using Recall.Web.Services.External.Omdb;

namespace Recall.Tests.Infrastructure.Timers;

/// <summary>
/// The two OMDb enrichment jobs (series and movies) follow the same script:
/// take a capped batch, resolve each IMDb id from the cached TheTVDB data, pay
/// for the call from the shared daily budget, store the result.
/// </summary>
[TestFixture]
public class OmdbEnrichmentTimerTests
{
    private Mock<IOmdbSnapshotStore> _seriesStore = null!;
    private Mock<IMovieOmdbSnapshotStore> _movieStore = null!;
    private Mock<IOmdbApiClient> _omdb = null!;
    private Mock<IOmdbRequestBudget> _budget = null!;
    private Mock<ITheTvDbService> _tvDb = null!;
    private OmdbOptions _options = null!;

    [SetUp]
    public void SetUp()
    {
        _seriesStore = new Mock<IOmdbSnapshotStore>();
        _movieStore = new Mock<IMovieOmdbSnapshotStore>();
        _omdb = new Mock<IOmdbApiClient>();
        _budget = new Mock<IOmdbRequestBudget>();
        _budget.Setup(x => x.TryAcquire()).Returns(true);
        _tvDb = new Mock<ITheTvDbService>();
        _options = new OmdbOptions { ApiKey = "configured" };

        // Every series/movie id N has the IMDb id "ttN" unless a test overrides it.
        _tvDb
            .Setup(x => x.GetSeriesAggregateByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, CancellationToken _) => new SeriesAggregate
            {
                TvdbId = id,
                RemoteIds = [new SeriesRemoteId { Id = $"tt{id}", SourceName = "IMDB" }]
            });
        _tvDb
            .Setup(x => x.GetMovieAggregateByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, CancellationToken _) => new MovieAggregate
            {
                TvdbId = id,
                RemoteIds = [new MovieRemoteId { Id = $"tt{id}", SourceName = "IMDB" }]
            });
        _omdb
            .Setup(x => x.GetSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string imdbId, CancellationToken _) => new OmdbSeries { Title = imdbId, Response = "True" });
        _omdb
            .Setup(x => x.GetMovieAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string imdbId, CancellationToken _) => new OmdbMovie { Title = imdbId, Response = "True" });
    }

    private void SeriesDue(params int[] ids) =>
        _seriesStore
            .Setup(x => x.GetSeriesNeedingOmdbAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ids);

    private void MoviesDue(params int[] ids) =>
        _movieStore
            .Setup(x => x.GetMoviesNeedingOmdbAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ids);

    private UpdateOmdbInfoTimer SeriesJob() => new(
        _seriesStore.Object, _omdb.Object, _budget.Object, _tvDb.Object, Options.Create(_options), NullLogger<UpdateOmdbInfoTimer>.Instance);

    private UpdateMovieOmdbInfoTimer MovieJob() => new(
        _movieStore.Object, _omdb.Object, _budget.Object, _tvDb.Object, Options.Create(_options), NullLogger<UpdateMovieOmdbInfoTimer>.Instance);

    // ---- series ------------------------------------------------------------------

    [Test]
    public async Task SeriesJob_Should_AskForAtMostThirtySeries_NotRefreshedInThirtyDays()
    {
        SeriesDue();
        var before = DateTime.UtcNow;

        await SeriesJob().Execute(Mock.Of<IJobExecutionContext>());

        _seriesStore.Verify(x => x.GetSeriesNeedingOmdbAsync(
            It.IsInRange(before.AddDays(-30), DateTime.UtcNow.AddDays(-30), Moq.Range.Inclusive), 30, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task SeriesJob_Should_LookUpAndStoreEachSeries_PayingOnePermitEach()
    {
        SeriesDue(1, 2);

        await SeriesJob().Execute(Mock.Of<IJobExecutionContext>());

        _seriesStore.Verify(x => x.UpsertAsync(1, "tt1", It.Is<OmdbSeries>(d => d.Title == "tt1"), It.IsAny<CancellationToken>()), Times.Once);
        _seriesStore.Verify(x => x.UpsertAsync(2, "tt2", It.Is<OmdbSeries>(d => d.Title == "tt2"), It.IsAny<CancellationToken>()), Times.Once);
        _budget.Verify(x => x.TryAcquire(), Times.Exactly(2));
    }

    [Test]
    public async Task SeriesJob_Should_CarryOn_WhenOneSeriesFails()
    {
        SeriesDue(1, 2, 3);
        _omdb.Setup(x => x.GetSeriesAsync("tt2", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("OMDb 503"));

        await SeriesJob().Execute(Mock.Of<IJobExecutionContext>());

        _seriesStore.Verify(x => x.UpsertAsync(1, "tt1", It.IsAny<OmdbSeries?>(), It.IsAny<CancellationToken>()), Times.Once);
        _seriesStore.Verify(x => x.UpsertAsync(3, "tt3", It.IsAny<OmdbSeries?>(), It.IsAny<CancellationToken>()), Times.Once);
        _seriesStore.Verify(x => x.UpsertAsync(2, It.IsAny<string?>(), It.IsAny<OmdbSeries?>(), It.IsAny<CancellationToken>()), Times.Never,
            "a failed lookup must not be recorded as \"checked, nothing found\" — it stays due for the next run");
    }

    [Test]
    public async Task SeriesJob_Should_StopForTheDay_WhenTheBudgetRunsOut_WithoutMarkingTheRestAsChecked()
    {
        SeriesDue(1, 2, 3);
        _budget.SetupSequence(x => x.TryAcquire()).Returns(true).Returns(false);

        await SeriesJob().Execute(Mock.Of<IJobExecutionContext>());

        _omdb.Verify(x => x.GetSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _seriesStore.Verify(x => x.UpsertAsync(1, "tt1", It.IsAny<OmdbSeries?>(), It.IsAny<CancellationToken>()), Times.Once);
        _seriesStore.Verify(x => x.UpsertAsync(It.IsIn(2, 3), It.IsAny<string?>(), It.IsAny<OmdbSeries?>(), It.IsAny<CancellationToken>()), Times.Never);
        _budget.Verify(x => x.TryAcquire(), Times.Exactly(2), "it stops asking after the first refusal");
    }

    [Test]
    public async Task SeriesJob_Should_RecordASeriesWithoutAnImdbId_WithoutSpendingAPermit()
    {
        SeriesDue(1);
        _tvDb
            .Setup(x => x.GetSeriesAggregateByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesAggregate { TvdbId = 1, RemoteIds = [new SeriesRemoteId { Id = "12345", SourceName = "TheMovieDB" }] });

        await SeriesJob().Execute(Mock.Of<IJobExecutionContext>());

        _seriesStore.Verify(x => x.UpsertAsync(1, null, null, It.IsAny<CancellationToken>()), Times.Once,
            "the dated marker row keeps it from being re-checked every hour");
        _budget.Verify(x => x.TryAcquire(), Times.Never);
        _omdb.VerifyNoOtherCalls();
    }

    [Test]
    public async Task SeriesJob_Should_StoreAnEmptyResult_WhenOmdbHasNothingForTheId()
    {
        SeriesDue(1);
        _omdb.Setup(x => x.GetSeriesAsync("tt1", It.IsAny<CancellationToken>())).ReturnsAsync((OmdbSeries?)null);

        await SeriesJob().Execute(Mock.Of<IJobExecutionContext>());

        _seriesStore.Verify(x => x.UpsertAsync(1, "tt1", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task BothJobs_Should_DoNothing_WhenNoApiKeyIsConfigured()
    {
        _options.ApiKey = "";
        SeriesDue(1);
        MoviesDue(1);

        await SeriesJob().Execute(Mock.Of<IJobExecutionContext>());
        await MovieJob().Execute(Mock.Of<IJobExecutionContext>());

        _seriesStore.VerifyNoOtherCalls();
        _movieStore.VerifyNoOtherCalls();
        _omdb.VerifyNoOtherCalls();
        _budget.VerifyNoOtherCalls();
    }

    // ---- movies ------------------------------------------------------------------

    [Test]
    public async Task MovieJob_Should_AskForAtMostThirtyMovies_AndStoreEachResult()
    {
        MoviesDue(7, 8);

        await MovieJob().Execute(Mock.Of<IJobExecutionContext>());

        _movieStore.Verify(x => x.GetMoviesNeedingOmdbAsync(It.IsAny<DateTime>(), 30, It.IsAny<CancellationToken>()), Times.Once);
        _movieStore.Verify(x => x.UpsertAsync(7, "tt7", It.Is<OmdbMovie>(d => d.Title == "tt7"), It.IsAny<CancellationToken>()), Times.Once);
        _movieStore.Verify(x => x.UpsertAsync(8, "tt8", It.Is<OmdbMovie>(d => d.Title == "tt8"), It.IsAny<CancellationToken>()), Times.Once);
        _budget.Verify(x => x.TryAcquire(), Times.Exactly(2));
    }

    [Test]
    public async Task MovieJob_Should_CarryOn_WhenOneMovieFails_AndStop_WhenTheBudgetRunsOut()
    {
        MoviesDue(1, 2, 3, 4);
        _omdb.Setup(x => x.GetMovieAsync("tt1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("OMDb 503"));
        _budget.SetupSequence(x => x.TryAcquire()).Returns(true).Returns(true).Returns(false);

        await MovieJob().Execute(Mock.Of<IJobExecutionContext>());

        _movieStore.Verify(x => x.UpsertAsync(2, "tt2", It.IsAny<OmdbMovie?>(), It.IsAny<CancellationToken>()), Times.Once,
            "the failure on the first movie must not stop the second");
        _movieStore.Verify(x => x.UpsertAsync(It.IsIn(1, 3, 4), It.IsAny<string?>(), It.IsAny<OmdbMovie?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
