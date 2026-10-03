using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.External.Omdb;
using Recall.Web.Infrastructure.Persistence.OmdbCache;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages.Episodes;
using Recall.Web.Services;
using Recall.Web.Services.External.Omdb;
using Recall.Web.Services.External.TheTvDb;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Pages;

/// <summary>An episode that can't be shown: the page says why, with the right status code and a way back.</summary>
[TestFixture]
public sealed class EpisodeDetailsGetTests
{
    private const int EpisodeId = 4201;

    private Mock<ITheTvDbService> _tvDb = null!;
    private DetailsModel _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _tvDb = new Mock<ITheTvDbService>();

        _sut = new DetailsModel(
            NullLogger<DetailsModel>.Instance,
            _tvDb.Object,
            Mock.Of<ICurrentUserService>(),
            Mock.Of<IEpisodeWatchRepository>(),
            Mock.Of<IWatchProgressService>(),
            Mock.Of<ILikeRepository>(),
            Mock.Of<IRatingRepository>(),
            Mock.Of<IOmdbApiClient>(),
            Mock.Of<IEpisodeOmdbSnapshotStore>(),
            Mock.Of<IOmdbRequestBudget>(),
            Options.Create(new OmdbOptions()),
            TimeProvider.System).WithTempData().WithHttpContext();
    }

    [Test]
    public async Task AnEpisodeTheTvDbDoesNotHave_Should_Be404_OnTheEpisodePage()
    {
        _tvDb.Setup(x => x.GetEpisodeDetailsAsync(EpisodeId, It.IsAny<CancellationToken>())).ReturnsAsync((Episode?)null);

        var result = await _sut.OnGetAsync(EpisodeId, CancellationToken.None);

        result.Should().BeOfType<PageResult>("the page renders its own not-found state, with a way back to the series");
        _sut.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        _sut.Episode.Should().BeNull();
        _sut.LoadFailed.Should().BeFalse();
    }

    [Test]
    public async Task ATheTvDbFailure_Should_Be503_AndSaySo()
    {
        _tvDb.Setup(x => x.GetEpisodeDetailsAsync(EpisodeId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TheTvDbApiException("down", 502));

        var result = await _sut.OnGetAsync(EpisodeId, CancellationToken.None);

        result.Should().BeOfType<PageResult>();
        _sut.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        _sut.LoadFailed.Should().BeTrue();
        _sut.Episode.Should().BeNull();
    }

    [Test]
    public async Task AnUnexpectedFailure_Should_Be500()
    {
        _tvDb.Setup(x => x.GetEpisodeDetailsAsync(EpisodeId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await _sut.OnGetAsync(EpisodeId, CancellationToken.None);

        _sut.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        _sut.LoadFailed.Should().BeTrue();
    }

    [TestCase("https://recall.test/Series/Details/81189", 81189)]
    [TestCase("https://recall.test/Series/Details/81189?season=5", 81189)]
    [TestCase("https://recall.test/Library", null)]
    [TestCase("https://elsewhere.example/Series/Details/81189", null)]
    [TestCase("", null)]
    public void CameFromSeriesId_Should_ComeFromASameSiteSeriesPage(string referer, int? expected)
    {
        _sut.Request.Host = new HostString("recall.test");
        _sut.Request.Headers.Referer = referer;

        _sut.CameFromSeriesId.Should().Be(expected);
    }

    // ---- Gold Rush S17E01: aired Friday 2026-10-02 at 20:00 ET = 00:00 UTC Saturday ----

    private DetailsModel GoldRushPageAt(DateTimeOffset now)
    {
        _tvDb.Setup(x => x.GetEpisodeDetailsAsync(11961330, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Episode { Id = 11961330, SeriesId = 208111, SeasonNumber = 17, Number = 1, Name = "The Most Gold Wins", Aired = "2026-10-02" });
        _tvDb.Setup(x => x.GetSeriesAggregateByIdAsync(208111, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesAggregate
            {
                TvdbId = 208111, Name = "Gold Rush", AirsTime = "20:00", OriginalCountry = "usa",
                Episodes = [new EpisodeSummary { Id = 11961330, SeasonNumber = 17, EpisodeNumber = 1, Aired = new DateOnly(2026, 10, 2) }]
            });

        return new DetailsModel(
            NullLogger<DetailsModel>.Instance,
            _tvDb.Object,
            Mock.Of<ICurrentUserService>(),
            Mock.Of<IEpisodeWatchRepository>(),
            Mock.Of<IWatchProgressService>(),
            Mock.Of<ILikeRepository>(),
            Mock.Of<IRatingRepository>(),
            Mock.Of<IOmdbApiClient>(),
            Mock.Of<IEpisodeOmdbSnapshotStore>(),
            Mock.Of<IOmdbRequestBudget>(),
            Options.Create(new OmdbOptions()),
            new FixedTimeProvider(now)).WithTempData().WithHttpContext();
    }

    [Test]
    public async Task GoldRush_At2200UtcOnOctoberSecond_Should_NotHaveAiredYet_ButMayBeMarked()
    {
        var sut = GoldRushPageAt(new DateTimeOffset(2026, 10, 2, 22, 0, 0, TimeSpan.Zero));

        await sut.OnGetAsync(11961330, CancellationToken.None);

        sut.Release!.Utc.Should().Be(new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc));
        sut.Release.DateTimeAttribute.Should().Be("2026-10-03T00:00:00Z", "the <time> element's datetime");
        sut.HasAired.Should().BeFalse("it says \"Airs\" until the release moment");
        sut.MayBeMarked.Should().BeTrue("marking goes by the air date, for viewers in any zone");
    }

    [Test]
    public async Task GoldRush_AtMidnightUtcOnOctoberThird_Should_HaveAired()
    {
        var sut = GoldRushPageAt(new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero));

        await sut.OnGetAsync(11961330, CancellationToken.None);

        sut.HasAired.Should().BeTrue();
        sut.MayBeMarked.Should().BeTrue();
    }
}
