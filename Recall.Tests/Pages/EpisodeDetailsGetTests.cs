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
}
