using Moq;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Episodes;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Search;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;
using Recall.Web.Services;
using Recall.Web.Services.External.TheTvDb;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Caching;
using Recall.Web.Infrastructure.Persistence.TvdbCache;

namespace Recall.Tests.Services;

[TestFixture]
public class TheTvDbServiceTests
{
    private Mock<ITheTvDbApiClient> _apiClient = null!;
    private Mock<IDistributedCacheJson> _cache = null!;
    private Mock<ITvdbSnapshotStore> _store = null!;
    private TheTvDbService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _apiClient = new Mock<ITheTvDbApiClient>();
        _cache = new Mock<IDistributedCacheJson>();
        _store = new Mock<ITvdbSnapshotStore>();
        _sut = new TheTvDbService(_apiClient.Object, _cache.Object, _store.Object, NullLogger<TheTvDbService>.Instance);
    }

    [Test]
    public async Task SearchSeriesAsync_Should_MapDtos_ToSummaries()
    {
        _apiClient
            .Setup(x => x.SearchSeriesAsync("dark", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SearchResultDto>
            {
                new()
                {
                    TvdbId = 1,
                    Name = "Dark",
                    Type = "series",
                    Year = "2017",
                    Overview = "A family saga with a supernatural twist."
                }
            });

        var result = await _sut.SearchSeriesAsync("dark");

        result.Should().HaveCount(1);
        result[0].TvdbId.Should().Be(1);
        result[0].Name.Should().Be("Dark");
        result[0].Year.Should().Be("2017");
        result[0].Overview.Should().Be("A family saga with a supernatural twist.");
    }

    [Test]
    public async Task SearchSeriesAsync_Should_FilterOut_NonSeriesTypes()
    {
        _apiClient
            .Setup(x => x.SearchSeriesAsync("batman", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SearchResultDto>
            {
                new() { TvdbId = 10, Name = "Batman Begins", Type = "movie" },
                new() { TvdbId = 11, Name = "The Batman", Type = "series" },
                new() { TvdbId = 12, Name = "Unknown Type Item", Type = null }
            });

        var result = await _sut.SearchSeriesAsync("batman");

        result.Should().HaveCount(2);
        result.Select(x => x.TvdbId).Should().BeEquivalentTo([11, 12]);
    }

    [Test]
    public async Task GetSeriesByIdAsync_Should_MapAggregate_ToDetails()
    {
        _apiClient
            .Setup(x => x.GetSeriesAggregateByIdAsync(10, "eng", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesAggregate
            {
                TvdbId = 10,
                Name = "Breaking Bad",
                Slug = "breaking-bad",
                ImageUrl = "https://example.com/breakingbad.jpg",
                FirstAired = new DateOnly(2008, 1, 20),
                Score = 9.5
            });

        var result = await _sut.GetSeriesByIdAsync(10);

        result.Should().NotBeNull();
        result!.TvdbId.Should().Be(10);
        result.Name.Should().Be("Breaking Bad");
        result.Slug.Should().Be("breaking-bad");
        result.FirstAired.Should().Be("2008-01-20");
        result.Score.Should().Be(9.5);
    }

    [Test]
    public async Task GetSeriesByIdAsync_Should_ReturnNull_WhenApiReturnsNull()
    {
        _apiClient
            .Setup(x => x.GetSeriesAggregateByIdAsync(404, "eng", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesAggregate?)null);

        var result = await _sut.GetSeriesByIdAsync(404);

        result.Should().BeNull();
    }

    [Test]
    public async Task GetSeriesAggregateByIdAsync_Should_NormalizeRelativeImagePaths_FromACacheEntryPredatingTheFix()
    {
        // Regression test: a SeriesAggregate cached before ArtworkUrl.Normalize was
        // applied at the mapping layer would otherwise keep serving broken
        // relative image paths (resolving against the app's own origin) forever,
        // since GetLayeredAsync's tiers have no staleness check of their own.
        var stale = new SeriesAggregate
        {
            TvdbId = 7,
            Name = "Cached Show",
            ImageUrl = "/banners/series/7.jpg",
            Episodes = [new EpisodeSummary { Id = 1, Name = "Pilot", Image = "/banners/v4/episode/11884343/screencap/6a9a171d96372.jpg" }]
        };
        _cache
            .Setup(c => c.GetAsync<SeriesAggregate>("series:aggregate:v1:7:eng", It.IsAny<CancellationToken>()))
            .ReturnsAsync(stale);

        var result = await _sut.GetSeriesAggregateByIdAsync(7);

        result!.ImageUrl.Should().Be("https://artworks.thetvdb.com/banners/series/7.jpg");
        result.Episodes.Single().Image.Should().Be("https://artworks.thetvdb.com/banners/v4/episode/11884343/screencap/6a9a171d96372.jpg");
    }

    [Test]
    public async Task GetSeriesAggregateByIdAsync_Should_ReturnFromCache_WithoutTouchingStoreOrApi()
    {
        var cached = new SeriesAggregate { TvdbId = 7, Name = "Cached Show" };
        _cache
            .Setup(c => c.GetAsync<SeriesAggregate>("series:aggregate:v1:7:eng", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cached);

        var result = await _sut.GetSeriesAggregateByIdAsync(7);

        result.Should().BeEquivalentTo(cached, "the service defensively re-normalizes images, so it returns a fresh copy rather than the same reference");
        _store.Verify(s => s.GetSeriesAggregateAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _apiClient.Verify(a => a.GetSeriesAggregateByIdAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task GetSeriesAggregateByIdAsync_Should_ReturnFromStore_AndWarmCache_WhenCacheMisses()
    {
        var stored = new SeriesAggregate { TvdbId = 8, Name = "Stored Show" };
        _store
            .Setup(s => s.GetSeriesAggregateAsync(8, "eng", It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);

        var result = await _sut.GetSeriesAggregateByIdAsync(8);

        result.Should().BeEquivalentTo(stored, "the service defensively re-normalizes images, so it returns a fresh copy rather than the same reference");
        _apiClient.Verify(a => a.GetSeriesAggregateByIdAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _cache.Verify(c => c.SetAsync("series:aggregate:v1:8:eng", stored, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task GetSeriesAggregateByIdAsync_Should_FetchFromApi_AndPersist_WhenCacheAndStoreMiss()
    {
        var fresh = new SeriesAggregate { TvdbId = 9, Name = "Fresh Show" };
        _apiClient
            .Setup(a => a.GetSeriesAggregateByIdAsync(9, "eng", It.IsAny<CancellationToken>()))
            .ReturnsAsync(fresh);

        var result = await _sut.GetSeriesAggregateByIdAsync(9);

        result.Should().BeEquivalentTo(fresh, "the service defensively re-normalizes images, so it returns a fresh copy rather than the same reference");
        _store.Verify(s => s.SaveSeriesAggregateAsync(fresh, "eng", It.IsAny<CancellationToken>()), Times.Once);
        _cache.Verify(c => c.SetAsync("series:aggregate:v1:9:eng", fresh, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task GetSeriesByIdExtendedAsync_Should_MapDtoToDomain_AndPersist_WhenTiersMiss()
    {
        _apiClient
            .Setup(a => a.GetSeriesByIdExtendedAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesDataDto { Id = 5, Name = "Extended Show" });

        var result = await _sut.GetSeriesByIdExtendedAsync(5);

        result.Should().NotBeNull();
        result!.Id.Should().Be(5);
        result.Name.Should().Be("Extended Show");
        _store.Verify(s => s.SaveSeriesExtendedAsync(It.Is<Series>(x => x.Id == 5), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task RefreshSeriesAggregateByIdAsync_BackfillsMatchingEpisodeImages()
    {
        var fresh = new SeriesAggregate
        {
            TvdbId = 20,
            Name = "Backfill Show",
            Episodes = [new EpisodeSummary { Id = 2001, Name = "Ep", Image = "https://example.com/still.jpg" }]
        };
        _apiClient
            .Setup(a => a.GetSeriesAggregateByIdAsync(20, "eng", It.IsAny<CancellationToken>()))
            .ReturnsAsync(fresh);
        _store
            .Setup(s => s.GetEpisodesExtendedAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, Episode>());

        var patchedEpisode = new Episode { Id = 2001, Name = "Ep", Image = "https://example.com/still.jpg" };
        _store
            .Setup(s => s.BackfillEpisodeImagesFromAggregateAsync(
                It.Is<SeriesAggregate>(a => a.TvdbId == 20), It.IsAny<CancellationToken>()))
            .ReturnsAsync([patchedEpisode]);

        var result = await _sut.RefreshSeriesAggregateByIdAsync(20);

        result.Should().BeTrue();
        _store.Verify(
            s => s.BackfillEpisodeImagesFromAggregateAsync(It.Is<SeriesAggregate>(a => a.TvdbId == 20), It.IsAny<CancellationToken>()),
            Times.Once);
        _cache.Verify(
            c => c.SetAsync("episode:extended:v2:2001:eng", patchedEpisode, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task RefreshSeriesAggregateByIdAsync_ReusesCachedEpisodeTranslation_InsteadOfCallingApi()
    {
        var fresh = new SeriesAggregate
        {
            TvdbId = 21,
            Name = "Show",
            Episodes = [new EpisodeSummary { Id = 3001, Name = "Original Language Title", Overview = "Original overview." }]
        };
        _apiClient
            .Setup(a => a.GetSeriesAggregateByIdAsync(21, "eng", It.IsAny<CancellationToken>()))
            .ReturnsAsync(fresh);
        _store
            .Setup(s => s.GetEpisodesExtendedAsync(It.Is<IReadOnlyCollection<int>>(ids => ids.Contains(3001)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, Episode>
            {
                [3001] = new Episode { Id = 3001, Name = "Cached English Title", Overview = "Cached English overview." }
            });
        _store
            .Setup(s => s.BackfillEpisodeImagesFromAggregateAsync(It.IsAny<SeriesAggregate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Episode>());

        await _sut.RefreshSeriesAggregateByIdAsync(21);

        _apiClient.Verify(
            a => a.GetEpisodeTranslationByLanguageAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _store.Verify(
            s => s.UpsertSeriesAggregateAsync(
                It.Is<SeriesAggregate>(a => a.Episodes[0].Name == "Cached English Title" && a.Episodes[0].Overview == "Cached English overview."),
                "eng",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task RefreshSeriesAggregateByIdAsync_FallsBackToTranslationApi_ForEpisodesNotCachedLocally()
    {
        var fresh = new SeriesAggregate
        {
            TvdbId = 22,
            Name = "Show",
            Episodes = [new EpisodeSummary { Id = 4001, Name = "Original Language Title", Overview = "Original overview." }]
        };
        _apiClient
            .Setup(a => a.GetSeriesAggregateByIdAsync(22, "eng", It.IsAny<CancellationToken>()))
            .ReturnsAsync(fresh);
        _store
            .Setup(s => s.GetEpisodesExtendedAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, Episode>());
        _apiClient
            .Setup(a => a.GetEpisodeTranslationByLanguageAsync(4001, "eng", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EpisodeTranslationDataDto { Name = "Live Translated Title", Overview = "Live translated overview." });
        _store
            .Setup(s => s.BackfillEpisodeImagesFromAggregateAsync(It.IsAny<SeriesAggregate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Episode>());

        await _sut.RefreshSeriesAggregateByIdAsync(22);

        _apiClient.Verify(
            a => a.GetEpisodeTranslationByLanguageAsync(4001, "eng", It.IsAny<CancellationToken>()),
            Times.Once);
        _store.Verify(
            s => s.UpsertSeriesAggregateAsync(
                It.Is<SeriesAggregate>(a => a.Episodes[0].Name == "Live Translated Title"),
                "eng",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
