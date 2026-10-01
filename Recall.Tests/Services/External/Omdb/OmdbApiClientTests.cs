using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Recall.Tests.TestSupport;
using Recall.Web.Infrastructure.External.Omdb;
using Recall.Web.Services.External.Omdb;

namespace Recall.Tests.Services.External.Omdb;

[TestFixture]
public class OmdbApiClientTests
{
    private const string Found = """
        {"Title":"Silo","Year":"2023–","Genre":"Drama, Sci-Fi","imdbRating":"8.1","imdbVotes":"200,000",
         "imdbID":"tt14688458","Type":"series","totalSeasons":"3",
         "Ratings":[{"Source":"Internet Movie Database","Value":"8.1/10"}],"Response":"True"}
        """;

    private const string NotFound = """{"Response":"False","Error":"Incorrect IMDb ID."}""";

    private static OmdbApiClient Create(StubHttpMessageHandler handler, string apiKey = "the-key") =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://omdb.test/") },
            Options.Create(new OmdbOptions { ApiKey = apiKey }),
            NullLogger<OmdbApiClient>.Instance);

    // ---- found -------------------------------------------------------------------

    [Test]
    public async Task GetSeriesAsync_Should_ReturnTheParsedRecord_WhenOmdbHasTheTitle()
    {
        var handler = StubHttpMessageHandler.Json(Found);

        var result = await Create(handler).GetSeriesAsync("tt14688458");

        result!.Title.Should().Be("Silo");
        result.ImdbRating.Should().Be("8.1");
        result.ImdbVotes.Should().Be("200,000");
        result.TotalSeasons.Should().Be("3");
        result.Ratings.Should().ContainSingle().Which.Value.Should().Be("8.1/10");
        result.IsSuccess.Should().BeTrue();
    }

    [Test]
    public async Task EachLookup_Should_SendTheKey_TheImdbId_AndItsOwnTypeFilter()
    {
        var handler = StubHttpMessageHandler.Json(Found);
        var sut = Create(handler);

        await sut.GetSeriesAsync("tt1");
        await sut.GetMovieAsync("tt2");
        await sut.GetEpisodeAsync("tt3");

        var queries = handler.Requests.Select(r => r.Uri!.Query).ToList();
        queries[0].Should().Be("?apikey=the-key&i=tt1&r=json", "a series lookup sends no type filter");
        queries[1].Should().Be("?apikey=the-key&i=tt2&r=json&type=movie");
        queries[2].Should().Be("?apikey=the-key&i=tt3&r=json&type=episode");
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Test]
    public async Task GetMovieAsync_And_GetEpisodeAsync_Should_ReturnTheirOwnTypes()
    {
        var sut = Create(StubHttpMessageHandler.Json(Found));

        (await sut.GetMovieAsync("tt1"))!.Title.Should().Be("Silo");
        (await sut.GetEpisodeAsync("tt1"))!.ImdbRating.Should().Be("8.1");
    }

    // ---- not found -----------------------------------------------------------------

    [Test]
    public async Task Should_ReturnNull_WhenOmdbSaysItHasNothingForTheId()
    {
        var sut = Create(StubHttpMessageHandler.Json(NotFound));

        (await sut.GetSeriesAsync("tt0000000")).Should().BeNull("OMDb answers 200 with Response:False for an unknown id");
    }

    [Test]
    public async Task Should_ReturnNull_WhenTheBodyIsNotJson()
    {
        var sut = Create(StubHttpMessageHandler.Json("<html>maintenance</html>"));

        (await sut.GetMovieAsync("tt1")).Should().BeNull();
    }

    // ---- errors ------------------------------------------------------------------

    [Test]
    public async Task Should_Throw_WhenOmdbRejectsTheApiKey()
    {
        var sut = Create(StubHttpMessageHandler.Json("""{"Response":"False","Error":"Invalid API key!"}""", HttpStatusCode.Unauthorized));

        var act = () => sut.GetSeriesAsync("tt1");

        (await act.Should().ThrowAsync<HttpRequestException>()).WithMessage("*API key*");
    }

    [TestCase(HttpStatusCode.InternalServerError)]
    [TestCase(HttpStatusCode.ServiceUnavailable)]
    [TestCase(HttpStatusCode.TooManyRequests)]
    public async Task Should_Throw_OnAnErrorStatus_SoTheCallerDoesNotStoreItAsNotFound(HttpStatusCode status)
    {
        var sut = Create(StubHttpMessageHandler.Json(NotFound, status));

        var act = () => sut.GetEpisodeAsync("tt1");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Test]
    public async Task Should_LetANetworkFailurePropagate()
    {
        var sut = Create(StubHttpMessageHandler.Throwing(new HttpRequestException("connection refused")));

        var act = () => sut.GetSeriesAsync("tt1");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Test]
    public async Task Should_Throw_WithoutCallingOmdb_WhenNoApiKeyIsConfigured()
    {
        var handler = StubHttpMessageHandler.Json(Found);
        var sut = Create(handler, apiKey: "");

        var act = () => sut.GetSeriesAsync("tt1");

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Requests.Should().BeEmpty();
    }

    [TestCase("")]
    [TestCase("  ")]
    public async Task Should_Throw_WithoutCallingOmdb_ForABlankImdbId(string imdbId)
    {
        var handler = StubHttpMessageHandler.Json(Found);
        var sut = Create(handler);

        var act = () => sut.GetSeriesAsync(imdbId);

        await act.Should().ThrowAsync<ArgumentException>();
        handler.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task Should_EscapeTheKeyAndTheId_InTheQueryString()
    {
        var handler = StubHttpMessageHandler.Json(Found);
        var sut = Create(handler, apiKey: "a&b=c");

        await sut.GetSeriesAsync("tt1&type=movie");

        handler.Requests.Single().Uri!.Query.Should().Be("?apikey=a%26b%3Dc&i=tt1%26type%3Dmovie&r=json");
    }
}
