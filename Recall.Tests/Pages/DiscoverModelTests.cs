using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages;
using Recall.Web.Services;
using Recall.Web.Services.Discover;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Pages;

/// <summary>
/// The Discover page model: the rows come from <see cref="IDiscoverService"/>;
/// "+" adds a series to the library or a movie to the watchlist, "×" hides a
/// title, each only for a title the page offers, each with a toast whose Undo
/// takes it back; and every POST returns to the card's place in its row.
/// </summary>
[TestFixture]
public sealed class DiscoverModelTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static readonly DiscoverTitle Severance = new()
    {
        Kind = DiscoverKind.Series, TvdbId = 371980, Name = "Severance", Year = 2022, ImageUrl = "https://img/s.jpg", Score = 9000
    };

    private static readonly DiscoverTitle Dune = new()
    {
        Kind = DiscoverKind.Movie, TvdbId = 338552, Name = "Dune: Part Two", Year = 2024, ImageUrl = "https://img/d.jpg", Score = 9000
    };

    private Mock<IDiscoverService> _discover = null!;
    private Mock<IDiscoverUserRepository> _users = null!;
    private Mock<ISeriesLibraryService> _library = null!;
    private Mock<IMovieTrackingService> _movies = null!;
    private Mock<ICurrentUserService> _currentUser = null!;

    [SetUp]
    public void SetUp()
    {
        _discover = new Mock<IDiscoverService>();
        _users = new Mock<IDiscoverUserRepository>();
        _library = new Mock<ISeriesLibraryService>(MockBehavior.Strict);
        _movies = new Mock<IMovieTrackingService>(MockBehavior.Strict);
        _currentUser = new Mock<ICurrentUserService>();
        _currentUser.SetupGet(x => x.UserId).Returns(UserId);

        _discover.Setup(x => x.FindAsync(UserId, DiscoverKind.Series, Severance.TvdbId, It.IsAny<CancellationToken>())).ReturnsAsync(Severance);
        _discover.Setup(x => x.FindAsync(UserId, DiscoverKind.Movie, Dune.TvdbId, It.IsAny<CancellationToken>())).ReturnsAsync(Dune);
    }

    // ---- the page -------------------------------------------------------------------

    [Test]
    public async Task Get_Should_ShowTheUsersRows()
    {
        var page = new DiscoverPage([Row("new")], HasLists: true);
        _discover.Setup(x => x.GetPageAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync(page);
        var sut = Sut();

        var result = await sut.OnGetAsync(CancellationToken.None);

        result.Should().BeOfType<PageResult>();
        sut.Suggestions.Should().BeSameAs(page);
        sut.LoadFailed.Should().BeFalse();
    }

    [Test]
    public async Task Get_Should_SayItFailed_WithA500_WhenThePageCannotBeBuilt()
    {
        _discover.Setup(x => x.GetPageAsync(UserId, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));
        var sut = Sut();

        await sut.OnGetAsync(CancellationToken.None);

        sut.LoadFailed.Should().BeTrue();
        sut.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        sut.Suggestions.HasLists.Should().BeFalse();
    }

    [Test]
    public async Task Get_WithoutAUser_Should_Challenge()
    {
        _currentUser.SetupGet(x => x.UserId).Returns((Guid?)null);

        (await Sut().OnGetAsync(CancellationToken.None)).Should().BeOfType<ChallengeResult>();
        _discover.VerifyNoOtherCalls();
    }

    [Test]
    public void ACard_Should_BeNamedByItsRowAndPlace()
    {
        DiscoverModel.CardId(Row("genre-science-fiction"), 5).Should().Be("discover-genre-science-fiction-5");
        DiscoverModel.RowPrefix(Row("new")).Should().Be("discover-new");
        DiscoverModel.RowPrefixes.Should().Contain(["discover-new", "discover-popular", "discover-favourites", "discover-movies",
            "discover-genre-crime", "discover-genre-science-fiction"]);
    }

    // ---- adding ---------------------------------------------------------------------

    [Test]
    public async Task AddingASeries_Should_PutItInTheLibrary_WithAnUndo_AndReturnToTheSamePlace()
    {
        _library.Setup(x => x.AddAsync(UserId, Severance.TvdbId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AddSeriesResult(AddSeriesOutcome.Added, "Severance"));
        var sut = Sut("discover-new-5");

        var result = await sut.OnPostAddAsync("Series", Severance.TvdbId, CancellationToken.None);

        Fragment(result).Should().Be("discover-new-5", "the next title now has the place");
        sut.SuccessToast().Should().Be("Added Severance to your library.");
        Undo(sut).Should().Equal("Add", "Series", "371980", "discover-new-5");
    }

    [Test]
    public async Task AddingAMovie_Should_PutItOnTheWatchlist_WithItsKnownName()
    {
        _movies.Setup(x => x.AddToWatchlistAsync(UserId, Dune.TvdbId, "Dune: Part Two", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MovieWatchlistOutcome.Added);
        var sut = Sut("discover-movies-2");

        var result = await sut.OnPostAddAsync("Movie", Dune.TvdbId, CancellationToken.None);

        Fragment(result).Should().Be("discover-movies-2");
        sut.SuccessToast().Should().Be("Added Dune: Part Two to your watchlist.");
        Undo(sut).Should().Equal("Add", "Movie", "338552", "discover-movies-2");
        _library.VerifyNoOtherCalls();
    }

    [TestCase(MovieWatchlistOutcome.AlreadyOnWatchlist, "Dune: Part Two is already on your watchlist.")]
    [TestCase(MovieWatchlistOutcome.AlreadyWatched, "You've already watched Dune: Part Two.")]
    public async Task AMovieTheUserAlreadyHas_Should_SaySo_WithoutAnUndo(MovieWatchlistOutcome outcome, string message)
    {
        _movies.Setup(x => x.AddToWatchlistAsync(UserId, Dune.TvdbId, "Dune: Part Two", It.IsAny<CancellationToken>())).ReturnsAsync(outcome);
        var sut = Sut("discover-movies-2");

        await sut.OnPostAddAsync("Movie", Dune.TvdbId, CancellationToken.None);

        sut.InfoToast().Should().Be(message);
        sut.TempData.ContainsKey(PageModelToastExtensions.UndoDiscoverActionKey).Should().BeFalse();
    }

    [Test]
    public async Task ASeriesAlreadyInTheLibrary_Should_SaySo()
    {
        _library.Setup(x => x.AddAsync(UserId, Severance.TvdbId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AddSeriesResult(AddSeriesOutcome.AlreadyInLibrary));
        var sut = Sut("discover-new-1");

        await sut.OnPostAddAsync("Series", Severance.TvdbId, CancellationToken.None);

        sut.InfoToast().Should().Be("Severance is already in your library.");
    }

    [Test]
    public async Task ASeriesTheTvdbNoLongerHas_Should_StayInItsPlace_WithAnError()
    {
        _library.Setup(x => x.AddAsync(UserId, Severance.TvdbId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AddSeriesResult(AddSeriesOutcome.NotFound));
        var sut = Sut("discover-new-3", last: true);

        var result = await sut.OnPostAddAsync("Series", Severance.TvdbId, CancellationToken.None);

        sut.ErrorToast().Should().Be("TheTVDB no longer has Severance, so it could not be added.");
        Fragment(result).Should().Be("discover-new-3", "nothing left the row");
    }

    [TestCase("Series", 999)]
    [TestCase("Episode", 371980)]
    [TestCase("1", 371980)]
    [TestCase(null, 371980)]
    [TestCase("Series", 0)]
    public async Task ATitleThePageDoesNotOffer_Should_NotBeAdded(string? kind, int id)
    {
        var sut = Sut("discover-new-1");

        await sut.OnPostAddAsync(kind, id, CancellationToken.None);

        sut.ErrorToast().Should().Be(DiscoverModel.NotOffered);
        _library.VerifyNoOtherCalls();
        _movies.VerifyNoOtherCalls();
    }

    [Test]
    public async Task AFailedAdd_Should_ShowAnError()
    {
        _library.Setup(x => x.AddAsync(UserId, Severance.TvdbId, It.IsAny<CancellationToken>())).ThrowsAsync(new TimeoutException());
        var sut = Sut("discover-new-1");

        await sut.OnPostAddAsync("Series", Severance.TvdbId, CancellationToken.None);

        sut.ErrorToast().Should().Be("Could not add that right now.");
    }

    // ---- not interested -------------------------------------------------------------

    [TestCase(true)]
    [TestCase(false)]
    public async Task Hiding_Should_StoreIt_WithAnUndo(bool firstPress)
    {
        _users.Setup(x => x.HideAsync(UserId, DiscoverKind.Movie, Dune.TvdbId, It.IsAny<CancellationToken>())).ReturnsAsync(firstPress);
        var sut = Sut("discover-genre-crime-4");

        var result = await sut.OnPostHideAsync("Movie", Dune.TvdbId, CancellationToken.None);

        _users.Verify(x => x.HideAsync(UserId, DiscoverKind.Movie, Dune.TvdbId, It.IsAny<CancellationToken>()), Times.Once);
        sut.SuccessToast().Should().Be("Hidden from Discover.");
        Undo(sut).Should().Equal("Hide", "Movie", "338552", "discover-genre-crime-4");
        Fragment(result).Should().Be("discover-genre-crime-4");
    }

    [Test]
    public async Task HidingATitleThePageDoesNotOffer_Should_StoreNothing()
    {
        var sut = Sut();

        await sut.OnPostHideAsync("Movie", 12345, CancellationToken.None);

        _users.Verify(x => x.HideAsync(It.IsAny<Guid>(), It.IsAny<DiscoverKind>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        sut.ErrorToast().Should().Be(DiscoverModel.NotOffered);
    }

    // ---- undo -----------------------------------------------------------------------

    [Test]
    public async Task UndoingAHide_Should_ShowTheTitleAgain_AtItsPlace()
    {
        _users.Setup(x => x.UnhideAsync(UserId, DiscoverKind.Movie, Dune.TvdbId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var sut = Sut("discover-genre-crime-4");

        var result = await sut.OnPostUndoAsync("Hide", "Movie", Dune.TvdbId, CancellationToken.None);

        sut.InfoToast().Should().Be("Dune: Part Two is back in Discover.");
        Fragment(result).Should().Be("discover-genre-crime-4");
    }

    [Test]
    public async Task UndoingASeriesAdd_Should_TakeItOutOfTheLibrary()
    {
        _library.Setup(x => x.RemoveAsync(UserId, Severance.TvdbId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var sut = Sut("discover-new-5");

        await sut.OnPostUndoAsync("Add", "Series", Severance.TvdbId, CancellationToken.None);

        sut.InfoToast().Should().Be("Took Severance out of your library again.");
    }

    [Test]
    public async Task UndoingAMovieAdd_Should_TakeItOffTheWatchlist()
    {
        _movies.Setup(x => x.RemoveFromWatchlistAsync(UserId, Dune.TvdbId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var sut = Sut("discover-movies-2");

        await sut.OnPostUndoAsync("Add", "Movie", Dune.TvdbId, CancellationToken.None);

        sut.InfoToast().Should().Be("Took Dune: Part Two off your watchlist again.");
        _library.VerifyNoOtherCalls();
    }

    [Test]
    public async Task UndoingTwice_Should_SayThereIsNothingLeft()
    {
        _users.Setup(x => x.UnhideAsync(UserId, DiscoverKind.Movie, Dune.TvdbId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var sut = Sut();

        await sut.OnPostUndoAsync("Hide", "Movie", Dune.TvdbId, CancellationToken.None);

        sut.InfoToast().Should().Be(DiscoverModel.NothingToUndo);
    }

    [TestCase("Delete", "Movie", 338552)]
    [TestCase("Hide", "Episode", 338552)]
    [TestCase("Hide", "Movie", 0)]
    public async Task AnUndoThatMakesNoSense_Should_ChangeNothing(string action, string kind, int id)
    {
        var sut = Sut();

        await sut.OnPostUndoAsync(action, kind, id, CancellationToken.None);

        sut.InfoToast().Should().Be(DiscoverModel.NothingToUndo);
        _users.VerifyNoOtherCalls();
        _library.VerifyNoOtherCalls();
        _movies.VerifyNoOtherCalls();
    }

    // ---- where the page comes back to -----------------------------------------------

    [Test]
    public async Task TheLastCardLeaving_Should_ReturnToTheNewLastOne()
    {
        _users.Setup(x => x.HideAsync(UserId, DiscoverKind.Series, Severance.TvdbId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await Sut("discover-popular-20", last: true).OnPostHideAsync("Series", Severance.TvdbId, CancellationToken.None);

        Fragment(result).Should().Be("discover-popular-19");
    }

    [Test]
    public async Task TheOnlyCardLeaving_Should_ReturnToItsRow()
    {
        _users.Setup(x => x.HideAsync(UserId, DiscoverKind.Series, Severance.TvdbId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await Sut("discover-genre-science-fiction-1", last: true).OnPostHideAsync("Series", Severance.TvdbId, CancellationToken.None);

        Fragment(result).Should().Be("discover-genre-science-fiction");
    }

    [TestCase("watching-card-3")]
    [TestCase("discover-unknown-3")]
    [TestCase("javascript:alert(1)")]
    [TestCase(null)]
    public async Task AnAnchorThatIsNotADiscoverCard_Should_BeDropped(string? anchor)
    {
        _users.Setup(x => x.HideAsync(UserId, DiscoverKind.Series, Severance.TvdbId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await Sut(anchor).OnPostHideAsync("Series", Severance.TvdbId, CancellationToken.None);

        Fragment(result).Should().BeNull();
    }

    // ---- helpers --------------------------------------------------------------------

    private DiscoverModel Sut(string? anchor = null, bool last = false)
    {
        var sut = new DiscoverModel(_discover.Object, _users.Object, _library.Object, _movies.Object, _currentUser.Object,
            NullLogger<DiscoverModel>.Instance).WithTempData().WithHttpContext();
        sut.Anchor = anchor;
        sut.AnchorIsLast = last;
        return sut;
    }

    private static string? Fragment(IActionResult result)
    {
        var redirect = result.Should().BeOfType<RedirectToPageResult>().Subject;
        redirect.PageName.Should().BeNull("a POST comes back to Discover");
        return redirect.Fragment;
    }

    private static string?[] Undo(PageModel page) =>
    [
        page.TempData[PageModelToastExtensions.UndoDiscoverActionKey] as string,
        page.TempData[PageModelToastExtensions.UndoDiscoverKindKey] as string,
        page.TempData[PageModelToastExtensions.UndoDiscoverIdKey] as string,
        page.TempData[PageModelToastExtensions.UndoDiscoverAnchorKey] as string
    ];

    private static DiscoverRow Row(string slug) => new(slug, "Heading", "Description", [Severance]);
}
