using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Services;

[TestFixture]
public class WatchProgressServiceTests
{
    private Mock<ITheTvDbService> _tvDbService = null!;
    private Mock<IEpisodeWatchRepository> _watchRepository = null!;
    private WatchProgressService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _tvDbService = new Mock<ITheTvDbService>();
        _watchRepository = new Mock<IEpisodeWatchRepository>();
        _sut = new WatchProgressService(
            _tvDbService.Object,
            _watchRepository.Object,
            NullLogger<WatchProgressService>.Instance);
    }

    private const int SeriesId = 42;
    private const string Past = "2025-01-01";
    private const string Future = "2999-01-01";
    private static readonly DateTime BatchStamp = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static EpisodeSummary Ep(int id, int season, int number, string? aired, bool isMovie = false) => new()
    {
        Id = id,
        SeasonNumber = season,
        EpisodeNumber = number,
        Aired = aired is null ? null : DateOnly.Parse(aired),
        IsMovie = isMovie,
        Name = $"S{season}E{number}"
    };

    private void SetupSeries(params EpisodeSummary[] episodes) =>
        _tvDbService
            .Setup(x => x.GetSeriesAggregateByIdAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesAggregate { TvdbId = SeriesId, Episodes = episodes });

    private void SetupEpisodeDetails(int episodeId, int? seriesId, string? aired) =>
        _tvDbService
            .Setup(x => x.GetEpisodeDetailsAsync(episodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Episode { Id = episodeId, SeriesId = seriesId, Aired = aired });

    private void SetupWatched(params int[] ids)
    {
        _watchRepository
            .Setup(x => x.GetWatchedEpisodeIdsAsync(It.IsAny<Guid>(), SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ids.ToHashSet());

        foreach (var id in ids)
        {
            _watchRepository
                .Setup(x => x.IsWatchedAsync(It.IsAny<Guid>(), id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        }
    }

    private IReadOnlyList<int> CaptureMarkedRange()
    {
        var marked = new List<int>();
        _watchRepository
            .Setup(x => x.MarkWatchedRangeAsync(It.IsAny<Guid>(), SeriesId, It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, int, IEnumerable<int>, CancellationToken>((_, _, ids, _) => marked.AddRange(ids))
            .ReturnsAsync(() => new WatchedBatch(marked.Count, BatchStamp));
        return marked;
    }

    private void VerifyNothingWritten()
    {
        _watchRepository.Verify(
            x => x.MarkWatchedAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _watchRepository.Verify(
            x => x.MarkWatchedRangeAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task GetOrderedEpisodesAsync_Should_DropMovies_AndSortBySeasonThenEpisode()
    {
        SetupSeries(
            Ep(3, 2, 1, "2026-01-01"),
            Ep(1, 1, 1, "2025-01-01"),
            Ep(99, 1, 5, "2025-02-01", isMovie: true),
            Ep(2, 1, 2, "2025-01-08"));

        var ordered = await _sut.GetOrderedEpisodesAsync(SeriesId);

        ordered.Select(e => e.Id).Should().ContainInOrder(1, 2, 3);
        ordered.Should().NotContain(e => e.Id == 99);
    }

    [Test]
    public async Task GetOrderedEpisodesAsync_Should_ReturnEmpty_WhenSeriesNotFound()
    {
        _tvDbService
            .Setup(x => x.GetSeriesAggregateByIdAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesAggregate?)null);

        (await _sut.GetOrderedEpisodesAsync(SeriesId)).Should().BeEmpty();
    }

    [Test]
    public async Task GetOrderedEpisodesAsync_Should_ReadTheAggregate_NotTheSeparateExtendedSnapshot()
    {
        SetupSeries(Ep(1, 1, 1, Past));

        await _sut.GetOrderedEpisodesAsync(SeriesId);

        _tvDbService.Verify(
            x => x.GetSeriesByIdExtendedAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "progress must use the same episode list the pages render from");
    }

    [Test]
    public async Task MarkWatchedThroughAsync_Should_MarkEpisodeAndAllEarlier()
    {
        SetupSeries(
            Ep(1, 1, 1, "2025-01-01"),
            Ep(2, 1, 2, "2025-01-08"),
            Ep(3, 1, 3, "2025-01-15"));
        var marked = CaptureMarkedRange();

        var result = await _sut.MarkWatchedThroughAsync(Guid.NewGuid(), SeriesId, episodeTvdbId: 2);

        result.EpisodeFound.Should().BeTrue();
        result.HasAired.Should().BeTrue();
        result.MarkedCount.Should().Be(2);
        marked.Should().Equal(1, 2);
    }

    [Test]
    public async Task MarkWatchedThroughAsync_Should_NotWriteAnything_WhenEpisodeNotInSeries()
    {
        SetupSeries(
            Ep(1, 1, 1, "2025-01-01"),
            Ep(2, 1, 2, "2025-01-08"));

        var result = await _sut.MarkWatchedThroughAsync(Guid.NewGuid(), SeriesId, episodeTvdbId: 999);

        result.EpisodeFound.Should().BeFalse();
        result.MarkedCount.Should().Be(0);
        VerifyNothingWritten();
    }

    [Test]
    public async Task MarkWatchedThroughAsync_Should_NotWriteAnything_WhenTheTargetHasNotAired()
    {
        SetupSeries(
            Ep(1, 1, 1, Past),
            Ep(2, 1, 2, Future));

        var result = await _sut.MarkWatchedThroughAsync(Guid.NewGuid(), SeriesId, episodeTvdbId: 2);

        result.EpisodeFound.Should().BeTrue();
        result.HasAired.Should().BeFalse();
        result.MarkedCount.Should().Be(0);
        VerifyNothingWritten();
    }

    [Test]
    public async Task MarkWatchedThroughAsync_Should_SkipEarlierEpisodesThatHaveNotAired_ButKeepUndatedOnes()
    {
        // Season 0 sorts first, so both specials count as "earlier" than S01E02.
        SetupSeries(
            Ep(10, 0, 1, Future),
            Ep(11, 0, 2, aired: null),
            Ep(1, 1, 1, Past),
            Ep(2, 1, 2, Past));
        var marked = CaptureMarkedRange();

        var result = await _sut.MarkWatchedThroughAsync(Guid.NewGuid(), SeriesId, episodeTvdbId: 2);

        marked.Should().Equal(11, 1, 2);
        result.MarkedCount.Should().Be(3);
    }

    [Test]
    public async Task GetPriorUnwatchedCountAsync_Should_CountOnlyWhatMarkWatchedThroughWouldWrite()
    {
        SetupSeries(
            Ep(10, 0, 1, Future),
            Ep(1, 1, 1, Past),
            Ep(2, 1, 2, Past),
            Ep(3, 1, 3, Past));
        SetupWatched(1);

        var count = await _sut.GetPriorUnwatchedCountAsync(Guid.NewGuid(), SeriesId, episodeTvdbId: 3);

        count.Should().Be(1, "only S01E02 is both earlier and markable — the unaired special is not");
    }

    [Test]
    public async Task GetSeriesProgressAsync_Should_ReturnNextUnwatchedReleasedEpisode()
    {
        SetupSeries(
            Ep(1, 1, 1, "2025-01-01"),
            Ep(2, 1, 2, "2025-01-08"),
            Ep(3, 1, 3, "2999-01-01"));
        SetupWatched(1);

        var progress = await _sut.GetSeriesProgressAsync(Guid.NewGuid(), SeriesId);

        progress.NextUnwatchedEpisode!.Id.Should().Be(2);
        progress.UnwatchedReleasedCount.Should().Be(1);
        progress.IsUpToDate.Should().BeFalse();
    }

    [Test]
    public async Task MarkEpisodeWatchedAsync_Should_Write_WhenTheEpisodeIsInTheSeriesAndHasAired()
    {
        var userId = Guid.NewGuid();
        SetupSeries(Ep(1, 1, 1, Past));

        var outcome = await _sut.MarkEpisodeWatchedAsync(userId, SeriesId, episodeTvdbId: 1);

        outcome.Should().Be(EpisodeWatchOutcome.MarkedWatched);
        _watchRepository.Verify(
            x => x.MarkWatchedAsync(userId, SeriesId, 1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task MarkEpisodeWatchedAsync_Should_Reject_AnEpisodeThatBelongsToAnotherSeries()
    {
        SetupSeries(Ep(1, 1, 1, Past));
        SetupEpisodeDetails(episodeId: 7001, seriesId: 700, aired: Past);

        var outcome = await _sut.MarkEpisodeWatchedAsync(Guid.NewGuid(), SeriesId, episodeTvdbId: 7001);

        outcome.Should().Be(EpisodeWatchOutcome.EpisodeNotInSeries);
        VerifyNothingWritten();
    }

    [Test]
    public async Task MarkEpisodeWatchedAsync_Should_Reject_AnEpisodeNobodyKnows()
    {
        SetupSeries(Ep(1, 1, 1, Past));
        _tvDbService
            .Setup(x => x.GetEpisodeDetailsAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Episode?)null);

        var outcome = await _sut.MarkEpisodeWatchedAsync(Guid.NewGuid(), SeriesId, episodeTvdbId: 999);

        outcome.Should().Be(EpisodeWatchOutcome.EpisodeNotInSeries);
        VerifyNothingWritten();
    }

    [Test]
    public async Task MarkEpisodeWatchedAsync_Should_FallBackToTheEpisodeRecord_WhenTheAggregateLacksIt()
    {
        var userId = Guid.NewGuid();
        SetupSeries(Ep(1, 1, 1, Past));
        SetupEpisodeDetails(episodeId: 5, seriesId: SeriesId, aired: Past);

        var outcome = await _sut.MarkEpisodeWatchedAsync(userId, SeriesId, episodeTvdbId: 5);

        outcome.Should().Be(EpisodeWatchOutcome.MarkedWatched);
        _watchRepository.Verify(
            x => x.MarkWatchedAsync(userId, SeriesId, 5, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task MarkEpisodeWatchedAsync_Should_Reject_AFutureAirDate_FromEitherSource()
    {
        SetupSeries(Ep(1, 1, 1, Future));
        SetupEpisodeDetails(episodeId: 5, seriesId: SeriesId, aired: Future);

        (await _sut.MarkEpisodeWatchedAsync(Guid.NewGuid(), SeriesId, 1)).Should().Be(EpisodeWatchOutcome.NotAired);
        (await _sut.MarkEpisodeWatchedAsync(Guid.NewGuid(), SeriesId, 5)).Should().Be(EpisodeWatchOutcome.NotAired);
        VerifyNothingWritten();
    }

    [Test]
    public async Task MarkEpisodeWatchedAsync_Should_Allow_AnUnknownAirDate()
    {
        SetupSeries(Ep(1, 1, 1, aired: null));

        var outcome = await _sut.MarkEpisodeWatchedAsync(Guid.NewGuid(), SeriesId, episodeTvdbId: 1);

        outcome.Should().Be(EpisodeWatchOutcome.MarkedWatched);
    }

    [Test]
    public async Task MarkEpisodeWatchedAsync_Should_Allow_AMovieFlaggedEntryOfTheSeries()
    {
        // Dropped from the ordered progress list, but still a real entry of this series.
        SetupSeries(Ep(99, 1, 5, Past, isMovie: true));

        var outcome = await _sut.MarkEpisodeWatchedAsync(Guid.NewGuid(), SeriesId, episodeTvdbId: 99);

        outcome.Should().Be(EpisodeWatchOutcome.MarkedWatched);
    }

    [Test]
    public async Task ToggleEpisodeWatchedAsync_Should_Unwatch_WithoutAnyTheTvDbLookup()
    {
        var userId = Guid.NewGuid();
        SetupWatched(1);

        var outcome = await _sut.ToggleEpisodeWatchedAsync(userId, SeriesId, episodeTvdbId: 1);

        outcome.Should().Be(EpisodeWatchOutcome.MarkedUnwatched);
        _watchRepository.Verify(x => x.MarkUnwatchedAsync(userId, 1, It.IsAny<CancellationToken>()), Times.Once);
        _tvDbService.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ToggleEpisodeWatchedAsync_Should_ValidateBeforeWatching()
    {
        SetupSeries(Ep(1, 1, 1, Past));
        SetupEpisodeDetails(episodeId: 7001, seriesId: 700, aired: Past);

        (await _sut.ToggleEpisodeWatchedAsync(Guid.NewGuid(), SeriesId, 1)).Should().Be(EpisodeWatchOutcome.MarkedWatched);
        (await _sut.ToggleEpisodeWatchedAsync(Guid.NewGuid(), SeriesId, 7001)).Should().Be(EpisodeWatchOutcome.EpisodeNotInSeries);

        _watchRepository.Verify(
            x => x.MarkWatchedAsync(It.IsAny<Guid>(), SeriesId, 7001, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task MarkWatchedThroughAsync_Should_ReturnTheBatch_SoTheCallerCanOfferAnUndo()
    {
        SetupSeries(Ep(1, 1, 1, Past), Ep(2, 1, 2, Past));
        CaptureMarkedRange();

        var result = await _sut.MarkWatchedThroughAsync(Guid.NewGuid(), SeriesId, episodeTvdbId: 2);

        result.Batch.Should().Be(new WatchedBatch(2, BatchStamp));
    }

    [Test]
    public async Task MarkSeasonWatchedAsync_Should_MarkOnlyThatSeasonsAiredEpisodes()
    {
        SetupSeries(
            Ep(1, 1, 1, Past),
            Ep(2, 1, 2, Past),
            Ep(3, 1, 3, Future),
            Ep(4, 1, 4, aired: null),
            Ep(99, 1, 5, Past, isMovie: true),
            Ep(20, 2, 1, Past));
        var marked = CaptureMarkedRange();

        var result = await _sut.MarkSeasonWatchedAsync(Guid.NewGuid(), SeriesId, seasonNumber: 1);

        result.SeasonFound.Should().BeTrue();
        marked.Should().BeEquivalentTo([1, 2, 4, 99], "the unaired episode and the other season are left alone");
        result.Batch.InsertedCount.Should().Be(4);
    }

    [Test]
    public async Task MarkSeasonWatchedAsync_Should_NotWriteAnything_ForASeasonTheSeriesDoesNotHave()
    {
        SetupSeries(Ep(1, 1, 1, Past));

        var result = await _sut.MarkSeasonWatchedAsync(Guid.NewGuid(), SeriesId, seasonNumber: 7);

        result.SeasonFound.Should().BeFalse();
        VerifyNothingWritten();
    }

    [Test]
    public async Task MarkSeasonUnwatchedAsync_Should_UnwatchEveryEpisodeOfThatSeasonOnly()
    {
        var userId = Guid.NewGuid();
        SetupSeries(
            Ep(1, 1, 1, Past),
            Ep(2, 1, 2, Future),
            Ep(20, 2, 1, Past));
        IEnumerable<int>? unwatched = null;
        _watchRepository
            .Setup(x => x.MarkUnwatchedRangeAsync(userId, It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, IEnumerable<int>, CancellationToken>((_, ids, _) => unwatched = ids.ToList())
            .ReturnsAsync(1);

        var removed = await _sut.MarkSeasonUnwatchedAsync(userId, SeriesId, seasonNumber: 1);

        removed.Should().Be(1);
        unwatched.Should().BeEquivalentTo([1, 2]);
    }
}
