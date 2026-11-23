using AwesomeAssertions;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Common;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Movies;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;
using Recall.Web.Mappings;

namespace Recall.Tests.Mappings;

[TestFixture]
public class MovieDataDtoMappingsTests
{
    private const string ArtworksBaseUrl = "https://artworks.thetvdb.com";

    [Test]
    public void ToAggregate_Should_NormalizeMovieImageUrl_WhenRelative()
    {
        var dto = new MovieDataDto { Id = 1, Name = "Oppenheimer", Image = "/banners/v4/movie/1/posters/1.jpg" };

        var aggregate = dto.ToAggregate();

        aggregate.ImageUrl.Should().Be($"{ArtworksBaseUrl}/banners/v4/movie/1/posters/1.jpg");
    }

    [Test]
    public void ToAggregate_Should_PickTheBestScoredBackground_AsItsThumbnail()
    {
        var dto = new MovieDataDto
        {
            Id = 1305,
            Name = "Heat",
            Artworks =
            [
                new ArtworkDto { Id = 1, Type = 14, Score = 900, Image = "/banners/movies/1305/posters/1305.jpg" },
                new ArtworkDto { Id = 2, Type = 15, Score = 10, Image = "/banners/movies/1305/backgrounds/low.jpg", Thumbnail = "/banners/movies/1305/backgrounds/low_t.jpg" },
                new ArtworkDto { Id = 3, Type = 15, Score = 100, Image = "/banners/movies/1305/backgrounds/1305.jpg", Thumbnail = "/banners/movies/1305/backgrounds/1305_t.jpg" }
            ]
        };

        dto.ToAggregate().BackgroundUrl.Should().Be($"{ArtworksBaseUrl}/banners/movies/1305/backgrounds/1305_t.jpg");
    }

    [Test]
    public void ToAggregate_Should_UseTheFullBackground_WithoutAThumbnail_AndNone_WithoutABackground()
    {
        var full = new MovieDataDto { Id = 1, Name = "Movie", Artworks = [new ArtworkDto { Id = 1, Type = 15, Image = "https://example.test/background.jpg" }] };
        var posterOnly = new MovieDataDto { Id = 2, Name = "Movie", Artworks = [new ArtworkDto { Id = 1, Type = 14, Image = "https://example.test/poster.jpg" }] };

        full.ToAggregate().BackgroundUrl.Should().Be("https://example.test/background.jpg");
        posterOnly.ToAggregate().BackgroundUrl.Should().BeNull("a series' background type (3) and a poster are not a movie's background");
    }

    [Test]
    public void ToAggregate_Should_KeepTheFirstReleasesDetail_WhenTheTvDbGivesOne()
    {
        new MovieDataDto { Id = 1, Name = "Movie", FirstRelease = new MovieReleaseDto { Date = "2026-03-06", Detail = " Theatrical " } }
            .ToAggregate().ReleaseDetail.Should().Be("Theatrical");
        new MovieDataDto { Id = 1, Name = "Movie", FirstRelease = new MovieReleaseDto { Date = "2026-03-06" } }
            .ToAggregate().ReleaseDetail.Should().BeNull();
    }

    [Test]
    public void ToAggregate_Should_PreferTranslatedNameAndOverview_OverRawDtoValues()
    {
        var dto = new MovieDataDto { Id = 1, Name = "Raw Name" };
        var translation = new SeriesTranslationDataDto { Name = "Translated Name", Overview = "Translated overview." };

        var aggregate = dto.ToAggregate(translation);

        aggregate.Name.Should().Be("Translated Name");
        aggregate.Overview.Should().Be("Translated overview.");
    }

    [Test]
    public void ToAggregate_Should_FallBackToRawName_WhenNoTranslation()
    {
        var dto = new MovieDataDto { Id = 1, Name = "Raw Name" };

        var aggregate = dto.ToAggregate();

        aggregate.Name.Should().Be("Raw Name");
        aggregate.Overview.Should().BeNull();
    }

    [Test]
    public void ToAggregate_Should_MapGenresAndDeduplicate()
    {
        var dto = new MovieDataDto
        {
            Id = 1,
            Name = "Movie",
            Genres =
            [
                new GenreDto { Name = "Drama" },
                new GenreDto { Name = "drama" },
                new GenreDto { Name = "Thriller" }
            ]
        };

        var aggregate = dto.ToAggregate();

        aggregate.Genres.Should().BeEquivalentTo(["Drama", "Thriller"]);
    }

    [Test]
    public void ToAggregate_Should_MapStudiosFromCompaniesStudio()
    {
        var dto = new MovieDataDto
        {
            Id = 1,
            Name = "Movie",
            Companies = new CompaniesDto
            {
                Studio = [new CompanyDto { Name = "Universal Pictures" }]
            }
        };

        var aggregate = dto.ToAggregate();

        aggregate.Studios.Should().ContainSingle().Which.Should().Be("Universal Pictures");
    }

    [Test]
    public void ToAggregate_Should_MapImdbRemoteId()
    {
        var dto = new MovieDataDto
        {
            Id = 1,
            Name = "Movie",
            RemoteIds =
            [
                new RemoteIdDto { Id = "tt15398776", SourceName = "IMDB", Type = 2 },
                new RemoteIdDto { Id = "872585", SourceName = "TheMovieDB.com", Type = 10 }
            ]
        };

        var aggregate = dto.ToAggregate();

        aggregate.RemoteIds.Should().Contain(r => r.SourceName == "IMDB" && r.Id == "tt15398776");
    }

    [Test]
    public void ToAggregate_Should_ParseFirstReleaseDate()
    {
        var dto = new MovieDataDto
        {
            Id = 1,
            Name = "Movie",
            FirstRelease = new MovieReleaseDto { Country = "global", Date = "2023-07-19" }
        };

        var aggregate = dto.ToAggregate();

        aggregate.ReleaseDate.Should().Be(new DateOnly(2023, 7, 19));
    }

    [Test]
    public void ToAggregate_Should_ParseBudgetAndBoxOffice_AndTreatZeroOrBlankAsNull()
    {
        var dto = new MovieDataDto { Id = 1, Name = "Movie", Budget = "100000000.00", BoxOffice = "" };

        var aggregate = dto.ToAggregate();

        aggregate.Budget.Should().Be(100000000m);
        aggregate.BoxOffice.Should().BeNull();
    }

    [Test]
    public void ToAggregate_Should_NormalizeCharacterImageAndPersonImage()
    {
        var dto = new MovieDataDto
        {
            Id = 1,
            Name = "Movie",
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
    public void ToAggregate_Should_DeduplicateCharactersById_KeepingFirstOccurrence()
    {
        var dto = new MovieDataDto
        {
            Id = 1,
            Name = "Movie",
            Characters =
            [
                new CharacterDataDto { Id = 5, Name = "First" },
                new CharacterDataDto { Id = 5, Name = "Duplicate" }
            ]
        };

        var aggregate = dto.ToAggregate();

        aggregate.Characters.Should().ContainSingle().Which.Name.Should().Be("First");
    }
}
