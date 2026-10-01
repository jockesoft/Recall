using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Recall.Web.Domain.Omdb;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.External.Omdb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.OmdbCache;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages.Episodes;
using Recall.Web.Services;
using Recall.Web.Services.External.Omdb;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Pages;

/// <summary>
/// Episodes/Details is the one place OMDb is called on a request path. It is
/// also public and listed in the sitemap, so an anonymous visitor (or a
/// crawler) must never be able to trigger that call.
/// </summary>
[TestFixture]
public class EpisodeDetailsOmdbTests
{
    private const int EpisodeId = 5001;
    private const string ImdbId = "tt1234567";

    private Mock<ITheTvDbService> _tvDbService = null!;
    private Mock<ICurrentUserService> _currentUser = null!;
    private Mock<IRatingRepository> _ratingRepository = null!;
    private Mock<IOmdbApiClient> _omdbApiClient = null!;
    private Mock<IEpisodeOmdbSnapshotStore> _snapshotStore = null!;
    private Mock<IOmdbRequestBudget> _budget = null!;

    [SetUp]
    public void SetUp()
    {
        _tvDbService = new Mock<ITheTvDbService>();
        _tvDbService
            .Setup(x => x.GetEpisodeDetailsAsync(EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Episode
            {
                Id = EpisodeId,
                SeriesId = 42,
                Name = "Pilot",
                Aired = "2025-01-01",
                RemoteIds = [new EpisodeRemoteId { Id = ImdbId, SourceName = "IMDB" }]
            });

        _currentUser = new Mock<ICurrentUserService>();

        _ratingRepository = new Mock<IRatingRepository>();
        _ratingRepository
            .Setup(x => x.GetSummaryAsync(RatingTargetType.Episode, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(RatingSummary.Empty);

        _omdbApiClient = new Mock<IOmdbApiClient>();
        _snapshotStore = new Mock<IEpisodeOmdbSnapshotStore>();

        _budget = new Mock<IOmdbRequestBudget>();
        _budget.Setup(x => x.TryAcquire()).Returns(true);
    }

    private void SignIn()
    {
        var userId = Guid.NewGuid();
        _currentUser.SetupGet(x => x.IsAuthenticated).Returns(true);
        _currentUser.SetupGet(x => x.ExternalUserId).Returns(userId.ToString());
        _currentUser.SetupGet(x => x.UserId).Returns(userId);
    }

    private DetailsModel CreateSut() => new(
        NullLogger<DetailsModel>.Instance,
        _tvDbService.Object,
        _currentUser.Object,
        Mock.Of<IEpisodeWatchRepository>(),
        Mock.Of<IWatchProgressService>(),
        Mock.Of<ILikeRepository>(),
        _ratingRepository.Object,
        _omdbApiClient.Object,
        _snapshotStore.Object,
        _budget.Object,
        Options.Create(new OmdbOptions { ApiKey = "configured" }))
    {
        // So an unexpected exception surfaces as a failed assertion on the
        // result rather than a NullReferenceException in the error-toast path.
        TempData = new TempDataDictionary(new DefaultHttpContext(), Mock.Of<ITempDataProvider>())
    };

    private void VerifyOmdbWasNeverCalled()
    {
        _omdbApiClient.VerifyNoOtherCalls();
        _budget.Verify(x => x.TryAcquire(), Times.Never, "an anonymous request must not spend the shared daily budget either");
        _snapshotStore.Verify(
            x => x.UpsertAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<OmdbSeries?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Anonymous_Should_NotCallOmdb_WhenTheEpisodeHasNeverBeenLookedUp()
    {
        var sut = CreateSut();

        var result = await sut.OnGetAsync(EpisodeId, CancellationToken.None);

        result.Should().BeOfType<PageResult>();
        sut.ImdbId.Should().Be(ImdbId);
        sut.Omdb.Should().BeNull("nothing is cached, and an anonymous visitor doesn't get a live lookup");
        VerifyOmdbWasNeverCalled();
    }

    [Test]
    public async Task Anonymous_Should_SeeTheCachedRating_EvenWhenItIsDueARefresh_WithoutCallingOmdb()
    {
        var cached = new OmdbSeries { Title = "Pilot", ImdbRating = "8.4", Response = "True" };
        _snapshotStore
            .Setup(x => x.GetAsync(EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cached);
        _snapshotStore
            .Setup(x => x.GetRetrievedUtcAsync(EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTime.UtcNow.AddDays(-90));
        var sut = CreateSut();

        await sut.OnGetAsync(EpisodeId, CancellationToken.None);

        sut.Omdb.Should().BeSameAs(cached);
        VerifyOmdbWasNeverCalled();
    }

    [Test]
    public async Task SignedIn_Should_StillFetchFromOmdb_WhenTheEpisodeHasNeverBeenLookedUp()
    {
        SignIn();
        var live = new OmdbSeries { Title = "Pilot", ImdbRating = "8.4", Response = "True" };
        _omdbApiClient
            .Setup(x => x.GetByImdbIdAsync(ImdbId, "episode", It.IsAny<CancellationToken>()))
            .ReturnsAsync(live);
        var sut = CreateSut();

        var result = await sut.OnGetAsync(EpisodeId, CancellationToken.None);

        result.Should().BeOfType<PageResult>();
        sut.Omdb.Should().BeSameAs(live);
        _budget.Verify(x => x.TryAcquire(), Times.Once);
        _snapshotStore.Verify(x => x.UpsertAsync(EpisodeId, ImdbId, live, It.IsAny<CancellationToken>()), Times.Once);
    }
}
