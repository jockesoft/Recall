using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.Favorites;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Services.Favorites;

[TestFixture]
public sealed class FavoritesServiceTests
{
    private Mock<ILikeRepository> _likeRepository = null!;
    private Mock<ITheTvDbService> _theTvDbService = null!;
    private Mock<IEpisodeWatchRepository> _episodeWatchRepository = null!;
    private Mock<IWatchProgressService> _watchProgressService = null!;
    private FavoritesService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _likeRepository = new Mock<ILikeRepository>();
        _theTvDbService = new Mock<ITheTvDbService>();
        _episodeWatchRepository = new Mock<IEpisodeWatchRepository>();
        _watchProgressService = new Mock<IWatchProgressService>();

        _episodeWatchRepository
            .Setup(x => x.GetWatchedEpisodeIdsAsync(It.IsAny<Guid>(), It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<int>());

        _watchProgressService
            .Setup(x => x.BuildProgress(It.IsAny<int>(), It.IsAny<IEnumerable<WatchableEpisode>>(), It.IsAny<IReadOnlySet<int>>()))
            .Returns((int seriesId, IEnumerable<WatchableEpisode> _, IReadOnlySet<int> _) => new SeriesWatchProgress
            {
                SeriesTvdbId = seriesId,
                OrderedEpisodes = [],
                WatchedEpisodeIds = new HashSet<int>(),
                ReleasedCount = 3,
                WatchedReleasedCount = 1
            });

        _sut = new FavoritesService(
            _likeRepository.Object,
            _theTvDbService.Object,
            _episodeWatchRepository.Object,
            _watchProgressService.Object,
            NullLogger<FavoritesService>.Instance);
    }

    private void SetUpLikes(IReadOnlyList<UserLike> seriesLikes, IReadOnlyList<UserLike> movieLikes)
    {
        _likeRepository
            .Setup(x => x.GetLikesAsync(It.IsAny<Guid>(), LikeTargetType.Series, It.IsAny<CancellationToken>()))
            .ReturnsAsync(seriesLikes);
        _likeRepository
            .Setup(x => x.GetLikesAsync(It.IsAny<Guid>(), LikeTargetType.Movie, It.IsAny<CancellationToken>()))
            .ReturnsAsync(movieLikes);
    }

    [Test]
    public async Task GetLikedTitlesAsync_Should_MergeSeriesAndMovies_OrderedByCreatedUtcDescending()
    {
        var now = DateTime.UtcNow;
        SetUpLikes(
            seriesLikes: [new UserLike(LikeTargetType.Series, 1, 1, now.AddMinutes(-10))],
            movieLikes: [new UserLike(LikeTargetType.Movie, 2, 2, now.AddMinutes(-5))]);

        _theTvDbService
            .Setup(x => x.GetSeriesAggregateByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesAggregate { TvdbId = 1, Name = "Some Series" });
        _theTvDbService
            .Setup(x => x.GetMovieAggregateByIdAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MovieAggregate { TvdbId = 2, Name = "Some Movie" });

        var result = await _sut.GetLikedTitlesAsync(Guid.NewGuid(), limit: null);

        result.Should().HaveCount(2);
        result[0].Type.Should().Be(SearchResultType.Movie);
        result[0].Name.Should().Be("Some Movie");
        result[1].Type.Should().Be(SearchResultType.Series);
        result[1].Name.Should().Be("Some Series");
    }

    [Test]
    public async Task GetLikedTitlesAsync_Should_RespectLimit_AcrossBothTypes()
    {
        var now = DateTime.UtcNow;
        SetUpLikes(
            seriesLikes:
            [
                new UserLike(LikeTargetType.Series, 1, 1, now.AddMinutes(-1)),
                new UserLike(LikeTargetType.Series, 2, 2, now.AddMinutes(-30))
            ],
            movieLikes: [new UserLike(LikeTargetType.Movie, 3, 3, now.AddMinutes(-2))]);

        _theTvDbService
            .Setup(x => x.GetSeriesAggregateByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, CancellationToken _) => new SeriesAggregate { TvdbId = id, Name = $"Series {id}" });
        _theTvDbService
            .Setup(x => x.GetMovieAggregateByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, CancellationToken _) => new MovieAggregate { TvdbId = id, Name = $"Movie {id}" });

        var result = await _sut.GetLikedTitlesAsync(Guid.NewGuid(), limit: 2);

        result.Should().HaveCount(2);
        result[0].Name.Should().Be("Series 1"); // liked 1 min ago
        result[1].Name.Should().Be("Movie 3");  // liked 2 min ago — series 2 (30 min ago) is trimmed
    }

    [Test]
    public async Task GetLikedTitlesAsync_Should_SkipTitle_WhenAggregateCannotBeLoaded()
    {
        var now = DateTime.UtcNow;
        SetUpLikes(
            seriesLikes: [new UserLike(LikeTargetType.Series, 1, 1, now)],
            movieLikes: [new UserLike(LikeTargetType.Movie, 2, 2, now)]);

        _theTvDbService
            .Setup(x => x.GetSeriesAggregateByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesAggregate?)null);
        _theTvDbService
            .Setup(x => x.GetMovieAggregateByIdAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MovieAggregate { TvdbId = 2, Name = "Still Here" });

        var result = await _sut.GetLikedTitlesAsync(Guid.NewGuid(), limit: null);

        result.Should().ContainSingle().Which.Name.Should().Be("Still Here");
    }

    [Test]
    public async Task GetLikedTitlesAsync_Should_ReturnEmpty_WhenNoLikes()
    {
        SetUpLikes(seriesLikes: [], movieLikes: []);

        var result = await _sut.GetLikedTitlesAsync(Guid.NewGuid(), limit: null);

        result.Should().BeEmpty();
        _theTvDbService.Verify(x => x.GetSeriesAggregateByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _theTvDbService.Verify(x => x.GetMovieAggregateByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task GetLikedTitlesAsync_Should_SetMovieProgressToZero_AndUseReleaseDateAsFirstAired()
    {
        SetUpLikes(
            seriesLikes: [],
            movieLikes: [new UserLike(LikeTargetType.Movie, 5, 5, DateTime.UtcNow)]);

        _theTvDbService
            .Setup(x => x.GetMovieAggregateByIdAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MovieAggregate { TvdbId = 5, Name = "A Movie", ReleaseDate = new DateOnly(2023, 7, 21) });

        var result = await _sut.GetLikedTitlesAsync(Guid.NewGuid(), limit: null);

        var movie = result.Should().ContainSingle().Which;
        movie.WatchedEpisodes.Should().Be(0);
        movie.ReleasedEpisodes.Should().Be(0);
        movie.FirstAired.Should().Be(new DateOnly(2023, 7, 21));
    }
}
