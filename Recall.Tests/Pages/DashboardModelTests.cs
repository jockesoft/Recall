using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages;
using Recall.Web.Services;
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

    [SetUp]
    public void SetUp()
    {
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
            .Setup(x => x.GetLastWatchedUtcBySeriesAsync(UserId, It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, DateTime>());

        _sut = new DashboardModel(
            _tvDb.Object,
            _library.Object,
            _watches.Object,
            _progress.Object,
            NullLogger<DashboardModel>.Instance,
            currentUser.Object,
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero))).WithTempData();
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

    private void SetUpSeries(params SeriesAggregate[] aggregates)
    {
        _library
            .Setup(x => x.GetByUserAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(aggregates.Select(a => new TrackedSeries { Id = Guid.NewGuid(), UserId = UserId, TvdbId = a.TvdbId, Name = a.Name }).ToList());

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
        bySeries[3].Should().Be("https://img/poster-3.jpg", "neither source has a still, so the series cover stands in");

        _tvDb.Verify(x => x.GetEpisodeDetailsAsync(11, It.IsAny<CancellationToken>()), Times.Never);
        _tvDb.Verify(x => x.GetEpisodeDetailsAsync(21, It.IsAny<CancellationToken>()), Times.Once);
        _tvDb.Verify(x => x.GetEpisodeDetailsAsync(31, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task CatchUp_Should_KeepTheSeriesCover_WhenTheEpisodeLookupFails()
    {
        SetUpSeries(new SeriesAggregate
        {
            TvdbId = 2, Name = "No Still", ImageUrl = "https://img/poster-2.jpg",
            Episodes = [Ep(21, 1, 1, Today.AddDays(-7))]
        });
        _tvDb
            .Setup(x => x.GetEpisodeDetailsAsync(21, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("TheTVDB is down"));

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.CatchUpEpisodes.Should().ContainSingle().Which.ImageUrl.Should().Be("https://img/poster-2.jpg");
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
    public async Task CatchUp_Should_PutTheMostRecentlyWatchedSeriesFirst_ThenTheRestByName()
    {
        SetUpSeries(
            new SeriesAggregate { TvdbId = 1, Name = "Zulu (never watched)", Episodes = [Ep(10, 1, 1, Today.AddDays(-9))] },
            new SeriesAggregate { TvdbId = 2, Name = "Watched Last Week", Episodes = [Ep(20, 1, 1, Today.AddDays(-9))] },
            new SeriesAggregate { TvdbId = 3, Name = "Watched Yesterday", Episodes = [Ep(30, 1, 1, Today.AddDays(-9))] },
            new SeriesAggregate { TvdbId = 4, Name = "Alpha (never watched)", Episodes = [Ep(40, 1, 1, Today.AddDays(-9))] });
        _watches
            .Setup(x => x.GetLastWatchedUtcBySeriesAsync(UserId, It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, DateTime>
            {
                [2] = new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc),
                [3] = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc)
            });

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.CatchUpEpisodes.Select(c => c.SeriesId).Should().Equal(3, 2, 4, 1);
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

    // ---- POST: mark watched ----------------------------------------------------

    [Test]
    public async Task MarkWatched_Should_GoThroughTheProgressService_AndRedirectWithoutAToast()
    {
        _progress
            .Setup(x => x.MarkEpisodeWatchedAsync(UserId, 1, 11, It.IsAny<CancellationToken>()))
            .ReturnsAsync(EpisodeWatchOutcome.MarkedWatched);

        var result = await _sut.OnPostMarkWatchedAsync(seriesId: 1, episodeId: 11, CancellationToken.None);

        result.Should().BeOfType<RedirectToPageResult>();
        _progress.Verify(x => x.MarkEpisodeWatchedAsync(UserId, 1, 11, It.IsAny<CancellationToken>()), Times.Once);
        _sut.ErrorToast().Should().BeNull();
        _watches.Verify(
            x => x.MarkWatchedAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never, "the page must not write a watch itself — the service validates the pair first");
    }

    [TestCase(EpisodeWatchOutcome.EpisodeNotInSeries, "That episode doesn't belong to this series.")]
    [TestCase(EpisodeWatchOutcome.NotAired, "You can't mark an episode as watched before it has aired.")]
    public async Task MarkWatched_Should_ExplainARefusal(EpisodeWatchOutcome outcome, string expectedToast)
    {
        _progress
            .Setup(x => x.MarkEpisodeWatchedAsync(UserId, 1, 11, It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        var result = await _sut.OnPostMarkWatchedAsync(1, 11, CancellationToken.None);

        result.Should().BeOfType<RedirectToPageResult>();
        _sut.ErrorToast().Should().Be(expectedToast);
    }

    [Test]
    public async Task MarkWatched_Should_ShowAnErrorToast_WhenTheServiceThrows()
    {
        _progress
            .Setup(x => x.MarkEpisodeWatchedAsync(UserId, 1, 11, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var result = await _sut.OnPostMarkWatchedAsync(1, 11, CancellationToken.None);

        result.Should().BeOfType<RedirectToPageResult>();
        _sut.ErrorToast().Should().Be("Could not update watched status right now.");
    }
}
