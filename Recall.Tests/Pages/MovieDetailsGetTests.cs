using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.Omdb;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Display;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.OmdbCache;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages.Movies;
using Recall.Web.Pages.Shared;
using Recall.Web.Services;
using Recall.Web.Services.External.TheTvDb;
using Recall.Web.Services.WatchTracking;
using Recall.Web.Services.PendingActions;

namespace Recall.Tests.Pages;

/// <summary>
/// What Movies/Details decides when it loads: the hero (status chip, meta
/// line, ratings) and its action row in every state, with the "⋯" menu and the
/// watchlist toggle, and how a failed load is reported.
/// </summary>
[TestFixture]
public sealed class MovieDetailsGetTests
{
    private const int MovieId = 287533;
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 10, 1);
    private static readonly DateTime WatchedOn = new(2026, 9, 11, 20, 0, 0, DateTimeKind.Utc);

    private Mock<ITheTvDbService> _tvDb = null!;
    private Mock<ICurrentUserService> _currentUser = null!;
    private Mock<IMovieWatchRepository> _watches = null!;
    private Mock<IMovieTrackingService> _tracking = null!;
    private Mock<IRatingRepository> _ratings = null!;
    private Mock<IMovieOmdbSnapshotStore> _omdb = null!;
    private DetailsModel _sut = null!;

    /// <summary>What the page logged: a timeout is a warning, an aborted request nothing at Error.</summary>
    private RecordingLogger<DetailsModel> _log = null!;

    [SetUp]
    public void SetUp()
    {
        _tvDb = new Mock<ITheTvDbService>();
        _currentUser = new Mock<ICurrentUserService>();
        _watches = new Mock<IMovieWatchRepository>();
        _tracking = new Mock<IMovieTrackingService>();
        _ratings = new Mock<IRatingRepository>();
        _omdb = new Mock<IMovieOmdbSnapshotStore>();
        _ratings
            .Setup(x => x.GetSummaryAsync(RatingTargetType.Movie, MovieId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(RatingSummary.Empty);

        var clock = new FixedTimeProvider(new DateTimeOffset(Today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero));
        _log = new RecordingLogger<DetailsModel>();

        _sut = new DetailsModel(
            _tvDb.Object,
            _currentUser.Object,
            Mock.Of<IPendingActions>(),
            Mock.Of<ILikeRepository>(),
            _watches.Object,
            _tracking.Object,
            _ratings.Object,
            _omdb.Object,
            clock,
            _log).WithTempData().WithHttpContext();
    }

    private void MovieExists(DateOnly? released = null, string status = "Released", string? detail = null, int? runtime = 180)
    {
        _tvDb.Setup(x => x.GetMovieAggregateByIdAsync(MovieId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MovieAggregate
            {
                TvdbId = MovieId,
                Name = "Oppenheimer",
                ImageUrl = "https://artworks.thetvdb.com/banners/movies/287533/posters/1.jpg",
                BackgroundUrl = "https://artworks.thetvdb.com/banners/movies/287533/backgrounds/1_t.jpg",
                ReleaseDate = released ?? new DateOnly(2023, 7, 19),
                ReleaseDetail = detail,
                RuntimeMinutes = runtime,
                Status = new MovieStatus { Name = status },
                Genres = ["Drama", "History"],
                Studios = ["Universal Pictures", "Syncopy"]
            });
    }

    private void SignedIn(DateTime? watchedOn = null, bool onWatchlist = false)
    {
        _currentUser.SetupGet(x => x.IsAuthenticated).Returns(true);
        _currentUser.SetupGet(x => x.UserId).Returns(UserId);
        _watches.Setup(x => x.GetWatchedUtcAsync(UserId, MovieId, It.IsAny<CancellationToken>())).ReturnsAsync(watchedOn);
        _tracking.Setup(x => x.IsOnWatchlistAsync(UserId, MovieId, It.IsAny<CancellationToken>())).ReturnsAsync(onWatchlist);
    }

    private async Task<TitleHeroModel> LoadHeroAsync()
    {
        await _sut.OnGetAsync(MovieId, CancellationToken.None);
        return _sut.BuildHero();
    }

    // ---- the action row, in every state --------------------------------------------------

    [Test]
    public async Task NotWatched_Should_OfferMarkAsWatched_WithTheRuntime_AndAnEmptyMenu()
    {
        MovieExists();
        SignedIn();

        var actions = (await LoadHeroAsync()).Actions;

        actions.Primary!.Label.Should().Be("Mark as watched");
        actions.Primary.Detail.Should().Be("3 h 0 min");
        actions.Primary.Handler.Should().Be("ToggleMovieWatched");
        actions.Status.Should().BeNull();
        actions.Menu.Should().BeEmpty("there is nothing to unmark or remove, so the \"⋯\" button is not drawn");
        actions.Watchlist!.IsPressed.Should().BeFalse();
        actions.Watchlist.CurrentLabel.Should().Be("Add to watchlist");
        actions.Watchlist.Handler.Should().Be("ToggleWatchlist");
        actions.Like.Should().NotBeNull();
        actions.ShareTitle.Should().Be("Oppenheimer");
        actions.MenuLabel.Should().Be("More actions for Oppenheimer");
    }

    [Test]
    public async Task Watched_Should_SaySoInAQuietBox_AndMoveMarkAsUnwatchedIntoTheMenu()
    {
        MovieExists();
        SignedIn(watchedOn: WatchedOn);

        var actions = (await LoadHeroAsync()).Actions;

        actions.Primary.Should().BeNull();
        actions.Status!.Text.Should().Be("Watched Fri, Sep 11");
        actions.Status.Icon.Should().Be(Icons.Check);
        var unwatch = actions.Menu.Should().ContainSingle().Subject;
        unwatch.Label.Should().Be("Mark as unwatched");
        unwatch.Handler.Should().Be("ToggleMovieWatched");
        unwatch.ModalId.Should().BeNull("it posts at once, as the old toggle did");
        actions.Watchlist.Should().BeNull("a watched movie cannot go on the watchlist");
    }

    [Test]
    public async Task OnTheWatchlist_Should_PressTheToggle_AndOfferRemovalInTheMenu()
    {
        MovieExists();
        SignedIn(onWatchlist: true);

        var hero = await LoadHeroAsync();
        var actions = hero.Actions;

        actions.Primary!.Label.Should().Be("Mark as watched");
        actions.Watchlist!.IsPressed.Should().BeTrue();
        actions.Watchlist.CurrentLabel.Should().Be("On your watchlist");
        actions.Watchlist.PressedModalId.Should().Be(DetailsModel.RemoveFromWatchlistModalId, "turning it off removes something, so it is confirmed");
        var remove = actions.Menu.Should().ContainSingle().Subject;
        remove.Label.Should().Be("Remove from watchlist");
        remove.ModalId.Should().Be(DetailsModel.RemoveFromWatchlistModalId);
        hero.Back.Should().Be(new TitleBackLink("Library", "/Library", UseHistory: false));
    }

    [Test]
    public async Task Unreleased_Should_SayWhenItReleases_AndNotOfferMarkingItWatched()
    {
        MovieExists(released: new DateOnly(2027, 3, 5), status: "Post Production");
        SignedIn();

        var hero = await LoadHeroAsync();

        _sut.IsUnreleased.Should().BeTrue();
        hero.Actions.Primary.Should().BeNull();
        hero.Actions.Status!.Text.Should().Be("Releases Mar 5, 2027");
        hero.Actions.Status.Icon.Should().Be(Icons.NotAiredYet);
        hero.Actions.Menu.Should().BeEmpty();
        hero.Actions.Watchlist!.CurrentLabel.Should().Be("Add to watchlist", "the watchlist is what an unreleased movie is for");
        hero.Status.Should().Be(new TitleStatusChip("Coming Mar 5, 2027", Active: true));
    }

    [Test]
    public async Task AnUnreleasedMovieWithATheatricalRelease_Should_SayInCinemas()
    {
        MovieExists(released: Today.AddDays(9), status: "Post Production", detail: "Theatrical");

        (await LoadHeroAsync()).Status.Should().Be(new TitleStatusChip("In cinemas Sat, Oct 10", Active: true));
    }

    [Test]
    public async Task AnUnreleasedMovieWithoutADate_Should_ShowTheTvDbsStatus()
    {
        _tvDb.Setup(x => x.GetMovieAggregateByIdAsync(MovieId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MovieAggregate { TvdbId = MovieId, Name = "Next One", Status = new MovieStatus { Name = "Announced" } });
        SignedIn();

        var hero = await LoadHeroAsync();

        hero.Status.Should().Be(new TitleStatusChip("Announced", Active: true));
        hero.Actions.Status!.Text.Should().Be("Not released yet");
        hero.Actions.Primary.Should().BeNull();
    }

    [Test]
    public async Task AMovieReleasedToday_Should_BeReleased()
    {
        MovieExists(released: Today, status: "Post Production");
        SignedIn();

        var hero = await LoadHeroAsync();

        _sut.IsUnreleased.Should().BeFalse("the date wins over TheTVDB's status");
        hero.Status.Should().Be(new TitleStatusChip("Released", Active: false));
        hero.Actions.Primary!.Label.Should().Be("Mark as watched");
    }

    [Test]
    public async Task AWatchedAndListedMovie_Should_NeverBothBeShown_ButTheMenuCanHoldBothEntries()
    {
        // The service keeps the two apart; if the rows ever disagreed, the menu still offers both ways out.
        MovieExists();
        SignedIn(watchedOn: WatchedOn, onWatchlist: true);

        (await LoadHeroAsync()).Actions.Menu.Select(m => m.Label).Should().Equal("Mark as unwatched", "Remove from watchlist");
    }

    [Test]
    public async Task AVisitor_Should_GetTheSameActionRow_TheWatchlistTheHeartAndTheRating()
    {
        MovieExists();

        var hero = await LoadHeroAsync();

        hero.Actions.Primary!.Label.Should().Be("Mark as watched");
        hero.Actions.Primary.Handler.Should().Be("ToggleMovieWatched");
        hero.Actions.Watchlist!.IsPressed.Should().BeFalse();
        hero.Actions.Watchlist.Handler.Should().Be("ToggleWatchlist");
        hero.Actions.Like!.Handler.Should().Be("ToggleMovieLike");
        hero.Actions.Menu.Should().BeEmpty();
        hero.Rating!.RateHandler.Should().Be("RateMovie");
        hero.Rating.CurrentValue.Should().BeNull();
        hero.Progress.Should().BeNull();
        hero.Back.Should().Be(new TitleBackLink("Back", "/Search", UseHistory: true));
    }

    // ---- the rest of the hero ------------------------------------------------------------

    [Test]
    public async Task Hero_Should_CarryTheArt_TheMetaLine_TheStatusChip_AndTheRatings()
    {
        MovieExists();
        SignedIn();
        _omdb.Setup(x => x.GetAsync(MovieId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OmdbMovie { ImdbRating = "8.3", ImdbVotes = "900,000", Awards = "Won 7 Oscars." });
        _ratings
            .Setup(x => x.GetSummaryAsync(RatingTargetType.Movie, MovieId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RatingSummary(8.5, 2));
        _ratings
            .Setup(x => x.GetRatingAsync(UserId, RatingTargetType.Movie, MovieId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(9);

        var hero = await LoadHeroAsync();

        hero.Name.Should().Be("Oppenheimer");
        hero.BackgroundUrl.Should().EndWith("backgrounds/1_t.jpg");
        hero.Meta.Should().Be("2023 · 3 h 0 min · Universal Pictures");
        hero.Genres.Should().Equal("Drama", "History");
        hero.Status.Should().Be(new TitleStatusChip("Released", Active: false));
        hero.ImdbRating.Should().Be("8.3");
        hero.RecallRating.Should().Be("8.5");
        hero.RatingChipText.Should().Be("Your rating 9");
        hero.Progress.Should().BeNull("a movie has no progress block");
        _sut.ImdbVotes.Should().Be("900,000");
        _sut.OmdbAwards.Should().Be("Won 7 Oscars.");
    }

    [Test]
    public async Task ATheTvDbFailure_Should_Be503_WithNothingToRender()
    {
        _tvDb.Setup(x => x.GetMovieAggregateByIdAsync(MovieId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TheTvDbApiException("down", 502));

        var result = await _sut.OnGetAsync(MovieId, CancellationToken.None);

        result.Should().BeOfType<PageResult>();
        _sut.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        _sut.Movie.Should().BeNull();
    }

    // ---- a timeout is TheTVDB failing; a visitor who went away is not an error ----

    [Test]
    public async Task ATheTvDbTimeout_Should_Be503_LikeAnyTheTvDbFailure()
    {
        _tvDb.Setup(x => x.GetMovieAggregateByIdAsync(MovieId, It.IsAny<CancellationToken>())).ThrowsAsync(Timeouts.Request());

        var result = await _sut.OnGetAsync(MovieId, CancellationToken.None);

        result.Should().BeOfType<PageResult>();
        _sut.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        _sut.Movie.Should().BeNull();
        _log.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("timeout"));
        _log.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    [Test]
    public async Task AnAbortedRequest_Should_NotRenderAFailure_NorLogAnError()
    {
        using var request = new CancellationTokenSource();
        _tvDb.Setup(x => x.GetMovieAggregateByIdAsync(MovieId, It.IsAny<CancellationToken>())).Callback(() => request.Cancel()).ThrowsAsync(new OperationCanceledException(request.Token));

        var act = () => _sut.OnGetAsync(MovieId, request.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "the request is gone: ASP.NET Core logs that at Debug, not as an error");
        _sut.Response.StatusCode.Should().Be(StatusCodes.Status200OK, "no error state was set for a visitor who left");
        _log.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }
}
