using AwesomeAssertions;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services.Push;

namespace Recall.Tests.Services.Push;

/// <summary>Several notifications become one push: one episode is named and opened, several are counted.</summary>
[TestFixture]
public class PushSummaryTests
{
    private static readonly DateTime Created = new(2026, 10, 28, 6, 0, 0, DateTimeKind.Utc);

    private static UnreadEpisodeNotice Notice(string? series, int episodes = 1, string? code = null, int? episodeId = 900, string title = "New episode of X") =>
        new(Guid.NewGuid(), title, series, episodeId, code, episodes, Created);

    [Test]
    public void OneEpisode_Should_NameTheSeriesAndTheEpisode_AndOpenIt()
    {
        var push = PushSummary.For([Notice("Silo", code: "S02E05", episodeId: 1234)])!;

        push.Title.Should().Be("Silo: S02E05 is out");
        push.Url.Should().Be("/Episodes/Details/1234");
        push.Icon.Should().Be("/images/icons/icon-192.png");
    }

    [Test]
    public void OneEpisodeWithoutNumbers_Should_StillNameTheSeries()
    {
        PushSummary.For([Notice("Silo")])!.Title.Should().Be("Silo: a new episode is out");
    }

    [Test]
    public void ThreeEpisodesOfThreeSeries_Should_BeCounted_NamingTwo_AndOpenTheNotificationsPage()
    {
        var push = PushSummary.For([Notice("Silo", code: "S02E05"), Notice("The Bear", code: "S04E01"), Notice("Severance", code: "S02E10")])!;

        push.Title.Should().Be("3 new episodes: Silo, The Bear and 1 more");
        push.Url.Should().Be("/Account/Notifications");
    }

    [Test]
    public void TwoSeries_Should_BothBeNamed()
    {
        PushSummary.For([Notice("Silo", code: "S02E05"), Notice("The Bear", episodes: 2)])!
            .Title.Should().Be("3 new episodes: Silo and The Bear");
    }

    [Test]
    public void ASeasonDropOfOneSeries_Should_CountItsEpisodes()
    {
        var push = PushSummary.For([Notice("Silo", episodes: 8)])!;

        push.Title.Should().Be("8 new episodes: Silo");
        push.Url.Should().Be("/Account/Notifications");
    }

    [TestCase("New episode of Severance", "Severance: a new episode is out")]
    [TestCase("Odd title", "Odd title: a new episode is out")]
    public void ANotificationFromBeforeTheNameWasStored_Should_TakeTheNameFromItsTitle(string title, string push)
    {
        PushSummary.For([Notice(series: null, title: title)])!.Title.Should().Be(push);
    }

    [Test]
    public void ASeasonDropFromBeforeTheNameWasStored_Should_TakeTheNameFromItsTitle()
    {
        PushSummary.For([Notice(series: null, episodes: 3, title: "3 new episodes of Slow Horses")])!
            .Title.Should().Be("3 new episodes: Slow Horses");
    }

    [Test]
    public void Nothing_Should_BeNoPush()
    {
        PushSummary.For([]).Should().BeNull();
    }
}
