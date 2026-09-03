using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services.Notifications;
using Recall.Web.Services.Notifications.Models;

namespace Recall.Tests.Services;

[TestFixture]
public class NotificationServiceTests
{
    private Mock<INotificationRepository> _repository = null!;
    private NotificationService _sut = null!;

    private static readonly Guid User = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [SetUp]
    public void SetUp()
    {
        _repository = new Mock<INotificationRepository>();

        _repository
            .Setup(x => x.GetAlreadyNotifiedEpisodeIdsAsync(It.IsAny<Guid>(), It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<int>)new HashSet<int>());

        _repository
            .Setup(x => x.AddNewEpisodeNotificationAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _sut = new NotificationService(_repository.Object, NullLogger<NotificationService>.Instance);
    }

    private static NewEpisodeItem Ep(int id, int? season, int? number, string? name = "Episode") =>
        new(id, season, number, name);

    [Test]
    public async Task NotifyNewEpisodesAsync_Should_BuildSingleEpisodeCopy_AndForwardToRepository()
    {
        var digest = new NewEpisodesDigest(42, "Severance", new[] { Ep(900, 2, 5, "Trojan's Horse") });

        var created = await _sut.NotifyNewEpisodesAsync(User, digest);

        created.Should().BeTrue();
        _repository.Verify(x => x.AddNewEpisodeNotificationAsync(
            User, 42, 900, 1,
            "New episode of Severance",
            "S02E05 · Trojan's Horse is out now.",
            It.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 900 })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task NotifyNewEpisodesAsync_Should_OmitTitle_WhenEpisodeNameIsPlaceholder()
    {
        string? capturedBody = null;
        _repository
            .Setup(x => x.AddNewEpisodeNotificationAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, int, int, int, string, string?, IReadOnlyCollection<int>, CancellationToken>(
                (_, _, _, _, _, body, _, _) => capturedBody = body)
            .ReturnsAsync(true);

        await _sut.NotifyNewEpisodesAsync(User, new NewEpisodesDigest(1, "Show", new[] { Ep(2, 1, 3, "TBA") }));

        capturedBody.Should().Be("S01E03 is out now.");
    }

    [Test]
    public async Task NotifyNewEpisodesAsync_Should_CollapseAFullSeasonDrop_IntoOneNotification()
    {
        var episodes = Enumerable.Range(1, 8).Select(n => Ep(1000 + n, 2, n)).ToArray();
        // Hand them in shuffled to prove the service orders them.
        var digest = new NewEpisodesDigest(42, "Show", episodes.Reverse().ToArray());

        var created = await _sut.NotifyNewEpisodesAsync(User, digest);

        created.Should().BeTrue();
        _repository.Verify(x => x.AddNewEpisodeNotificationAsync(
            User, 42,
            1001,                                   // link = earliest episode
            8,                                      // count
            "8 new episodes of Show",
            "S02 · E01–E08 are out now.",
            It.Is<IReadOnlyCollection<int>>(ids => ids.Count == 8 && ids.First() == 1001 && ids.Last() == 1008),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task NotifyNewEpisodesAsync_Should_OnlyCoverEpisodesNotAlreadyNotified()
    {
        _repository
            .Setup(x => x.GetAlreadyNotifiedEpisodeIdsAsync(User, It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<int>)new HashSet<int> { 1001, 1002 });

        var episodes = Enumerable.Range(1, 5).Select(n => Ep(1000 + n, 2, n)).ToArray();

        await _sut.NotifyNewEpisodesAsync(User, new NewEpisodesDigest(42, "Show", episodes));

        _repository.Verify(x => x.AddNewEpisodeNotificationAsync(
            User, 42, 1003, 3,
            "3 new episodes of Show",
            "S02 · E03–E05 are out now.",
            It.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 1003, 1004, 1005 })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task NotifyNewEpisodesAsync_Should_ReturnFalse_WhenEveryEpisodeAlreadyNotified()
    {
        _repository
            .Setup(x => x.GetAlreadyNotifiedEpisodeIdsAsync(User, It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<int>)new HashSet<int> { 900 });

        var created = await _sut.NotifyNewEpisodesAsync(User, new NewEpisodesDigest(42, "Show", new[] { Ep(900, 1, 1) }));

        created.Should().BeFalse();
        _repository.Verify(x => x.AddNewEpisodeNotificationAsync(
            It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task NotifyNewEpisodesAsync_Should_ReturnFalse_ForAnEmptyDigest()
    {
        var created = await _sut.NotifyNewEpisodesAsync(User, new NewEpisodesDigest(42, "Show", Array.Empty<NewEpisodeItem>()));

        created.Should().BeFalse();
    }

    [Test]
    public async Task GetRecentAsync_Should_ResolveEpisodeDeepLink()
    {
        _repository
            .Setup(x => x.GetForUserAsync(User, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new Notification(Guid.NewGuid(), NotificationType.NewEpisode, "New episode of Show", "S01E01 is out now.",
                    42, 900, EpisodeCount: 1, IsRead: false, DateTime.UtcNow, ReadUtc: null)
            });

        var items = await _sut.GetRecentAsync(User);

        items.Should().ContainSingle();
        items[0].TargetHref.Should().Be("/Episodes/Details/900");
    }

    [Test]
    public async Task OpenAsync_Should_MarkRead_AndReturnTarget()
    {
        var id = Guid.NewGuid();
        _repository
            .Setup(x => x.GetAsync(User, id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Notification(id, NotificationType.NewEpisode, "New episode of Show", null,
                42, 900, EpisodeCount: 1, IsRead: false, DateTime.UtcNow, ReadUtc: null));

        var target = await _sut.OpenAsync(User, id);

        target.Should().Be("/Episodes/Details/900");
        _repository.Verify(x => x.MarkReadAsync(User, id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task OpenAsync_Should_ReturnNull_AndNotMarkRead_WhenNotificationMissing()
    {
        var id = Guid.NewGuid();
        _repository
            .Setup(x => x.GetAsync(User, id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Notification?)null);

        var target = await _sut.OpenAsync(User, id);

        target.Should().BeNull();
        _repository.Verify(x => x.MarkReadAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
