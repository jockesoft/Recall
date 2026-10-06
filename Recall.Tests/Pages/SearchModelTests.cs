using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages;
using Recall.Web.Services;
using Recall.Web.Services.External.TheTvDb;

namespace Recall.Tests.Pages;

[TestFixture]
public class SearchModelTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private Mock<ITheTvDbService> _tvDb = null!;
    private Mock<ITrackedSeriesRepository> _series = null!;
    private Mock<ITrackedMovieRepository> _watchlist = null!;
    private Mock<IMovieWatchRepository> _movieWatches = null!;
    private SearchModel _sut = null!;

    private static readonly SearchResultItem TrackedSeries = new(1, "Tracked", null, null, "2008", SearchResultType.Series);
    private static readonly SearchResultItem OtherSeries = new(2, "Other", null, null, "2010", SearchResultType.Series);
    private static readonly SearchResultItem WatchedMovie = new(3, "Seen", null, null, "1995", SearchResultType.Movie);
    private static readonly SearchResultItem WatchlistMovie = new(4, "Wanted", null, null, "2014", SearchResultType.Movie);
    // Same id as the tracked series: ids are only unique per type.
    private static readonly SearchResultItem MovieWithASeriesId = new(1, "Collision", null, null, null, SearchResultType.Movie);

    [SetUp]
    public void SetUp()
    {
        _tvDb = new Mock<ITheTvDbService>();
        _series = new Mock<ITrackedSeriesRepository>();
        _watchlist = new Mock<ITrackedMovieRepository>();
        _movieWatches = new Mock<IMovieWatchRepository>();

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.UserId).Returns(UserId);

        _series.Setup(x => x.GetByUserAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TrackedSeries { Id = Guid.NewGuid(), UserId = UserId, TvdbId = 1, Name = "Tracked" }]);
        _watchlist.Setup(x => x.GetByUserAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TrackedMovie(4, "Wanted", DateTime.UtcNow)]);
        _movieWatches.Setup(x => x.GetWatchedMoviesAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MovieWatch(3, DateTime.UtcNow)]);

        _sut = new SearchModel(
            _tvDb.Object,
            currentUser.Object,
            _series.Object,
            _watchlist.Object,
            _movieWatches.Object,
            NullLogger<SearchModel>.Instance).WithTempData().WithHttpContext();
    }

    private void SearchReturns(params SearchResultItem[] items) =>
        _tvDb.Setup(x => x.SearchAsync("dark", It.IsAny<CancellationToken>())).ReturnsAsync(items);

    [Test]
    public async Task Results_Should_BeMarked_WithWhatTheUserAlreadyHas()
    {
        SearchReturns(TrackedSeries, OtherSeries, WatchedMovie, WatchlistMovie, MovieWithASeriesId);
        _sut.Query = "dark";

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.LibraryBadge(TrackedSeries).Should().Be("In library");
        _sut.LibraryBadge(OtherSeries).Should().BeNull();
        _sut.LibraryBadge(WatchedMovie).Should().Be("Watched");
        _sut.LibraryBadge(WatchlistMovie).Should().Be("In library");
        _sut.LibraryBadge(MovieWithASeriesId).Should().BeNull("a movie is not in the library because a series shares its id");
    }

    [Test]
    public async Task Results_Should_StillShow_WhenTheLibraryCannotBeRead()
    {
        SearchReturns(TrackedSeries);
        _series.Setup(x => x.GetByUserAsync(UserId, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));
        _sut.Query = "dark";

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Results.Should().ContainSingle();
        _sut.ErrorMessage.Should().BeNull();
        _sut.LibraryBadge(TrackedSeries).Should().BeNull();
    }

    [Test]
    public async Task AnEmptyPage_Should_CallNothing()
    {
        await _sut.OnGetAsync(CancellationToken.None);

        _sut.HasSearched.Should().BeFalse();
        _tvDb.VerifyNoOtherCalls();
        _series.VerifyNoOtherCalls();
    }

    [Test]
    public async Task NoResults_Should_NotReadTheLibrary()
    {
        SearchReturns();
        _sut.Query = "dark";

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Results.Should().BeEmpty();
        _series.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ATheTvDbFailure_Should_BecomeAMessage_NotAnException()
    {
        _tvDb.Setup(x => x.SearchAsync("dark", It.IsAny<CancellationToken>())).ThrowsAsync(new TheTvDbApiException("down"));
        _sut.Query = "dark";

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.ErrorMessage.Should().Contain("TheTVDB");
        _sut.Results.Should().BeEmpty();
    }

    [Test]
    public async Task AStoppedSeries_Should_BeMarkedStopped_InsteadOfInLibrary()
    {
        var stoppedSeries = TrackedSeries with { TvdbId = 7, Name = "Put Aside" };
        _series.Setup(x => x.GetByUserAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new TrackedSeries { Id = Guid.NewGuid(), UserId = UserId, TvdbId = 1, Name = "Tracked" },
                new TrackedSeries { Id = Guid.NewGuid(), UserId = UserId, TvdbId = 7, Name = "Put Aside", StoppedUtc = new DateTime(2026, 9, 4, 18, 0, 0, DateTimeKind.Utc) }
            ]);
        // A movie that shares the stopped series' id is not stopped: only series can be.
        var movieWithTheSameId = WatchlistMovie with { TvdbId = 7 };
        SearchReturns(TrackedSeries, stoppedSeries, movieWithTheSameId);
        _sut.Query = "dark";

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.LibraryBadge(stoppedSeries).Should().Be("Stopped");
        _sut.IsStopped(stoppedSeries).Should().BeTrue();
        _sut.LibraryBadge(TrackedSeries).Should().Be("In library");
        _sut.IsStopped(TrackedSeries).Should().BeFalse();
        _sut.LibraryBadge(movieWithTheSameId).Should().BeNull();
        _sut.IsStopped(movieWithTheSameId).Should().BeFalse();
    }
}
