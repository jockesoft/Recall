using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Recall.Web.Infrastructure.External.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Auth;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Common;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Search;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;

namespace Recall.Web.Services.External.TheTvDb;

public sealed class TheTvDbApiClient(
    HttpClient httpClient,
    IOptions<TheTvDbOptions> options,
    ILogger<TheTvDbApiClient> logger)
    : ITheTvDbApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TheTvDbOptions _options = options.Value;

    private string? _bearerToken;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    public async Task<IReadOnlyList<SearchResultDto>> SearchSeriesAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<SearchResultDto>();

        await EnsureAuthenticatedAsync(cancellationToken);

        var path = $"search?query={Uri.EscapeDataString(query)}&type=series";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        var envelope = await SendAsync<TheTvDbEnvelopeDto<List<SearchResultDto>>>(request, cancellationToken);
        return envelope.Data!;
    }

    public async Task<SeriesDataDto?> GetSeriesByIdAsync(int seriesId, CancellationToken cancellationToken = default)
    {
        if (seriesId <= 0)
            throw new ArgumentOutOfRangeException(nameof(seriesId));

        await EnsureAuthenticatedAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"series/{seriesId}");
        var envelope = await SendAsync<TheTvDbEnvelopeDto<SeriesDataDto>>(request, cancellationToken);

        return envelope.Data;
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_bearerToken))
            return;

        await _authLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_bearerToken))
                return;

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

            _bearerToken = token;
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
            logger.LogInformation("TheTVDB authentication succeeded.");
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("TheTVDB request failed. StatusCode: {StatusCode}. Body: {Body}", (int)response.StatusCode, body);
            throw new TheTvDbApiException("TheTVDB request failed.", (int)response.StatusCode);
        }

        try
        {
            var model = JsonSerializer.Deserialize<T>(body, JsonOptions);
            return model ?? throw new TheTvDbApiException("TheTVDB response deserialized to null.");
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to deserialize TheTVDB response.");
            throw new TheTvDbApiException("Failed to deserialize TheTVDB response.", null, ex);
        }
    }
}