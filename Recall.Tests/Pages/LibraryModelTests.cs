using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages;
using Recall.Web.Services;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Pages;

[TestFixture]
public class LibraryModelTests
{
    private static readonly DateOnly Today = new(2026, 10, 1);
    private static readonly Guid UserId = Guid.NewGuid();

    private Mock<ICurrentUserService> _currentUser = null!;
    private Mock<ITrackedSeriesRepository> _tracked = null!;
    private Mock<IEpisodeWatchRepository> _watches = null!;
    private Mock<IMovieWatchRepository> _movieWatches = null!;
    private Mock<ITrackedMovieRepository> _watchlist = null!;
    private Mock<ITheTvDbService> _tvDb = null!;
    private Mock<ILikeRepository> _likes = null!;
    private LibraryModel _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _currentUser = new Mock<ICurrentUserService>();
        _currentUser.SetupGet(x => x.IsAuthenticated).Returns(true);
        _currentUser.SetupGet(x => x.ExternalUserId).Returns(UserId.ToString());
        _currentUser.SetupGet(x => x.UserId).Returns(UserId);

        _tracked = new Mock<ITrackedSeriesRepository>();
        _watches = new Mock<IEpisodeWatchRepository>();
        _movieWatches = new Mock<IMovieWatchRepository>();
        _watchlist = new Mock<ITrackedMovieRepository>();
        _tvDb = new Mock<ITheTvDbService>();
        _likes = new Mock<ILikeRepository>();

        var progress = new Mock<IWatchProgressService>();
        progress
            .Setup(x => x.BuildProgress(It.IsAny<int>(), It.IsAny<IEnumerable<WatchableEpisode>>(), It.IsAny<IReadOnlySet<int>>()))
            .Returns((int id, IEnumerable<WatchableEpisode> episodes, IReadOnlySet<int> watched) =>
                WatchProgressCalculator.Build(id, episodes, watched, Today));

        // An empty library unless a test says otherwise.
        _tracked.Setup(x => x.GetByUserAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _likes.Setup(x => x.GetLikesAsync(UserId, LikeTargetType.Series, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _movieWatches.Setup(x => x.GetWatchedMoviesAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _watchlist.Setup(x => x.GetByUserAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        SetUpWatched();

        _sut = new LibraryModel(
            _currentUser.Object,
            _tracked.Object,
            progress.Object,
            _watches.Object,
            _movieWatches.Object,
            _watchlist.Object,
            _tvDb.Object,
            _likes.Object,
            new FixedTimeProvider(new DateTimeOffset(Today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)),
            NullLogger<LibraryModel>.Instance).WithTempData();
    }

    private static EpisodeSummary Ep(int id, int number, DateOnly aired) =>
        new() { Id = id, SeasonNumber = 1, EpisodeNumber = number, Name = $"E{number}", Aired = aired };

    private static SeriesAggregate Series(int id, string name, string status, params EpisodeSummary[] episodes) => new()
    {
        TvdbId = id,
        Name = name,
        ImageUrl = $"https://img/{id}.jpg",
        FirstAired = new DateOnly(2020, 1, 1),
        Status = new SeriesStatus { Name = status },
        Episodes = episodes
    };

    private void SetUpSeries(params SeriesAggregate[] aggregates)
    {
        _tracked
            .Setup(x => x.GetByUserAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(aggregates.Select(a => new TrackedSeries { Id = Guid.NewGuid(), UserId = UserId, TvdbId = a.TvdbId, Name = a.Name }).ToList());

        foreach (var aggregate in aggregates)
            _tvDb.Setup(x => x.GetSeriesAggregateByIdAsync(aggregate.TvdbId, It.IsAny<CancellationToken>())).ReturnsAsync(aggregate);
    }

    private void SetUpWatched(params int[] episodeIds) =>
        _watches
            .Setup(x => x.GetWatchedEpisodeIdsAsync(UserId, It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(episodeIds.ToHashSet());

    // ---- sections ----------------------------------------------------------------

    [Test]
    public async Task OnGet_Should_SortSeriesIntoWatching_UpToDate_AndWatched()
    {
        SetUpSeries(
            Series(1, "Behind", "Continuing", Ep(10, 1, Today.AddDays(-20)), Ep(11, 2, Today.AddDays(-10))),
            Series(2, "Caught Up, Still Running", "Continuing", Ep(20, 1, Today.AddDays(-20)), Ep(21, 2, Today.AddDays(5))),
            Series(3, "Finished", "Ended", Ep(30, 1, Today.AddDays(-400)), Ep(31, 2, Today.AddDays(-390))),
            Series(4, "Ended But Unfinished", "Ended", Ep(40, 1, Today.AddDays(-400)), Ep(41, 2, Today.AddDays(-390))));
        SetUpWatched(10, 20, 30, 31, 40);

        var result = await _sut.OnGetAsync(CancellationToken.None);

        result.Should().BeOfType<PageResult>();
        _sut.Watching.Select(i => i.Name).Should().Equal("Behind", "Ended But Unfinished");
        _sut.UpToDate.Select(i => i.Name).Should().Equal("Caught Up, Still Running");
        _sut.Watched.Select(i => i.Name).Should().Equal("Finished");
        _sut.IsEmpty.Should().BeFalse();

        var behind = _sut.Watching[0];
        behind.Type.Should().Be(SearchResultType.Series);
        behind.WatchedEpisodes.Should().Be(1);
        behind.ReleasedEpisodes.Should().Be(2);
        behind.ImageUrl.Should().Be("https://img/1.jpg");

        behind.ProgressText.Should().Be("1 of 2 · S01", "a card under Watching says where the viewer is");

        _sut.UpToDate[0].ReleasedEpisodes.Should().Be(1, "the episode airing in five days isn't released yet");
        _sut.UpToDate[0].ProgressText.Should().BeNull();
    }

    [Test]
    public async Task OnGet_Should_NotKeepASeriesUnderWatching_ForUnwatchedSpecials()
    {
        var special = new EpisodeSummary { Id = 5, SeasonNumber = 0, EpisodeNumber = 1, Name = "Making of", Aired = Today.AddDays(-300) };
        SetUpSeries(
            Series(1, "Running", "Continuing", special, Ep(10, 1, Today.AddDays(-20))),
            Series(2, "Over", "Ended", special, Ep(20, 1, Today.AddDays(-400))));
        SetUpWatched(10, 20);

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Watching.Should().BeEmpty("every regular episode is watched; a special never counts");
        _sut.UpToDate.Select(i => i.Name).Should().Equal("Running");
        _sut.Watched.Select(i => i.Name).Should().Equal("Over");
        _sut.Watched[0].ReleasedEpisodes.Should().Be(1, "the special is not in the count either");
    }

    [Test]
    public async Task OnGet_Should_OrderEachSectionByName_IgnoringCase()
    {
        SetUpSeries(
            Series(1, "zebra", "Continuing", Ep(10, 1, Today.AddDays(-5))),
            Series(2, "Alpha", "Continuing", Ep(20, 1, Today.AddDays(-5))),
            Series(3, "beta", "Continuing", Ep(30, 1, Today.AddDays(-5))));

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Watching.Select(i => i.Name).Should().Equal("Alpha", "beta", "zebra");
    }

    [Test]
    public async Task OnGet_Should_FlagLikedSeries()
    {
        SetUpSeries(
            Series(1, "Liked", "Continuing", Ep(10, 1, Today.AddDays(-5))),
            Series(2, "Not Liked", "Continuing", Ep(20, 1, Today.AddDays(-5))));
        _likes
            .Setup(x => x.GetLikesAsync(UserId, LikeTargetType.Series, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new UserLike(LikeTargetType.Series, 1, 1, DateTime.UtcNow)]);

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Watching.Single(i => i.TvdbId == 1).IsLiked.Should().BeTrue();
        _sut.Watching.Single(i => i.TvdbId == 2).IsLiked.Should().BeFalse();
    }

    [Test]
    public async Task OnGet_Should_ListWatchedMoviesUnderWatched_AndWatchlistMoviesUnderToWatch()
    {
        _movieWatches
            .Setup(x => x.GetWatchedMoviesAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MovieWatch(500, new DateTime(2026, 9, 12, 20, 0, 0, DateTimeKind.Utc))]);
        _watchlist
            .Setup(x => x.GetByUserAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new TrackedMovie(601, "Stored Name Newest", new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc)),
                new TrackedMovie(602, "Stored Name Older", new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc))
            ]);
        _tvDb.Setup(x => x.GetMovieAggregateByIdAsync(500, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MovieAggregate { TvdbId = 500, Name = "Seen It", ImageUrl = "https://img/500.jpg", ReleaseDate = new DateOnly(1987, 3, 6) });
        _tvDb.Setup(x => x.GetMovieAggregateByIdAsync(601, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MovieAggregate { TvdbId = 601, Name = "Want To See", ImageUrl = "https://img/601.jpg" });
        _tvDb.Setup(x => x.GetMovieAggregateByIdAsync(602, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("TheTVDB is down"));

        await _sut.OnGetAsync(CancellationToken.None);

        var watched = _sut.Watched.Should().ContainSingle().Subject;
        watched.Type.Should().Be(SearchResultType.Movie);
        watched.Name.Should().Be("Seen It");
        watched.Caption.Should().Be("watched Sep 12", "the meta line reads \"Movie · watched Sep 12\"; this year, so no year");

        _sut.ToWatch.Select(i => i.TvdbId).Should().Equal([601, 602], "the watchlist keeps its own order, most recently added first");
        _sut.ToWatch[0].Name.Should().Be("Want To See");
        _sut.ToWatch[0].Caption.Should().BeNull("a watchlist card's meta line is the type and the release year");
        _sut.ToWatch[1].Name.Should().Be("Stored Name Older", "a movie that can't be loaded still gets a card, from the title stored when it was added");
        _sut.ToWatch[1].ImageUrl.Should().BeNull();
    }

    [Test]
    public async Task OnGet_Should_BeEmpty_ForANewUser()
    {
        await _sut.OnGetAsync(CancellationToken.None);

        _sut.IsEmpty.Should().BeTrue();
        _tvDb.VerifyNoOtherCalls();
    }

    [Test]
    public async Task OnGet_Should_LeaveOutASeriesThatCannotBeLoaded_AndKeepTheRest()
    {
        SetUpSeries(
            Series(1, "Fine", "Continuing", Ep(10, 1, Today.AddDays(-5))),
            Series(2, "Broken", "Continuing"));
        _tvDb.Setup(x => x.GetSeriesAggregateByIdAsync(2, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Watching.Select(i => i.Name).Should().Equal("Fine");
        _sut.ErrorToast().Should().BeNull();
    }

    [Test]
    public async Task OnGet_Should_ShowAnErrorToast_WhenTheDatabaseFails()
    {
        _tracked
            .Setup(x => x.GetByUserAsync(UserId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var result = await _sut.OnGetAsync(CancellationToken.None);

        result.Should().BeOfType<PageResult>();
        _sut.ErrorToast().Should().Be("Could not load your library right now.");
        _sut.IsEmpty.Should().BeTrue();
    }

    [Test]
    public async Task OnGet_Should_AskForSignIn_WhenThereIsNoUser()
    {
        _currentUser.SetupGet(x => x.IsAuthenticated).Returns(false);

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.ErrorToast().Should().Be("You need to be signed in to view your library.");
        _tracked.VerifyNoOtherCalls();
    }

    // ---- POST --------------------------------------------------------------------

    [Test]
    public async Task ToggleSeriesLike_Should_ToggleTheLike_AndRedirect()
    {
        var result = await _sut.OnPostToggleSeriesLikeAsync(42, CancellationToken.None);

        result.Should().BeOfType<RedirectToPageResult>();
        _likes.Verify(x => x.ToggleAsync(UserId, LikeTargetType.Series, 42, 42, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ToggleSeriesLike_Should_ShowAnErrorToast_WhenTheRepositoryThrows_OrThereIsNoUser()
    {
        _likes
            .Setup(x => x.ToggleAsync(UserId, LikeTargetType.Series, 42, 42, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        await _sut.OnPostToggleSeriesLikeAsync(42, CancellationToken.None);
        _sut.ErrorToast().Should().Be("Could not update your like right now.");

        _currentUser.SetupGet(x => x.UserId).Returns((Guid?)null);
        await _sut.OnPostToggleSeriesLikeAsync(42, CancellationToken.None);
        _sut.ErrorToast().Should().Be("You need to be signed in to like a series.");
    }

    [Test]
    public async Task Remove_Should_RemoveTheUsersOwnTrackedSeries_AndRedirect()
    {
        var trackedId = Guid.NewGuid();

        var result = await _sut.OnPostRemoveAsync(trackedId, CancellationToken.None);

        result.Should().BeOfType<RedirectToPageResult>();
        _tracked.Verify(x => x.RemoveAsync(UserId, trackedId, It.IsAny<CancellationToken>()), Times.Once,
            "the repository is given the user id, so one user can't remove another's row");
    }

    [Test]
    public async Task Remove_Should_ShowAnErrorToast_AndReRenderThePage_WhenTheRepositoryThrows()
    {
        var trackedId = Guid.NewGuid();
        _tracked
            .Setup(x => x.RemoveAsync(UserId, trackedId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var result = await _sut.OnPostRemoveAsync(trackedId, CancellationToken.None);

        result.Should().BeOfType<PageResult>();
        _sut.ErrorToast().Should().Be("Could not remove the series right now.");
    }
}
