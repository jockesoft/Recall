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

    private static Episode Ep(int id, int season, int number, string? aired, bool isMovie = false) => new()
    {
        Id = id,
        SeasonNumber = season,
        Number = number,
        Aired = aired,
        IsMovie = isMovie,
        Name = $"S{season}E{number}"
    };

    private void SetupSeries(params Episode[] episodes) =>
        _tvDbService
            .Setup(x => x.GetSeriesByIdExtendedAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Series { Id = 42, Episodes = episodes });

    private void SetupWatched(params int[] ids) =>
        _watchRepository
            .Setup(x => x.GetWatchedEpisodeIdsAsync(It.IsAny<Guid>(), 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ids.ToHashSet());

    [Test]
    public async Task GetOrderedEpisodesAsync_Should_DropMovies_AndSortBySeasonThenEpisode()
    {
        SetupSeries(
            Ep(3, 2, 1, "2026-01-01"),
            Ep(1, 1, 1, "2025-01-01"),
            Ep(99, 1, 5, "2025-02-01", isMovie: true),
            Ep(2, 1, 2, "2025-01-08"));

        var ordered = await _sut.GetOrderedEpisodesAsync(42);

        ordered.Select(e => e.Id).Should().ContainInOrder(1, 2, 3);
        ordered.Should().NotContain(e => e.Id == 99);
    }

    [Test]
    public async Task GetOrderedEpisodesAsync_Should_ReturnEmpty_WhenSeriesNotFound()
    {
        _tvDbService
            .Setup(x => x.GetSeriesByIdExtendedAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Series?)null);

        (await _sut.GetOrderedEpisodesAsync(42)).Should().BeEmpty();
    }

    [Test]
    public async Task MarkWatchedThroughAsync_Should_MarkEpisodeAndAllEarlier()
    {
        SetupSeries(
            Ep(1, 1, 1, "2025-01-01"),
            Ep(2, 1, 2, "2025-01-08"),
            Ep(3, 1, 3, "2025-01-15"));

        IEnumerable<int>? marked = null;
        _watchRepository
            .Setup(x => x.MarkWatchedRangeAsync(It.IsAny<Guid>(), 42, It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, int, IEnumerable<int>, CancellationToken>((_, _, ids, _) => marked = ids.ToList())
            .Returns(Task.CompletedTask);

        var result = await _sut.MarkWatchedThroughAsync(Guid.NewGuid(), 42, episodeTvdbId: 2);

        result.EpisodeFound.Should().BeTrue();
        result.MarkedCount.Should().Be(2);
        marked.Should().Equal(1, 2);
    }

    [Test]
    public async Task GetSeriesProgressAsync_Should_ReturnNextUnwatchedReleasedEpisode()
    {
        SetupSeries(
            Ep(1, 1, 1, "2025-01-01"),
            Ep(2, 1, 2, "2025-01-08"),
            Ep(3, 1, 3, "2999-01-01"));
        SetupWatched(1);

        var progress = await _sut.GetSeriesProgressAsync(Guid.NewGuid(), 42);

        progress.NextUnwatchedEpisode!.Id.Should().Be(2);
        progress.UnwatchedReleasedCount.Should().Be(1);
        progress.IsUpToDate.Should().BeFalse();
    }
}
