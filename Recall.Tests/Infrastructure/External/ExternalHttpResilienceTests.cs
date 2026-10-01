using System.Net;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.External;
using Recall.Web.Infrastructure.External.Omdb;
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

        /// <summary>Runs at the start of every data attempt, while the request is "on the wire".</summary>
        public Action? OnDataAttempt { get; set; }

        /// <summary>Completes once the first data attempt has been answered.</summary>
        public TaskCompletionSource FirstDataAttemptAnswered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                LoginAttempts++;
                return Task.FromResult(Login());
            }

            DataAttempts++;
            OnDataAttempt?.Invoke();
            FirstDataAttemptAnswered.TrySetResult();
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

    /// <summary>Counts the permits the client's own retry takes. The caller's permit for the first attempt is not its concern.</summary>
    private sealed class CountingBudget(int available = int.MaxValue) : IOmdbRequestBudget
    {
        private int _available = available;

        public int Acquired { get; private set; }
        public int Refused { get; private set; }

        public bool TryAcquire()
        {
            if (_available <= 0)
            {
                Refused++;
                return false;
            }

            _available--;
            Acquired++;
            return true;
        }
    }

    private static ServiceProvider BuildServices(
        ScriptedHandler handler, TimeSpan? retryDelay = null, IOmdbRequestBudget? omdbBudget = null)
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
        if (omdbBudget is not null)
            services.AddSingleton(omdbBudget); // the last registration wins
        services.Configure<ExternalHttpResilienceOptions>(o =>
        {
            o.RetryBaseDelay = retryDelay ?? TimeSpan.FromMilliseconds(1);
            // A test that passes its own delay needs it to be exactly that long.
            o.UseJitter = retryDelay is null;
        });

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
    public async Task TheTvDb_Should_HoldOneThrottleSlot_DuringAnAttempt()
    {
        var handler = new ScriptedHandler(() => Json(SearchJson));
        await using var services = BuildServices(handler);
        var throttle = services.GetRequiredService<TheTvDbClientState>().RequestThrottle;
        var free = throttle.CurrentCount;
        var freeDuringAttempt = -1;
        handler.OnDataAttempt = () => freeDuringAttempt = throttle.CurrentCount;

        await services.GetRequiredService<ITheTvDbApiClient>().SearchAsync("anything");

        freeDuringAttempt.Should().Be(free - 1);
        throttle.CurrentCount.Should().Be(free);
    }

    [Test]
    public async Task TheTvDb_Should_GiveItsThrottleSlotBack_WhileItWaitsToRetry()
    {
        // The first attempt fails; the retry is a full second away. In that
        // second the request must not be occupying one of the five slots.
        var handler = new ScriptedHandler(
            () => Status(HttpStatusCode.ServiceUnavailable),
            () => Json(SearchJson));
        await using var services = BuildServices(handler, retryDelay: TimeSpan.FromSeconds(1));
        var throttle = services.GetRequiredService<TheTvDbClientState>().RequestThrottle;
        var free = throttle.CurrentCount;

        var call = services.GetRequiredService<ITheTvDbApiClient>().SearchAsync("anything");
        await handler.FirstDataAttemptAnswered.Task;

        // The release happens just after the handler returns; give it a moment
        // (far less than the retry delay) rather than racing it.
        var deadline = DateTime.UtcNow.AddMilliseconds(500);
        while (throttle.CurrentCount != free && DateTime.UtcNow < deadline)
            await Task.Delay(5);

        call.IsCompleted.Should().BeFalse("the request should still be waiting out its backoff");
        handler.DataAttempts.Should().Be(1);
        throttle.CurrentCount.Should().Be(free, "a request that is only sleeping must not hold a slot");

        await call;
        handler.DataAttempts.Should().Be(2);
        throttle.CurrentCount.Should().Be(free);
    }

    [Test]
    public async Task TheTvDb_Should_LetOtherRequestsThrough_WhileEverySlotHolderIsBackingOff()
    {
        // Five requests (one per slot) all fail their first attempt and back off
        // for a second. A sixth, healthy request arriving meanwhile must not have
        // to wait for them.
        var failFirstFive = 0;
        var handler = new ScriptedHandler(() =>
            Interlocked.Increment(ref failFirstFive) <= 5 ? Status(HttpStatusCode.ServiceUnavailable) : Json(SearchJson));
        await using var services = BuildServices(handler, retryDelay: TimeSpan.FromSeconds(1));
        var client = services.GetRequiredService<ITheTvDbApiClient>();

        var backingOff = Enumerable.Range(0, 5).Select(_ => client.SearchAsync("anything")).ToArray();
        while (handler.DataAttempts < 5)
            await Task.Delay(5);

        var sixth = client.SearchAsync("anything");
        var finishedFirst = await Task.WhenAny(sixth, Task.Delay(TimeSpan.FromMilliseconds(500)));

        finishedFirst.Should().BeSameAs(sixth, "it should get a slot immediately, not after the others' backoff");
        backingOff.Should().OnlyContain(t => !t.IsCompleted);
        await Task.WhenAll(backingOff);
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
    public async Task TheTvDb_Should_GiveUp_AfterItsRetriesAreSpent()
    {
        var handler = new ScriptedHandler(() => Status(HttpStatusCode.ServiceUnavailable));
        await using var services = BuildServices(handler);

        var act = () => services.GetRequiredService<ITheTvDbApiClient>().SearchAsync("anything");

        (await act.Should().ThrowAsync<TheTvDbApiException>()).Which.StatusCode.Should().Be(503);
        handler.DataAttempts.Should().Be(ExternalHttpResilience.TheTvDbMaxRetries + 1);
    }

    [Test]
    public void TheTvDb_WorstCase_Should_FitInsideItsOverallBudget()
    {
        // Every attempt running to its timeout, plus the longest wait before each retry.
        var worstCase = ExternalHttpResilience.WorstCase(
            ExternalHttpResilience.TheTvDbAttemptTimeout, ExternalHttpResilience.TheTvDbMaxRetries);

        worstCase.Should().BeLessThanOrEqualTo(ExternalHttpResilience.TheTvDbOverallBudget);
    }

    [Test]
    public void Omdb_WorstCase_Should_FitInsideTheClientTimeout()
    {
        var worstCase = ExternalHttpResilience.WorstCase(
            ExternalHttpResilience.OmdbAttemptTimeout, ExternalHttpResilience.OmdbMaxRetries);

        worstCase.Should().BeLessThan(ExternalHttpResilience.OmdbOverallTimeout,
            "otherwise the client timeout cuts the retry off and the caller sees a cancellation, not the real failure");
    }

    [Test]
    public async Task RegisteredClients_Should_UseTheTimeoutsTheBudgetArithmeticAssumes()
    {
        await using var services = BuildServices(new ScriptedHandler(() => Json(SearchJson)));
        var factory = services.GetRequiredService<IHttpClientFactory>();

        factory.CreateClient(nameof(ITheTvDbApiClient)).Timeout.Should().Be(ExternalHttpResilience.TheTvDbAttemptTimeout);
        factory.CreateClient(nameof(IOmdbApiClient)).Timeout.Should().Be(ExternalHttpResilience.OmdbOverallTimeout);
    }

    [Test]
    public void ShouldRetry_Should_TreatAClientTimeoutAsTransient_ButNotACallerCancellation()
    {
        var clientTimeout = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException());
        var callerGaveUp = new TaskCanceledException();

        ExternalHttpResilience.ShouldRetry(Polly.Outcome.FromException<HttpResponseMessage>(clientTimeout), retryTooManyRequests: true)
            .Should().BeTrue();
        ExternalHttpResilience.ShouldRetry(Polly.Outcome.FromException<HttpResponseMessage>(callerGaveUp), retryTooManyRequests: true)
            .Should().BeFalse();
    }

    [Test]
    public void RetryAfter_Should_BeHonored_OnlyUpToTheMaximumRetryDelay()
    {
        using var shortWait = Status(HttpStatusCode.TooManyRequests, retryAfterSeconds: 1);
        using var longWait = Status(HttpStatusCode.TooManyRequests, retryAfterSeconds: 3);

        ExternalHttpResilience.RetryAfter(shortWait).Should().Be(TimeSpan.FromSeconds(1));
        ExternalHttpResilience.RetryAfter(longWait).Should().BeNull();
        ExternalHttpResilience.ShouldRetry(Polly.Outcome.FromResult(longWait), retryTooManyRequests: true)
            .Should().BeFalse("a wait longer than the budget allows is not shortened — the call just fails");
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

        var result = await services.GetRequiredService<IOmdbApiClient>().GetSeriesAsync("tt0000001");

        result!.Title.Should().Be("Example");
        handler.DataAttempts.Should().Be(2);
    }

    [Test]
    public async Task Omdb_Should_MakeAtMostTwoAttempts()
    {
        var handler = new ScriptedHandler(() => Status(HttpStatusCode.ServiceUnavailable));
        await using var services = BuildServices(handler);

        var act = () => services.GetRequiredService<IOmdbApiClient>().GetSeriesAsync("tt0000001");

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

            var act = () => services.GetRequiredService<IOmdbApiClient>().GetSeriesAsync("tt0000001");

            await act.Should().ThrowAsync<HttpRequestException>();
            handler.DataAttempts.Should().Be(1);
        }
    }

    [Test]
    public async Task Omdb_Retry_Should_TakeOnePermitFromTheBudget()
    {
        var budget = new CountingBudget();
        var handler = new ScriptedHandler(
            () => Status(HttpStatusCode.ServiceUnavailable),
            () => Json(OmdbJson));
        await using var services = BuildServices(handler, omdbBudget: budget);

        await services.GetRequiredService<IOmdbApiClient>().GetSeriesAsync("tt0000001");

        handler.DataAttempts.Should().Be(2);
        budget.Acquired.Should().Be(1, "the second request is a real one against OMDb's daily limit");
    }

    [Test]
    public async Task Omdb_Should_NotRetry_WhenTheBudgetHasNoPermitLeft()
    {
        var budget = new CountingBudget(available: 0);
        var handler = new ScriptedHandler(
            () => Status(HttpStatusCode.ServiceUnavailable),
            () => Json(OmdbJson));
        await using var services = BuildServices(handler, omdbBudget: budget);

        var act = () => services.GetRequiredService<IOmdbApiClient>().GetSeriesAsync("tt0000001");

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.DataAttempts.Should().Be(1, "without a permit the retry is not sent");
        budget.Refused.Should().Be(1);
    }

    [Test]
    public async Task Omdb_Should_TakeOnlyOnePermit_WhenTheRetryFailsToo()
    {
        // Two requests go out. The pipeline is asked "retry?" after each, but
        // after the second there is no retry left to pay for.
        var budget = new CountingBudget();
        var handler = new ScriptedHandler(() => Status(HttpStatusCode.ServiceUnavailable));
        await using var services = BuildServices(handler, omdbBudget: budget);

        var act = () => services.GetRequiredService<IOmdbApiClient>().GetSeriesAsync("tt0000001");

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.DataAttempts.Should().Be(2);
        budget.Acquired.Should().Be(1);
    }

    [Test]
    public async Task Omdb_Should_TakeNoPermit_WhenNothingIsRetried()
    {
        var budget = new CountingBudget();

        // First try succeeds.
        await using (var services = BuildServices(new ScriptedHandler(() => Json(OmdbJson)), omdbBudget: budget))
            await services.GetRequiredService<IOmdbApiClient>().GetSeriesAsync("tt0000001");

        // Failures that are never retried: quota exhausted, and a client error.
        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.NotFound })
        {
            await using var services = BuildServices(new ScriptedHandler(() => Status(status)), omdbBudget: budget);
            var act = () => services.GetRequiredService<IOmdbApiClient>().GetSeriesAsync("tt0000001");
            await act.Should().ThrowAsync<HttpRequestException>();
        }

        budget.Acquired.Should().Be(0);
        budget.Refused.Should().Be(0, "the budget must not even be asked for a request that won't be retried");
    }
}
