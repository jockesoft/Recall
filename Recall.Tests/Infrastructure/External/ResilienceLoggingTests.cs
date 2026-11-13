using System.Net;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.External;
using Recall.Web.Services.External.Omdb;
using Recall.Web.Services.External.TheTvDb;
using Recall.Tests.TestSupport;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Recall.Tests.Infrastructure.External;

/// <summary>
/// What the resilience pipelines write to the log, through the Serilog levels
/// the app ships with. The clients come from the real registrations with only
/// the socket swapped, and the logger factory is a Serilog one, as
/// <c>UseSerilog</c> makes it, with the <c>Serilog:MinimumLevel</c> section of
/// the real appsettings files and an in-memory sink (<see cref="ShippedSerilog"/>).
/// </summary>
[TestFixture]
public class ResilienceLoggingTests
{
    private const string SearchJson = """{ "status": "success", "data": [] }""";
    private const string LoginJson = """{ "status": "success", "data": { "token": "t" } }""";
    private const string OmdbJson = """{ "Title": "Example", "Response": "True" }""";

    private sealed record PollyEntry(LogEventLevel Level, string Source, string Message);

    /// <summary>What reached the sink from Polly's telemetry (category <c>Polly</c>), in order.</summary>
    private static IReadOnlyList<PollyEntry> PollyEntries(InMemorySink sink) =>
        sink.Events
            .Where(e => InMemorySink.SourceContext(e) == "Polly")
            .Select(e => new PollyEntry(
                e.Level,
                // The "Source" of Polly's message: pipeline/instance/strategy.
                $"{InMemorySink.Scalar(e, "PipelineName")}/{InMemorySink.Scalar(e, "PipelineInstance")}/{InMemorySink.Scalar(e, "StrategyName")}",
                e.RenderMessage()))
            .ToList();

    /// <summary>Answers the TheTVDB login, then replays the scripted data responses (repeating the last).</summary>
    private sealed class ScriptedHandler(params Func<HttpResponseMessage>[] dataResponses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _dataResponses = new(dataResponses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
                return Task.FromResult(Json(LoginJson));

            var next = _dataResponses.Count > 1 ? _dataResponses.Dequeue() : _dataResponses.Peek();
            return Task.FromResult(next());
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(HttpStatusCode status) =>
        new(status) { Content = new StringContent("") };

    private static ServiceProvider BuildServices(ScriptedHandler handler, Logger logger)
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
        // What UseSerilog does: Serilog's factory replaces Microsoft's, so its levels are the only filter.
        services.AddSingleton<ILoggerFactory>(new SerilogLoggerFactory(logger, dispose: true));
        services.AddTheTvDb(configuration);
        services.AddOmdb(configuration);
        services.Configure<ExternalHttpResilienceOptions>(o => o.RetryBaseDelay = TimeSpan.FromMilliseconds(1));
        services.AddHttpClient<ITheTvDbApiClient, TheTvDbApiClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddHttpClient<IOmdbApiClient, OmdbApiClient>().ConfigurePrimaryHttpMessageHandler(() => handler);

        return services.BuildServiceProvider();
    }

    private static readonly string?[] Environments = ShippedSerilog.Environments;

    private static async Task<IReadOnlyList<PollyEntry>> RunAsync(
        string? environment, Func<IServiceProvider, Task> call, params Func<HttpResponseMessage>[] script)
    {
        var sink = new InMemorySink();
        await using (var services = BuildServices(new ScriptedHandler(script), ShippedSerilog.CreateLogger(sink, environment)))
        {
            await call(services);
        } // disposing the provider disposes the logger, which flushes the sink

        return PollyEntries(sink);
    }

    private static Task TheTvDbSearch(IServiceProvider services) =>
        services.GetRequiredService<ITheTvDbApiClient>().SearchAsync("anything");

    private static Task OmdbMovie(IServiceProvider services) =>
        services.GetRequiredService<IOmdbApiClient>().GetMovieAsync("tt0000001");

    [TestCaseSource(nameof(Environments))]
    public async Task TheTvDb_SuccessfulCall_Should_LogNothingFromPolly(string? environment)
    {
        var entries = await RunAsync(environment, TheTvDbSearch, () => Json(SearchJson));

        // Polly writes "Execution attempt ... Handled: 'False'" at Information for every success.
        entries.Should().BeEmpty();
    }

    [TestCaseSource(nameof(Environments))]
    public async Task TheTvDb_RetriedCall_Should_LogTheFailedAttemptAndTheRetry_AtWarning(string? environment)
    {
        var entries = await RunAsync(environment, TheTvDbSearch,
            () => Status(HttpStatusCode.ServiceUnavailable),
            () => Json(SearchJson));

        entries.Should().HaveCount(2);
        entries.Should().OnlyContain(e => e.Level == LogEventLevel.Warning);
        entries.Should().OnlyContain(e => e.Source == "thetvdb/standard/Retry");
        entries[0].Message.Should().StartWith("Execution attempt.").And.Contain("503");
        entries[1].Message.Should().StartWith("Resilience event occurred.").And.Contain("OnRetry");
    }

    [TestCaseSource(nameof(Environments))]
    public async Task TheTvDb_FailedCall_Should_LogEachRetryAtWarning_AndTheLastAttemptAtError(string? environment)
    {
        var entries = await RunAsync(environment,
            async services =>
            {
                var act = () => TheTvDbSearch(services);
                await act.Should().ThrowAsync<TheTvDbApiException>();
            },
            () => Status(HttpStatusCode.ServiceUnavailable));

        var retries = ExternalHttpResilience.TheTvDbMaxRetries;
        entries.Should().HaveCount(retries * 2 + 1, "an attempt and an OnRetry per retry, then the last attempt");
        entries.Take(retries * 2).Should().OnlyContain(e => e.Level == LogEventLevel.Warning);
        entries[^1].Level.Should().Be(LogEventLevel.Error);
        entries[^1].Message.Should().StartWith("Execution attempt.").And.Contain("503");
        entries.Should().OnlyContain(e => e.Source == "thetvdb/standard/Retry");
    }

    [TestCaseSource(nameof(Environments))]
    public async Task Omdb_SuccessfulCall_Should_LogNothingFromPolly(string? environment)
    {
        var entries = await RunAsync(environment, OmdbMovie, () => Json(OmdbJson));

        entries.Should().BeEmpty();
    }

    [TestCaseSource(nameof(Environments))]
    public async Task Omdb_RetriedCall_Should_LogTheFailedAttemptAndTheRetry_AtWarning(string? environment)
    {
        var entries = await RunAsync(environment, OmdbMovie,
            () => Status(HttpStatusCode.ServiceUnavailable),
            () => Json(OmdbJson));

        entries.Should().HaveCount(2);
        entries.Should().OnlyContain(e => e.Level == LogEventLevel.Warning);
        entries.Should().OnlyContain(e => e.Source == "omdb/standard/Retry");
    }
}
