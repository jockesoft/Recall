using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Recall.Web.Infrastructure.External.Omdb;
using Polly.Timeout;

namespace Recall.Web.Infrastructure.External;

/// <summary>
/// Tunables for the retry pipelines below. Not bound to configuration — the
/// defaults are the production values; tests shrink the delay so a retry
/// doesn't cost real seconds, and switch the jitter off when they need a
/// delay of a known length.
/// </summary>
public sealed class ExternalHttpResilienceOptions
{
    /// <summary>First retry delay; later ones back off exponentially, capped at <see cref="ExternalHttpResilience.MaxRetryDelay"/>.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    public bool UseJitter { get; set; } = true;
}

/// <summary>
/// Retry rules for the outbound metadata clients, and the arithmetic that
/// keeps a call's worst case inside its time budget.
///
/// The two clients are wired differently on purpose:
/// <list type="bullet">
/// <item>TheTVDB's pipeline is run by <c>TheTvDbApiClient</c> itself, around
/// each throttled attempt, so a request gives its concurrency slot back while
/// it waits to retry. <c>HttpClient.Timeout</c> is the per-attempt limit.</item>
/// <item>OMDb has no throttle, so its pipeline is an ordinary handler inside
/// the <c>HttpClient</c>, whose <c>Timeout</c> is the overall limit. Its retry
/// draws on the shared daily request budget.</item>
/// </list>
/// </summary>
public static class ExternalHttpResilience
{
    /// <summary>Key of the TheTVDB retry pipeline (<c>ResiliencePipeline&lt;HttpResponseMessage&gt;</c>, keyed service).</summary>
    public const string TheTvDbPipeline = "thetvdb";

    /// <summary>
    /// The longest wait between attempts: caps the exponential backoff, and is
    /// the longest server-requested <c>Retry-After</c> worth sitting through
    /// inside a web request. A longer one means "come back later": the call
    /// fails now instead of being retried early against the server's wishes.
    /// </summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>One TheTVDB round trip, response body included (it is the typed client's <c>HttpClient.Timeout</c>).</summary>
    public static readonly TimeSpan TheTvDbAttemptTimeout = TimeSpan.FromSeconds(8);

    public const int TheTvDbMaxRetries = 2;

    /// <summary>
    /// What a page or job may spend on one TheTVDB call, retries and waits
    /// included — the figure the single-attempt client timeout used to be.
    /// <see cref="WorstCase"/> for the two values above must stay within it.
    /// Time spent queueing for a throttle slot is not part of it (it never
    /// was), and the rare 401 re-login inside an attempt adds up to two more
    /// round trips.
    /// </summary>
    public static readonly TimeSpan TheTvDbOverallBudget = TimeSpan.FromSeconds(30);

    /// <summary>Time to response headers for one OMDb attempt.</summary>
    public static readonly TimeSpan OmdbAttemptTimeout = TimeSpan.FromSeconds(8);

    public const int OmdbMaxRetries = 1;

    /// <summary>The OMDb client's <c>HttpClient.Timeout</c>: the whole call, its retry included.</summary>
    public static readonly TimeSpan OmdbOverallTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Every attempt running to its timeout, with the longest allowed wait before each retry.</summary>
    public static TimeSpan WorstCase(TimeSpan attemptTimeout, int maxRetries) =>
        attemptTimeout * (maxRetries + 1) + MaxRetryDelay * maxRetries;

    /// <summary>
    /// TheTVDB: up to <see cref="TheTvDbMaxRetries"/> retries on a transient
    /// failure (network error, timeout, 408, 5xx) or a 429 whose
    /// <c>Retry-After</c> is short. Only the client's data calls (all GETs) run
    /// through it; a failed login surfaces as <c>TheTvDbApiException</c>, which
    /// is not retried.
    /// </summary>
    public static IServiceCollection AddTheTvDbRetryPipeline(this IServiceCollection services)
    {
        services.AddResiliencePipeline<string, HttpResponseMessage>(TheTvDbPipeline, (pipeline, context) =>
        {
            pipeline.AddRetry(CreateRetry(context.ServiceProvider, TheTvDbMaxRetries, retryTooManyRequests: true));
        });

        return services;
    }

    /// <summary>
    /// OMDb: a single retry on a transient failure, and never on 429 (OMDb's
    /// 429 means the daily quota is gone, so a retry cannot succeed).
    ///
    /// Every request OMDb receives counts against its daily limit, retries
    /// included. The caller takes a permit from <see cref="IOmdbRequestBudget"/>
    /// for the first attempt; a retry takes its own here, and when the budget
    /// has none left there is no retry — the first failure is what the caller
    /// sees.
    /// </summary>
    public static IHttpClientBuilder AddOmdbResilience(this IHttpClientBuilder builder)
    {
        builder.AddResilienceHandler("omdb", (pipeline, context) =>
        {
            var budget = context.ServiceProvider.GetRequiredService<IOmdbRequestBudget>();
            var retry = CreateRetry(context.ServiceProvider, OmdbMaxRetries, retryTooManyRequests: false);

            // Polly also asks this after the final attempt, when no retry can
            // follow; the attempt-number check keeps that from spending a permit
            // on a request that will never be sent. The budget is asked last, so
            // a permit is only taken for a retry that is otherwise going ahead.
            retry.ShouldHandle = args => ValueTask.FromResult(
                args.AttemptNumber < OmdbMaxRetries
                && ShouldRetry(args.Outcome, retryTooManyRequests: false)
                && budget.TryAcquire());

            retry.DisableForUnsafeHttpMethods();

            pipeline.AddRetry(retry);
            pipeline.AddTimeout(OmdbAttemptTimeout);
        });

        return builder;
    }

    private static HttpRetryStrategyOptions CreateRetry(
        IServiceProvider services, int maxRetryAttempts, bool retryTooManyRequests)
    {
        var settings = services.GetRequiredService<IOptions<ExternalHttpResilienceOptions>>().Value;

        return new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = maxRetryAttempts,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = settings.UseJitter,
            Delay = settings.RetryBaseDelay,
            MaxDelay = MaxRetryDelay,
            ShouldHandle = args => ValueTask.FromResult(ShouldRetry(args.Outcome, retryTooManyRequests)),
            DelayGenerator = args => ValueTask.FromResult(RetryAfter(args.Outcome.Result))
        };
    }

    public static bool ShouldRetry(Outcome<HttpResponseMessage> outcome, bool retryTooManyRequests)
    {
        if (outcome.Exception is { } exception)
        {
            // TaskCanceledException wrapping TimeoutException is how HttpClient
            // reports its own Timeout elapsing — a slow attempt, worth another
            // try. Any other cancellation is the caller giving up.
            return exception is HttpRequestException
                or TimeoutRejectedException
                or TaskCanceledException { InnerException: TimeoutException };
        }

        if (outcome.Result is not { } response)
            return false;

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return retryTooManyRequests && !AsksToWaitTooLong(response);

        return response.StatusCode == HttpStatusCode.RequestTimeout
               || ((int)response.StatusCode >= 500 && !AsksToWaitTooLong(response));
    }

    /// <summary>The server's requested wait, when it sent one we're willing to honor; null falls back to the backoff.</summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage? response) =>
        RequestedWait(response) is { } wait && wait > TimeSpan.Zero && wait <= MaxRetryDelay
            ? wait
            : null;

    private static bool AsksToWaitTooLong(HttpResponseMessage response) =>
        RequestedWait(response) is { } wait && wait > MaxRetryDelay;

    private static TimeSpan? RequestedWait(HttpResponseMessage? response) =>
        response?.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => null
        };
}
