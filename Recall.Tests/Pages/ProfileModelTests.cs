using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages.Account;
using Recall.Web.Services;
using Recall.Web.Services.Favorites;
using Recall.Web.Services.Favorites.Models;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Pages;

[TestFixture]
public class ProfileModelTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private Mock<IWatchlistImportRepository> _imports = null!;
    private Mock<IFavoritesService> _favorites = null!;
    private Mock<IWatchTimeService> _watchTime = null!;
    private ProfileModel _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.UserId).Returns(UserId);
        currentUser.SetupGet(x => x.DisplayName).Returns("dev-user");

        _imports = new Mock<IWatchlistImportRepository>();
        _favorites = new Mock<IFavoritesService>();
        _favorites
            .Setup(x => x.GetLikedTitlesAsync(UserId, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FavoriteTitle>());
        _watchTime = new Mock<IWatchTimeService>();
        _watchTime
            .Setup(x => x.GetTotalWatchTimeAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WatchTimeSummary(60 * 24 * 33, 400));

        _sut = new ProfileModel(
            currentUser.Object,
            _watchTime.Object,
            _favorites.Object,
            Mock.Of<ILikeRepository>(),
            _imports.Object,
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<ProfileModel>.Instance).WithTempData();
    }

    private void LatestJobIs(WatchlistImportJobStatus status, int processed, int imported = 0, int skipped = 0, int notFound = 0, int failed = 0) =>
        _imports
            .Setup(x => x.GetLatestJobForUserAsync(UserId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WatchlistImportJob(
                Guid.NewGuid(), UserId, "ratings.csv", status, 40, processed, imported, skipped, notFound, failed,
                new DateTime(2026, 9, 25, 8, 0, 0, DateTimeKind.Utc), null, []));

    [Test]
    public async Task OnGet_Should_LoadTheHeaderStat_AndOneRowOfFavorites()
    {
        await _sut.OnGetAsync(CancellationToken.None);

        _sut.WatchTime.Readable.Should().Be("1 month, 3 days");
        _favorites.Verify(x => x.GetLikedTitlesAsync(UserId, ProfileModel.FavoritesPreviewCount, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ImportSummary_Should_Invite_WhenThereHasNeverBeenAnImport()
    {
        await _sut.OnGetAsync(CancellationToken.None);

        _sut.ImportSummary.Should().StartWith("Bring in your ratings");
    }

    [Test]
    public async Task ImportSummary_Should_ShowProgress_WhileAnImportRuns()
    {
        LatestJobIs(WatchlistImportJobStatus.Processing, processed: 12);

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.ImportSummary.Should().Be("Importing ratings.csv: 12 of 40 rows matched so far.");
    }

    [Test]
    public async Task ImportSummary_Should_GiveTheOutcome_InTheImportPagesWords_LeavingOutWhatIsZero()
    {
        LatestJobIs(WatchlistImportJobStatus.Completed, processed: 40, imported: 30, skipped: 7, notFound: 3);

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.ImportSummary.Should().Be("Last import: ratings.csv, Fri, Sep 25. 30 imported, 7 skipped, 3 no match.");
    }
}
