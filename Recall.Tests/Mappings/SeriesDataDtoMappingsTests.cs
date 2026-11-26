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
    public void ToAggregate_Should_MapGenres_TrimmedAndDeduplicated()
    {
        var dto = new SeriesDataDto
        {
            Id = 1,
            Name = "Show",
            Genres =
            [
                new GenreDto { Id = 1, Name = "Drama" },
                new GenreDto { Id = 2, Name = " Thriller " },
                new GenreDto { Id = 3, Name = "drama" },
                new GenreDto { Id = 4, Name = " " }
            ]
        };

        dto.ToAggregate().Genres.Should().Equal("Drama", "Thriller");
    }

    [Test]
    public void ToAggregate_Should_HaveNoGenres_WhenTheTvDbSendsNone()
    {
        new SeriesDataDto { Id = 1, Name = "Show" }.ToAggregate().Genres.Should().BeEmpty();
    }

    [Test]
    public void ToAggregate_Should_KeepTheOriginalNetwork_AndFallBackToTheLatest()
    {
        var both = new SeriesDataDto
        {
            Id = 1,
            Name = "Show",
            OriginalNetwork = new CompanyDto { Id = 7, Name = " Discovery " },
            LatestNetwork = new CompanyDto { Id = 8, Name = "Max" }
        };
        var latestOnly = new SeriesDataDto
        {
            Id = 2,
            Name = "Show",
            OriginalNetwork = new CompanyDto { Id = 7, Name = " " },
            LatestNetwork = new CompanyDto { Id = 8, Name = "Max" }
        };

        both.ToAggregate().Network.Should().Be("Discovery");
        latestOnly.ToAggregate().Network.Should().Be("Max");
        new SeriesDataDto { Id = 3, Name = "Show" }.ToAggregate().Network.Should().BeNull();
    }

    [Test]
    public void TheNetwork_Should_BeReadFromTheExtendedRecord()
    {
        const string json = """{ "id": 78957, "name": "Deadliest Catch", "originalNetwork": { "id": 15, "name": "Discovery" }, "latestNetwork": { "id": 15, "name": "Discovery" } }""";

        var dto = System.Text.Json.JsonSerializer.Deserialize<SeriesDataDto>(json)!;

        dto.ToAggregate().Network.Should().Be("Discovery");
    }

    [Test]
    public void ToAggregate_Should_PickTheBestScoredBackground_AsItsThumbnail()
    {
        var dto = new SeriesDataDto
        {
            Id = 1,
            Name = "Show",
            Artworks =
            [
                new ArtworkDto { Id = 1, Type = 2, Score = 900, Image = "/banners/posters/1.jpg" },                       // a poster
                new ArtworkDto { Id = 2, Type = 3, Score = 10, Image = "/banners/fanart/low.jpg", Thumbnail = "/banners/fanart/low_t.jpg" },
                new ArtworkDto { Id = 3, Type = 3, Score = 50, Image = "/banners/fanart/best.jpg", Thumbnail = "/banners/fanart/best_t.jpg" },
            ]
        };

        dto.ToAggregate().BackgroundUrl.Should().Be($"{ArtworksBaseUrl}/banners/fanart/best_t.jpg");
    }

    [Test]
    public void ToAggregate_Should_UseTheFullBackground_WhenItHasNoThumbnail_AndNone_WhenThereIsNoBackground()
    {
        var withFull = new SeriesDataDto
        {
            Id = 1, Name = "Show",
            Artworks = [new ArtworkDto { Id = 3, Type = 3, Image = "https://example.test/fanart.jpg", Thumbnail = " " }]
        };
        var posterOnly = new SeriesDataDto
        {
            Id = 1, Name = "Show",
            Artworks = [new ArtworkDto { Id = 1, Type = 2, Image = "/banners/posters/1.jpg" }]
        };

        withFull.ToAggregate().BackgroundUrl.Should().Be("https://example.test/fanart.jpg");
        posterOnly.ToAggregate().BackgroundUrl.Should().BeNull();
        new SeriesDataDto { Id = 1, Name = "Show" }.ToAggregate().BackgroundUrl.Should().BeNull();
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

    // ---- the overview when the English translation is missing ----------------------

    [Test]
    public void ToAggregate_Should_UseTheTranslatedOverview_WhenThereIsOne()
    {
        var dto = new SeriesDataDto { Id = 1, Name = "Bron", OriginalLanguage = "swe", Overview = "En kropp hittas på bron." };

        var aggregate = dto.ToAggregate(new SeriesTranslationDataDto { Name = "The Bridge", Overview = " A body is found on the bridge. " });

        aggregate.Overview.Should().Be("A body is found on the bridge.");
    }

    [TestCase(null)]
    [TestCase("")]
    public void ToAggregate_Should_UseTheOriginalOverview_OfAnEnglishSeries_WhenTheTranslationHasNone(string? translatedOverview)
    {
        var dto = new SeriesDataDto { Id = 1, Name = "Severance", OriginalLanguage = "eng", Overview = " Mark leads a team of office workers. " };
        var translation = translatedOverview is null ? null : new SeriesTranslationDataDto { Name = "Severance", Overview = translatedOverview };

        var aggregate = dto.ToAggregate(translation);

        aggregate.Overview.Should().Be("Mark leads a team of office workers.",
            "the record's own overview is in English, so it serves when the translation failed or timed out");
    }

    [TestCase("swe")]
    [TestCase("deu")]
    [TestCase(null)]
    public void ToAggregate_Should_LeaveTheOverviewEmpty_ForASeriesNotInEnglish_WhenTheTranslationIsMissing(string? originalLanguage)
    {
        var dto = new SeriesDataDto { Id = 1, Name = "Bron", OriginalLanguage = originalLanguage, Overview = "En kropp hittas på bron." };

        var aggregate = dto.ToAggregate();

        aggregate.Overview.Should().BeNull("an overview in another language is not shown in its place");
    }

    [Test]
    public void ToAggregate_Should_LeaveTheOverviewEmpty_ForAnEnglishSeriesWithNoOverviewAtAll()
    {
        new SeriesDataDto { Id = 1, Name = "Show", OriginalLanguage = "eng", Overview = "  " }.ToAggregate().Overview.Should().BeNull();
    }
}
