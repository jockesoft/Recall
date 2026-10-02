using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages.Account;
using Recall.Web.Services;
using Recall.Web.Services.Digest;
using Recall.Web.Services.Favorites;
using Recall.Web.Services.Favorites.Models;
using Recall.Web.Services.Stats;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Pages;

[TestFixture]
public class ProfileModelTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private Mock<IWatchlistImportRepository> _imports = null!;
    private Mock<IFavoritesService> _favorites = null!;
    private Mock<IStatsService> _stats = null!;
    private Mock<IAppUserRepository> _users = null!;
    private DigestOptions _digestOptions = null!;
    private ProfileModel _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.UserId).Returns(UserId);
        currentUser.SetupGet(x => x.DisplayName).Returns("dev-user");

        _imports = new Mock<IWatchlistImportRepository>();
        _users = new Mock<IAppUserRepository>();
        _digestOptions = new DigestOptions { Enabled = true };
        _favorites = new Mock<IFavoritesService>();
        _favorites
            .Setup(x => x.GetLikedTitlesAsync(UserId, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FavoriteTitle>());
        _stats = new Mock<IStatsService>();
        _stats
            .Setup(x => x.GetAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(UserStats.Empty with { Totals = new StatsTotals(60 * 24 * 33, Episodes: 400, Movies: 3, SeriesFinished: 2) });

        _sut = new ProfileModel(
            currentUser.Object,
            _stats.Object,
            _favorites.Object,
            Mock.Of<ILikeRepository>(),
            _imports.Object,
            _users.Object,
            Options.Create(_digestOptions),
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

        // The total is the Stats page's own (episodes and movies), put into words.
        _sut.WatchTime.Readable.Should().Be("1 month, 3 days");
        _sut.WatchTime.Across.Should().Be("400 episodes and 3 movies");
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

    // ---- weekly email switch ----------------------------------------------------

    [Test]
    public async Task TheDigestSwitch_Should_BeOff_UntilTheUserTurnsItOn()
    {
        _users.Setup(x => x.GetByIdAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppUserEntity { Id = UserId, Username = "dev-user", Email = "dev@example.com" });

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.DigestAvailable.Should().BeTrue();
        _sut.DigestOn.Should().BeFalse("the digest is opt-in");
    }

    [Test]
    public async Task TheDigestSwitch_Should_ShowOn_ForAUserWhoOptedIn()
    {
        _users.Setup(x => x.GetByIdAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppUserEntity { Id = UserId, DigestOptedInUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) });

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.DigestOn.Should().BeTrue();
    }

    [TestCase(true, "Weekly email is on. It arrives on Fridays, when there is something new.")]
    [TestCase(false, "Weekly email is off.")]
    public async Task SetDigest_Should_StoreTheChoice_AndConfirmIt(bool on, string expectedToast)
    {
        var result = await _sut.OnPostSetDigestAsync(on, CancellationToken.None);

        result.Should().BeOfType<RedirectToPageResult>();
        _users.Verify(x => x.SetDigestOptInAsync(UserId, on, It.IsAny<CancellationToken>()), Times.Once);
        _sut.SuccessToast().Should().Be(expectedToast);
    }

    [Test]
    public async Task SetDigest_Should_DoNothing_WhenTheDigestIsNotEnabledOnThisInstallation()
    {
        _digestOptions.Enabled = false;

        await _sut.OnPostSetDigestAsync(true, CancellationToken.None);

        _sut.DigestAvailable.Should().BeFalse();
        _users.Verify(x => x.SetDigestOptInAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
