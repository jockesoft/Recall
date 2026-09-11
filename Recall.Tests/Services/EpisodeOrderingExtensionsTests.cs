using AwesomeAssertions;
using Recall.Web.Services;

namespace Recall.Tests.Services;

[TestFixture]
public class EpisodeOrderingExtensionsTests
{
    private sealed record Item(int Id, int? Season, int? Episode);

    [Test]
    public void OrderBySeasonAndEpisode_Should_OrderBySeasonThenEpisode()
    {
        var items = new[]
        {
            new Item(1, 2, 1),
            new Item(2, 1, 2),
            new Item(3, 1, 1)
        };

        var ordered = items.OrderBySeasonAndEpisode(i => i.Season, i => i.Episode, i => i.Id);

        ordered.Select(i => i.Id).Should().ContainInOrder(3, 2, 1);
    }

    [Test]
    public void OrderBySeasonAndEpisode_Should_SortUnknownSeasonOrEpisode_Last()
    {
        var items = new[]
        {
            new Item(1, null, 1),
            new Item(2, 1, 1),
            new Item(3, 1, null)
        };

        var ordered = items.OrderBySeasonAndEpisode(i => i.Season, i => i.Episode, i => i.Id);

        ordered.Select(i => i.Id).Should().ContainInOrder(2, 3, 1);
    }

    [Test]
    public void OrderBySeasonAndEpisode_Should_UseTieBreakId_WhenSeasonAndEpisodeMatch()
    {
        var items = new[]
        {
            new Item(5, 1, 1),
            new Item(2, 1, 1),
            new Item(9, 1, 1)
        };

        var ordered = items.OrderBySeasonAndEpisode(i => i.Season, i => i.Episode, i => i.Id);

        ordered.Select(i => i.Id).Should().ContainInOrder(2, 5, 9);
    }
}
