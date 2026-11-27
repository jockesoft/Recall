using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.PendingActions;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Services.PendingActions;

[TestFixture]
public sealed class PendingActionServiceTests
{
    private const int SeriesId = 81189;
    private const int MovieId = 1157;
    private const int EpisodeId = 349232;

    private static readonly Guid UserId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private Mock<ITheTvDbService> _tvDb = null!;
    private Mock<ISeriesLibraryService> _library = null!;
    private Mock<IMovieTrackingService> _movies = null!;
    private Mock<IWatchProgressService> _progress = null!;
    private Mock<ILikeRepository> _likes = null!;
    private Mock<IRatingRepository> _ratings = null!;
    private RecordingSiteMetrics _metrics = null!;
    private PendingActionService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        // Strict: describing an action may only read the caches, and completing one only what is set up.
        _tvDb = new Mock<ITheTvDbService>(MockBehavior.Strict);
        _library = new Mock<ISeriesLibraryService>(MockBehavior.Strict);
        _movies = new Mock<IMovieTrackingService>(MockBehavior.Strict);
        _progress = new Mock<IWatchProgressService>(MockBehavior.Strict);
        _likes = new Mock<ILikeRepository>(MockBehavior.Strict);
        _ratings = new Mock<IRatingRepository>(MockBehavior.Strict);
        _metrics = new RecordingSiteMetrics();

        _sut = new PendingActionService(_tvDb.Object, _library.Object, _movies.Object, _progress.Object,
            _likes.Object, _ratings.Object, _metrics, NullLogger<PendingActionService>.Instance);
    }

    private static SeriesAggregate Series => new() { TvdbId = SeriesId, Name = "Breaking Bad", ImageUrl = "https://artworks.thetvdb.com/bb.jpg" };
    private static MovieAggregate Movie => new() { TvdbId = MovieId, Name = "Heat", ImageUrl = "https://artworks.thetvdb.com/heat.jpg" };
    private static Episode Episode => new() { Id = EpisodeId, SeriesId = SeriesId, SeasonNumber = 3, Number = 1, Name = "No Más" };

    private void Cached()
    {
        _tvDb.Setup(x => x.GetCachedSeriesAggregateAsync(SeriesId, It.IsAny<CancellationToken>())).ReturnsAsync(Series);
        _tvDb.Setup(x => x.GetCachedMovieAggregateAsync(MovieId, It.IsAny<CancellationToken>())).ReturnsAsync(Movie);
        _tvDb.Setup(x => x.GetCachedEpisodeDetailsAsync(EpisodeId, It.IsAny<CancellationToken>())).ReturnsAsync(Episode);
    }

    private void NothingCached()
    {
        _tvDb.Setup(x => x.GetCachedSeriesAggregateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync((SeriesAggregate?)null);
        _tvDb.Setup(x => x.GetCachedMovieAggregateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync((MovieAggregate?)null);
        _tvDb.Setup(x => x.GetCachedEpisodeDetailsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync((Episode?)null);
    }

    private void Exists()
    {
        _tvDb.Setup(x => x.GetSeriesAggregateByIdAsync(SeriesId, It.IsAny<CancellationToken>())).ReturnsAsync(Series);
        _tvDb.Setup(x => x.GetMovieAggregateByIdAsync(MovieId, It.IsAny<CancellationToken>())).ReturnsAsync(Movie);
        _tvDb.Setup(x => x.GetEpisodeDetailsAsync(EpisodeId, It.IsAny<CancellationToken>())).ReturnsAsync(Episode);
    }

    // ---- the sign-in page's sentence ------------------------------------------------

    [TestCase(PendingActionKind.AddSeries, null, "Sign in to add Breaking Bad to your library.", "You'll get a notification when new episodes air. Free, no password.")]
    [TestCase(PendingActionKind.LikeSeries, null, "Sign in to like Breaking Bad.", "Everything you like is kept together under Favorites. Free, no password.")]
    [TestCase(PendingActionKind.RateSeries, 9, "Sign in to rate Breaking Bad 9 out of 10.", "Your rating counts towards the Recall rating. Free, no password.")]
    [TestCase(PendingActionKind.MarkEpisodeWatched, null, "Sign in to mark S03E01 as watched.", "Recall keeps your place in Breaking Bad. Free, no password.")]
    [TestCase(PendingActionKind.LikeEpisode, null, "Sign in to like S03E01.", "Everything you like is kept together under Favorites. Free, no password.")]
    [TestCase(PendingActionKind.MarkMovieWatched, null, "Sign in to mark Heat as watched.", "Recall keeps track of everything you've watched. Free, no password.")]
    [TestCase(PendingActionKind.AddMovieToWatchlist, null, "Sign in to add Heat to your watchlist.", "Keep the movies you want to see in one place. Free, no password.")]
    public async Task DescribeAsync_Should_NameTheTitle_FromTheCachesOnly(PendingActionKind kind, int? rating, string lead, string detail)
    {
        Cached();
        var id = kind switch
        {
            PendingActionKind.MarkMovieWatched or PendingActionKind.AddMovieToWatchlist => MovieId,
            PendingActionKind.MarkEpisodeWatched or PendingActionKind.LikeEpisode => EpisodeId,
            _ => SeriesId
        };

        var prompt = await _sut.DescribeAsync(new PendingAction(kind, id, rating));

        prompt.Lead.Should().Be(lead);
        prompt.Detail.Should().Be(detail);
        prompt.PosterUrl.Should().NotBeNull("the series' or the movie's poster");
    }

    [TestCase(PendingActionKind.AddSeries, SeriesId, null, "Once you're signed in, we'll add Breaking Bad to your library.")]
    [TestCase(PendingActionKind.RateSeries, SeriesId, 9, "Once you're signed in, we'll rate Breaking Bad 9 out of 10.")]
    [TestCase(PendingActionKind.LikeMovie, MovieId, null, "Once you're signed in, we'll add Heat to your favorites.")]
    [TestCase(PendingActionKind.AddMovieToWatchlist, MovieId, null, "Once you're signed in, we'll add Heat to your watchlist.")]
    [TestCase(PendingActionKind.MarkMovieWatched, MovieId, null, "Once you're signed in, we'll mark Heat as watched.")]
    [TestCase(PendingActionKind.MarkEpisodeWatched, EpisodeId, null, "Once you're signed in, we'll mark S03E01 as watched.")]
    public async Task DescribeAsync_Should_SayWhatHappensAfterSignIn_ForCheckYourInbox(PendingActionKind kind, int id, int? rating, string after)
    {
        Cached();

        (await _sut.DescribeAsync(new PendingAction(kind, id, rating))).AfterSignIn.Should().Be(after);
    }

    [Test]
    public async Task DescribeAsync_Should_SpeakGenerally_WhenTheTitleIsNotCached()
    {
        NothingCached();

        (await _sut.DescribeAsync(new PendingAction(PendingActionKind.AddSeries, SeriesId))).Lead
            .Should().Be("Sign in to add this series to your library.");
        (await _sut.DescribeAsync(new PendingAction(PendingActionKind.MarkEpisodeWatched, EpisodeId))).Should()
            .Be(new PendingActionPrompt("Sign in to mark this episode as watched.", "Recall keeps your place in every series you watch. Free, no password.", null,
                "Once you're signed in, we'll mark this episode as watched."));
    }

    // ---- doing it after sign-in -------------------------------------------------------

    [Test]
    public async Task AddSeries_Should_AddIt_WithAnUndo_AndCountTheCompletion()
    {
        _library.Setup(x => x.AddAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AddSeriesResult(AddSeriesOutcome.Added, "Breaking Bad"));

        var outcome = await _sut.CompleteAsync(UserId, new PendingAction(PendingActionKind.AddSeries, SeriesId));

        outcome.Tone.Should().Be(PendingOutcomeTone.Success);
        outcome.Message.Should().Be("Added Breaking Bad to your library.");
        outcome.UndoAdd.Should().Be(new PendingUndo(PendingTitle.Series, SeriesId));
        _metrics.Keys.Should().Equal("action.completed.add-series");
    }

    [Test]
    public async Task AddSeries_Should_SaySo_WhenItIsAlreadyInTheLibrary()
    {
        Exists();
        _library.Setup(x => x.AddAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AddSeriesResult(AddSeriesOutcome.AlreadyInLibrary));

        var outcome = await _sut.CompleteAsync(UserId, new PendingAction(PendingActionKind.AddSeries, SeriesId));

        outcome.Should().Be(new PendingActionOutcome(PendingOutcomeTone.Info, "Breaking Bad is already in your library."));
    }

    [Test]
    public async Task AnActionThatCannotBeDone_Should_BeAnError_AndNotCounted()
    {
        _library.Setup(x => x.AddAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AddSeriesResult(AddSeriesOutcome.NotFound));

        var outcome = await _sut.CompleteAsync(UserId, new PendingAction(PendingActionKind.AddSeries, SeriesId));

        outcome.Tone.Should().Be(PendingOutcomeTone.Error);
        _metrics.Keys.Should().BeEmpty();
    }

    [Test]
    public async Task LikeSeries_Should_Like_ButNeverTakeAwayAnEarlierLike()
    {
        Exists();
        _likes.Setup(x => x.IsLikedAsync(UserId, LikeTargetType.Series, SeriesId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _likes.Setup(x => x.ToggleAsync(UserId, LikeTargetType.Series, SeriesId, SeriesId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        (await _sut.CompleteAsync(UserId, new PendingAction(PendingActionKind.LikeSeries, SeriesId)))
            .Should().Be(new PendingActionOutcome(PendingOutcomeTone.Success, "Liked Breaking Bad."));

        _likes.Setup(x => x.IsLikedAsync(UserId, LikeTargetType.Series, SeriesId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        (await _sut.CompleteAsync(UserId, new PendingAction(PendingActionKind.LikeSeries, SeriesId)))
            .Should().Be(new PendingActionOutcome(PendingOutcomeTone.Info, "You already like Breaking Bad."));

        _likes.Verify(x => x.ToggleAsync(UserId, LikeTargetType.Series, SeriesId, SeriesId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task RateMovie_Should_SaveTheRating()
    {
        Exists();
        _ratings.Setup(x => x.GetRatingAsync(UserId, RatingTargetType.Movie, MovieId, It.IsAny<CancellationToken>())).ReturnsAsync(6);
        _ratings.Setup(x => x.RateAsync(UserId, RatingTargetType.Movie, MovieId, MovieId, 8, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        (await _sut.CompleteAsync(UserId, new PendingAction(PendingActionKind.RateMovie, MovieId, 8)))
            .Should().Be(new PendingActionOutcome(PendingOutcomeTone.Success, "Rated Heat 8 out of 10."));
        _ratings.VerifyAll();
    }

    [Test]
    public async Task MarkMovieWatched_Should_MarkIt_AsASingleWatch()
    {
        Exists();
        _movies.Setup(x => x.MarkWatchedAsync(UserId, MovieId, WatchSource.Single, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        (await _sut.CompleteAsync(UserId, new PendingAction(PendingActionKind.MarkMovieWatched, MovieId)))
            .Should().Be(new PendingActionOutcome(PendingOutcomeTone.Success, "Marked Heat as watched."));
        _metrics.Keys.Should().Equal("action.completed.watched-movie");
    }

    [Test]
    public async Task AddMovieToWatchlist_Should_AddIt_WithAnUndo()
    {
        Exists();
        _movies.Setup(x => x.AddToWatchlistAsync(UserId, MovieId, "Heat", It.IsAny<CancellationToken>())).ReturnsAsync(MovieWatchlistOutcome.Added);

        var outcome = await _sut.CompleteAsync(UserId, new PendingAction(PendingActionKind.AddMovieToWatchlist, MovieId));

        outcome.Message.Should().Be("Added Heat to your watchlist.");
        outcome.UndoAdd.Should().Be(new PendingUndo(PendingTitle.Movie, MovieId));
    }

    [Test]
    public async Task MarkEpisodeWatched_Should_MarkThatEpisodeOfItsSeries_WithTheUndoOfTheMark()
    {
        Exists();
        var batch = new WatchedBatch(1, Guid.NewGuid());
        _progress.Setup(x => x.MarkEpisodeWatchedUndoablyAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UndoableEpisodeWatch(EpisodeWatchOutcome.MarkedWatched, batch, AddedToLibrary: "Breaking Bad"));

        var outcome = await _sut.CompleteAsync(UserId, new PendingAction(PendingActionKind.MarkEpisodeWatched, EpisodeId));

        outcome.Tone.Should().Be(PendingOutcomeTone.Success);
        outcome.Message.Should().Be("Marked S03E01 as watched and added Breaking Bad to your library.");
        outcome.WatchedSeriesId.Should().Be(SeriesId);
        outcome.Batch.Should().Be(batch);
    }

    [Test]
    public async Task MarkEpisodeWatched_Should_BeRefused_ForAnEpisodeNotAiredYet()
    {
        Exists();
        _progress.Setup(x => x.MarkEpisodeWatchedUndoablyAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UndoableEpisodeWatch(EpisodeWatchOutcome.NotAired));

        var outcome = await _sut.CompleteAsync(UserId, new PendingAction(PendingActionKind.MarkEpisodeWatched, EpisodeId));

        outcome.Should().Be(new PendingActionOutcome(PendingOutcomeTone.Error, "You can't mark an episode as watched before it has aired."));
        _metrics.Keys.Should().BeEmpty();
    }

    [Test]
    public async Task AnEpisodeWithoutASeries_Should_BeRefused()
    {
        _tvDb.Setup(x => x.GetEpisodeDetailsAsync(EpisodeId, It.IsAny<CancellationToken>())).ReturnsAsync(new Episode { Id = EpisodeId });

        (await _sut.CompleteAsync(UserId, new PendingAction(PendingActionKind.LikeEpisode, EpisodeId))).Tone
            .Should().Be(PendingOutcomeTone.Error);
    }

    // ---- already done for the account: an info toast, nothing changed, no Undo ----------

    public static IEnumerable<TestCaseData> AlreadyDone()
    {
        yield return new TestCaseData(new PendingAction(PendingActionKind.AddSeries, SeriesId),
            (Action<PendingActionServiceTests>)(t => t._library.Setup(x => x.AddAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AddSeriesResult(AddSeriesOutcome.AlreadyInLibrary))),
            "Breaking Bad is already in your library.").SetName("Already in the library");
        yield return new TestCaseData(new PendingAction(PendingActionKind.AddMovieToWatchlist, MovieId),
            (Action<PendingActionServiceTests>)(t => t._movies.Setup(x => x.AddToWatchlistAsync(UserId, MovieId, "Heat", It.IsAny<CancellationToken>()))
                .ReturnsAsync(MovieWatchlistOutcome.AlreadyOnWatchlist)),
            "Heat is already on your watchlist.").SetName("Already on the watchlist");
        yield return new TestCaseData(new PendingAction(PendingActionKind.AddMovieToWatchlist, MovieId),
            (Action<PendingActionServiceTests>)(t => t._movies.Setup(x => x.AddToWatchlistAsync(UserId, MovieId, "Heat", It.IsAny<CancellationToken>()))
                .ReturnsAsync(MovieWatchlistOutcome.AlreadyWatched)),
            "You've already watched Heat.").SetName("On the watchlist asked for, the movie already watched");
        yield return new TestCaseData(new PendingAction(PendingActionKind.MarkMovieWatched, MovieId),
            (Action<PendingActionServiceTests>)(t => t._movies.Setup(x => x.MarkWatchedAsync(UserId, MovieId, WatchSource.Single, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true)),
            "You had already watched Heat.").SetName("The movie already watched");
        yield return new TestCaseData(new PendingAction(PendingActionKind.MarkEpisodeWatched, EpisodeId),
            (Action<PendingActionServiceTests>)(t => t._progress.Setup(x => x.MarkEpisodeWatchedUndoablyAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UndoableEpisodeWatch(EpisodeWatchOutcome.MarkedWatched, new WatchedBatch(0, Guid.NewGuid())))),
            "You had already watched S03E01.").SetName("The episode already watched");
        foreach (var (kind, type, id, name) in new[]
                 {
                     (PendingActionKind.LikeSeries, LikeTargetType.Series, SeriesId, "Breaking Bad"),
                     (PendingActionKind.LikeMovie, LikeTargetType.Movie, MovieId, "Heat"),
                     (PendingActionKind.LikeEpisode, LikeTargetType.Episode, EpisodeId, "S03E01")
                 })
        {
            yield return new TestCaseData(new PendingAction(kind, id),
                (Action<PendingActionServiceTests>)(t => t._likes.Setup(x => x.IsLikedAsync(UserId, type, id, It.IsAny<CancellationToken>())).ReturnsAsync(true)),
                $"You already like {name}.").SetName($"Already liked: {kind}");
        }
        yield return new TestCaseData(new PendingAction(PendingActionKind.RateEpisode, EpisodeId, 9),
            (Action<PendingActionServiceTests>)(t => t._ratings.Setup(x => x.GetRatingAsync(UserId, RatingTargetType.Episode, EpisodeId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(9)),
            "You had already rated S03E01 9 out of 10.").SetName("Already given the same rating");
    }

    [TestCaseSource(nameof(AlreadyDone))]
    public async Task AnActionTheAccountHadAlready_Should_ChangeNothing_AndOfferNoUndo(
        PendingAction action, Action<PendingActionServiceTests> already, string message)
    {
        Exists();
        already(this);

        var outcome = await _sut.CompleteAsync(UserId, action);

        // The mocks are strict: a write that was not set up up there would have thrown.
        outcome.Should().Be(new PendingActionOutcome(PendingOutcomeTone.Info, message));
        outcome.UndoAdd.Should().BeNull();
        outcome.Batch.Should().BeNull();
        outcome.WatchedSeriesId.Should().BeNull();
        _likes.Verify(x => x.ToggleAsync(It.IsAny<Guid>(), It.IsAny<LikeTargetType>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _ratings.Verify(x => x.RateAsync(It.IsAny<Guid>(), It.IsAny<RatingTargetType>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task AnUndo_Should_OnlyEverBeOffered_ForWhatTheActionItselfAdded()
    {
        Exists();
        _library.Setup(x => x.AddAsync(UserId, SeriesId, It.IsAny<CancellationToken>())).ReturnsAsync(new AddSeriesResult(AddSeriesOutcome.Added, "Breaking Bad"));
        _movies.Setup(x => x.AddToWatchlistAsync(UserId, MovieId, "Heat", It.IsAny<CancellationToken>())).ReturnsAsync(MovieWatchlistOutcome.Added);
        var batch = new WatchedBatch(1, Guid.NewGuid());
        _progress.Setup(x => x.MarkEpisodeWatchedUndoablyAsync(UserId, SeriesId, EpisodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UndoableEpisodeWatch(EpisodeWatchOutcome.MarkedWatched, batch));

        (await _sut.CompleteAsync(UserId, new PendingAction(PendingActionKind.AddSeries, SeriesId))).UndoAdd
            .Should().Be(new PendingUndo(PendingTitle.Series, SeriesId), "the library row was made by this action");
        (await _sut.CompleteAsync(UserId, new PendingAction(PendingActionKind.AddMovieToWatchlist, MovieId))).UndoAdd
            .Should().Be(new PendingUndo(PendingTitle.Movie, MovieId), "the watchlist row was made by this action");
        (await _sut.CompleteAsync(UserId, new PendingAction(PendingActionKind.MarkEpisodeWatched, EpisodeId))).Batch
            .Should().Be(batch, "the Undo of a mark removes only the rows of its own batch");
    }

    [Test]
    public async Task AFailure_Should_BeAnErrorToast_NotAnException()
    {
        _library.Setup(x => x.AddAsync(UserId, SeriesId, It.IsAny<CancellationToken>())).ThrowsAsync(Timeouts.Request());

        var outcome = await _sut.CompleteAsync(UserId, new PendingAction(PendingActionKind.AddSeries, SeriesId));

        outcome.Tone.Should().Be(PendingOutcomeTone.Error);
        outcome.Message.Should().StartWith("You're signed in, but that could not be done right now.");
    }
}
