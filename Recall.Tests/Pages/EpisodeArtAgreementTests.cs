using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.External.Omdb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.OmdbCache;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages;
using Recall.Web.Services;
using Recall.Web.Services.Digest;
using Recall.Web.Services.External.Omdb;
using Recall.Web.Services.Favorites;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Pages;

/// <summary>
/// One episode, three places that show its image: Episode Details, the
/// Dashboard's continue-watching card and the Favorites card. Whatever the
/// caches hold, all three must show the same thing (<see cref="EpisodeArt"/>).
/// </summary>
[TestFixture]
public sealed class EpisodeArtAgreementTests
{
    private const int SeriesId = 208111;
    private const int EpisodeId = 11961330;
    private const string Still = "https://artworks.thetvdb.com/banners/episodes/208111/still.jpg";
    private const string RecordStill = "https://artworks.thetvdb.com/banners/v4/episode/11961330/screencap/later.jpg";
    private const string Background = "https://artworks.thetvdb.com/banners/fanart/original/208111-1_t.jpg";

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 10, 2);

    private Mock<ITheTvDbService> _tvDb = null!;

    [SetUp]
    public void SetUp() => _tvDb = new Mock<ITheTvDbService>();

    /// <summary>What the two caches hold: the series aggregate's copy of the episode, and the episode's own record.</summary>
    private void Cached(string? stillInAggregate, string? stillOnRecord, string? background)
    {
        _tvDb.Setup(x => x.GetSeriesAggregateByIdAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesAggregate
            {
                TvdbId = SeriesId,
                Name = "Gold Rush",
                BackgroundUrl = background,
                Episodes =
                [
                    new EpisodeSummary
                    {
                        Id = EpisodeId, SeasonNumber = 17, EpisodeNumber = 1, Name = "The Most Gold Wins",
                        Aired = Today, Image = stillInAggregate
                    }
                ],
                RemoteIds = []
            });
        _tvDb.Setup(x => x.GetEpisodeDetailsAsync(EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Episode
            {
                Id = EpisodeId, SeriesId = SeriesId, SeasonNumber = 17, Number = 1, Name = "The Most Gold Wins",
                Aired = "2026-10-02", Image = stillOnRecord
            });
    }

    private async Task<EpisodeArt> OnEpisodeDetailsAsync()
    {
        var page = new Recall.Web.Pages.Episodes.DetailsModel(
            NullLogger<Recall.Web.Pages.Episodes.DetailsModel>.Instance,
            _tvDb.Object,
            Mock.Of<ICurrentUserService>(),
            Mock.Of<IEpisodeWatchRepository>(),
            Mock.Of<IWatchProgressService>(),
            Mock.Of<ILikeRepository>(),
            Mock.Of<IRatingRepository>(r => r.GetSummaryAsync(RatingTargetType.Episode, EpisodeId, It.IsAny<CancellationToken>())
                                            == Task.FromResult(RatingSummary.Empty)),
            Mock.Of<IOmdbApiClient>(),
            Mock.Of<IEpisodeOmdbSnapshotStore>(),
            Mock.Of<IOmdbRequestBudget>(),
            Options.Create(new OmdbOptions()),
            TimeProvider.System).WithTempData().WithHttpContext();

        await page.OnGetAsync(EpisodeId, CancellationToken.None);
        page.Episode.Should().NotBeNull();
        return page.Art;
    }

    private async Task<string?> OnTheDashboardAsync()
    {
        var library = new Mock<ITrackedSeriesRepository>();
        library.Setup(x => x.GetByUserAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TrackedSeries { Id = Guid.NewGuid(), UserId = UserId, TvdbId = SeriesId, Name = "Gold Rush", CreatedUtc = DateTime.UtcNow }]);

        var watches = new Mock<IEpisodeWatchRepository>();
        watches.Setup(x => x.GetWatchedEpisodeIdsAsync(UserId, It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<int>());
        watches.Setup(x => x.GetLastWatchedUtcBySeriesAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, DateTime>());

        var progress = new Mock<IWatchProgressService>();
        progress
            .Setup(x => x.BuildProgress(It.IsAny<int>(), It.IsAny<IEnumerable<WatchableEpisode>>(), It.IsAny<IReadOnlySet<int>>()))
            .Returns((int id, IEnumerable<WatchableEpisode> episodes, IReadOnlySet<int> watched) =>
                WatchProgressCalculator.Build(id, episodes, watched, Today));

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.IsAuthenticated).Returns(true);
        currentUser.SetupGet(x => x.UserId).Returns(UserId);

        var page = new DashboardModel(
            _tvDb.Object, library.Object, watches.Object, progress.Object, NullLogger<DashboardModel>.Instance,
            currentUser.Object,
            new FixedTimeProvider(new DateTimeOffset(Today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero)),
            Options.Create(new LibraryOptions()), Mock.Of<IAppUserRepository>(), Options.Create(new DigestOptions())).WithTempData();

        await page.OnGetAsync(CancellationToken.None);
        return page.CatchUpEpisodes.Should().ContainSingle().Which.ImageUrl;
    }

    private async Task<string?> OnFavoritesAsync()
    {
        var likes = new Mock<ILikeRepository>();
        likes.Setup(x => x.GetLikesAsync(UserId, It.IsAny<LikeTargetType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, LikeTargetType type, CancellationToken _) => type == LikeTargetType.Episode
                ? [new UserLike(LikeTargetType.Episode, EpisodeId, SeriesId, DateTime.UtcNow)]
                : []);

        var service = new FavoritesService(
            likes.Object, _tvDb.Object, Mock.Of<IEpisodeWatchRepository>(), Mock.Of<IWatchProgressService>(),
            NullLogger<FavoritesService>.Instance);

        var view = await service.GetAllFavoritesAsync(UserId, CancellationToken.None);
        return view.Episodes.Should().ContainSingle().Which.ImageUrl;
    }

    [TestCase(Still, null, Background, Still, true, Description = "the still in the series aggregate")]
    [TestCase(Still, RecordStill, Background, Still, true, Description = "both have one: the aggregate's, on every page")]
    [TestCase(null, RecordStill, Background, RecordStill, true, Description = "only the episode's own record has it yet")]
    [TestCase("", RecordStill, null, RecordStill, true, Description = "a blank still is no still")]
    [TestCase(null, null, Background, Background, false, Description = "TheTVDB has no still: the series' background art")]
    [TestCase(null, null, null, null, false, Description = "nothing at all: every page shows its placeholder")]
    public async Task EveryPage_Should_ShowTheSameImage_ForTheSameEpisode(
        string? stillInAggregate, string? stillOnRecord, string? background, string? expected, bool isStill)
    {
        Cached(stillInAggregate, stillOnRecord, background);

        var details = await OnEpisodeDetailsAsync();
        var dashboard = await OnTheDashboardAsync();
        var favorites = await OnFavoritesAsync();

        details.Url.Should().Be(expected);
        dashboard.Should().Be(expected);
        favorites.Should().Be(expected);

        details.IsStill.Should().Be(isStill);
        details.StillUrl.Should().Be(isStill ? expected : null, "structured data must not pass series art off as the episode's still");
    }

    [Test]
    public async Task TheCards_Should_NotLookUpTheEpisode_WhenTheAggregateAlreadyHasItsStill()
    {
        Cached(Still, null, Background);

        await OnTheDashboardAsync();
        await OnFavoritesAsync();

        _tvDb.Verify(x => x.GetEpisodeDetailsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ACard_Should_FallBackToTheBackgroundArt_WhenTheEpisodeLookupFails()
    {
        Cached(null, null, Background);
        _tvDb.Setup(x => x.GetEpisodeDetailsAsync(EpisodeId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("TheTVDB is away"));

        (await OnTheDashboardAsync()).Should().Be(Background);
        (await OnFavoritesAsync()).Should().Be(Background);
    }
}
