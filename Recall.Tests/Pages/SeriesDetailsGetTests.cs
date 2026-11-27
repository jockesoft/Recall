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
using Recall.Web.Pages.Series;
using Recall.Web.Pages.Shared;
using Recall.Web.Services;
using Recall.Web.Services.External.TheTvDb;
using Recall.Web.Services.WatchTracking;
using Recall.Web.Services.PendingActions;

namespace Recall.Tests.Pages;

/// <summary>
/// What Series/Details decides when it loads: the hero (its action row in
/// every state, the progress and the remaining time), which season cards
/// open, the earlier-seasons group, "Jump to next", and how a failed load is
/// reported.
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
    private Mock<IOmdbSnapshotStore> _omdb = null!;
    private DetailsModel _sut = null!;

    /// <summary>What the page logged: a timeout is a warning, an aborted request nothing at Error.</summary>
    private RecordingLogger<DetailsModel> _log = null!;

    /// <summary>TheTVDB's status of the series <see cref="SeriesExists(EpisodeSummary[])"/> sets up.</summary>
    private string _status = "Continuing";

    /// <summary>The series' average runtime, the fallback for an episode without one.</summary>
    private int? _averageRuntime;

    [SetUp]
    public void SetUp()
    {
        _status = "Continuing";
        _averageRuntime = null;
        _tvDb = new Mock<ITheTvDbService>();
        _currentUser = new Mock<ICurrentUserService>();
        _tracked = new Mock<ITrackedSeriesRepository>();
        _watches = new Mock<IEpisodeWatchRepository>();
        _ratings = new Mock<IRatingRepository>();
        _omdb = new Mock<IOmdbSnapshotStore>();
        _ratings
            .Setup(x => x.GetSummaryAsync(RatingTargetType.Series, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(RatingSummary.Empty);
        _watches
            .Setup(x => x.GetWatchedUtcByEpisodeAsync(It.IsAny<Guid>(), SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, DateTime>());

        var clock = new FixedTimeProvider(new DateTimeOffset(Today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        _log = new RecordingLogger<DetailsModel>();

        _sut = new DetailsModel(
            _tvDb.Object,
            _currentUser.Object,
            Mock.Of<IPendingActions>(),
            _tracked.Object,
            new SeriesLibraryService(_tracked.Object, _tvDb.Object),
            _watches.Object,
            new WatchProgressService(_tvDb.Object, _watches.Object, _tracked.Object, _ratings.Object, clock, NullLogger<WatchProgressService>.Instance),
            Mock.Of<ILikeRepository>(),
            _ratings.Object,
            _omdb.Object,
            clock,
            _log).WithTempData().WithHttpContext();
    }

    private static EpisodeSummary Ep(int id, int season, int number, DateOnly? aired = null, int? runtime = null) =>
        new() { Id = id, SeasonNumber = season, EpisodeNumber = number, Name = $"S{season}E{number}", Aired = aired ?? Past, RuntimeMinutes = runtime };

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
                Network = "Discovery",
                FirstAired = new DateOnly(2010, 4, 12),
                AverageRuntimeMinutes = _averageRuntime,
                Status = new SeriesStatus { Name = _status, KeepUpdated = _status != "Ended" },
                Genres = genres,
                Seasons = episodes.Select(e => e.SeasonNumber).Distinct()
                    .Select((n, i) => new SeasonSummary { Id = i + 1, Number = n }).ToList(),
                Episodes = episodes,
                RemoteIds = []
            });
    }

    private void SignedIn(bool tracked, params int[] watched) => SignedIn(tracked, stoppedUtc: null, watched);

    private void SignedIn(bool tracked, DateTime? stoppedUtc, params int[] watched)
    {
        _currentUser.SetupGet(x => x.IsAuthenticated).Returns(true);
        _currentUser.SetupGet(x => x.UserId).Returns(UserId);
        _tracked.Setup(x => x.GetByUserAndTvdbIdAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tracked
                ? new TrackedSeries { Id = Guid.NewGuid(), UserId = UserId, TvdbId = SeriesId, Name = "Show", StoppedUtc = stoppedUtc }
                : null);
        _watches
            .Setup(x => x.GetWatchedEpisodeIdsAsync(UserId, It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(watched.ToHashSet());
    }

    private IEnumerable<int> OpenSeasons() => _sut.Seasons.Where(s => s.IsOpen).Select(s => s.Number);

    private async Task<TitleHeroModel> LoadHeroAsync()
    {
        await _sut.OnGetAsync(SeriesId, CancellationToken.None);
        return _sut.BuildHero();
    }

    // ---- season cards ---------------------------------------------------------------

    [Test]
    public async Task Seasons_Should_BeListedWithTheSpecialsLast()
    {
        SeriesExists();

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);

        _sut.Seasons.Select(s => s.Number).Should().Equal(1, 2, 0);
        _sut.Seasons[^1].Title.Should().Be("Specials");
        _sut.Seasons[^1].IsSpecials.Should().BeTrue();
        _sut.Seasons[0].Summary.Should().Be("Season 1 · 2026 · 2 episodes");
    }

    [Test]
    public async Task AVisitor_Should_SeeTheFirstSeasonOpen_NotTheSpecials()
    {
        SeriesExists();

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);

        OpenSeasons().Should().Equal(1);
    }

    [Test]
    public async Task TheSeasonOfTheNextEpisode_Should_BeOpen()
    {
        SeriesExists();
        SignedIn(tracked: true, watched: [11, 12]);

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);

        OpenSeasons().Should().Equal(2);
    }

    [Test]
    public async Task TheLatestNumberedSeason_Should_BeOpen_WhenEverythingIsWatched()
    {
        SeriesExists();
        SignedIn(tracked: true, watched: [1, 2, 11, 12, 21, 22]);

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);

        OpenSeasons().Should().Equal(2);
    }

    [Test]
    public async Task TheSeasonInTheUrl_Should_OpenToo_AndAnUnknownOneShould_BeIgnored()
    {
        SeriesExists();
        SignedIn(tracked: true);

        _sut.Season = 0;
        await _sut.OnGetAsync(SeriesId, CancellationToken.None);
        OpenSeasons().Should().Equal(1, 0);

        _sut.Season = 99;
        await _sut.OnGetAsync(SeriesId, CancellationToken.None);
        OpenSeasons().Should().Equal(1);
    }

    [Test]
    public async Task SeasonsBeforeTheThreeLatest_Should_BeGroupedAsEarlier_ButNeverTheSpecials()
    {
        SeriesExists(Ep(1, 0, 1), Ep(11, 1, 1), Ep(21, 2, 1), Ep(31, 3, 1), Ep(41, 4, 1), Ep(51, 5, 1));
        SignedIn(tracked: true, watched: [11, 21, 31, 41]);

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);

        _sut.Seasons.Where(s => s.IsEarlier).Select(s => s.Number).Should().Equal(1, 2);
        _sut.EarlierSeasonCount.Should().Be(2);
        OpenSeasons().Should().Equal(5);
    }

    [Test]
    public async Task ThreeSeasonsOrFewer_Should_HaveNoEarlierGroup()
    {
        SeriesExists(Ep(11, 1, 1), Ep(21, 2, 1), Ep(31, 3, 1));

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);

        _sut.EarlierSeasonCount.Should().Be(0);
    }

    [Test]
    public async Task ANextEpisodeInAnEarlierSeason_Should_OpenItsCard_InsideTheGroup()
    {
        SeriesExists(Ep(11, 1, 1), Ep(21, 2, 1), Ep(31, 3, 1), Ep(41, 4, 1), Ep(51, 5, 1));
        SignedIn(tracked: true, watched: [11]);

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);

        var open = _sut.Seasons.Should().ContainSingle(s => s.IsOpen).Subject;
        open.Number.Should().Be(2);
        open.IsEarlier.Should().BeTrue("the page then opens the earlier-seasons group around it");
    }

    [Test]
    public async Task ASeasonCard_Should_CountWatchedOfReleased_AndMarkEachRow()
    {
        SeriesExists(Ep(11, 1, 1, runtime: 82), Ep(12, 1, 2), Ep(13, 1, 3, Today.AddDays(7)));
        SignedIn(tracked: true, watched: [11]);

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);
        var season = _sut.Seasons.Single();

        season.ReleasedCount.Should().Be(2, "the third episode airs next week");
        season.WatchedReleasedCount.Should().Be(1);
        season.AllWatched.Should().BeFalse();
        season.Percent.Should().Be(50);
        season.HasMarkable.Should().BeTrue();
        season.Episodes.Select(e => e.Mark).Should().Equal(EpisodeMark.Watched, EpisodeMark.Unwatched, EpisodeMark.NotAired);
        season.Episodes.Select(e => e.IsReady).Should().Equal(false, true, false);
        season.Episodes[0].AiredText.Should().Be(DisplayDate.Format(Past, Today));
        season.Episodes[0].RuntimeMinutes.Should().Be(82);
        season.Episodes[2].AiredText.Should().BeNull();
        season.Episodes[2].Upcoming.Should().NotBeNull("an episode not released yet shows its release time instead");
    }

    [Test]
    public async Task ASeasonWithEveryReleasedEpisodeWatched_Should_BeAllWatched()
    {
        SeriesExists(Ep(11, 1, 1), Ep(12, 1, 2), Ep(13, 1, 3, Today.AddDays(7)));
        SignedIn(tracked: true, watched: [11, 12]);

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);

        var season = _sut.Seasons.Single();
        season.AllWatched.Should().BeTrue();
        season.Percent.Should().Be(100);
        season.HasMarkable.Should().BeFalse();
    }

    [Test]
    public async Task TheEpisodesHeading_Should_CountSeasons_ReleasedRegularEpisodes_AndTheirTime()
    {
        _averageRuntime = 50;
        SeriesExists(Ep(1, 0, 1, runtime: 90), Ep(11, 1, 1, runtime: 60), Ep(12, 1, 2), Ep(21, 2, 1, runtime: 70), Ep(22, 2, 2, Today.AddDays(7)));

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);

        _sut.EpisodesSummary.Should().Be("2 seasons · 3 episodes · 3 h", "60 + 50 (the average) + 70; the special and next week's episode do not count");
    }

    // ---- the "ready" control --------------------------------------------------------------

    private IEnumerable<int> ReadyEpisodes() => _sut.Seasons.SelectMany(s => s.Episodes).Where(e => e.IsReady).Select(e => e.Id);

    [Test]
    public async Task OnlyTheEpisodeThePrimaryButtonMarks_Should_BeReady_WhenNothingIsWatched()
    {
        SeriesExists();
        SignedIn(tracked: true);

        var hero = await LoadHeroAsync();

        hero.Actions.Primary!.HiddenFields["episodeId"].Should().Be("11");
        ReadyEpisodes().Should().Equal(11);
        _sut.Seasons.SelectMany(s => s.Episodes).Where(e => e.Id != 11)
            .Should().OnlyContain(e => e.Mark == EpisodeMark.Unwatched && !e.IsReady,
                "every other aired, unwatched episode (the specials too) gets the plain ring");
    }

    [Test]
    public async Task TheReadyEpisode_Should_BeTheNextOne_EvenInAnOlderSeason()
    {
        SeriesExists(Ep(11, 1, 1), Ep(21, 2, 1), Ep(22, 2, 2), Ep(31, 3, 1), Ep(41, 4, 1), Ep(51, 5, 1));
        SignedIn(tracked: true, watched: [11, 31, 41, 51]);

        var hero = await LoadHeroAsync();

        hero.Actions.Primary!.Label.Should().Be("Mark S02E01 watched");
        ReadyEpisodes().Should().Equal(21);
        _sut.Seasons.Single(s => s.Number == 2).IsEarlier.Should().BeTrue();
    }

    [Test]
    public async Task NoEpisode_Should_BeReady_WhenThePrimaryButtonMarksNone()
    {
        SeriesExists();

        SignedIn(tracked: false);
        await _sut.OnGetAsync(SeriesId, CancellationToken.None);
        ReadyEpisodes().Should().BeEmpty("not in the library: the primary button adds it");

        SignedIn(tracked: true, stoppedUtc: StoppedOn, watched: [11]);
        await _sut.OnGetAsync(SeriesId, CancellationToken.None);
        ReadyEpisodes().Should().BeEmpty("stopped: the primary button resumes it");

        SignedIn(tracked: true, watched: [11, 12, 21, 22]);
        await _sut.OnGetAsync(SeriesId, CancellationToken.None);
        ReadyEpisodes().Should().BeEmpty("up to date: only specials are left, and they are never next");
    }

    // ---- jump to next ------------------------------------------------------------------

    [Test]
    public async Task JumpToNext_Should_PointAtTheNextEpisode_OnlyWhenThereIsOne()
    {
        SeriesExists();
        SignedIn(tracked: true, watched: [11]);
        await _sut.OnGetAsync(SeriesId, CancellationToken.None);
        _sut.NextEpisodeId.Should().Be(12);

        SignedIn(tracked: true, watched: [11, 12, 21, 22]);
        await _sut.OnGetAsync(SeriesId, CancellationToken.None);
        _sut.NextEpisodeId.Should().BeNull("up to date: there is nothing to jump to");
    }

    [Test]
    public async Task JumpToNext_Should_NotBeOffered_ToAVisitor()
    {
        SeriesExists();

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);

        _sut.NextEpisodeId.Should().BeNull();
    }

    // ---- the action row, in every state --------------------------------------------------

    [Test]
    public async Task Actions_Should_OfferTheNextEpisode_WithItsAirDateAndRuntime()
    {
        SeriesExists(Ep(11, 1, 1), Ep(12, 1, 2, runtime: 82));
        SignedIn(tracked: true, watched: [11]);

        var actions = (await LoadHeroAsync()).Actions;

        actions.Primary!.Label.Should().Be("Mark S01E02 watched");
        actions.Primary.Detail.Should().Be($"Aired {DisplayDate.Format(Past, Today)} · 82 min");
        actions.Primary.Handler.Should().Be("ToggleEpisodeWatched");
        actions.Primary.HiddenFields["episodeId"].Should().Be("12");
        actions.Status.Should().BeNull();
        actions.Notice.Should().BeNull();
        actions.Menu.Select(m => m.Label).Should().Equal("Stop watching", "Remove from library");
        actions.Menu[1].ModalId.Should().Be(DetailsModel.RemoveFromLibraryModalId, "removing is confirmed first");
        actions.MenuLabel.Should().Be("More actions for Show");
        actions.Like.Should().NotBeNull();
        actions.ShareTitle.Should().Be("Show");
    }

    [Test]
    public async Task TheNextEpisodesRuntime_Should_FallBackToTheSeriesAverage()
    {
        _averageRuntime = 44;
        SeriesExists(Ep(11, 1, 1), Ep(12, 1, 2));
        SignedIn(tracked: true, watched: [11]);

        (await LoadHeroAsync()).Actions.Primary!.Detail.Should().EndWith("· 44 min");
    }

    [Test]
    public async Task Actions_Should_SkipTheSpecials_ForASeriesNotStarted()
    {
        SeriesExists();
        SignedIn(tracked: true);

        (await LoadHeroAsync()).Actions.Primary!.Label.Should().Be("Mark S01E01 watched");
    }

    [Test]
    public async Task Actions_Should_SayUpToDate_WithTheNextRelease()
    {
        SeriesExists(Ep(11, 1, 1), Ep(12, 1, 2, Today.AddDays(7)));
        SignedIn(tracked: true, watched: [11]);

        var actions = (await LoadHeroAsync()).Actions;

        actions.Primary.Should().BeNull();
        actions.Status!.Text.Should().Be("Up to date");
        actions.Status.Icon.Should().Be(Icons.UpToDate);
        actions.Status.Detail.Should().Be("S01E02 airs");
        actions.Status.Release!.Release.AirDate.Should().Be(Today.AddDays(7));
        actions.Menu.Select(m => m.Label).Should().Equal("Stop watching", "Remove from library");
    }

    [Test]
    public async Task Actions_Should_SayNothingAnnouncedYet_WhenUpToDateWithNothingComing()
    {
        SeriesExists(Ep(11, 1, 1), Ep(12, 1, 2));
        SignedIn(tracked: true, watched: [11, 12]);

        var status = (await LoadHeroAsync()).Actions.Status!;

        status.Text.Should().Be("Up to date");
        status.Detail.Should().Be("Nothing announced yet");
        status.Release.Should().BeNull();
    }

    [Test]
    public async Task Actions_Should_SayTheSeriesHasEnded_ForAFinishedSeries_AndOfferOnlyRemoval()
    {
        _status = "Ended";
        SeriesExists(Ep(11, 1, 1), Ep(12, 1, 2));
        SignedIn(tracked: true, watched: [11, 12]);

        var actions = (await LoadHeroAsync()).Actions;

        actions.Status!.Detail.Should().Be("The series has ended");
        actions.Menu.Select(m => m.Label).Should().Equal("Remove from library");
    }

    [Test]
    public async Task UnwatchedSpecials_Should_NotKeepASeriesFromBeingUpToDate()
    {
        SeriesExists();
        SignedIn(tracked: true, watched: [11, 12, 21, 22]);

        var actions = (await LoadHeroAsync()).Actions;

        actions.Primary.Should().BeNull("a special is never offered as the next episode");
        actions.Status!.Text.Should().Be("Up to date");
        OpenSeasons().Should().Equal(new[] { 2 }, "the page opens on the latest season, not on the specials");
    }

    [Test]
    public async Task Actions_Should_OfferAddToLibrary_ForASeriesNotInTheLibrary_WithNoMenu()
    {
        SeriesExists();
        SignedIn(tracked: false);

        var hero = await LoadHeroAsync();

        hero.Actions.Primary!.Label.Should().Be("Add to library");
        hero.Actions.Primary.Handler.Should().Be("ToggleLibrary");
        hero.Actions.Menu.Should().BeEmpty("there is nothing to stop or remove");
        hero.Progress.Should().BeNull("progress is only shown for a series in the library");
        hero.Back.Should().Be(new TitleBackLink("Back", "/Search", UseHistory: true));
    }

    [Test]
    public async Task AStoppedSeries_Should_SayWhenItWasStopped_AndOfferResume()
    {
        SeriesExists(Ep(11, 1, 1), Ep(12, 1, 2));
        SignedIn(tracked: true, stoppedUtc: StoppedOn, watched: [11]);

        var actions = (await LoadHeroAsync()).Actions;

        _sut.StoppedUtc.Should().Be(StoppedOn);
        actions.Notice!.Text.Should().Be("You stopped watching on Fri, Sep 4", "the sentence comes before the button it explains");
        actions.Primary!.Label.Should().Be("Resume watching");
        actions.Primary.Handler.Should().Be("ResumeWatching");
        actions.Primary.RouteId.Should().Be(SeriesId);
        actions.Status.Should().BeNull();
        actions.Menu.Select(m => m.Label).Should().Equal("Remove from library");
        _sut.CanStopWatching.Should().BeFalse("it is stopped already");
    }

    [Test]
    public async Task AStoppedSeries_Should_OfferResume_EvenWhenNothingIsLeftToWatch()
    {
        SeriesExists(Ep(11, 1, 1), Ep(12, 1, 2, Today.AddDays(7)));
        SignedIn(tracked: true, stoppedUtc: StoppedOn, watched: [11]);

        var actions = (await LoadHeroAsync()).Actions;

        actions.Notice!.Text.Should().StartWith("You stopped watching on");
        actions.Primary!.Label.Should().Be("Resume watching");
    }

    [Test]
    public async Task Actions_Should_SayNothingAiredYet_ForASeriesInTheLibrary_NeverStarted_WithNoAiredRegularEpisode()
    {
        // Announced: its first episode airs next week.
        SeriesExists(Ep(11, 1, 1, Today.AddDays(7)));
        SignedIn(tracked: true);

        var hero = await LoadHeroAsync();

        hero.Actions.Primary.Should().BeNull("there is nothing to mark watched");
        hero.Actions.Status!.Text.Should().Be("In your library · nothing aired yet");
        hero.Actions.Status.Detail.Should().Be("S01E01 airs");
        hero.Progress.Should().BeNull("nothing is released, so there is no progress to show");
        _sut.NextEpisodeId.Should().BeNull();
    }

    [Test]
    public async Task Actions_Should_SayNothingAiredYet_WhenOnlySpecialsHaveAired_EvenIfOneWasWatched()
    {
        // A special does not start a series, and is never the next episode.
        SeriesExists(Ep(1, 0, 1), Ep(11, 1, 1, Today.AddDays(7)));
        SignedIn(tracked: true, watched: [1]);

        (await LoadHeroAsync()).Actions.Status!.Text.Should().Be("In your library · nothing aired yet");
    }

    [Test]
    public async Task Actions_Should_SayNothingAiredYet_ForATrackedSeriesWithNoEpisodesAtAll()
    {
        _tvDb.Setup(x => x.GetSeriesAggregateByIdAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesAggregate { TvdbId = SeriesId, Name = "Show", Episodes = [], RemoteIds = [] });
        SignedIn(tracked: true);

        var hero = await LoadHeroAsync();

        hero.Actions.Status!.Text.Should().Be("In your library · nothing aired yet");
        _sut.Seasons.Should().BeEmpty();
    }

    [Test]
    public async Task AVisitor_Should_GetAddToLibrary_TheHeartAndTheRating_AndNoProgress()
    {
        SeriesExists();

        var hero = await LoadHeroAsync();

        hero.Actions.Primary!.Label.Should().Be("Add to library");
        hero.Actions.Primary.Handler.Should().Be("ToggleLibrary");
        hero.Actions.Like!.Handler.Should().Be("ToggleSeriesLike");
        hero.Actions.Menu.Should().BeEmpty();
        hero.Rating!.RateHandler.Should().Be("RateSeries");
        hero.Rating.CurrentValue.Should().BeNull();
        hero.Progress.Should().BeNull();
    }

    // ---- progress -----------------------------------------------------------------------

    [Test]
    public async Task Progress_Should_CountReleasedRegularEpisodes_AndTheTimeLeft_WithoutTheSpecials()
    {
        _averageRuntime = 50;
        SeriesExists(
            Ep(1, 0, 1, runtime: 100), Ep(2, 0, 2, runtime: 100),
            Ep(11, 1, 1, runtime: 45), Ep(12, 1, 2, runtime: 45),
            Ep(21, 2, 1), Ep(22, 2, 2, runtime: 60),
            Ep(23, 2, 3, Today.AddDays(7), runtime: 60));
        SignedIn(tracked: true, watched: [11, 1]);
        _watches
            .Setup(x => x.GetWatchedUtcByEpisodeAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, DateTime>
            {
                [11] = new(2026, 9, 20, 21, 0, 0, DateTimeKind.Utc),
                [1] = new(2026, 9, 22, 21, 0, 0, DateTimeKind.Utc)
            });

        var progress = (await LoadHeroAsync()).Progress!;

        progress.WatchedCount.Should().Be(1, "the special watched does not count");
        progress.ReleasedCount.Should().Be(4, "neither the specials nor next week's episode count");
        progress.CountText.Should().Be("1 of 4 episodes · 25%");
        progress.RemainingMinutes.Should().Be(155, "45 + 50 (the average, for an episode without a runtime) + 60");
        progress.LeftText.Should().Be("3 left · 2 h 35 min");
        progress.LastWatchedText.Should().Be("Last watched Tue, Sep 22");
    }

    [Test]
    public async Task Progress_Should_LeaveOutTheTime_WhenNoRuntimeIsKnown()
    {
        SeriesExists(Ep(11, 1, 1), Ep(12, 1, 2), Ep(13, 1, 3));
        SignedIn(tracked: true);

        var progress = (await LoadHeroAsync()).Progress!;

        progress.RemainingMinutes.Should().BeNull();
        progress.LeftText.Should().Be("3 left");
        progress.LastWatchedText.Should().Be("Not started yet");
    }

    [Test]
    public void ThePercent_Should_RoundDown_SoOnlyEverythingIs100()
    {
        new TitleProgressModel(342, 343, 82, null).Percent.Should().Be(99);
        new TitleProgressModel(342, 343, 82, null).CountText.Should().Be("342 of 343 episodes · 99%");
        new TitleProgressModel(343, 343, null, null).Percent.Should().Be(100);
        new TitleProgressModel(343, 343, null, null).LeftText.Should().Be("All watched");
        new TitleProgressModel(342, 343, 82, null).LeftText.Should().Be("1 left · 1 h 22 min");
    }

    // ---- the rest of the hero ------------------------------------------------------------

    [Test]
    public async Task Hero_Should_CarryTheMetaLine_TheStatusChip_AndTheRatings()
    {
        SeriesExists();
        SignedIn(tracked: true);
        _omdb.Setup(x => x.GetAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OmdbSeries { ImdbRating = "7.3", ImdbVotes = "12,345", Genre = "Drama" });
        _ratings
            .Setup(x => x.GetSummaryAsync(RatingTargetType.Series, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RatingSummary(9.04, 3));
        _ratings
            .Setup(x => x.GetRatingAsync(UserId, RatingTargetType.Series, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(9);

        var hero = await LoadHeroAsync();

        hero.Name.Should().Be("Show");
        hero.Meta.Should().Be("2010– · 2 seasons · Discovery");
        hero.Status.Should().Be(new TitleStatusChip("Continuing", Active: true));
        hero.ImdbRating.Should().Be("7.3");
        hero.RecallRating.Should().Be("9.0");
        hero.RecallRatingCount.Should().Be(3);
        hero.RatingChipText.Should().Be("Your rating 9");
        hero.Back.Should().Be(new TitleBackLink("Library", "/Library", UseHistory: false));
        // The series has no TheTVDB genres yet (a row cached before they were
        // kept), so the chips fall back to OMDb's.
        hero.Genres.Should().Equal("Drama");
    }

    [Test]
    public async Task TheRatingChip_Should_SayRate_WhileUnrated()
    {
        SeriesExists();
        SignedIn(tracked: false);

        (await LoadHeroAsync()).RatingChipText.Should().Be("Rate");
    }

    [Test]
    public async Task Hero_Should_ShowTheTvDbGenres_AndIgnoreOmdbs_WhenTheSeriesHasThem()
    {
        SeriesExists(genres: ["Science Fiction", "Thriller"]);
        _omdb.Setup(x => x.GetAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OmdbSeries { Genre = "Sci-Fi, Mystery" });

        (await LoadHeroAsync()).Genres.Should().Equal("Science Fiction", "Thriller");
    }

    [Test]
    public async Task Hero_Should_HaveNoGenres_WhenNeitherSourceHasAny()
    {
        SeriesExists();

        (await LoadHeroAsync()).Genres.Should().BeEmpty();
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

    // ---- stop watching ------------------------------------------------------------------

    private static readonly DateTime StoppedOn = new(2026, 9, 4, 18, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task StopWatching_Should_BeOffered_ForASeriesBeingWatched()
    {
        SeriesExists(Ep(11, 1, 1), Ep(12, 1, 2));
        SignedIn(tracked: true, watched: [11]);

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);

        _sut.CanStopWatching.Should().BeTrue();
        _sut.StoppedUtc.Should().BeNull();
    }

    [Test]
    public async Task StopWatching_Should_BeOffered_ForAnUpToDateSeriesThatContinues()
    {
        SeriesExists(Ep(11, 1, 1), Ep(12, 1, 2, Today.AddDays(7)));
        SignedIn(tracked: true, watched: [11]);

        var actions = (await LoadHeroAsync()).Actions;

        actions.Status!.Text.Should().Be("Up to date");
        _sut.CanStopWatching.Should().BeTrue("more episodes are coming, and the user may not want them");
    }

    [Test]
    public async Task StopWatching_Should_NotBeOffered_ForAFinishedSeries()
    {
        _status = "Ended";
        SeriesExists(Ep(1, 0, 1), Ep(11, 1, 1), Ep(12, 1, 2));
        SignedIn(tracked: true, watched: [11, 12]);

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);

        _sut.CanStopWatching.Should().BeFalse("ended and every aired regular episode watched: there is nothing to stop");
        _sut.IsTrackedByCurrentUser.Should().BeTrue("\"Remove from library\" is still there");
    }

    [Test]
    public async Task StopWatching_Should_BeOffered_ForAnEndedSeriesThatIsNotFullyWatched()
    {
        _status = "Ended";
        SeriesExists(Ep(11, 1, 1), Ep(12, 1, 2));
        SignedIn(tracked: true, watched: [11]);

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);

        _sut.CanStopWatching.Should().BeTrue();
    }

    [Test]
    public async Task StopWatching_Should_NotBeOffered_ForASeriesThatIsNotInTheLibrary_OrToAVisitor()
    {
        SeriesExists();

        await _sut.OnGetAsync(SeriesId, CancellationToken.None);
        _sut.CanStopWatching.Should().BeFalse("a visitor");

        SignedIn(tracked: false);
        await _sut.OnGetAsync(SeriesId, CancellationToken.None);
        _sut.CanStopWatching.Should().BeFalse("not in the library");
    }

    // ---- a timeout is TheTVDB failing; a visitor who went away is not an error ----

    [Test]
    public async Task ATheTvDbTimeout_Should_Be503_LikeAnyTheTvDbFailure()
    {
        _tvDb.Setup(x => x.GetSeriesAggregateByIdAsync(SeriesId, It.IsAny<CancellationToken>())).ThrowsAsync(Timeouts.Request());

        var result = await _sut.OnGetAsync(SeriesId, CancellationToken.None);

        result.Should().BeOfType<PageResult>();
        _sut.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        _sut.Series.Should().BeNull();
        _sut.Aggregate.Should().BeNull();
        _log.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("timeout"));
        _log.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    [Test]
    public async Task AnAbortedRequest_Should_NotRenderAFailure_NorLogAnError()
    {
        using var request = new CancellationTokenSource();
        _tvDb.Setup(x => x.GetSeriesAggregateByIdAsync(SeriesId, It.IsAny<CancellationToken>())).Callback(() => request.Cancel()).ThrowsAsync(new OperationCanceledException(request.Token));

        var act = () => _sut.OnGetAsync(SeriesId, request.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "the request is gone: ASP.NET Core logs that at Debug, not as an error");
        _sut.Response.StatusCode.Should().Be(StatusCodes.Status200OK, "no error state was set for a visitor who left");
        _log.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }
}
