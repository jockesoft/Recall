using System.Text.Json;
using AwesomeAssertions;
using Recall.Web.Domain.Omdb;
using Recall.Web.Infrastructure;

namespace Recall.Tests.Domain;

/// <summary>
/// The <c>cached_*_omdb.payload</c> columns hold OMDb records as JSON. Movie
/// and episode rows written before <see cref="OmdbMovie"/> and
/// <see cref="OmdbEpisode"/> existed were serialized from the series type, so
/// they carry a <c>totalSeasons</c> key (and movie rows carry OMDb fields the
/// app never mapped). Those rows must keep loading.
/// </summary>
[TestFixture]
public class OmdbTitleJsonTests
{
    // The key set found in real cached movie rows.
    private const string LegacyMoviePayload = """
        {"DVD": "N/A", "Plot": "A dramatization.", "Type": "movie", "Year": "2023", "Error": null,
         "Genre": "Biography, Drama, History", "Rated": "R", "Title": "Oppenheimer", "Actors": "Cillian Murphy",
         "Awards": "Won 7 Oscars.", "Poster": "https://example.test/p.jpg", "Writer": "Christopher Nolan",
         "imdbID": "tt15398776", "Country": "United States", "Ratings": [{"Value": "8.2/10", "Source": "Internet Movie Database"}],
         "Runtime": "180 min", "Website": "N/A", "Director": "Christopher Nolan", "Language": "English",
         "Released": "21 Jul 2023", "Response": "True", "BoxOffice": "$330,078,895", "Metascore": "90",
         "imdbVotes": "1,000,000", "Production": "N/A", "imdbRating": "8.2", "totalSeasons": null}
        """;

    private const string LegacyEpisodePayload = """
        {"Plot": "The pilot.", "Type": "episode", "Year": "2008", "Error": null, "Genre": "Crime, Drama",
         "Rated": "TV-MA", "Title": "Pilot", "Actors": "Bryan Cranston", "Awards": "N/A", "Poster": "N/A",
         "Writer": "Vince Gilligan", "imdbID": "tt0959621", "Country": "United States", "Ratings": [],
         "Runtime": "58 min", "Director": "Vince Gilligan", "Language": "English", "Released": "20 Jan 2008",
         "Response": "True", "Metascore": "N/A", "imdbVotes": "50,000", "imdbRating": "9.0", "totalSeasons": null}
        """;

    [Test]
    public void OmdbMovie_Should_ReadARowWrittenBeforeTheTypesWereSplit()
    {
        var movie = JsonSerializer.Deserialize<OmdbMovie>(LegacyMoviePayload, RecallJsonOptions.Web)!;

        movie.Title.Should().Be("Oppenheimer");
        movie.ImdbRating.Should().Be("8.2");
        movie.ImdbVotes.Should().Be("1,000,000");
        movie.ImdbId.Should().Be("tt15398776");
        movie.Awards.Should().Be("Won 7 Oscars.");
        movie.Ratings.Should().ContainSingle().Which.Value.Should().Be("8.2/10");
        movie.IsSuccess.Should().BeTrue();
    }

    [Test]
    public void OmdbEpisode_Should_ReadARowWrittenBeforeTheTypesWereSplit()
    {
        var episode = JsonSerializer.Deserialize<OmdbEpisode>(LegacyEpisodePayload, RecallJsonOptions.Web)!;

        episode.Title.Should().Be("Pilot");
        episode.ImdbRating.Should().Be("9.0");
        episode.ImdbVotes.Should().Be("50,000");
        episode.IsSuccess.Should().BeTrue();
    }

    [Test]
    public void EveryType_Should_WriteTheSameKeys_ItUsedToBeStoredWith()
    {
        var series = Keys(new OmdbSeries { Title = "x", TotalSeasons = "5" });
        var movie = Keys(new OmdbMovie { Title = "x" });
        var episode = Keys(new OmdbEpisode { Title = "x" });

        string[] shared =
        [
            "Title", "Year", "Rated", "Released", "Runtime", "Genre", "Director", "Writer", "Actors", "Plot",
            "Language", "Country", "Awards", "Poster", "Ratings", "Metascore", "imdbRating", "imdbVotes",
            "imdbID", "Type", "Response", "Error"
        ];

        series.Should().BeEquivalentTo([.. shared, "totalSeasons"]);
        movie.Should().BeEquivalentTo(shared, "the only difference from the old format is the series-only key");
        episode.Should().BeEquivalentTo(shared);
    }

    [Test]
    public void OmdbSeries_Should_RoundTrip_IncludingItsOwnField()
    {
        var json = JsonSerializer.Serialize(new OmdbSeries { Title = "Silo", TotalSeasons = "3", Response = "True" }, RecallJsonOptions.Web);

        var series = JsonSerializer.Deserialize<OmdbSeries>(json, RecallJsonOptions.Web)!;

        series.Title.Should().Be("Silo");
        series.TotalSeasons.Should().Be("3");
        series.IsSuccess.Should().BeTrue();
    }

    private static List<string> Keys<T>(T value) where T : OmdbTitle
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, RecallJsonOptions.Web));
        return document.RootElement.EnumerateObject().Select(p => p.Name).ToList();
    }
}
