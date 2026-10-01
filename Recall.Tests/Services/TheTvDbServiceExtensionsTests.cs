using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Services;

namespace Recall.Tests.Services;

[TestFixture]
public class TheTvDbServiceExtensionsTests
{
    private Mock<ITheTvDbService> _theTvDbService = null!;

    [SetUp]
    public void SetUp()
    {
        _theTvDbService = new Mock<ITheTvDbService>();
    }

    [Test]
    public async Task TryGetSeriesAggregateAsync_Should_ReturnAggregate_OnSuccess()
    {
        var aggregate = new SeriesAggregate { TvdbId = 42, Name = "Dark" };
        _theTvDbService
            .Setup(x => x.GetSeriesAggregateByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(aggregate);

        var result = await _theTvDbService.Object.TryGetSeriesAggregateAsync(
            42, NullLogger.Instance, "UnitTest");

        result.Should().BeSameAs(aggregate);
    }

    [Test]
    public async Task TryGetSeriesAggregateAsync_Should_ReturnNull_WhenCallThrows()
    {
        _theTvDbService
            .Setup(x => x.GetSeriesAggregateByIdAsync(42, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _theTvDbService.Object.TryGetSeriesAggregateAsync(
            42, NullLogger.Instance, "UnitTest");

        result.Should().BeNull("one bad series shouldn't fail the whole caller");
    }

    [Test]
    public void TryGetSeriesAggregateAsync_Should_Propagate_OperationCanceledException()
    {
        _theTvDbService
            .Setup(x => x.GetSeriesAggregateByIdAsync(42, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        Func<Task> act = () => _theTvDbService.Object.TryGetSeriesAggregateAsync(42, NullLogger.Instance, "UnitTest");

        act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task TryGetSeriesSummaryAsync_Should_ReturnSummary_OnSuccess()
    {
        var summary = new SeriesSummary { TvdbId = 42, Name = "Dark" };
        _theTvDbService
            .Setup(x => x.GetSeriesSummaryByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(summary);

        var result = await _theTvDbService.Object.TryGetSeriesSummaryAsync(42, NullLogger.Instance, "UnitTest");

        result.Should().BeSameAs(summary);
    }

    [Test]
    public async Task TryGetSeriesSummaryAsync_Should_ReturnNull_WhenCallThrows()
    {
        _theTvDbService
            .Setup(x => x.GetSeriesSummaryByIdAsync(42, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _theTvDbService.Object.TryGetSeriesSummaryAsync(42, NullLogger.Instance, "UnitTest");

        result.Should().BeNull("one bad series shouldn't fail a page that is loading fifty of them");
    }

    [Test]
    public async Task TryGetSeriesSummaryAsync_Should_Propagate_OperationCanceledException()
    {
        _theTvDbService
            .Setup(x => x.GetSeriesSummaryByIdAsync(42, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        Func<Task> act = () => _theTvDbService.Object.TryGetSeriesSummaryAsync(42, NullLogger.Instance, "UnitTest");

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
