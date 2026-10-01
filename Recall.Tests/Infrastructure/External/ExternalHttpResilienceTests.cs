using System.Net;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.External;
using Recall.Web.Services.External.Omdb;
using Recall.Web.Services.External.TheTvDb;

namespace Recall.Tests.Infrastructure.External;

/// <summary>
/// Resolves the typed clients from the real registrations (<c>AddTheTvDb</c> /
/// <c>AddOmdb</c>) with only the socket swapped for a scripted handler, so the
/// retry pipeline under test is the one production runs.
/// </summary>
[TestFixture]
public class ExternalHttpResilienceTests
{
    private const string SearchJson = """{ "status": "success", "data": [] }""";
    private const string LoginJson = """{ "status": "success", "data": { "token": "t" } }""";
    private const string OmdbJson = """{ "Title": "Example", "Response": "True" }""";

    /// <summary>Replays a scripted list of responses per request kind and counts what it was sent.</summary>
    private sealed class ScriptedHandler(params Func<HttpResponseMessage>[] dataResponses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _dataResponses = new(dataResponses);

        public Func<HttpResponseMessage> Login { get; init; } = () => Json(LoginJson);
        public int LoginAttempts { get; private set; }
        public int DataAttempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                LoginAttempts++;
                return Task.FromResult(Login());
            }

            DataAttempts++;
            // Past the end of the script, keep repeating the last response.
            var next = _dataResponses.Count > 1 ? _dataResponses.Dequeue() : _dataResponses.Peek();
            return Task.FromResult(next());
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(HttpStatusCode status, int? retryAfterSeconds = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("") };
        if (retryAfterSeconds is { } seconds)
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(seconds));
        return response;
    }

    private static ServiceProvider BuildServices(ScriptedHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TheTvDb:ApiKey"] = "key",
                ["TheTvDb:BaseUrl"] = "https://tvdb.test/v4/",
                ["Omdb:ApiKey"] = "key",
                ["Omdb:BaseUrl"] = "https://omdb.test/"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTheTvDb(configuration);
        services.AddOmdb(configuration);
        services.Configure<ExternalHttpResilienceOptions>(o => o.RetryBaseDelay = TimeSpan.FromMilliseconds(1));

        // Same named clients the lines above registered; only the transport changes.
        services.AddHttpClient<ITheTvDbApiClient, TheTvDbApiClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddHttpClient<IOmdbApiClient, OmdbApiClient>().ConfigurePrimaryHttpMessageHandler(() => handler);

        return services.BuildServiceProvider();
    }

    [Test]
    public async Task TheTvDb_Should_RetryATransientServerError_AndSucceed()
    {
        var handler = new ScriptedHandler(
            () => Status(HttpStatusCode.ServiceUnavailable),
            () => Status(HttpStatusCode.BadGateway),
            () => Json(SearchJson));
        await using var services = BuildServices(handler);

        var results = await services.GetRequiredService<ITheTvDbApiClient>().SearchAsync("anything");

        results.Should().BeEmpty();
        handler.DataAttempts.Should().Be(3);
    }

    [Test]
    public async Task TheTvDb_Should_RetryA429_WhenTheRequestedWaitIsShort()
    {
        var handler = new ScriptedHandler(
            () => Status(HttpStatusCode.TooManyRequests, retryAfterSeconds: 1),
            () => Json(SearchJson));
        await using var services = BuildServices(handler);

        await services.GetRequiredService<ITheTvDbApiClient>().SearchAsync("anything");

        handler.DataAttempts.Should().Be(2);
    }

    [Test]
    public async Task TheTvDb_Should_NotRetryA429_ThatAsksForALongWait()
    {
        var handler = new ScriptedHandler(() => Status(HttpStatusCode.TooManyRequests, retryAfterSeconds: 120));
        await using var services = BuildServices(handler);

        var act = () => services.GetRequiredService<ITheTvDbApiClient>().SearchAsync("anything");

        (await act.Should().ThrowAsync<TheTvDbApiException>()).Which.StatusCode.Should().Be(429);
        handler.DataAttempts.Should().Be(1, "retrying sooner than the server asked would only dig the hole deeper");
    }

    [Test]
    public async Task TheTvDb_Should_GiveUp_AfterThreeRetries()
    {
        var handler = new ScriptedHandler(() => Status(HttpStatusCode.ServiceUnavailable));
        await using var services = BuildServices(handler);

        var act = () => services.GetRequiredService<ITheTvDbApiClient>().SearchAsync("anything");

        (await act.Should().ThrowAsync<TheTvDbApiException>()).Which.StatusCode.Should().Be(503);
        handler.DataAttempts.Should().Be(4);
    }

    [Test]
    public async Task TheTvDb_Should_NotRetry_AClientError()
    {
        var handler = new ScriptedHandler(() => Status(HttpStatusCode.NotFound));
        await using var services = BuildServices(handler);

        var act = () => services.GetRequiredService<ITheTvDbApiClient>().SearchAsync("anything");

        await act.Should().ThrowAsync<TheTvDbApiException>();
        handler.DataAttempts.Should().Be(1);
    }

    [Test]
    public async Task TheTvDb_Should_NotRetry_TheLoginPost()
    {
        var handler = new ScriptedHandler(() => Json(SearchJson))
        {
            Login = () => Status(HttpStatusCode.ServiceUnavailable)
        };
        await using var services = BuildServices(handler);

        var act = () => services.GetRequiredService<ITheTvDbApiClient>().SearchAsync("anything");

        await act.Should().ThrowAsync<TheTvDbApiException>();
        handler.LoginAttempts.Should().Be(1);
        handler.DataAttempts.Should().Be(0);
    }

    [Test]
    public async Task Omdb_Should_RetryATransientServerError_Once()
    {
        var handler = new ScriptedHandler(
            () => Status(HttpStatusCode.ServiceUnavailable),
            () => Json(OmdbJson));
        await using var services = BuildServices(handler);

        var result = await services.GetRequiredService<IOmdbApiClient>().GetByImdbIdAsync("tt0000001");

        result!.Title.Should().Be("Example");
        handler.DataAttempts.Should().Be(2);
    }

    [Test]
    public async Task Omdb_Should_MakeAtMostTwoAttempts()
    {
        var handler = new ScriptedHandler(() => Status(HttpStatusCode.ServiceUnavailable));
        await using var services = BuildServices(handler);

        var act = () => services.GetRequiredService<IOmdbApiClient>().GetByImdbIdAsync("tt0000001");

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.DataAttempts.Should().Be(2);
    }

    [Test]
    public async Task Omdb_Should_NeverRetryA429()
    {
        // With or without a Retry-After: a 429 from OMDb is the daily quota, and
        // a retry would be a request the shared budget never counted.
        foreach (var retryAfter in new int?[] { null, 1 })
        {
            var handler = new ScriptedHandler(() => Status(HttpStatusCode.TooManyRequests, retryAfter));
            await using var services = BuildServices(handler);

            var act = () => services.GetRequiredService<IOmdbApiClient>().GetByImdbIdAsync("tt0000001");

            await act.Should().ThrowAsync<HttpRequestException>();
            handler.DataAttempts.Should().Be(1);
        }
    }
}
