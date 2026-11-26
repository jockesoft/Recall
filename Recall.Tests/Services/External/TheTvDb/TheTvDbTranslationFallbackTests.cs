using System.Net;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Recall.Tests.TestSupport;
using Recall.Web.Infrastructure.External;
using Recall.Web.Infrastructure.External.TheTvDb;
using Recall.Web.Services.External.TheTvDb;

namespace Recall.Tests.Services.External.TheTvDb;

/// <summary>
/// The best-effort translations inside a series or movie fetch (the series
/// translation, the translated episode list, the movie translation): when one
/// does not answer within its attempt, the title still comes back, with
/// exactly the fallback a failed translation gives (the original-language
/// names), and one Warning per fallback; the caller's own cancellation still
/// stops the fetch. The attempt timeout is the client's own, shrunk here.
/// </summary>
[TestFixture]
public sealed class TheTvDbTranslationFallbackTests
{
    private static readonly TimeSpan ShortAttempt = TimeSpan.FromMilliseconds(200);

    /// <summary>How an endpoint answers.</summary>
    private enum Answer { Ok, Fails, Hangs }

    private const string SeriesExtended = """
        {"status":"success","data":{
          "id":1,"name":"Bron","overview":"En kropp hittas på bron.","averageRuntime":58,
          "seasons":[{"id":1,"number":1}],
          "episodes":[{"id":10,"seasonNumber":1,"number":1,"name":"Avsnitt 1","overview":"Del ett.","aired":"2011-09-21","runtime":58}]
        }}
        """;

    private const string SeriesTranslation = """{"status":"success","data":{"name":"The Bridge","overview":"A body is found on the bridge."}}""";

    private const string EpisodesTranslated = """
        {"status":"success","data":{"episodes":[{"id":10,"seasonNumber":1,"number":1,"name":"Episode 1","overview":"Part one.","aired":"2011-09-21"}]},
         "links":{"next":null}}
        """;

    private const string MovieExtended = """{"status":"success","data":{"id":2,"name":"Jagten","year":"2012","runtime":115}}""";

    private const string MovieTranslation = """{"status":"success","data":{"name":"The Hunt","overview":"A teacher is accused."}}""";

    [Test]
    public async Task ASeriesWhoseTranslationsTimeOut_Should_StillComeBack_WithTheFallbackOfAFailedTranslation()
    {
        var failedLog = new RecordingLogger<TheTvDbApiClient>();
        var failed = await Client(new TheTvDbStub(translations: Answer.Fails), failedLog).GetSeriesAggregateByIdAsync(1);

        var log = new RecordingLogger<TheTvDbApiClient>();
        var timedOut = await Client(new TheTvDbStub(translations: Answer.Hangs), log).GetSeriesAggregateByIdAsync(1);

        timedOut.Should().NotBeNull("the series data is not lost to a slow translation");
        timedOut!.Name.Should().Be("Bron", "the original-language name, as when the translation fails");
        timedOut.Episodes.Should().ContainSingle().Which.Name.Should().Be("Avsnitt 1", "episodes keep their original names");
        timedOut.Should().BeEquivalentTo(failed, "a timed-out translation falls back exactly as a failed one does");

        Fallbacks(log).Should().HaveCount(2, "one Warning for the series translation and one for the episode list");
        Fallbacks(log).Should().Contain(m => m.Contains("Translation fetch failed or timed out for series 1"))
            .And.Contain(m => m.Contains("Translated episode list failed or timed out for series 1"));
    }

    [Test]
    public async Task AnEnglishSeriesWhoseTranslationTimesOut_Should_KeepItsOwnOverview()
    {
        var english = SeriesExtended.Replace("\"name\":\"Bron\",\"overview\":\"En kropp hittas på bron.\"",
            "\"name\":\"Severance\",\"overview\":\"Mark leads a team of office workers.\",\"originalLanguage\":\"eng\"");
        english.Should().Contain("originalLanguage", "the test's record must be an English one");

        var series = await Client(new TheTvDbStub(translations: Answer.Hangs, english), new RecordingLogger<TheTvDbApiClient>())
            .GetSeriesAggregateByIdAsync(1);

        series!.Name.Should().Be("Severance");
        series.Overview.Should().Be("Mark leads a team of office workers.", "the record's own overview is English");
    }

    [Test]
    public async Task ASwedishSeriesWhoseTranslationTimesOut_Should_HaveNoOverview()
    {
        var swedish = SeriesExtended.Replace("\"averageRuntime\":58", "\"averageRuntime\":58,\"originalLanguage\":\"swe\"");

        var series = await Client(new TheTvDbStub(translations: Answer.Hangs, swedish), new RecordingLogger<TheTvDbApiClient>())
            .GetSeriesAggregateByIdAsync(1);

        series!.Overview.Should().BeNull("a Swedish overview is not shown in place of the English one");
    }

    [Test]
    public async Task ASeriesWithItsTranslations_Should_UseThem()
    {
        var log = new RecordingLogger<TheTvDbApiClient>();

        var series = await Client(new TheTvDbStub(translations: Answer.Ok), log).GetSeriesAggregateByIdAsync(1);

        series!.Name.Should().Be("The Bridge");
        series.Episodes.Single().Name.Should().Be("Episode 1");
        Fallbacks(log).Should().BeEmpty();
    }

    [Test]
    public async Task AMovieWhoseTranslationTimesOut_Should_StillComeBack_WithTheFallbackOfAFailedTranslation()
    {
        var failed = await Client(new TheTvDbStub(translations: Answer.Fails), new RecordingLogger<TheTvDbApiClient>())
            .GetMovieAggregateByIdAsync(2);

        var log = new RecordingLogger<TheTvDbApiClient>();
        var timedOut = await Client(new TheTvDbStub(translations: Answer.Hangs), log).GetMovieAggregateByIdAsync(2);

        timedOut.Should().NotBeNull();
        timedOut!.Name.Should().Be("Jagten");
        timedOut.Should().BeEquivalentTo(failed);
        Fallbacks(log).Should().ContainSingle().Which.Should().Contain("Translation fetch failed or timed out for movie 2");
    }

    [Test]
    public async Task TheCallersOwnCancellation_Should_StopASeriesFetch_WithoutAFallback()
    {
        // The attempt would wait 30 s; the caller gives up after 150 ms.
        var log = new RecordingLogger<TheTvDbApiClient>();
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var act = () => Client(new TheTvDbStub(translations: Answer.Hangs), log, TimeSpan.FromSeconds(30))
            .GetSeriesAggregateByIdAsync(1, cancellationToken: caller.Token);

        (await act.Should().ThrowAsync<OperationCanceledException>()).Which.InnerException.Should().NotBeOfType<TimeoutException>();
        Fallbacks(log).Should().BeEmpty("cancelling is not a failed translation");
    }

    [Test]
    public async Task TheCallersOwnCancellation_Should_StopAMovieFetch_WithoutAFallback()
    {
        var log = new RecordingLogger<TheTvDbApiClient>();
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var act = () => Client(new TheTvDbStub(translations: Answer.Hangs), log, TimeSpan.FromSeconds(30))
            .GetMovieAggregateByIdAsync(2, cancellationToken: caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Fallbacks(log).Should().BeEmpty();
    }

    private static IReadOnlyList<string> Fallbacks(RecordingLogger<TheTvDbApiClient> log) =>
        log.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("timed out", StringComparison.Ordinal))
            .Select(e => e.Message).ToList();

    private static TheTvDbApiClient Client(HttpMessageHandler handler, ILogger<TheTvDbApiClient> log, TimeSpan? attempt = null)
    {
        var options = Options.Create(new TheTvDbOptions { BaseUrl = "https://api4.thetvdb.com/v4/", ApiKey = "unit-test-api-key" });

        return new TheTvDbApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api4.thetvdb.com/v4/"), Timeout = Timeout.InfiniteTimeSpan },
            new TheTvDbClientState(options, NullLogger<TheTvDbClientState>.Instance),
            Polly.ResiliencePipeline<HttpResponseMessage>.Empty,
            log,
            Options.Create(new ExternalHttpResilienceOptions { TheTvDbAttemptTimeout = attempt ?? ShortAttempt }));
    }

    /// <summary>TheTVDB, with the records answering at once and the translations as the test asks.</summary>
    private sealed class TheTvDbStub(Answer translations, string seriesExtended = SeriesExtended) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/login", StringComparison.Ordinal))
                return Json("""{"status":"success","data":{"token":"t"}}""");
            if (path.EndsWith("/series/1/extended", StringComparison.Ordinal))
                return Json(seriesExtended);
            if (path.EndsWith("/movies/2/extended", StringComparison.Ordinal))
                return Json(MovieExtended);

            var body = path switch
            {
                _ when path.EndsWith("/series/1/translations/eng", StringComparison.Ordinal) => SeriesTranslation,
                _ when path.EndsWith("/series/1/episodes/default/eng", StringComparison.Ordinal) => EpisodesTranslated,
                _ when path.EndsWith("/movies/2/translations/eng", StringComparison.Ordinal) => MovieTranslation,
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };

            switch (translations)
            {
                case Answer.Fails:
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("down") };
                case Answer.Hangs:
                    // Until the attempt (or the caller) gives up.
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("unreachable");
                default:
                    return Json(body);
            }
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
