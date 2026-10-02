using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Pages.Account;
using Recall.Web.Services;
using Recall.Web.Services.Notifications;
using Recall.Web.Services.Notifications.Models;

namespace Recall.Tests.Pages;

[TestFixture]
public class NotificationsModelTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private Mock<INotificationService> _notifications = null!;
    private Mock<ITheTvDbService> _tvDb = null!;
    private NotificationsModel _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.UserId).Returns(UserId);

        _notifications = new Mock<INotificationService>();
        _tvDb = new Mock<ITheTvDbService>();

        _sut = new NotificationsModel(
            currentUser.Object,
            _notifications.Object,
            _tvDb.Object,
            NullLogger<NotificationsModel>.Instance).WithTempData();
    }

    private static NotificationListItem Item(int? seriesId, bool isRead = false) =>
        new(Guid.NewGuid(), NotificationType.NewEpisode, "New episode", "S01E02", 1, isRead, DateTime.UtcNow, "/Episodes/Details/5", seriesId);

    [Test]
    public async Task OnGet_Should_GiveEachRowItsSeriesPoster_ReadingEachSeriesOnce()
    {
        var first = Item(seriesId: 1);
        var second = Item(seriesId: 1, isRead: true);
        var other = Item(seriesId: 2);
        var noSeries = Item(seriesId: null);
        _notifications.Setup(x => x.GetRecentAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync([first, second, other, noSeries]);
        _tvDb.Setup(x => x.GetSeriesAggregateByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesAggregate { TvdbId = 1, Name = "One", ImageUrl = "https://img/1.jpg" });
        _tvDb.Setup(x => x.GetSeriesAggregateByIdAsync(2, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("TheTVDB is down"));

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.PosterFor(first).Should().Be("https://img/1.jpg");
        _sut.PosterFor(second).Should().Be("https://img/1.jpg");
        _sut.PosterFor(other).Should().BeNull("a series that can't be read falls back to the icon");
        _sut.PosterFor(noSeries).Should().BeNull();
        _sut.UnreadCount.Should().Be(3);
        _sut.ErrorToast().Should().BeNull("a missing poster is not an error");
        _tvDb.Verify(x => x.GetSeriesAggregateByIdAsync(1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task MarkRead_Should_MarkThatOneNotification_AndStayOnTheList()
    {
        var id = Guid.NewGuid();

        var result = await _sut.OnPostMarkReadAsync(id, CancellationToken.None);

        result.Should().BeOfType<RedirectToPageResult>().Which.PageName.Should().BeNull("back to the list, not to the episode");
        _notifications.Verify(x => x.MarkReadAsync(UserId, id, It.IsAny<CancellationToken>()), Times.Once);
        _notifications.Verify(x => x.OpenAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task MarkRead_Should_ShowAnErrorToast_WhenItFails()
    {
        _notifications
            .Setup(x => x.MarkReadAsync(UserId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var result = await _sut.OnPostMarkReadAsync(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<RedirectToPageResult>();
        _sut.ErrorToast().Should().Be("Could not update your notifications right now.");
    }
}
