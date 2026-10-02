using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.OmdbCache;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages.Series;
using Recall.Web.Services;
using Recall.Web.Services.External.TheTvDb;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Pages;

/// <summary>
/// What Series/Details decides when it loads: which season opens, what the one
/// primary button is, and how a failed load is reported.
/// </summary>
[TestFixture]
public sealed class SeriesDetailsGetTests
{
    private const int SeriesId = 42;
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 10, 1);
    private static readonly DateOnly Past = Today.AddDays(-30);

    private Mock<ITheTvDbService> _tvDb = null!;
    private Mock<ICurrentUserService> _currentUser = null!;
    private Mock<ITrackedSeriesRepository> _tracked = null!;
    private Mock<IEpisodeWatchRepository> _watches = null!;
    private Mock<IRatingRepository> _ratings = null!;
    private DetailsModel _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _tvDb = new Mock<ITheTvDbService>();
        _currentUser = new Mock<ICurrentUserService>();
        _tracked = new Mock<ITrackedSeriesRepository>();
        _watches = new Mock<IEpisodeWatchRepository>();
        _ratings = new Mock<IRatingRepository>();
        _ratings
            .Setup(x => x.GetSummaryAsync(RatingTargetType.Series, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(RatingSummary.Empty);
        _watches
            .Setup(x => x.GetWatchedUtcByEpisodeAsync(It.IsAny<Guid>(), SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, DateTime>());

        var clock = new FixedTimeProvider(new DateTimeOffset(Today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

        _sut = new DetailsModel(
            _tvDb.Object,
            _currentUser.Object,
            _tracked.Object,
            _watches.Object,
            new WatchProgressService(_tvDb.Object, _watches.Object, _tracked.Object, _ratings.Object, clock, NullLogger<WatchProgressService>.Instance),
            Mock.Of<ILikeRepository>(),
            _ratings.Object,
            Mock.Of<IOmdbSnapshotStore>(),
            clock,
            NullLogger<DetailsModel>.Instance).WithTempData().WithHttpContext();
    }

    private static EpisodeSummary Ep(int id, int season, int number, DateOnly? aired = null) =>
        new() { Id = id, SeasonNumber = season, EpisodeNumber = number, Name = $"S{season}E{number}", Aired = aired ?? Past };

    /// <summary>Two specials and two seasons of two episodes; everything has aired unless a test says otherwise.</summary>
    private void SeriesExists(params EpisodeSummary[] episodes) => SeriesExists([], episodes);

    private void SeriesExists(string[] genres, params EpisodeSummary[] episodes)
    {
        if (episodes.Length == 0)
            episodes = [Ep(1, 0, 1), Ep(2, 0, 2), Ep(11, 1, 1), Ep(12, 1, 2), Ep(21, 2, 1), Ep(22, 2, 2)];

        _tvDb.Setup(x => x.GetSeriesAggregateByIdAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesAggregate
            {
                TvdbId = SeriesId,
                Name = "Show",
                Status = new SeriesStatus { Name = "Continuing", KeepUpdated = true },
                Genres = genres,
                Seasons = episodes.Select(e => e.SeasonNumber).Distinct()
                    .Select((n, i) => new SeasonSummary { Id = i + 1, Number = n }).ToList(),
                Episodes = episodes,
                RemoteIds = []
            });
    }

    private void SignedIn(bool tracked, params int[] watched)
    {
        _currentUser.SetupGet(x => x.IsAuthenticated).Returns(true);
        _currentUser.SetupGet(x => x.UserId).Returns(UserId);
        _tracked.Setup(x => x.ExistsAsync(UserId, SeriesId, It.IsAny<CancellationToken>())).ReturnsAsync(tracked);
        _watches
            .Setup(x => x.GetWatchedEpisodeIdsAsync(UserId, It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(watched.ToHashSet());
    }

    [Test]
    public async Task Seasons_Should_BeListedWithTheSpecialsLast()
    {
        SeriesExists();

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);

        _sut.SeasonNumbers.Should().Equal(1, 2, 0);
        _sut.GetSeasonName(0).Should().Be("Specials");
        _sut.GetSeasonName(2).Should().Be("S02");
    }

    [Test]
    public async Task AVisitor_Should_LandOnTheFirstSeason_NotTheSpecials()
    {
        SeriesExists();

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);

        _sut.SelectedSeason.Should().Be(1);
    }

    [Test]
    public async Task DefaultSeason_Should_BeTheFirstWithUnwatchedAiredEpisodes()
    {
        SeriesExists();
        SignedIn(tracked: true, watched: [11, 12]);

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);

        _sut.SelectedSeason.Should().Be(2);
    }

    [Test]
    public async Task DefaultSeason_Should_BeTheLatestNumberedSeason_WhenEverythingIsWatched()
    {
        SeriesExists();
        SignedIn(tracked: true, watched: [1, 2, 11, 12, 21, 22]);

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);

        _sut.SelectedSeason.Should().Be(2);
    }

    [Test]
    public async Task UnwatchedSpecials_Should_NotKeepASeriesFromBeingUpToDate()
    {
        SeriesExists();
        SignedIn(tracked: true, watched: [11, 12, 21, 22]);

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);
        var header = _sut.BuildHeader([]);

        header.Primary.Should().BeNull("a special is never offered as the next episode");
        header.State!.Text.Should().Be("Up to date");
        _sut.SelectedSeason.Should().Be(2, "the page opens on the latest season, not on the specials");
    }

    [Test]
    public async Task TheSeasonInTheUrl_Should_Win_AndAnUnknownOneShould_FallBack()
    {
        SeriesExists();
        SignedIn(tracked: true);

        _sut.Season = 0;
        await _sut.OnGetAsync(SeriesId, CancellationToken.None);
        _sut.SelectedSeason.Should().Be(0);

        _sut.Season = 99;
        await _sut.OnGetAsync(SeriesId, CancellationToken.None);
        _sut.SelectedSeason.Should().Be(1);
    }

    [Test]
    public async Task Header_Should_OfferTheNextEpisode_ForASeriesInTheLibrary()
    {
        SeriesExists();
        SignedIn(tracked: true, watched: [11]);

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);
        var header = _sut.BuildHeader([]);

        header.Primary!.Label.Should().Be("Mark S01E02 watched");
        header.Primary.Handler.Should().Be("ToggleEpisodeWatched");
        header.Primary.HiddenFields["episodeId"].Should().Be("12");
        header.State.Should().BeNull();
    }

    [Test]
    public async Task Header_Should_SkipTheSpecials_ForASeriesNotStarted()
    {
        SeriesExists();
        SignedIn(tracked: true);

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);

        _sut.BuildHeader([]).Primary!.Label.Should().Be("Mark S01E01 watched");
    }

    [Test]
    public async Task Header_Should_SayUpToDate_WhenNothingAiredIsLeft()
    {
        SeriesExists(Ep(11, 1, 1), Ep(12, 1, 2, Today.AddDays(7)));
        SignedIn(tracked: true, watched: [11]);

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);
        var header = _sut.BuildHeader([]);

        header.Primary.Should().BeNull();
        header.State!.Text.Should().Be("Up to date");
    }

    [Test]
    public async Task Header_Should_SayNothingAiredYet_ForASeriesInTheLibrary_NeverStarted_WithNoAiredRegularEpisode()
    {
        // Announced: its first episode airs next week.
        SeriesExists(Ep(11, 1, 1, Today.AddDays(7)));
        SignedIn(tracked: true);

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);
        var header = _sut.BuildHeader([]);

        header.Primary.Should().BeNull("there is nothing to mark watched");
        header.State!.Text.Should().Be("In your library · nothing aired yet");
    }

    [Test]
    public async Task Header_Should_SayNothingAiredYet_WhenOnlySpecialsHaveAired_EvenIfOneWasWatched()
    {
        // A special does not start a series, and is never the next episode.
        SeriesExists(Ep(1, 0, 1), Ep(11, 1, 1, Today.AddDays(7)));
        SignedIn(tracked: true, watched: [1]);

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);

        _sut.BuildHeader([]).State!.Text.Should().Be("In your library · nothing aired yet");
    }

    [Test]
    public async Task Header_Should_SayNothingAiredYet_ForATrackedSeriesWithNoEpisodesAtAll()
    {
        _tvDb.Setup(x => x.GetSeriesAggregateByIdAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesAggregate { TvdbId = SeriesId, Name = "Show", Episodes = [], RemoteIds = [] });
        SignedIn(tracked: true);

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);

        _sut.BuildHeader([]).State!.Text.Should().Be("In your library · nothing aired yet");
    }

    [Test]
    public async Task Header_Should_OfferAddToLibrary_ForASeriesNotInTheLibrary()
    {
        SeriesExists();
        SignedIn(tracked: false);

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);
        var header = _sut.BuildHeader([]);

        header.Primary!.Label.Should().Be("Add to library");
        header.Primary.Handler.Should().Be("ToggleLibrary");
    }

    [Test]
    public async Task Header_Should_CarryTheSummaryLine_AndTheSignInState()
    {
        SeriesExists();

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);
        var header = _sut.BuildHeader(["Drama"], "/Series/Details/42");

        header.IsAuthenticated.Should().BeFalse();
        header.ReturnUrl.Should().Be("/Series/Details/42");
        header.Summary.Should().Be("Continuing · 2 seasons");
        // The series has no TheTVDB genres yet (a row cached before they were
        // kept), so the chips fall back to OMDb's.
        header.Genres.Should().Equal("Drama");
    }

    [Test]
    public async Task Header_Should_ShowTheTvDbGenres_AndIgnoreOmdbs_WhenTheSeriesHasThem()
    {
        SeriesExists(genres: ["Science Fiction", "Thriller"]);

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);
        var header = _sut.BuildHeader(["Sci-Fi", "Mystery"]);

        header.Genres.Should().Equal("Science Fiction", "Thriller");
    }

    [Test]
    public async Task Header_Should_HaveNoGenres_WhenNeitherSourceHasAny()
    {
        SeriesExists();

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);

        _sut.BuildHeader([]).Genres.Should().BeEmpty();
    }

    [Test]
    public async Task ATheTvDbFailure_Should_Be503_WithNothingToRender()
    {
        _tvDb.Setup(x => x.GetSeriesAggregateByIdAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TheTvDbApiException("down", 502));

        var result = await _sut.OnGetAsync(SeriesId, CancellationToken.None);

        result.Should().BeOfType<PageResult>();
        _sut.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        _sut.Series.Should().BeNull();
        _sut.Aggregate.Should().BeNull();
    }
}
