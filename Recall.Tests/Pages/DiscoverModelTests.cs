using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Pages;
using Recall.Web.Services;
using Recall.Web.Services.Discover;

namespace Recall.Tests.Pages;

/// <summary>
/// The Discover page model: the rows come from <see cref="IDiscoverService"/>,
/// a failure is said on the page with a 500, and the cards are named by their
/// row and place.
/// </summary>
[TestFixture]
public sealed class DiscoverModelTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private Mock<IDiscoverService> _discover = null!;
    private Mock<ICurrentUserService> _currentUser = null!;

    [SetUp]
    public void SetUp()
    {
        _discover = new Mock<IDiscoverService>();
        _currentUser = new Mock<ICurrentUserService>();
        _currentUser.SetupGet(x => x.UserId).Returns(UserId);
    }

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
    }

    private DiscoverModel Sut() =>
        new DiscoverModel(_discover.Object, _currentUser.Object, NullLogger<DiscoverModel>.Instance).WithTempData().WithHttpContext();

    private static DiscoverRow Row(string slug) =>
        new(slug, "Heading", "Description",
            [new DiscoverTitle { Kind = DiscoverKind.Series, TvdbId = 1, Name = "Severance", Year = 2022, Score = 1000 }]);
}
