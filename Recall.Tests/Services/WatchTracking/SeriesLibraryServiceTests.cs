using AwesomeAssertions;
using Moq;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Services.WatchTracking;

/// <summary>
/// <see cref="SeriesLibraryService"/>, the one "Add to library" (Series
/// Details and Discover): adds with the series' own details, says when it was
/// already there or does not exist, and takes it out again for an Undo.
/// </summary>
[TestFixture]
public sealed class SeriesLibraryServiceTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private const int SeriesId = 371980;

    private Mock<ITrackedSeriesRepository> _tracked = null!;
    private Mock<ITheTvDbService> _tvDb = null!;
    private SeriesLibraryService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _tracked = new Mock<ITrackedSeriesRepository>();
        _tvDb = new Mock<ITheTvDbService>(MockBehavior.Strict);
        _sut = new SeriesLibraryService(_tracked.Object, _tvDb.Object);
    }

    [Test]
    public async Task Add_Should_StoreTheSeriesWithItsDetails()
    {
        _tvDb.Setup(x => x.GetSeriesByIdAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TvSeriesDetails(SeriesId, "Severance", "severance", "Work and life.", "https://img/p.jpg", "2022-02-18", 8.7, "Continuing"));
        _tracked.Setup(x => x.AddAsync(It.IsAny<TrackedSeries>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await _sut.AddAsync(UserId, SeriesId);

        result.Should().Be(new AddSeriesResult(AddSeriesOutcome.Added, "Severance"));
        _tracked.Verify(x => x.AddAsync(It.Is<TrackedSeries>(s =>
            s.UserId == UserId && s.TvdbId == SeriesId && s.Name == "Severance" && s.ImageUrl == "https://img/p.jpg"
            && s.FirstAired == new DateOnly(2022, 2, 18)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Add_Should_SayAlreadyThere_WithoutLookingTheSeriesUp()
    {
        _tracked.Setup(x => x.ExistsAsync(UserId, SeriesId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        (await _sut.AddAsync(UserId, SeriesId)).Outcome.Should().Be(AddSeriesOutcome.AlreadyInLibrary);
        _tracked.Verify(x => x.AddAsync(It.IsAny<TrackedSeries>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Add_Should_SayAlreadyThere_WhenAConcurrentRequestAddedItFirst()
    {
        _tvDb.Setup(x => x.GetSeriesByIdAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TvSeriesDetails(SeriesId, "Severance", "severance", null, null, null, null, "Continuing"));
        _tracked.Setup(x => x.AddAsync(It.IsAny<TrackedSeries>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        (await _sut.AddAsync(UserId, SeriesId)).Should().Be(new AddSeriesResult(AddSeriesOutcome.AlreadyInLibrary, "Severance"));
    }

    [Test]
    public async Task Add_Should_AddNothing_ForASeriesTheTvdbDoesNotHave()
    {
        _tvDb.Setup(x => x.GetSeriesByIdAsync(SeriesId, It.IsAny<CancellationToken>())).ReturnsAsync((TvSeriesDetails?)null);

        (await _sut.AddAsync(UserId, SeriesId)).Outcome.Should().Be(AddSeriesOutcome.NotFound);
        _tracked.Verify(x => x.AddAsync(It.IsAny<TrackedSeries>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Remove_Should_DeleteTheRow_WhenTheSeriesIsInTheLibrary()
    {
        var trackedId = Guid.NewGuid();
        _tracked.Setup(x => x.GetByUserAndTvdbIdAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TrackedSeries { Id = trackedId, UserId = UserId, TvdbId = SeriesId, Name = "Severance" });

        (await _sut.RemoveAsync(UserId, SeriesId)).Should().BeTrue();
        _tracked.Verify(x => x.RemoveAsync(UserId, trackedId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Remove_Should_ReportFalse_WhenTheSeriesIsNotThere()
    {
        (await _sut.RemoveAsync(UserId, SeriesId)).Should().BeFalse();
        _tracked.Verify(x => x.RemoveAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
