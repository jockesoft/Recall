using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    // "Now" for the release-moment rules: noon UTC on Today.
    private static readonly DateTime Now = Today.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);
    private static readonly Guid UserId = Guid.NewGuid();

    private Mock<ICurrentUserService> _currentUser = null!;
    private Mock<ITrackedSeriesRepository> _tracked = null!;
    private Mock<IEpisodeWatchRepository> _watches = null!;
    private Mock<IMovieWatchRepository> _movieWatches = null!;
    private Mock<ITrackedMovieRepository> _watchlist = null!;
    private Mock<ITheTvDbService> _tvDb = null!;
    private Mock<ILikeRepository> _likes = null!;
    private LibraryModel _sut = null!;
    private LibraryOptions _libraryOptions = null!;

    [SetUp]
    public void SetUp()
    {
        // A fresh instance per test: some tests change the settings.
        _libraryOptions = new LibraryOptions();
        _addedUtc.Clear();
        _stoppedUtc.Clear();

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
                WatchProgressCalculator.Build(id, episodes, watched, Now));

        // An empty library unless a test says otherwise.
        _tracked.Setup(x => x.GetByUserAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _likes.Setup(x => x.GetLikesAsync(UserId, LikeTargetType.Series, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _movieWatches.Setup(x => x.GetWatchedMoviesAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _watchlist.Setup(x => x.GetByUserAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        SetUpWatched();
        _watches
            .Setup(x => x.GetLastWatchedUtcBySeriesAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, DateTime>());

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
            Options.Create(_libraryOptions),
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

    /// <summary>When a series was added to the library, for tests about the order; set before <see cref="SetUpSeries"/>.</summary>
    private readonly Dictionary<int, DateTime> _addedUtc = [];

    /// <summary>The series the user stopped watching, and when; set before <see cref="SetUpSeries"/>.</summary>
    private readonly Dictionary<int, DateTime> _stoppedUtc = [];

    private void SetUpSeries(params SeriesAggregate[] aggregates)
    {
        _tracked
            .Setup(x => x.GetByUserAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(aggregates.Select(a => new TrackedSeries
            {
                Id = Guid.NewGuid(), UserId = UserId, TvdbId = a.TvdbId, Name = a.Name,
                CreatedUtc = _addedUtc.GetValueOrDefault(a.TvdbId),
                StoppedUtc = _stoppedUtc.TryGetValue(a.TvdbId, out var stoppedUtc) ? stoppedUtc : null
            }).ToList());

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
    public async Task AnEndedSeries_Should_BeWatched_OnlyWhenItWasStarted_AndEverythingAiredIsWatched()
    {
        var special = new EpisodeSummary { Id = 5, SeasonNumber = 0, EpisodeNumber = 1, Name = "Making of", Aired = Today.AddDays(-300) };
        _addedUtc[1] = DaysAgo(10);
        _addedUtc[3] = DaysAgo(10);
        SetUpSeries(
            Series(1, "Ended, Never Started", "Ended", Ep(10, 1, Today.AddDays(-400)), Ep(11, 2, Today.AddDays(-390))),
            Series(2, "Ended, Fully Watched", "Ended", Ep(20, 1, Today.AddDays(-400)), Ep(21, 2, Today.AddDays(-390))),
            Series(3, "Ended, Only The Special Watched", "Ended", special, Ep(30, 1, Today.AddDays(-400))));
        SetUpWatched(20, 21, 5);
        SetUpLastWatched((2, 3), (3, 3));

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Watched.Select(i => i.Name).Should().Equal("Ended, Fully Watched");
        _sut.UpToDate.Should().BeEmpty();
        _sut.Watching.Select(i => i.Name).Should().Equal(
            ["Ended, Only The Special Watched", "Ended, Never Started"],
            "watching a special is activity, so that series comes first; the other is ordered by when it was added");
        _sut.Watching[1].ProgressText.Should().Be("0 of 2 · S01");
    }

    [Test]
    public async Task AnEndedSeries_NeverStarted_Should_GoDormant_MeasuredFromTheDateItWasAdded()
    {
        _addedUtc[1] = DaysAgo(200);
        _addedUtc[2] = DaysAgo(5);
        SetUpSeries(
            Series(1, "Ended, Added Long Ago", "Ended", Ep(10, 1, Today.AddDays(-400))),
            Series(2, "Ended, Added This Week", "Ended", Ep(20, 1, Today.AddDays(-400))));

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Watching.Select(i => i.TvdbId).Should().Equal(2);
        _sut.Dormant.Select(i => i.TvdbId).Should().Equal(1);
        _sut.Watched.Should().BeEmpty();
    }

    [Test]
    public async Task ASeriesWithNothingAiredYet_NeverStarted_Should_BeUnderWatching_AndNeverDormant()
    {
        // Ended with only a special, and announced but not yet premiered: there
        // is nothing to have watched, so neither is "Watched" or "Up to date",
        // and nothing to have left unwatched for a while either.
        var special = new EpisodeSummary { Id = 5, SeasonNumber = 0, EpisodeNumber = 1, Name = "Pilot film", Aired = Today.AddDays(-300) };
        _addedUtc[1] = DaysAgo(200);
        _addedUtc[2] = DaysAgo(150);
        SetUpSeries(
            Series(1, "Ended, Only A Special", "Ended", special),
            Series(2, "Announced", "Upcoming", Ep(20, 1, Today.AddDays(20))));

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Watching.Select(i => i.Name).Should().Equal(["Announced", "Ended, Only A Special"], "newest added first");
        _sut.Dormant.Should().BeEmpty("added long ago, but there has been nothing to watch");
        _sut.Watched.Should().BeEmpty();
        _sut.UpToDate.Should().BeEmpty();
        _sut.Watching[0].ProgressText.Should().BeNull();
    }

    // ---- one section on its own (?section=) ---------------------------------------

    [Test]
    public async Task Section_Should_SelectOneSection_ByItsSlug()
    {
        SetUpSeries(
            Series(1, "Behind", "Continuing", Ep(10, 1, Today.AddDays(-20))),
            Series(3, "Finished", "Ended", Ep(30, 1, Today.AddDays(-400))));
        SetUpWatched(30);
        _sut.Section = "Watched";

        await _sut.OnGetAsync(CancellationToken.None);

        var selected = _sut.SelectedSection!;
        selected.Section.Should().Be(LibrarySection.Watched);
        selected.Title.Should().Be("Watched");
        selected.Items.Select(i => i.Name).Should().Equal("Finished");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("everything")]
    public async Task Section_Should_GiveTheFullLibrary_ForAnythingThatIsNotASection(string? section)
    {
        SetUpSeries(Series(1, "Behind", "Continuing", Ep(10, 1, Today.AddDays(-20))));
        _sut.Section = section;

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.SelectedSection.Should().BeNull();
        _sut.Sections.Select(s => s.Slug).Should().Equal("watching", "dormant", "to-watch", "up-to-date", "watched", "stopped");
    }

    [Test]
    public async Task ALikeFromTheSectionView_Should_ComeBackToIt()
    {
        _sut.Section = "up-to-date";

        var fromSection = await _sut.OnPostToggleSeriesLikeAsync(1, CancellationToken.None);

        fromSection.Should().BeOfType<RedirectToPageResult>()
            .Which.RouteValues.Should().Contain("section", "up-to-date");

        _sut.Section = null;
        var fromLibrary = await _sut.OnPostToggleSeriesLikeAsync(1, CancellationToken.None);

        fromLibrary.Should().BeOfType<RedirectToPageResult>().Which.RouteValues.Should().BeNull();
    }

    [Test]
    public async Task Watching_Should_UseTheContinueWatchingOrder_RecentActivityFirst_ThenNewestAdded()
    {
        _addedUtc[1] = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        _addedUtc[4] = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
        SetUpSeries(
            Series(1, "Alpha, Never Watched, Added In August", "Continuing", Ep(10, 1, Today.AddDays(-5))),
            Series(2, "Watched Last Week", "Continuing", Ep(20, 1, Today.AddDays(-9)), Ep(21, 2, Today.AddDays(-5))),
            Series(3, "Watched Yesterday", "Continuing", Ep(30, 1, Today.AddDays(-9)), Ep(31, 2, Today.AddDays(-5))),
            Series(4, "Zulu, Never Watched, Added This Week", "Continuing", Ep(40, 1, Today.AddDays(-5))));
        SetUpWatched(20, 30);
        _watches
            .Setup(x => x.GetLastWatchedUtcBySeriesAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, DateTime>
            {
                [2] = new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc),
                [3] = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc)
            });

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Watching.Select(i => i.TvdbId).Should().Equal(
            [3, 2, 4, 1], "the same order as the Dashboard's Continue watching");
        _watches.Verify(x => x.GetLastWatchedUtcBySeriesAsync(UserId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- haven't watched in a while -------------------------------------------

    private static DateTime DaysAgo(int days) => Today.AddDays(-days).ToDateTime(new TimeOnly(20, 0), DateTimeKind.Utc);

    private void SetUpLastWatched(params (int SeriesId, int DaysAgo)[] activity) =>
        _watches
            .Setup(x => x.GetLastWatchedUtcBySeriesAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(activity.ToDictionary(a => a.SeriesId, a => DaysAgo(a.DaysAgo)));

    [Test]
    public async Task Watching_Should_HoldOnlyActiveSeries_AndTheRestShouldBeTheDormantGroup()
    {
        _addedUtc[4] = DaysAgo(200);
        _addedUtc[5] = DaysAgo(3);
        SetUpSeries(
            Series(1, "Watched Yesterday", "Continuing", Ep(10, 1, Today.AddDays(-400)), Ep(11, 2, Today.AddDays(-390))),
            Series(2, "Watched Four Months Ago", "Continuing", Ep(20, 1, Today.AddDays(-400)), Ep(21, 2, Today.AddDays(-390))),
            Series(3, "Watched A Year Ago", "Ended", Ep(30, 1, Today.AddDays(-400)), Ep(31, 2, Today.AddDays(-390))),
            Series(4, "Never Started, Added Long Ago", "Continuing", Ep(40, 1, Today.AddDays(-400))),
            Series(5, "Never Started, Added This Week", "Continuing", Ep(50, 1, Today.AddDays(-400))));
        SetUpWatched(10, 20, 30);
        SetUpLastWatched((1, 1), (2, 120), (3, 365));

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Watching.Select(i => i.TvdbId).Should().Equal([1, 5], "the Watching count covers active series only");
        _sut.Dormant.Select(i => i.TvdbId).Should().Equal([2, 3, 4], "most recently watched first, then the never-started one");
        _sut.Dormant[0].ProgressText.Should().Be("1 of 2 · S01", "a dormant card still says where the viewer stopped");
        _sut.IsEmpty.Should().BeFalse();
    }

    [Test]
    public async Task TheDormantGroup_Should_HaveItsOwnOneSectionView()
    {
        SetUpSeries(
            Series(1, "Watched Yesterday", "Continuing", Ep(10, 1, Today.AddDays(-400)), Ep(11, 2, Today.AddDays(-390))),
            Series(2, "Watched A Year Ago", "Continuing", Ep(20, 1, Today.AddDays(-400)), Ep(21, 2, Today.AddDays(-390))));
        SetUpWatched(10, 20);
        SetUpLastWatched((1, 1), (2, 365));
        _sut.Section = "dormant";

        await _sut.OnGetAsync(CancellationToken.None);

        var selected = _sut.SelectedSection!;
        selected.Section.Should().Be(LibrarySection.Dormant);
        selected.Title.Should().Be("Haven't watched in a while");
        selected.Items.Select(i => i.TvdbId).Should().Equal(2);
    }

    [Test]
    public async Task ALibraryOfOnlyDormantSeries_Should_NotBeEmpty()
    {
        SetUpSeries(Series(2, "Watched A Year Ago", "Continuing", Ep(20, 1, Today.AddDays(-400)), Ep(21, 2, Today.AddDays(-390))));
        SetUpWatched(20);
        SetUpLastWatched((2, 365));

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Watching.Should().BeEmpty();
        _sut.Dormant.Should().ContainSingle();
        _sut.IsEmpty.Should().BeFalse();
    }

    [Test]
    public async Task TheDormantGroup_Should_BeEmpty_WhenTheFeatureIsOff()
    {
        _libraryOptions.DormantAfterDays = 0;
        SetUpSeries(
            Series(1, "Watched Yesterday", "Continuing", Ep(10, 1, Today.AddDays(-400)), Ep(11, 2, Today.AddDays(-390))),
            Series(2, "Watched A Year Ago", "Continuing", Ep(20, 1, Today.AddDays(-400)), Ep(21, 2, Today.AddDays(-390))));
        SetUpWatched(10, 20);
        SetUpLastWatched((1, 1), (2, 365));

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Watching.Select(i => i.TvdbId).Should().Equal(1, 2);
        _sut.Dormant.Should().BeEmpty();
    }

    [Test]
    public async Task ASeasonPremiere_Should_BringADormantSeriesBackIntoWatching()
    {
        var newSeason = new EpisodeSummary { Id = 22, SeasonNumber = 2, EpisodeNumber = 1, Name = "S2E1", Aired = Today.AddDays(-5) };
        var newSpecial = new EpisodeSummary { Id = 32, SeasonNumber = 0, EpisodeNumber = 1, Name = "Special", Aired = Today.AddDays(-5) };
        SetUpSeries(
            Series(1, "Watched Yesterday", "Continuing", Ep(10, 1, Today.AddDays(-400)), Ep(11, 2, Today.AddDays(-390))),
            Series(2, "New Season This Week", "Continuing", Ep(20, 1, Today.AddDays(-400)), Ep(21, 2, Today.AddDays(-390)), newSeason),
            Series(3, "Only A New Special", "Continuing", Ep(30, 1, Today.AddDays(-400)), Ep(31, 2, Today.AddDays(-390)), newSpecial));
        SetUpWatched(10, 20, 30);
        SetUpLastWatched((1, 1), (2, 300), (3, 300));

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Watching.Select(i => i.TvdbId).Should().Equal([1, 2], "back in the main list, after the series with real activity");
        _sut.Dormant.Select(i => i.TvdbId).Should().Equal([3], "a special is not a season premiere");
    }

    [Test]
    public async Task OnGet_Should_KeepTheOtherSectionsInNameOrder_WhateverTheActivity()
    {
        SetUpSeries(
            Series(1, "zebra", "Continuing", Ep(10, 1, Today.AddDays(-5))),
            Series(2, "Alpha", "Continuing", Ep(20, 1, Today.AddDays(-5))),
            Series(3, "beta", "Continuing", Ep(30, 1, Today.AddDays(-5))),
            Series(4, "Zodiac", "Ended", Ep(40, 1, Today.AddDays(-500))),
            Series(5, "apex", "Ended", Ep(50, 1, Today.AddDays(-500))));
        SetUpWatched(10, 20, 30, 40, 50);
        _watches
            .Setup(x => x.GetLastWatchedUtcBySeriesAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, DateTime>
            {
                [1] = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc),
                [4] = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc)
            });

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.UpToDate.Select(i => i.Name).Should().Equal("Alpha", "beta", "zebra");
        _sut.Watched.Select(i => i.Name).Should().Equal("apex", "Zodiac");
    }

    [Test]
    public async Task Watching_Should_FallBackToNameOrder_WhenNothingDistinguishesTheSeries()
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

    // ---- stopped -----------------------------------------------------------------

    [Test]
    public async Task StoppedSeries_Should_BeInTheirOwnSection_AndInNoOther_WhateverTheirProgress()
    {
        _stoppedUtc[2] = new DateTime(2026, 9, 4, 18, 0, 0, DateTimeKind.Utc);
        _stoppedUtc[4] = new DateTime(2026, 9, 20, 18, 0, 0, DateTimeKind.Utc);
        _stoppedUtc[6] = new DateTime(2025, 12, 24, 18, 0, 0, DateTimeKind.Utc);
        _stoppedUtc[8] = new DateTime(2026, 9, 10, 18, 0, 0, DateTimeKind.Utc);
        SetUpSeries(
            Series(1, "Behind", "Continuing", Ep(10, 1, Today.AddDays(-20)), Ep(11, 2, Today.AddDays(-10))),
            Series(2, "Behind, Stopped", "Continuing", Ep(20, 1, Today.AddDays(-20)), Ep(21, 2, Today.AddDays(-10))),
            Series(3, "Caught Up", "Continuing", Ep(30, 1, Today.AddDays(-20))),
            Series(4, "Caught Up, Stopped", "Continuing", Ep(40, 1, Today.AddDays(-20))),
            Series(5, "Finished", "Ended", Ep(50, 1, Today.AddDays(-400))),
            Series(6, "Finished, Stopped", "Ended", Ep(60, 1, Today.AddDays(-400))),
            Series(8, "Never Started, Stopped", "Continuing", Ep(80, 1, Today.AddDays(-20))));
        SetUpWatched(10, 20, 30, 40, 50, 60);
        SetUpLastWatched((1, 1), (2, 1), (3, 1), (4, 1), (5, 1), (6, 1));

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Watching.Select(i => i.TvdbId).Should().Equal(1);
        _sut.Dormant.Should().BeEmpty();
        _sut.UpToDate.Select(i => i.TvdbId).Should().Equal(3);
        _sut.Watched.Select(i => i.TvdbId).Should().Equal(5);
        _sut.Stopped.Select(i => i.TvdbId).Should().Equal([4, 8, 2, 6], "most recently stopped first");
    }

    [Test]
    public async Task AStoppedCard_Should_SayWhenItWasStopped_AsItsMetaLine()
    {
        _stoppedUtc[1] = new DateTime(2026, 9, 4, 18, 0, 0, DateTimeKind.Utc);
        _stoppedUtc[2] = new DateTime(2025, 12, 24, 18, 0, 0, DateTimeKind.Utc);
        SetUpSeries(
            Series(1, "Stopped This Year", "Continuing", Ep(10, 1, Today.AddDays(-20)), Ep(11, 2, Today.AddDays(-10))),
            Series(2, "Stopped Last Year", "Continuing", Ep(20, 1, Today.AddDays(-400))));
        SetUpWatched(10);

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Stopped.Select(i => i.MetaLine).Should().Equal("Stopped Sep 4", "Stopped Dec 24, 2025");
        _sut.Stopped[0].WatchedEpisodes.Should().Be(1, "the card keeps its progress bar: the history is still there");
        _sut.Stopped[0].ReleasedEpisodes.Should().Be(2);

        new Recall.Web.Pages.Shared.SeriesCardModel { TvdbId = 1, Name = "Stopped This Year", MetaText = _sut.Stopped[0].MetaLine }
            .MetaLine.Should().Be("Stopped Sep 4", "the whole line, not \"Series · Stopped Sep 4\"");
    }

    [Test]
    public async Task Stopped_Should_BeatDormant_AndASeasonPremiere()
    {
        var premiere = new EpisodeSummary { Id = 22, SeasonNumber = 2, EpisodeNumber = 1, Name = "S2E1", Aired = Today.AddDays(-5) };
        _stoppedUtc[2] = DaysAgo(200);
        _stoppedUtc[3] = DaysAgo(200);
        SetUpSeries(
            Series(1, "Watched Yesterday", "Continuing", Ep(10, 1, Today.AddDays(-400)), Ep(11, 2, Today.AddDays(-390))),
            Series(2, "Stopped, New Season This Week", "Continuing", Ep(20, 1, Today.AddDays(-400)), Ep(21, 2, Today.AddDays(-390)), premiere),
            Series(3, "Stopped, Not Watched For A Year", "Continuing", Ep(30, 1, Today.AddDays(-400)), Ep(31, 2, Today.AddDays(-390))),
            Series(4, "Not Watched For A Year", "Continuing", Ep(40, 1, Today.AddDays(-400)), Ep(41, 2, Today.AddDays(-390))));
        SetUpWatched(10, 20, 30, 40);
        SetUpLastWatched((1, 1), (2, 300), (3, 300), (4, 300));

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Watching.Select(i => i.TvdbId).Should().Equal([1], "a premiere never brings a stopped series back");
        _sut.Dormant.Select(i => i.TvdbId).Should().Equal([4], "a stopped series is not \"haven't watched in a while\"");
        _sut.Stopped.Select(i => i.TvdbId).Should().BeEquivalentTo([2, 3]);
    }

    [Test]
    public async Task Section_Should_ShowTheStoppedSeriesOnTheirOwn()
    {
        _stoppedUtc[2] = new DateTime(2026, 9, 4, 18, 0, 0, DateTimeKind.Utc);
        SetUpSeries(
            Series(1, "Behind", "Continuing", Ep(10, 1, Today.AddDays(-20))),
            Series(2, "Put Aside", "Continuing", Ep(20, 1, Today.AddDays(-20))));
        _sut.Section = "stopped";

        await _sut.OnGetAsync(CancellationToken.None);

        var selected = _sut.SelectedSection!;
        selected.Section.Should().Be(LibrarySection.Stopped);
        selected.Title.Should().Be("Stopped");
        selected.Slug.Should().Be("stopped");
        selected.Items.Select(i => i.Name).Should().Equal("Put Aside");
        _sut.Sections[^1].Section.Should().Be(LibrarySection.Stopped, "it comes after Watched");
    }

    [Test]
    public async Task TheStoppedSection_Should_BeEmpty_WhenNothingIsStopped_AndALibraryOfOnlyStoppedSeriesIsNotEmpty()
    {
        SetUpSeries(Series(1, "Behind", "Continuing", Ep(10, 1, Today.AddDays(-20))));

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Stopped.Should().BeEmpty("the page leaves an empty section out");

        _stoppedUtc[1] = DaysAgo(3);
        SetUpSeries(Series(1, "Behind", "Continuing", Ep(10, 1, Today.AddDays(-20))));

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Watching.Should().BeEmpty();
        _sut.Stopped.Should().ContainSingle();
        _sut.IsEmpty.Should().BeFalse("the series is still in the library");
    }

    // ---- a series removed from the library keeps its watch history ---------------------

    [Test]
    public async Task WatchesOfASeriesNoLongerInTheLibrary_Should_LeaveNoGhostEntry()
    {
        // Series 9 was removed from the library; its watches (episodes 90, 91) and its activity remain.
        SetUpSeries(Series(1, "Behind", "Continuing", Ep(10, 1, Today.AddDays(-20)), Ep(11, 2, Today.AddDays(-10))));
        SetUpWatched(10, 90, 91);
        SetUpLastWatched((1, 2), (9, 1));

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Sections.SelectMany(s => s.Items).Select(i => i.TvdbId).Should().Equal([1], "only what is in the library is listed");
        _sut.Watching.Single().WatchedEpisodes.Should().Be(1, "another series' watches are not counted here");
        _tvDb.Verify(x => x.GetSeriesAggregateByIdAsync(9, It.IsAny<CancellationToken>()), Times.Never);
        _sut.ErrorToast().Should().BeNull();
    }
}
