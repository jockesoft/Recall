using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Recall.Web.Infrastructure.External.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Auth;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Common;

namespace Recall.Web.Services.External.TheTvDb;

/// <summary>
/// Holds state that must survive across individual TheTvDbApiClient instances:
/// the cached bearer token and a concurrency throttle for outbound requests.
///
/// IMPORTANT: register this as a Singleton in DI. TheTvDbApiClient itself should
/// stay as a normal typed client (transient), which means a new instance is created
/// per resolution — so the token cache and throttle must live somewhere that isn't
/// re-created every time, otherwise every single call re-authenticates.
///
///   services.AddSingleton&lt;TheTvDbClientState&gt;();
///   services.AddHttpClient&lt;ITheTvDbApiClient, TheTvDbApiClient&gt;(...);
/// </summary>
public sealed class TheTvDbClientState(IOptions<TheTvDbOptions> options, ILogger<TheTvDbClientState> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // How many concurrent HTTP calls we're willing to make against TVDB at once.
    // Keep this modest — we parallelize episode/season fetches, and TVDB will
    // throttle or ban aggressive clients. Tune if you have a Pro subscription.
    private const int MaxConcurrentRequests = 5;

    private readonly TheTvDbOptions _options = options.Value;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    public SemaphoreSlim RequestThrottle { get; } = new(MaxConcurrentRequests, MaxConcurrentRequests);

    private volatile string? _token;

    public string? CurrentToken => _token;

    public void Invalidate() => _token = null;

    /// <summary>
    /// Returns the cached token, or logs in (or re-logs in, if forceRefresh) and caches the result.
    /// Safe to call concurrently — only one login request will actually go out.
    /// </summary>
    public async Task<string> GetOrRefreshTokenAsync(HttpClient httpClient, bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh && _token is not null)
            return _token;

        await _authLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check after acquiring the lock — someone else may have already refreshed it.
            if (!forceRefresh && _token is not null)
                return _token;

            if (string.IsNullOrWhiteSpace(_options.ApiKey))
                throw new TheTvDbApiException("TheTvDb API key is missing in configuration.");

            var login = new LoginRequestDto
            {
                ApiKey = _options.ApiKey,
                Pin = string.IsNullOrWhiteSpace(_options.Pin) ? null : _options.Pin
            };

            using var response = await httpClient.PostAsJsonAsync("login", login, JsonOptions, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("TheTVDB login failed. StatusCode: {StatusCode}. Body: {Body}", (int)response.StatusCode, body);
                throw new TheTvDbApiException("TheTVDB login failed.", (int)response.StatusCode);
            }

            var envelope = await response.Content.ReadFromJsonAsync<TheTvDbEnvelopeDto<LoginDataDto>>(JsonOptions, cancellationToken);
            var token = envelope?.Data?.Token;

            if (string.IsNullOrWhiteSpace(token))
                throw new TheTvDbApiException("TheTVDB login returned an empty token.");

            _token = token;
            logger.LogInformation("TheTVDB authentication succeeded.");
            return _token;
        }
        finally
        {
            _authLock.Release();
        }
    }
}