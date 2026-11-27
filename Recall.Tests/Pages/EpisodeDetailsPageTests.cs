using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.External.Omdb;
using Recall.Web.Infrastructure.Persistence.OmdbCache;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages.Episodes;
using Recall.Web.Pages.Shared;
using Recall.Web.Services;
using Recall.Web.Services.External.Omdb;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Pages;

/// <summary>
/// What Episode Details decides on load: the hero (its meta line, the series
/// above the title), the action row in each state, the place in the season,
/// the Up next card, the guest stars, and the previous and next episodes.
/// </summary>
/// <remarks>
/// The series: season 52 with three episodes (two aired, one in December),
/// then two specials, which come last in watch order. "Now" is Friday
/// 2026-11-27, noon UTC.
/// </remarks>
[TestFixture]
public sealed class EpisodeDetailsPageTests
{
    private const int SeriesId = 76177;
    private const int E1 = 5201, E2 = 5202, E3 = 5203, Special1 = 9001, Special2 = 9002;
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    private Mock<ITheTvDbService> _tvDb = null!;
    private Mock<ICurrentUserService> _currentUser = null!;
    private Mock<IEpisodeWatchRepository> _watches = null!;
    private Mock<IWatchProgressService> _progress = null!;
    private Mock<IRatingRepository> _ratings = null!;
    private HashSet<int> _watched = null!;
    private DetailsModel _sut = null!;

    private static readonly EpisodeSummary[] Episodes =
    [
        new() { Id = E1, SeasonNumber = 52, EpisodeNumber = 1, Name = "Ryan Gosling", Aired = new DateOnly(2026, 9, 27), RuntimeMinutes = 68, Image = "https://art.test/e1.jpg" },
        new() { Id = E2, SeasonNumber = 52, EpisodeNumber = 2, Name = "Sabrina Carpenter", Aired = new DateOnly(2026, 10, 4), RuntimeMinutes = 68 },
        new() { Id = E3, SeasonNumber = 52, EpisodeNumber = 3, Name = "TBA", Aired = new DateOnly(2026, 12, 5) },
        new() { Id = Special1, SeasonNumber = 0, EpisodeNumber = 1, Name = "Christmas Special", Aired = new DateOnly(2025, 12, 20), Image = "https://art.test/s1.jpg" },
        new() { Id = Special2, SeasonNumber = 0, EpisodeNumber = 2, Name = "Best Of", Aired = new DateOnly(2026, 2, 1) },
    ];

    [SetUp]
    public void SetUp()
    {
        _tvDb = new Mock<ITheTvDbService>();
        _currentUser = new Mock<ICurrentUserService>();
        _watches = new Mock<IEpisodeWatchRepository>();
        _progress = new Mock<IWatchProgressService>();
        _ratings = new Mock<IRatingRepository>();
        _ratings.Setup(x => x.GetSummaryAsync(It.IsAny<Recall.Web.Infrastructure.Persistence.Entities.RatingTargetType>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RatingSummary.Empty);
        _watched = [];

        _tvDb.Setup(x => x.GetSeriesAggregateByIdAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesAggregate
            {
                TvdbId = SeriesId, Name = "Saturday Night Live", Slug = "saturday-night-live", AirsTime = "23:30", OriginalCountry = "usa",
                BackgroundUrl = "https://art.test/background.jpg", Episodes = Episodes
            });
        foreach (var summary in Episodes)
        {
            var record = new Episode
            {
                Id = summary.Id, SeriesId = SeriesId, SeasonNumber = summary.SeasonNumber, Number = summary.EpisodeNumber,
                Name = summary.Name, Aired = summary.Aired?.ToString("yyyy-MM-dd"), Runtime = summary.RuntimeMinutes
            };
            _tvDb.Setup(x => x.GetEpisodeDetailsAsync(summary.Id, It.IsAny<CancellationToken>())).ReturnsAsync(record);
        }

        _watches.Setup(x => x.GetWatchedEpisodeIdsAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _watched);

        _sut = new DetailsModel(
            NullLogger<DetailsModel>.Instance,
            _tvDb.Object,
            _currentUser.Object,
            _watches.Object,
            _progress.Object,
            Mock.Of<ILikeRepository>(),
            _ratings.Object,
            Mock.Of<IOmdbApiClient>(),
            Mock.Of<IEpisodeOmdbSnapshotStore>(),
            Mock.Of<IOmdbRequestBudget>(),
            Options.Create(new OmdbOptions()),
            new FixedTimeProvider(Now)).WithTempData().WithHttpContext();
    }

    private void SignIn()
    {
        _currentUser.SetupGet(x => x.IsAuthenticated).Returns(true);
        _currentUser.SetupGet(x => x.UserId).Returns(UserId);
    }

    private void Watched(int episodeId, DateTime? on = null)
    {
        _watched.Add(episodeId);
        _watches.Setup(x => x.GetWatchedUtcAsync(UserId, episodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(on ?? new DateTime(2026, 9, 28, 1, 0, 0, DateTimeKind.Utc));
    }

    private void PriorUnwatched(int episodeId, int count) =>
        _progress.Setup(x => x.GetPriorUnwatchedCountAsync(UserId, SeriesId, episodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(count);

    private async Task<TitleHeroModel> HeroOf(int episodeId)
    {
        var result = await _sut.OnGetAsync(episodeId, CancellationToken.None);
        result.Should().BeOfType<Microsoft.AspNetCore.Mvc.RazorPages.PageResult>();
        return _sut.BuildHero("/Episodes/Details/" + episodeId);
    }

    // ---- the hero ----

    [Test]
    public async Task TheHero_Should_HaveTheSeriesAboveTheTitle_TheStillBehindIt_AndNoPoster()
    {
        var hero = await HeroOf(E1);

        hero.Name.Should().Be("Ryan Gosling");
        hero.ShowPoster.Should().BeFalse();
        hero.BackgroundUrl.Should().Be("https://art.test/e1.jpg", "the episode's still, by the shared rule");
        hero.Back.Should().Be(new TitleBackLink("Saturday Night Live", "/Series/Details", UseHistory: false, RouteId: SeriesId));
        hero.Parent.Should().Be(new TitleParentLink("Saturday Night Live", "/Series/Details", SeriesId));
    }

    [Test]
    public async Task TheHero_Should_FallBackToTheSeriesBackgroundArt_WithoutAStill()
    {
        var hero = await HeroOf(E2);

        hero.BackgroundUrl.Should().Be("https://art.test/background.jpg");
    }

    [Test]
    public async Task TheMetaLine_Should_BeTheCode_TheRuntime_AndWhenItAired()
    {
        var hero = await HeroOf(E1);

        hero.MetaCode.Should().Be("S52 · E1");
        hero.Meta.Should().Be("1 h 8 min · Aired Sun, Sep 27");
        hero.MetaRelease.Should().BeNull();
    }

    [Test]
    public async Task TheMetaLine_Should_EndInTheReleaseTime_ForAnEpisodeNotAiredYet()
    {
        var hero = await HeroOf(E3);

        hero.MetaCode.Should().Be("S52 · E3");
        hero.Meta.Should().BeNull("no runtime is known and it has not aired");
        hero.MetaRelease!.Prefix.Should().Be("Airs");
        hero.MetaRelease.Release.Release.DateTimeAttribute.Should().Be("2026-12-06T04:30:00Z",
            "23:30 Eastern on Dec 5, for the viewer's own clock");
    }

    [Test]
    public async Task ASpecial_Should_SaySpecial_InTheMetaLine()
    {
        var hero = await HeroOf(Special2);

        hero.MetaCode.Should().Be("Special · E2");
    }

    // ---- the action row ----

    [Test]
    public async Task NotWatchedAndReleased_Should_OfferMarkAsWatched()
    {
        SignIn();

        var actions = (await HeroOf(E1)).Actions;

        actions.IsAuthenticated.Should().BeTrue();
        actions.Primary!.Label.Should().Be("Mark as watched");
        actions.Primary.Handler.Should().Be("ToggleWatched");
        actions.Primary.RouteId.Should().Be(E1);
        actions.Primary.Detail.Should().Be("1 h 8 min");
        actions.Primary.PriorCheckEpisodeId.Should().BeNull("nothing earlier is unwatched, so nothing to ask");
        actions.Status.Should().BeNull();
        actions.Menu.Should().BeEmpty();
        actions.Like!.Handler.Should().Be("ToggleLike");
    }

    [Test]
    public async Task MarkAsWatched_Should_AskAboutEarlierEpisodes_WhenThereAreAny()
    {
        SignIn();
        PriorUnwatched(E2, 1);

        var primary = (await HeroOf(E2)).Actions.Primary!;

        primary.PriorCheckEpisodeId.Should().Be(E2);
        primary.PriorUnwatchedCount.Should().Be(1);
        primary.HiddenFields["episodeId"].Should().Be(E2.ToString(), "so the dialog's forms always mark this episode");
    }

    [Test]
    public async Task Watched_Should_BeTheGreenBox_WithMarkAsUnwatchedInTheMenu()
    {
        SignIn();
        Watched(E1, new DateTime(2026, 9, 27, 23, 0, 0, DateTimeKind.Utc));

        var actions = (await HeroOf(E1)).Actions;

        actions.Primary.Should().BeNull();
        actions.Status!.Text.Should().Be("Watched Sun, Sep 27");
        actions.Menu.Should().ContainSingle().Which.Should().Match<TitleMenuItem>(m =>
            m.Label == "Mark as unwatched" && m.Handler == "ToggleWatched" && m.RouteId == E1 && m.Id == "markUnwatched");
    }

    [Test]
    public async Task NotReleasedYet_Should_BeABoxSayingWhenItAirs_AndNoWayToMarkIt()
    {
        SignIn();

        var actions = (await HeroOf(E3)).Actions;

        actions.Primary.Should().BeNull();
        actions.Status!.Text.Should().Be("Airs");
        actions.Status.ReleaseInLabel.Should().BeTrue("\"Airs Sat, Dec 5 · 11:30 PM ET\" on one line");
        actions.Status.Release!.Release.DateTimeAttribute.Should().Be("2026-12-06T04:30:00Z");
        actions.Menu.Should().BeEmpty();
    }

    [Test]
    public async Task AnEpisodeThatMayBeMarked_ButIsNotReleasedHere_Should_StillOfferTheButton()
    {
        // 20:00 UTC on Dec 5: Dec 6 has begun at UTC+14, so it may be marked, though it airs in 8 hours.
        _sut = PageAt(new DateTimeOffset(2026, 12, 5, 20, 0, 0, TimeSpan.Zero));
        SignIn();

        var hero = await HeroOfSut(E3);

        hero.Actions.Primary!.Label.Should().Be("Mark as watched");
        hero.MetaRelease!.Prefix.Should().Be("Airs", "it is still not released");
    }

    [Test]
    public async Task AVisitor_Should_GetTheSignInCard()
    {
        var actions = (await HeroOf(E1)).Actions;

        actions.IsAuthenticated.Should().BeFalse();
        actions.Noun.Should().Be("episode");
        actions.ReturnUrl.Should().Be("/Episodes/Details/" + E1);
    }

    // ---- where you are ----

    [Test]
    public async Task ThePosition_Should_BeThePlaceInTheSeason_AndTheReleasedEpisodesWatched()
    {
        SignIn();
        Watched(E1);

        await _sut.OnGetAsync(E2, CancellationToken.None);

        _sut.Position.Should().NotBeNull();
        _sut.Position!.Label.Should().Be("Episode 2 of 3 · Season 52", "the season has three episodes");
        _sut.Position.WatchedCount.Should().Be(1);
        _sut.Position.ReleasedCount.Should().Be(2, "the December episode is not released");
        _sut.Position.WatchedText.Should().Be("1 of 2 watched");
        _sut.Position.HasBar.Should().BeTrue();
        _sut.Position.Percent.Should().Be(50);
    }

    [Test]
    public async Task ASpecial_Should_BeSpecialNOfM_WithoutAWatchedCountOrBar()
    {
        SignIn();
        Watched(Special1);

        await _sut.OnGetAsync(Special2, CancellationToken.None);

        _sut.Position!.Label.Should().Be("Special 2 of 2");
        _sut.Position.WatchedText.Should().BeNull("specials never count toward progress");
        _sut.Position.HasBar.Should().BeFalse();
    }

    [Test]
    public async Task AVisitor_Should_SeeThePosition_WithoutAWatchedCount()
    {
        await _sut.OnGetAsync(E1, CancellationToken.None);

        _sut.Position!.Label.Should().Be("Episode 1 of 3 · Season 52");
        _sut.Position.WatchedText.Should().BeNull();
        _sut.Position.HasBar.Should().BeFalse();
    }

    // ---- up next ----

    [Test]
    public async Task UpNext_Should_BeTheNextEpisode_WithTheCheck_WhenItHasAired()
    {
        SignIn();
        PriorUnwatched(E2, 1);

        await _sut.OnGetAsync(E1, CancellationToken.None);

        var next = _sut.UpNext!;
        next.EpisodeId.Should().Be(E2);
        next.Code.Should().Be("S52E02");
        next.Title.Should().Be("Sabrina Carpenter");
        next.DateText.Should().Be("Aired Sun, Oct 4");
        next.Release.Should().BeNull();
        next.Mark.Should().Be(UpNextMark.Unwatched);
        next.Handler.Should().Be("ToggleWatched");
        next.RouteId.Should().Be(E1, "the check posts to this page, which marks the other episode and comes back here");
        next.PriorUnwatchedCount.Should().Be(1, "this one is unwatched, so marking the next asks first");
        next.MarkLabel.Should().Be("S52E02 Sabrina Carpenter watched");
    }

    [Test]
    public async Task UpNext_Should_BeTheDashedClock_WhenItMayNotBeMarkedYet()
    {
        SignIn();

        await _sut.OnGetAsync(E2, CancellationToken.None);

        var next = _sut.UpNext!;
        next.EpisodeId.Should().Be(E3);
        next.Mark.Should().Be(UpNextMark.NotAired);
        next.DateText.Should().Be("Airs");
        next.Release!.Release.DateTimeAttribute.Should().Be("2026-12-06T04:30:00Z");
    }

    [Test]
    public async Task UpNext_Should_BeTheGreenCheck_WhenTheNextEpisodeIsWatched()
    {
        SignIn();
        Watched(E2);

        await _sut.OnGetAsync(E1, CancellationToken.None);

        _sut.UpNext!.Mark.Should().Be(UpNextMark.Watched);
        _progress.Verify(x => x.GetPriorUnwatchedCountAsync(UserId, SeriesId, E2, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task UpNext_Should_HaveNoControl_ForAVisitor()
    {
        await _sut.OnGetAsync(E1, CancellationToken.None);

        _sut.UpNext!.Mark.Should().Be(UpNextMark.None);
    }

    [Test]
    public async Task UpNext_Should_FollowWatchOrder_IntoTheSpecials_AndEndAfterTheLastEpisode()
    {
        await _sut.OnGetAsync(E3, CancellationToken.None);
        _sut.UpNext!.EpisodeId.Should().Be(Special1, "specials come last in watch order");

        var last = PageAt(Now);
        await last.OnGetAsync(Special2, CancellationToken.None);
        last.UpNext.Should().BeNull("there is no card after the last episode");
    }

    [Test]
    public async Task UpNext_Should_UseTheSharedImageRule_ReadingTheNextRecordFromTheCachesOnly()
    {
        _tvDb.Setup(x => x.GetCachedEpisodeDetailsAsync(E2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Episode { Id = E2, Image = "https://art.test/e2-record.jpg" });

        await _sut.OnGetAsync(E1, CancellationToken.None);

        _sut.UpNext!.ImageUrl.Should().Be("https://art.test/e2-record.jpg", "the still on its own record, as the Dashboard would show");
        _tvDb.Verify(x => x.GetEpisodeDetailsAsync(E2, It.IsAny<CancellationToken>()), Times.Never,
            "the next episode's record is never fetched from TheTVDB for this page");
    }

    [Test]
    public async Task UpNext_Should_FallBackToTheBackgroundArt_WhenNoCacheHasAStill()
    {
        await _sut.OnGetAsync(E1, CancellationToken.None);

        _sut.UpNext!.ImageUrl.Should().Be("https://art.test/background.jpg");
    }

    [Test]
    public async Task UpNext_Should_UseTheAggregatesStill_WithoutReadingTheRecord()
    {
        await _sut.OnGetAsync(E3, CancellationToken.None);

        _sut.UpNext!.ImageUrl.Should().Be("https://art.test/s1.jpg");
        _tvDb.Verify(x => x.GetCachedEpisodeDetailsAsync(Special1, It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- guest stars ----

    [Test]
    public async Task GuestStars_Should_ComeFromTheEpisodesOwnRecord()
    {
        _tvDb.Setup(x => x.GetEpisodeDetailsAsync(E1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Episode
            {
                Id = E1, SeriesId = SeriesId, SeasonNumber = 52, Number = 1, Name = "Ryan Gosling", Aired = "2026-09-27",
                Characters =
                [
                    new Character { Id = 1, PeopleId = 10, PersonName = "Ryan Gosling", PeopleType = "Host", Sort = 1 },
                    new Character { Id = 2, PeopleId = 11, PersonName = "Kenan Thompson", PeopleType = "Actor", Name = "Various" },
                    new Character { Id = 3, PeopleId = 12, PersonName = "Dave McCary", PeopleType = "Director" },
                ]
            });

        await _sut.OnGetAsync(E1, CancellationToken.None);

        _sut.GuestStars.Should().ContainSingle().Which.Should().Be(new CastPerson("Ryan Gosling", "Host", null, null));
    }

    [Test]
    public async Task GuestStars_Should_BeEmpty_WhenTheRecordListsNone()
    {
        await _sut.OnGetAsync(E1, CancellationToken.None);

        _sut.GuestStars.Should().BeEmpty("the section is then left out");
    }

    // ---- previous and next ----

    [Test]
    public async Task TheFirstEpisode_Should_HaveNoPrevious_AndTheNextWithItsTitle()
    {
        await _sut.OnGetAsync(E1, CancellationToken.None);

        _sut.PreviousEpisode.Should().BeNull();
        _sut.NextEpisode!.Id.Should().Be(E2);
        _sut.NextEpisode.Code.Should().Be("S52E02");
        _sut.NextEpisode.Title.Should().Be("Sabrina Carpenter");
    }

    [Test]
    public async Task TheLastEpisode_Should_HaveNoNext()
    {
        await _sut.OnGetAsync(Special2, CancellationToken.None);

        _sut.PreviousEpisode!.Id.Should().Be(Special1);
        _sut.NextEpisode.Should().BeNull();
    }

    [Test]
    public async Task WithoutTheSeries_Should_ShowTheEpisode_WithoutPositionUpNextOrNeighbours()
    {
        _tvDb.Setup(x => x.GetSeriesAggregateByIdAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cache down"));

        var hero = await HeroOf(E1);

        hero.Name.Should().Be("Ryan Gosling");
        _sut.Position.Should().BeNull();
        _sut.UpNext.Should().BeNull();
        _sut.PreviousEpisode.Should().BeNull();
        _sut.NextEpisode.Should().BeNull();
        hero.Back.Text.Should().Be("Back to the series", "the series' name is not known");
        hero.Parent.Should().BeNull();
    }

    // ---- the Up next card's check posts here ----

    [Test]
    public async Task ToggleWatched_WithAnotherEpisode_Should_MarkThatOne_AndComeBackHere()
    {
        SignIn();
        _progress.Setup(x => x.ToggleEpisodeWatchedAsync(UserId, SeriesId, E2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EpisodeWatchResult(EpisodeWatchOutcome.MarkedWatched, AddedToLibrary: "Saturday Night Live"));

        var result = await _sut.OnPostToggleWatchedAsync(E1, CancellationToken.None, episodeId: E2);

        result.Should().BeOfType<RedirectToPageResult>().Which.RouteValues!["id"].Should().Be(E1);
        _sut.SuccessToast().Should().Be("Marked S52E02 as watched and added Saturday Night Live to your library.",
            "the toast names the episode that was marked, from the cached aggregate");
        _progress.Verify(x => x.ToggleEpisodeWatchedAsync(UserId, SeriesId, E1, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task MarkWatchedThrough_WithAnotherEpisode_Should_MarkThroughThatOne_WithTheUndo()
    {
        SignIn();
        var batch = new WatchedBatch(2, Guid.NewGuid());
        _progress.Setup(x => x.MarkWatchedThroughAsync(UserId, SeriesId, E2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarkWatchedThroughResult(EpisodeFound: true, HasAired: true, MarkedCount: 2, Batch: batch));

        var result = await _sut.OnPostMarkWatchedThroughAsync(E1, CancellationToken.None, episodeId: E2);

        result.Should().BeOfType<RedirectToPageResult>().Which.RouteValues!["id"].Should().Be(E1);
        _sut.SuccessToast().Should().Be("Marked 2 episodes as watched.");
        _sut.TempData[PageModelToastExtensions.UndoWatchedBatchKey].Should().NotBeNull("the Undo is kept");
    }

    private DetailsModel PageAt(DateTimeOffset now) =>
        new DetailsModel(
            NullLogger<DetailsModel>.Instance,
            _tvDb.Object,
            _currentUser.Object,
            _watches.Object,
            _progress.Object,
            Mock.Of<ILikeRepository>(),
            _ratings.Object,
            Mock.Of<IOmdbApiClient>(),
            Mock.Of<IEpisodeOmdbSnapshotStore>(),
            Mock.Of<IOmdbRequestBudget>(),
            Options.Create(new OmdbOptions()),
            new FixedTimeProvider(now)).WithTempData().WithHttpContext();

    private async Task<TitleHeroModel> HeroOfSut(int episodeId)
    {
        await _sut.OnGetAsync(episodeId, CancellationToken.None);
        return _sut.BuildHero();
    }
}
