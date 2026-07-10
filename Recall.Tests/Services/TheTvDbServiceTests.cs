using Moq;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Search;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;
using Recall.Web.Services;
using Recall.Web.Services.External.TheTvDb;
using AwesomeAssertions;

namespace Recall.Tests.Services;

[TestFixture]
public class TheTvDbServiceTests
{
    [Test]
    public async Task SearchSeriesAsync_Should_MapDtos_ToSummaries()
    {
        // Arrange
        var apiClientMock = new Mock<ITheTvDbApiClient>();

        apiClientMock
            .Setup(x => x.SearchSeriesAsync("dark", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SearchResultDto>
            {
                new()
                {
                    TvdbId = 1,
                    Name = "Dark",
                    Type = "series",
                    Year = "2017",
                    Overview = "A family saga with a supernatural twist."
                }
            });

        var sut = new TheTvDbService(apiClientMock.Object);

        // Act
        var result = await sut.SearchSeriesAsync("dark");

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(1);

        var item = result[0];
        item.TvdbId.Should().Be(1);
        item.Name.Should().Be("Dark");
        item.Year.Should().Be("2017");
        item.Overview.Should().Be("A family saga with a supernatural twist.");
    }

    [Test]
    public async Task SearchSeriesAsync_Should_FilterOut_NonSeriesTypes()
    {
        // Arrange
        var apiClientMock = new Mock<ITheTvDbApiClient>();

        apiClientMock
            .Setup(x => x.SearchSeriesAsync("batman", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SearchResultDto>
            {
                new() { TvdbId = 10, Name = "Batman Begins", Type = "movie" },
                new() { TvdbId = 11, Name = "The Batman", Type = "series" },
                new() { TvdbId = 12, Name = "Unknown Type Item", Type = null } // allowed by service
            });

        var sut = new TheTvDbService(apiClientMock.Object);

        // Act
        var result = await sut.SearchSeriesAsync("batman");

        // Assert
        result.Should().HaveCount(2);
        result.Select(x => x.TvdbId).Should().BeEquivalentTo(new[] { 11, 12 });
    }

    [Test]
    public async Task GetSeriesByIdAsync_Should_MapDto_ToDetails()
    {
        // Arrange
        var apiClientMock = new Mock<ITheTvDbApiClient>();

        apiClientMock
            .Setup(x => x.GetSeriesByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesDataDto
            {
                Id = 10,
                Name = "Breaking Bad",
                Slug = "breaking-bad",
                Overview = "A chemistry teacher turned meth producer.",
                Image = "https://example.com/breakingbad.jpg",
                FirstAired = "2008-01-20",
                Score = 9.5
            });

        var sut = new TheTvDbService(apiClientMock.Object);

        // Act
        var result = await sut.GetSeriesByIdAsync(10);

        // Assert
        result.Should().NotBeNull();
        result!.TvdbId.Should().Be(10);
        result.Name.Should().Be("Breaking Bad");
        result.Slug.Should().Be("breaking-bad");
        result.FirstAired.Should().Be("2008-01-20");
        result.Score.Should().Be(9.5);
    }

    [Test]
    public async Task GetSeriesByIdAsync_Should_ReturnNull_WhenApiReturnsNull()
    {
        // Arrange
        var apiClientMock = new Mock<ITheTvDbApiClient>();

        apiClientMock
            .Setup(x => x.GetSeriesByIdAsync(404, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesDataDto?)null);

        var sut = new TheTvDbService(apiClientMock.Object);

        // Act
        var result = await sut.GetSeriesByIdAsync(404);

        // Assert
        result.Should().BeNull();
    }
}