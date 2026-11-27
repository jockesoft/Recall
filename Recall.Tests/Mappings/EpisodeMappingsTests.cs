using AwesomeAssertions;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Common;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Episodes;
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

    [Test]
    public void ToDomain_Should_KeepThePeopleOfTheExtendedRecord_WithNormalizedPhotos()
    {
        var dto = new EpisodeExtendedDto
        {
            Id = 10,
            Characters =
            [
                new CharacterDataDto { Id = 1, PeopleId = 7, PersonName = "Danny Trejo", PeopleType = "Guest Star", Name = "Tortuga", PersonImgUrl = "/banners/person/7.jpg", Sort = 2 },
                new CharacterDataDto { Id = 0, PersonName = "No id", PeopleType = "Guest Star" },
            ]
        };

        var episode = dto.ToDomain();

        episode.Characters.Should().ContainSingle("a row without an id is not a credit");
        var person = episode.Characters[0];
        person.PersonName.Should().Be("Danny Trejo");
        person.PeopleType.Should().Be("Guest Star");
        person.Name.Should().Be("Tortuga");
        person.PersonImageUrl.Should().Be("https://artworks.thetvdb.com/banners/person/7.jpg");
    }

    [Test]
    public void ToDomain_Should_HaveNoPeople_WhenTheRecordListsNone()
    {
        new EpisodeExtendedDto { Id = 10 }.ToDomain().Characters.Should().BeEmpty();
    }

    [Test]
    public void ACachedRecord_Should_KeepItsPeople_ThroughTheJsonPayload_AndAnOldOne_ShouldHaveNone()
    {
        var episode = new EpisodeExtendedDto
        {
            Id = 10,
            Characters = [new CharacterDataDto { Id = 1, PeopleId = 7, PersonName = "Danny Trejo", PeopleType = "Guest Star" }]
        }.ToDomain();

        var json = System.Text.Json.JsonSerializer.Serialize(episode, Recall.Web.Infrastructure.RecallJsonOptions.Web);
        System.Text.Json.JsonSerializer.Deserialize<Recall.Web.Domain.TheTvDb.Episode>(json, Recall.Web.Infrastructure.RecallJsonOptions.Web)!
            .Characters.Should().ContainSingle().Which.PersonName.Should().Be("Danny Trejo");

        // A record cached before the field existed.
        System.Text.Json.JsonSerializer.Deserialize<Recall.Web.Domain.TheTvDb.Episode>("""{"id":10,"name":"Pilot"}""", Recall.Web.Infrastructure.RecallJsonOptions.Web)!
            .Characters.Should().BeEmpty();
    }
}
