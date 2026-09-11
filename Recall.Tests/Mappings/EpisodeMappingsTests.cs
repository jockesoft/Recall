using AwesomeAssertions;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;
using Recall.Web.Mappings;

namespace Recall.Tests.Mappings;

[TestFixture]
public class EpisodeMappingsTests
{
    [Test]
    public void ToDomain_Should_NormalizeImage_WhenRelative()
    {
        var dto = new EpisodeDto { Id = 10, Image = "/banners/episodes/10.jpg" };

        var episode = dto.ToDomain();

        episode.Image.Should().Be("https://artworks.thetvdb.com/banners/episodes/10.jpg");
    }

    [Test]
    public void ToDomain_Should_LeaveAbsoluteImage_Unchanged()
    {
        const string absolute = "https://example.test/still.jpg";
        var dto = new EpisodeDto { Id = 10, Image = absolute };

        var episode = dto.ToDomain();

        episode.Image.Should().Be(absolute);
    }
}
