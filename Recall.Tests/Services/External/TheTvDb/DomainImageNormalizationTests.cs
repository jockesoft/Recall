using AwesomeAssertions;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Services.External.TheTvDb;

namespace Recall.Tests.Services.External.TheTvDb;

[TestFixture]
public class DomainImageNormalizationTests
{
    private const string ArtworksBaseUrl = "https://artworks.thetvdb.com";

    [Test]
    public void SeriesAggregate_WithNormalizedImages_Should_NormalizeTopLevelImageUrl()
    {
        var aggregate = new SeriesAggregate { TvdbId = 1, Name = "Show", ImageUrl = "/banners/series/1.jpg" };

        var normalized = aggregate.WithNormalizedImages();

        normalized.ImageUrl.Should().Be($"{ArtworksBaseUrl}/banners/series/1.jpg");
    }

    [Test]
    public void SeriesAggregate_WithNormalizedImages_Should_LeaveAbsoluteImageUrl_Unchanged()
    {
        const string absolute = "https://artworks.thetvdb.com/banners/series/366625/episodes/5fca7460f169d.jpg";
        var aggregate = new SeriesAggregate { TvdbId = 1, Name = "Show", ImageUrl = absolute };

        var normalized = aggregate.WithNormalizedImages();

        normalized.ImageUrl.Should().Be(absolute);
    }

    [Test]
    public void SeriesAggregate_WithNormalizedImages_Should_NormalizeNestedSeasonEpisodeAndCharacterImages()
    {
        var aggregate = new SeriesAggregate
        {
            TvdbId = 1,
            Name = "Show",
            Seasons = [new SeasonSummary { Id = 10, Name = "Season 1", ImageUrl = "/banners/seasons/10.jpg" }],
            Episodes =
            [
                new EpisodeSummary
                {
                    Id = 100,
                    Name = "Pilot",
                    Image = "/banners/v4/episode/11884343/screencap/6a9a171d96372.jpg"
                }
            ],
            Characters =
            [
                new Character
                {
                    Id = 5,
                    Image = "/banners/actors/5.jpg",
                    PersonImageUrl = "/banners/actors/5-person.jpg"
                }
            ]
        };

        var normalized = aggregate.WithNormalizedImages();

        normalized.Seasons.Single().ImageUrl.Should().Be($"{ArtworksBaseUrl}/banners/seasons/10.jpg");
        normalized.Episodes.Single().Image.Should().Be($"{ArtworksBaseUrl}/banners/v4/episode/11884343/screencap/6a9a171d96372.jpg");
        normalized.Characters.Single().Image.Should().Be($"{ArtworksBaseUrl}/banners/actors/5.jpg");
        normalized.Characters.Single().PersonImageUrl.Should().Be($"{ArtworksBaseUrl}/banners/actors/5-person.jpg");
    }

    [Test]
    public void Episode_WithNormalizedImages_Should_NormalizeRelativeImage()
    {
        var episode = new Episode { Id = 42, Image = "/banners/v4/episode/11884343/screencap/6a9a171d96372.jpg" };

        var normalized = episode.WithNormalizedImages();

        normalized.Image.Should().Be($"{ArtworksBaseUrl}/banners/v4/episode/11884343/screencap/6a9a171d96372.jpg");
    }

    [Test]
    public void Episode_WithNormalizedImages_Should_LeaveNullImage_Null()
    {
        var episode = new Episode { Id = 42, Image = null };

        var normalized = episode.WithNormalizedImages();

        normalized.Image.Should().BeNull();
    }
}
