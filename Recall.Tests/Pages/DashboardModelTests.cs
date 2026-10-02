using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Services;
using Recall.Web.Services.Digest;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Pages;

[TestFixture]
public class DashboardModelTests
{
    // The clock reads 2026-10-01 (UTC).
    private static readonly DateOnly Today = new(2026, 10, 1);
    private static readonly Guid UserId = Guid.NewGuid();

    private Mock<ITheTvDbService> _tvDb = null!;
    private Mock<ITrackedSeriesRepository> _library = null!;
    private Mock<IEpisodeWatchRepository> _watches = null!;
    private Mock<IWatchProgressService> _progress = null!;
    private DashboardModel _sut = null!;
    private LibraryOptions _libraryOptions = null!;
    private Mock<IAppUserRepository> _users = null!;
    private DigestOptions _digestOptions = null!;

    [SetUp]
    public void SetUp()
    {
        // A fresh instance per test: some tests change the settings.
        _libraryOptions = new LibraryOptions();
        _users = new Mock<IAppUserRepository>();
        _digestOptions = new DigestOptions { Enabled = true };

        _tvDb = new Mock<ITheTvDbService>();
        _library = new Mock<ITrackedSeriesRepository>();
        _watches = new Mock<IEpisodeWatchRepository>();

        // The real progress rules, with "today" pinned.
        _progress = new Mock<IWatchProgressService>();
        _progress
            .Setup(x => x.BuildProgress(It.IsAny<int>(), It.IsAny<IEnumerable<WatchableEpisode>>(), It.IsAny<IReadOnlySet<int>>()))
            .Returns((int id, IEnumerable<WatchableEpisode> episodes, IReadOnlySet<int> watched) =>
                WatchProgressCalculator.Build(id, episodes, watched, Today));

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.IsAuthenticated).Returns(true);
        currentUser.SetupGet(x => x.UserId).Returns(UserId);

        SetUpWatched();
        _watches
            .Setup(x => x.GetLastWatchedUtcBySeriesAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, DateTime>());

        _sut = new DashboardModel(
            _tvDb.Object,
            _library.Object,
            _watches.Object,
            _progress.Object,
            NullLogger<DashboardModel>.Instance,
            currentUser.Object,
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)),
            Options.Create(_libraryOptions),
            _users.Object,
            Options.Create(_digestOptions)).WithTempData();
    }

    private static EpisodeSummary Ep(int id, int season, int number, DateOnly? aired, string? image = null, string? finale = null) => new()
    {
        Id = id,
        SeasonNumber = season,
        EpisodeNumber = number,
        Name = $"S{season}E{number}",
        Aired = aired,
        Image = image,
        FinaleType = finale
    };

    /// <summary>When a series was added to the library, for tests about the order; set before <see cref="SetUpSeries"/>.</summary>
    private readonly Dictionary<int, DateTime> _addedUtc = [];

    private void SetUpSeries(params SeriesAggregate[] aggregates)
    {
        _library
            .Setup(x => x.GetByUserAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(aggregates.Select(a => new TrackedSeries
            {
                Id = Guid.NewGuid(), UserId = UserId, TvdbId = a.TvdbId, Name = a.Name,
                CreatedUtc = _addedUtc.GetValueOrDefault(a.TvdbId)
            }).ToList());

        foreach (var aggregate in aggregates)
        {
            _tvDb
                .Setup(x => x.GetSeriesAggregateByIdAsync(aggregate.TvdbId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(aggregate);
        }
    }

    private void SetUpWatched(params int[] episodeIds) =>
        _watches
            .Setup(x => x.GetWatchedEpisodeIdsAsync(UserId, It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(episodeIds.ToHashSet());

    private void VerifyNoEpisodeLookups() =>
        _tvDb.Verify(x => x.GetEpisodeDetailsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

    // ---- catch-up images -----------------------------------------------------

    [Test]
    public async Task CatchUp_Should_UseTheAggregatesStill_WithoutLookingTheEpisodeUp()
    {
        SetUpSeries(new SeriesAggregate
        {
            TvdbId = 1, Name = "Show", ImageUrl = "https://img/poster.jpg",
            Episodes = [Ep(11, 1, 1, Today.AddDays(-7), image: "https://img/still-11.jpg")]
        });

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.CatchUpEpisodes.Should().ContainSingle().Which.ImageUrl.Should().Be("https://img/still-11.jpg");
        VerifyNoEpisodeLookups();
    }

    [Test]
    public async Task CatchUp_Should_LookUpOnlyTheEpisodes_WhoseStillTheAggregateLacks()
    {
        SetUpSeries(
            new SeriesAggregate
            {
                TvdbId = 1, Name = "Has Still", ImageUrl = "https://img/poster-1.jpg",
                Episodes = [Ep(11, 1, 1, Today.AddDays(-7), image: "https://img/still-11.jpg")]
            },
            new SeriesAggregate
            {
                TvdbId = 2, Name = "No Still", ImageUrl = "https://img/poster-2.jpg",
                Episodes = [Ep(21, 1, 1, Today.AddDays(-7))]
            },
            new SeriesAggregate
            {
                TvdbId = 3, Name = "Blank Still", ImageUrl = "https://img/poster-3.jpg",
                Episodes = [Ep(31, 1, 1, Today.AddDays(-7), image: " ")]
            });
        _tvDb
            .Setup(x => x.GetEpisodeDetailsAsync(21, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Episode { Id = 21, Image = "https://img/screencap-21.jpg" });
        _tvDb
            .Setup(x => x.GetEpisodeDetailsAsync(31, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Episode { Id = 31, Image = null });

        await _sut.OnGetAsync(CancellationToken.None);

        var bySeries = _sut.CatchUpEpisodes.ToDictionary(c => c.SeriesId, c => c.ImageUrl);
        bySeries[1].Should().Be("https://img/still-11.jpg");
        bySeries[2].Should().Be("https://img/screencap-21.jpg", "the episode's own record had the still the aggregate lacked");
        bySeries[3].Should().BeNull("neither source has a still and the series has no background art: a dark placeholder, never the cropped poster");

        _tvDb.Verify(x => x.GetEpisodeDetailsAsync(11, It.IsAny<CancellationToken>()), Times.Never);
        _tvDb.Verify(x => x.GetEpisodeDetailsAsync(21, It.IsAny<CancellationToken>()), Times.Once);
        _tvDb.Verify(x => x.GetEpisodeDetailsAsync(31, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task CatchUp_Should_UseTheSeriesBackgroundArt_WhenThereIsNoStill()
    {
        SetUpSeries(new SeriesAggregate
        {
            TvdbId = 2, Name = "No Still", ImageUrl = "https://img/poster-2.jpg", BackgroundUrl = "https://img/fanart-2.jpg",
            Episodes = [Ep(21, 1, 1, Today.AddDays(-7))]
        });
        _tvDb
            .Setup(x => x.GetEpisodeDetailsAsync(21, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Episode { Id = 21, Image = null });

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.CatchUpEpisodes.Should().ContainSingle().Which.ImageUrl.Should().Be("https://img/fanart-2.jpg");
    }

    [Test]
    public async Task CatchUp_Should_PreferTheEpisodesOwnStill_OverTheBackgroundArt()
    {
        SetUpSeries(new SeriesAggregate
        {
            TvdbId = 2, Name = "No Still", BackgroundUrl = "https://img/fanart-2.jpg",
            Episodes = [Ep(21, 1, 1, Today.AddDays(-7))]
        });
        _tvDb
            .Setup(x => x.GetEpisodeDetailsAsync(21, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Episode { Id = 21, Image = "https://img/screencap-21.jpg" });

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.CatchUpEpisodes.Should().ContainSingle().Which.ImageUrl.Should().Be("https://img/screencap-21.jpg");
    }

    [Test]
    public async Task CatchUp_Should_KeepTheBackgroundArt_WhenTheEpisodeLookupFails()
    {
        SetUpSeries(new SeriesAggregate
        {
            TvdbId = 2, Name = "No Still", ImageUrl = "https://img/poster-2.jpg", BackgroundUrl = "https://img/fanart-2.jpg",
            Episodes = [Ep(21, 1, 1, Today.AddDays(-7))]
        });
        _tvDb
            .Setup(x => x.GetEpisodeDetailsAsync(21, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("TheTVDB is down"));

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.CatchUpEpisodes.Should().ContainSingle().Which.ImageUrl.Should().Be("https://img/fanart-2.jpg");
    }

    [Test]
    public async Task Specials_Should_NeverBeACatchUpCard_OrCountAsUnwatched()
    {
        SetUpSeries(new SeriesAggregate
        {
            TvdbId = 1, Name = "Finished",
            Episodes = [Ep(5, 0, 1, Today.AddDays(-40)), Ep(10, 1, 1, Today.AddDays(-30)), Ep(11, 1, 2, Today.AddDays(6))]
        });
        SetUpWatched(10);

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.CatchUpEpisodes.Should().BeEmpty("the only unwatched aired episode is a special");
        _sut.UnwatchedCount.Should().Be(0);
        _sut.UpcomingEpisodes.Should().ContainSingle().Which.SeriesCaughtUp.Should().BeTrue();
    }

    // ---- what the page shows ---------------------------------------------------

    [Test]
    public async Task OnGet_Should_ShowAnEmptyDashboard_AndCallNothing_WhenNoSeriesAreTracked()
    {
        _library
            .Setup(x => x.GetByUserAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.TrackedSeriesCount.Should().Be(0);
        _sut.UpcomingEpisodes.Should().BeEmpty();
        _sut.CatchUpEpisodes.Should().BeEmpty();
        _sut.UnwatchedCount.Should().Be(0);
        _tvDb.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Upcoming_Should_ListTheNextThirtyDays_SoonestFirst_AndCountThisWeek()
    {
        SetUpSeries(
            new SeriesAggregate
            {
                TvdbId = 1, Name = "Alpha", ImageUrl = "https://img/a.jpg",
                Episodes =
                [
                    Ep(10, 1, 1, Today.AddDays(-1)),                       // already aired
                    Ep(11, 1, 2, Today.AddDays(20)),
                    Ep(12, 1, 3, Today.AddDays(31)),                       // beyond the window
                    Ep(13, 1, 4, aired: null)                              // no date
                ]
            },
            new SeriesAggregate
            {
                TvdbId = 2, Name = "Beta", ImageUrl = "https://img/b.jpg",
                Episodes = [Ep(20, 2, 5, Today, finale: "season"), Ep(21, 2, 6, Today.AddDays(7)), Ep(22, 2, 7, Today.AddDays(30))]
            });

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.UpcomingEpisodes.Select(e => e.EpisodeId).Should().Equal(20, 21, 11, 22);
        _sut.UpcomingThisWeekCount.Should().Be(2, "today and seven days out are inside the week; twenty days is not");

        var first = _sut.UpcomingEpisodes[0];
        first.SeriesName.Should().Be("Beta");
        first.ImageUrl.Should().Be("https://img/b.jpg");
        first.FinaleType.Should().Be("season");
        first.AiredDate.Should().Be(Today);
    }

    [Test]
    public async Task Counts_Should_AddUpUnwatchedAiredEpisodes_AcrossSeries()
    {
        SetUpSeries(
            new SeriesAggregate
            {
                TvdbId = 1, Name = "Alpha",
                Episodes = [Ep(10, 1, 1, Today.AddDays(-30)), Ep(11, 1, 2, Today.AddDays(-20)), Ep(12, 1, 3, Today.AddDays(5))]
            },
            new SeriesAggregate
            {
                TvdbId = 2, Name = "Beta",
                Episodes = [Ep(20, 1, 1, Today.AddDays(-10)), Ep(21, 1, 2, Today.AddDays(-3))]
            });
        SetUpWatched(10, 20, 21);

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.TrackedSeriesCount.Should().Be(2);
        _sut.UnwatchedCount.Should().Be(1, "only Alpha S1E2 has aired and is unwatched; the future episode doesn't count");
        _sut.CatchUpEpisodes.Should().ContainSingle().Which.EpisodeId.Should().Be(11);
        _sut.UpcomingEpisodes.Should().ContainSingle().Which.SeriesCaughtUp.Should().BeFalse();
    }

    [Test]
    public async Task ContinueWatching_Should_UseTheSharedOrder_RecentActivityFirst_ThenNewestAdded()
    {
        _addedUtc[1] = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        _addedUtc[4] = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
        _addedUtc[5] = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        SetUpSeries(
            new SeriesAggregate { TvdbId = 1, Name = "Alpha (never watched, added in August)", Episodes = [Ep(10, 1, 1, Today.AddDays(-9))] },
            new SeriesAggregate { TvdbId = 2, Name = "Watched Last Week", Episodes = [Ep(20, 1, 1, Today.AddDays(-9))] },
            new SeriesAggregate { TvdbId = 3, Name = "Watched Yesterday", Episodes = [Ep(30, 1, 1, Today.AddDays(-9))] },
            new SeriesAggregate { TvdbId = 4, Name = "Zulu (never watched, added this week)", Episodes = [Ep(40, 1, 1, Today.AddDays(-9))] },
            new SeriesAggregate { TvdbId = 5, Name = "Beta (never watched, added in August)", Episodes = [Ep(50, 1, 1, Today.AddDays(-9))] });
        _watches
            .Setup(x => x.GetLastWatchedUtcBySeriesAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, DateTime>
            {
                [2] = new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc),
                [3] = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc)
            });

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.CatchUpEpisodes.Select(c => c.SeriesId).Should().Equal(
            [3, 2, 4, 1, 5],
            "watched most recently first; then the never-started ones, newest added first, and by name when added together");
        _watches.Verify(x => x.GetLastWatchedUtcBySeriesAsync(UserId, It.IsAny<CancellationToken>()), Times.Once,
            "one grouped query for the whole page, not one per series");
    }

    [Test]
    public async Task ContinueWatching_Should_CountAWatchedSpecialAsActivity_ButNotOfferItAsTheNextEpisode()
    {
        SetUpSeries(
            new SeriesAggregate
            {
                TvdbId = 1, Name = "Special Watched Yesterday",
                Episodes = [Ep(5, 0, 1, Today.AddDays(-40)), Ep(10, 1, 1, Today.AddDays(-30))]
            },
            new SeriesAggregate { TvdbId = 2, Name = "Episode Watched Last Week", Episodes = [Ep(20, 1, 1, Today.AddDays(-30)), Ep(21, 1, 2, Today.AddDays(-20))] });
        SetUpWatched(5, 20);
        _watches
            .Setup(x => x.GetLastWatchedUtcBySeriesAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, DateTime>
            {
                [1] = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc),
                [2] = new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc)
            });

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.CatchUpEpisodes.Select(c => (c.SeriesId, c.EpisodeId)).Should().Equal(
            [(1, 10), (2, 21)],
            "the special is activity for the order, and S01E01 is still the episode to watch next");
    }

    // ---- haven't watched in a while -------------------------------------------

    private static DateTime DaysAgo(int days) => Today.AddDays(-days).ToDateTime(new TimeOnly(20, 0), DateTimeKind.Utc);

    private void SetUpLastWatched(params (int SeriesId, int DaysAgo)[] activity) =>
        _watches
            .Setup(x => x.GetLastWatchedUtcBySeriesAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(activity.ToDictionary(a => a.SeriesId, a => DaysAgo(a.DaysAgo)));

    [Test]
    public async Task DormantSeries_Should_GetNoCard_ButBeCountedForTheLink_AndStillCountAsUnwatched()
    {
        _addedUtc[4] = DaysAgo(200);
        SetUpSeries(
            new SeriesAggregate { TvdbId = 1, Name = "Watched Yesterday", Episodes = [Ep(10, 1, 1, Today.AddDays(-400)), Ep(11, 1, 2, Today.AddDays(-390))] },
            new SeriesAggregate { TvdbId = 2, Name = "Watched Four Months Ago", Episodes = [Ep(20, 1, 1, Today.AddDays(-400)), Ep(21, 1, 2, Today.AddDays(-390)), Ep(22, 1, 3, Today.AddDays(-380))] },
            new SeriesAggregate { TvdbId = 3, Name = "Watched A Year Ago", Episodes = [Ep(30, 1, 1, Today.AddDays(-400)), Ep(31, 1, 2, Today.AddDays(-390))] },
            new SeriesAggregate { TvdbId = 4, Name = "Never Started, Added Long Ago", Episodes = [Ep(40, 1, 1, Today.AddDays(-400))] });
        SetUpWatched(10, 20, 30);
        SetUpLastWatched((1, 1), (2, 120), (3, 365));

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.CatchUpEpisodes.Select(c => c.SeriesId).Should().Equal(1);
        _sut.DormantSeriesCount.Should().Be(3, "the link reads \"3 series you haven't watched in a while\"");
        _sut.UnwatchedCount.Should().Be(5, "the stat keeps counting every series: 1 + 2 + 1 + 1 unwatched aired episodes");
    }

    [Test]
    public async Task TheDormantLink_Should_HaveNothingToCount_WhenEverythingIsRecent_OrTheFeatureIsOff()
    {
        SetUpSeries(
            new SeriesAggregate { TvdbId = 1, Name = "Watched Yesterday", Episodes = [Ep(10, 1, 1, Today.AddDays(-400)), Ep(11, 1, 2, Today.AddDays(-390))] },
            new SeriesAggregate { TvdbId = 2, Name = "Watched A Year Ago", Episodes = [Ep(20, 1, 1, Today.AddDays(-400)), Ep(21, 1, 2, Today.AddDays(-390))] });
        SetUpWatched(10, 20);

        SetUpLastWatched((1, 1), (2, 30));
        await _sut.OnGetAsync(CancellationToken.None);
        _sut.DormantSeriesCount.Should().Be(0, "nothing is older than ninety days, so the page shows no link");

        SetUpLastWatched((1, 1), (2, 365));
        _libraryOptions.DormantAfterDays = 0;
        await _sut.OnGetAsync(CancellationToken.None);
        _sut.DormantSeriesCount.Should().Be(0, "at 0 the feature is off");
        _sut.CatchUpEpisodes.Select(c => c.SeriesId).Should().Equal(1, 2);
    }

    [Test]
    public async Task ASeasonPremiere_Should_BringADormantSeriesBack_ButAnOrdinaryEpisodeOrASpecialShouldNot()
    {
        SetUpSeries(
            new SeriesAggregate { TvdbId = 1, Name = "Watched Yesterday", Episodes = [Ep(10, 1, 1, Today.AddDays(-400)), Ep(11, 1, 2, Today.AddDays(-390))] },
            new SeriesAggregate
            {
                TvdbId = 2, Name = "New Season Last Week",
                Episodes = [Ep(20, 1, 1, Today.AddDays(-400)), Ep(21, 1, 2, Today.AddDays(-390)), Ep(22, 2, 1, Today.AddDays(-6))]
            },
            new SeriesAggregate
            {
                TvdbId = 3, Name = "Weekly Show, Another Episode Last Week",
                Episodes = [Ep(30, 1, 1, Today.AddDays(-400)), Ep(31, 1, 2, Today.AddDays(-390)), Ep(32, 1, 40, Today.AddDays(-6))]
            },
            new SeriesAggregate
            {
                TvdbId = 4, Name = "New Special Last Week",
                Episodes = [Ep(40, 1, 1, Today.AddDays(-400)), Ep(41, 1, 2, Today.AddDays(-390)), Ep(42, 0, 1, Today.AddDays(-6))]
            });
        SetUpWatched(10, 20, 30, 40);
        SetUpLastWatched((1, 1), (2, 300), (3, 300), (4, 300));

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.CatchUpEpisodes.Select(c => c.SeriesId).Should().Equal([1, 2], "the premiere brings series 2 back, after the series with real activity");
        _sut.DormantSeriesCount.Should().Be(2);

        _libraryOptions.PremiereReturnDays = 3;
        await _sut.OnGetAsync(CancellationToken.None);
        _sut.CatchUpEpisodes.Select(c => c.SeriesId).Should().Equal([1], "the window comes from configuration: six days ago is outside three");
    }

    [Test]
    public async Task OnGet_Should_StillRender_WhenOneSeriesCannotBeLoaded()
    {
        SetUpSeries(
            new SeriesAggregate { TvdbId = 1, Name = "Fine", Episodes = [Ep(10, 1, 1, Today.AddDays(-9))] },
            new SeriesAggregate { TvdbId = 2, Name = "Broken" });
        _tvDb
            .Setup(x => x.GetSeriesAggregateByIdAsync(2, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.TrackedSeriesCount.Should().Be(2, "the count is of what the user tracks, not of what loaded");
        _sut.CatchUpEpisodes.Should().ContainSingle().Which.SeriesName.Should().Be("Fine");
    }

    // ---- the one-time offer of the weekly email --------------------------------

    private void UserIs(DateTime? optedIn = null, DateTime? dismissed = null) =>
        _users.Setup(x => x.GetByIdAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppUserEntity { Id = UserId, DigestOptedInUtc = optedIn, DigestPromptDismissedUtc = dismissed });

    private void SetUpAnyLibrary() =>
        SetUpSeries(new SeriesAggregate { TvdbId = 1, Name = "Show", Episodes = [Ep(10, 1, 1, Today.AddDays(-9))] });

    [Test]
    public async Task TheDigestOffer_Should_BeShown_ToSomeoneWhoHasNotAnsweredIt()
    {
        SetUpAnyLibrary();
        UserIs();

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.ShowDigestPrompt.Should().BeTrue();
    }

    [Test]
    public async Task TheDigestOffer_Should_BeHidden_OnceEitherAnswerIsGiven()
    {
        SetUpAnyLibrary();

        UserIs(optedIn: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        await _sut.OnGetAsync(CancellationToken.None);
        _sut.ShowDigestPrompt.Should().BeFalse("it is already on");

        UserIs(dismissed: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        await _sut.OnGetAsync(CancellationToken.None);
        _sut.ShowDigestPrompt.Should().BeFalse("\"No thanks\" is remembered");
    }

    [Test]
    public async Task TheDigestOffer_Should_BeHidden_WhereTheDigestIsNotEnabled_OrTheLibraryIsEmpty()
    {
        UserIs();

        _library.Setup(x => x.GetByUserAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        await _sut.OnGetAsync(CancellationToken.None);
        _sut.ShowDigestPrompt.Should().BeFalse("with nothing tracked there is nothing a digest could say");

        SetUpAnyLibrary();
        _digestOptions.Enabled = false;
        await _sut.OnGetAsync(CancellationToken.None);
        _sut.ShowDigestPrompt.Should().BeFalse();
        _users.Verify(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never, "no lookup for a feature that is off");
    }

    [Test]
    public async Task TheDigestOffer_Should_JustNotShow_WhenThePreferenceCannotBeRead()
    {
        SetUpAnyLibrary();
        _users.Setup(x => x.GetByIdAsync(UserId, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.ShowDigestPrompt.Should().BeFalse();
        _sut.CatchUpEpisodes.Should().ContainSingle("the rest of the Dashboard still renders");
    }

    [Test]
    public async Task TurnItOn_Should_OptIn_AndConfirm()
    {
        var result = await _sut.OnPostDigestOptInAsync(CancellationToken.None);

        result.Should().BeOfType<RedirectToPageResult>();
        _users.Verify(x => x.SetDigestOptInAsync(UserId, true, It.IsAny<CancellationToken>()), Times.Once);
        _sut.SuccessToast().Should().StartWith("Weekly email is on.");
    }

    [Test]
    public async Task NoThanks_Should_RecordTheDismissal_AndNotOptIn()
    {
        var result = await _sut.OnPostDigestDismissAsync(CancellationToken.None);

        result.Should().BeOfType<RedirectToPageResult>();
        _users.Verify(x => x.DismissDigestPromptAsync(UserId, It.IsAny<CancellationToken>()), Times.Once);
        _users.Verify(x => x.SetDigestOptInAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _sut.SuccessToast().Should().BeNull();
    }

    [Test]
    public void BothAnswers_Should_BePostHandlers()
    {
        typeof(DashboardModel).GetMethods().Select(m => m.Name)
            .Should().Contain(["OnPostDigestOptInAsync", "OnPostDigestDismissAsync"])
            .And.NotContain(n => n.StartsWith("OnGetDigest", StringComparison.Ordinal), "answering the offer changes state");
    }

    // ---- POST: mark watched ----------------------------------------------------

    private static readonly DateTime BatchStamp = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task MarkWatched_Should_GoThroughTheProgressService_AndOfferAnUndo()
    {
        SetUpSeries(new SeriesAggregate { TvdbId = 1, Name = "Show", Episodes = [Ep(11, 2, 6, Today.AddDays(-7))] });
        _progress
            .Setup(x => x.MarkEpisodeWatchedUndoablyAsync(UserId, 1, 11, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UndoableEpisodeWatch(EpisodeWatchOutcome.MarkedWatched, new WatchedBatch(1, BatchStamp)));

        var result = await _sut.OnPostMarkWatchedAsync(seriesId: 1, episodeId: 11, CancellationToken.None);

        result.Should().BeOfType<RedirectToPageResult>();
        _sut.ErrorToast().Should().BeNull();
        _sut.SuccessToast().Should().Be("Marked Show S02E06 as watched.");
        _sut.TempData[PageModelToastExtensions.UndoWatchedSeriesKey].Should().Be("1",
            "one tap with no confirmation, and the card is gone afterwards: the toast is the only way back");
        _sut.TempData[PageModelToastExtensions.UndoWatchedStampKey].Should().Be(BatchStamp.Ticks.ToString());
        _watches.Verify(
            x => x.MarkWatchedAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never, "the page must not write a watch itself — the service validates the pair first");
    }

    [Test]
    public async Task MarkWatched_Should_SayNothing_WhenTheEpisodeWasAlreadyWatched()
    {
        _progress
            .Setup(x => x.MarkEpisodeWatchedUndoablyAsync(UserId, 1, 11, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UndoableEpisodeWatch(EpisodeWatchOutcome.MarkedWatched, WatchedBatch.Empty));

        await _sut.OnPostMarkWatchedAsync(1, 11, CancellationToken.None);

        _sut.SuccessToast().Should().BeNull("a double tap wrote nothing, so there is nothing to undo");
        _sut.TempData.ContainsKey(PageModelToastExtensions.UndoWatchedStampKey).Should().BeFalse();
    }

    [Test]
    public async Task MarkWatched_Should_StillConfirm_WhenTheSeriesCannotBeRead()
    {
        _progress
            .Setup(x => x.MarkEpisodeWatchedUndoablyAsync(UserId, 1, 11, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UndoableEpisodeWatch(EpisodeWatchOutcome.MarkedWatched, new WatchedBatch(1, BatchStamp)));

        await _sut.OnPostMarkWatchedAsync(1, 11, CancellationToken.None);

        _sut.SuccessToast().Should().Be("Marked as watched.");
        _sut.TempData.ContainsKey(PageModelToastExtensions.UndoWatchedStampKey).Should().BeTrue();
    }

    [TestCase(EpisodeWatchOutcome.EpisodeNotInSeries, "That episode doesn't belong to this series.")]
    [TestCase(EpisodeWatchOutcome.NotAired, "You can't mark an episode as watched before it has aired.")]
    public async Task MarkWatched_Should_ExplainARefusal(EpisodeWatchOutcome outcome, string expectedToast)
    {
        _progress
            .Setup(x => x.MarkEpisodeWatchedUndoablyAsync(UserId, 1, 11, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UndoableEpisodeWatch(outcome));

        var result = await _sut.OnPostMarkWatchedAsync(1, 11, CancellationToken.None);

        result.Should().BeOfType<RedirectToPageResult>();
        _sut.ErrorToast().Should().Be(expectedToast);
    }

    [Test]
    public async Task MarkWatched_Should_ShowAnErrorToast_WhenTheServiceThrows()
    {
        _progress
            .Setup(x => x.MarkEpisodeWatchedUndoablyAsync(UserId, 1, 11, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var result = await _sut.OnPostMarkWatchedAsync(1, 11, CancellationToken.None);

        result.Should().BeOfType<RedirectToPageResult>();
        _sut.ErrorToast().Should().Be("Could not update watched status right now.");
    }
}
