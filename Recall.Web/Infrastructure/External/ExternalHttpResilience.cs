using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;

namespace Recall.Web.Infrastructure.External;

/// <summary>
/// Tunables for the retry pipelines below. Not bound to configuration — the
/// defaults are the production values; tests shrink the delay so a retry
/// doesn't cost real seconds.
/// </summary>
public sealed class ExternalHttpResilienceOptions
{
    /// <summary>First retry delay; later ones back off exponentially with jitter.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// Retry + per-attempt timeout for the outbound metadata clients. Each typed
/// client's own <c>HttpClient.Timeout</c> stays in place as the overall budget
/// for a call including its retries.
/// </summary>
public static class ExternalHttpResilience
{
    /// <summary>
    /// The longest server-requested wait (<c>Retry-After</c>) worth sitting
    /// through inside a web request. A longer one means "come back later":
    /// the call fails now instead of being retried early against the server's
    /// wishes.
    /// </summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(5);

    /// <summary>
    /// TheTVDB: up to 3 retries on a transient failure (network error, timeout,
    /// 408, 5xx) or a 429 whose <c>Retry-After</c> is short. GET only — the
    /// login POST is left to the client's own 401 handling.
    /// </summary>
    public static IHttpClientBuilder AddTheTvDbResilience(this IHttpClientBuilder builder)
    {
        builder.AddResilienceHandler("thetvdb", (pipeline, context) =>
        {
            pipeline.AddRetry(CreateRetry(context.ServiceProvider, maxRetryAttempts: 3, retryTooManyRequests: true));
            pipeline.AddTimeout(TimeSpan.FromSeconds(10));
        });

        return builder;
    }

    /// <summary>
    /// OMDb: a single retry on a transient failure, and never on 429. OMDb's
    /// 429 means the daily quota is gone, so a retry cannot succeed, and a
    /// retry does not take a permit from <c>IOmdbRequestBudget</c> — keeping
    /// them to one, and to failures that didn't count against the quota in the
    /// first place, is what keeps that budget honest.
    /// </summary>
    public static IHttpClientBuilder AddOmdbResilience(this IHttpClientBuilder builder)
    {
        builder.AddResilienceHandler("omdb", (pipeline, context) =>
        {
            pipeline.AddRetry(CreateRetry(context.ServiceProvider, maxRetryAttempts: 1, retryTooManyRequests: false));
            pipeline.AddTimeout(TimeSpan.FromSeconds(8));
        });

        return builder;
    }

    private static HttpRetryStrategyOptions CreateRetry(
        IServiceProvider services, int maxRetryAttempts, bool retryTooManyRequests)
    {
        var settings = services.GetRequiredService<IOptions<ExternalHttpResilienceOptions>>().Value;

        var retry = new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = maxRetryAttempts,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = settings.RetryBaseDelay,
            ShouldHandle = args => ValueTask.FromResult(ShouldRetry(args.Outcome, retryTooManyRequests)),
            DelayGenerator = args => ValueTask.FromResult(RetryAfter(args.Outcome.Result))
        };

        retry.DisableForUnsafeHttpMethods();
        return retry;
    }

    internal static bool ShouldRetry(Outcome<HttpResponseMessage> outcome, bool retryTooManyRequests)
    {
        if (outcome.Exception is { } exception)
            return exception is HttpRequestException or TimeoutRejectedException;

        if (outcome.Result is not { } response)
            return false;

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return retryTooManyRequests && !AsksToWaitTooLong(response);

        return response.StatusCode == HttpStatusCode.RequestTimeout
               || ((int)response.StatusCode >= 500 && !AsksToWaitTooLong(response));
    }

    /// <summary>The server's requested wait, when it sent one we're willing to honor; null falls back to the backoff.</summary>
    internal static TimeSpan? RetryAfter(HttpResponseMessage? response) =>
        RequestedWait(response) is { } wait && wait > TimeSpan.Zero && wait <= MaxRetryAfter
            ? wait
            : null;

    private static bool AsksToWaitTooLong(HttpResponseMessage response) =>
        RequestedWait(response) is { } wait && wait > MaxRetryAfter;

    private static TimeSpan? RequestedWait(HttpResponseMessage? response) =>
        response?.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => null
        };
}
