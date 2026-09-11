using AwesomeAssertions;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Common;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;
using Recall.Web.Mappings;

namespace Recall.Tests.Mappings;

[TestFixture]
public class SeriesDataDtoMappingsTests
{
    private const string ArtworksBaseUrl = "https://artworks.thetvdb.com";

    [Test]
    public void ToAggregate_Should_NormalizeSeriesImageUrl_WhenRelative()
    {
        var dto = new SeriesDataDto { Id = 1, Name = "Show", Image = "/banners/series/1.jpg" };

        var aggregate = dto.ToAggregate();

        aggregate.ImageUrl.Should().Be($"{ArtworksBaseUrl}/banners/series/1.jpg");
    }

    [Test]
    public void ToAggregate_Should_LeaveAbsoluteSeriesImageUrl_Unchanged()
    {
        const string absolute = "https://example.test/already-absolute.jpg";
        var dto = new SeriesDataDto { Id = 1, Name = "Show", Image = absolute };

        var aggregate = dto.ToAggregate();

        aggregate.ImageUrl.Should().Be(absolute);
    }

    [Test]
    public void ToAggregate_Should_NormalizeSeasonImageUrl()
    {
        var dto = new SeriesDataDto
        {
            Id = 1,
            Name = "Show",
            Seasons = [new SeasonDto { Id = 100, Number = 1, Image = "/banners/seasons/100.jpg" }]
        };

        var aggregate = dto.ToAggregate();

        aggregate.Seasons.Should().ContainSingle()
            .Which.ImageUrl.Should().Be($"{ArtworksBaseUrl}/banners/seasons/100.jpg");
    }

    [Test]
    public void ToAggregate_Should_NormalizeEpisodeImage()
    {
        var dto = new SeriesDataDto
        {
            Id = 1,
            Name = "Show",
            Episodes = [new EpisodeDto { Id = 10, SeasonNumber = 1, Number = 1, Image = "/banners/episodes/10.jpg" }]
        };

        var aggregate = dto.ToAggregate();

        aggregate.Episodes.Should().ContainSingle()
            .Which.Image.Should().Be($"{ArtworksBaseUrl}/banners/episodes/10.jpg");
    }

    [Test]
    public void ToAggregate_Should_NormalizeCharacterImageAndPersonImage()
    {
        var dto = new SeriesDataDto
        {
            Id = 1,
            Name = "Show",
            Characters =
            [
                new CharacterDataDto
                {
                    Id = 5,
                    Image = "/banners/actors/5.jpg",
                    PersonImgUrl = "/banners/actors/5-person.jpg"
                }
            ]
        };

        var aggregate = dto.ToAggregate();

        var character = aggregate.Characters.Should().ContainSingle().Which;
        character.Image.Should().Be($"{ArtworksBaseUrl}/banners/actors/5.jpg");
        character.PersonImageUrl.Should().Be($"{ArtworksBaseUrl}/banners/actors/5-person.jpg");
    }

    [Test]
    public void ToDomain_CharacterDataDto_Should_NormalizeImages()
    {
        var dto = new CharacterDataDto
        {
            Id = 5,
            Image = "/banners/actors/5.jpg",
            PersonImgUrl = "/banners/actors/5-person.jpg"
        };

        var character = dto.ToDomain();

        character.Image.Should().Be($"{ArtworksBaseUrl}/banners/actors/5.jpg");
        character.PersonImageUrl.Should().Be($"{ArtworksBaseUrl}/banners/actors/5-person.jpg");
    }
}
