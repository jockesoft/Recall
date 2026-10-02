using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Infrastructure.Display;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages.Account;
using Recall.Web.Services;
using Recall.Web.Services.Import;

namespace Recall.Tests.Pages;

[TestFixture]
public class ImportWatchlistModelTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid JobId = Guid.NewGuid();

    private Mock<IWatchlistImportRepository> _imports = null!;
    private ImportWatchlistModel _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.UserId).Returns(UserId);
        _imports = new Mock<IWatchlistImportRepository>();

        _sut = new ImportWatchlistModel(
            currentUser.Object,
            _imports.Object,
            Mock.Of<IWatchlistImportService>(),
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<ImportWatchlistModel>.Instance).WithTempData();
    }

    private static WatchlistImportItem Item(int row, WatchlistImportItemStatus status, string type = "Movie", int? rating = null, int? tvdbId = null) =>
        new(Guid.NewGuid(), JobId, UserId, row, $"tt{row:D7}", $"Title {row}", type, rating, status, tvdbId, null, DateTime.UtcNow, null);

    private void LatestJobIs(WatchlistImportJobStatus status, params WatchlistImportItem[] items) =>
        _imports
            .Setup(x => x.GetLatestJobForUserAsync(UserId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WatchlistImportJob(
                JobId, UserId, "ratings.csv", status,
                TotalCount: items.Length,
                ProcessedCount: items.Count(i => i.Status != WatchlistImportItemStatus.Pending),
                ImportedCount: 0, SkippedCount: 0, NotFoundCount: 0, FailedCount: 0,
                DateTime.UtcNow, null, items));

    [Test]
    public async Task Summary_Should_ListEveryOutcomeInOrder_CountedFromTheRows()
    {
        LatestJobIs(WatchlistImportJobStatus.Completed,
            Item(1, WatchlistImportItemStatus.Imported),
            Item(2, WatchlistImportItemStatus.Imported),
            Item(3, WatchlistImportItemStatus.AlreadyInLibrary),
            Item(4, WatchlistImportItemStatus.Unsupported),
            Item(5, WatchlistImportItemStatus.NotFound));

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Summary.Select(o => (o.Display.Label, o.Count)).Should().Equal(
            ("Imported", 2), ("Already had it", 1), ("No match", 1), ("Unsupported", 1), ("Failed", 0));
        _sut.IsProcessing.Should().BeFalse();
    }

    [Test]
    public async Task IsProcessing_Should_BeTrue_WhileRowsAreWaiting_AndWaitingRowsAreNotAnOutcome()
    {
        LatestJobIs(WatchlistImportJobStatus.Processing,
            Item(1, WatchlistImportItemStatus.Imported),
            Item(2, WatchlistImportItemStatus.Pending));

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.IsProcessing.Should().BeTrue();
        _sut.Summary.Sum(o => o.Count).Should().Be(1);
    }

    [Test]
    public async Task Summary_Should_BeEmpty_ForSomeoneWhoHasNeverImported()
    {
        await _sut.OnGetAsync(CancellationToken.None);

        _sut.LatestJob.Should().BeNull();
        _sut.Summary.Should().BeEmpty();
        _sut.IsProcessing.Should().BeFalse();
    }

    [Test]
    public void MetaLine_Should_SayTheTypeAndTheRating()
    {
        ImportWatchlistModel.MetaLine(Item(1, WatchlistImportItemStatus.Imported, "Movie", rating: 8)).Should().Be("Movie · rated 8");
        ImportWatchlistModel.MetaLine(Item(2, WatchlistImportItemStatus.Imported, "TV Series")).Should().Be("TV Series");
    }

    [Test]
    public void DetailsPage_Should_FollowTheTitleType_AndBeNullWithoutAMatch()
    {
        ImportWatchlistModel.DetailsPage(Item(1, WatchlistImportItemStatus.Imported, "Movie", tvdbId: 5)).Should().Be("/Movies/Details");
        ImportWatchlistModel.DetailsPage(Item(2, WatchlistImportItemStatus.Imported, "TV Mini Series", tvdbId: 6)).Should().Be("/Series/Details");
        ImportWatchlistModel.DetailsPage(Item(3, WatchlistImportItemStatus.NotFound)).Should().BeNull();
    }
}
