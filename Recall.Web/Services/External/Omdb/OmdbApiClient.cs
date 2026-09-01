using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Recall.Web.Domain.Omdb;
using Recall.Web.Infrastructure.External.Omdb;

namespace Recall.Web.Services.External.Omdb;

public sealed class OmdbApiClient(
    HttpClient httpClient,
    IOptions<OmdbOptions> options,
    ILogger<OmdbApiClient> logger)
    : IOmdbApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly OmdbOptions _options = options.Value;

    public async Task<OmdbSeries?> GetByImdbIdAsync(string imdbId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
            throw new ArgumentException("IMDb id is required.", nameof(imdbId));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("OMDb ApiKey is not configured (Omdb:ApiKey / Omdb__ApiKey).");

        var url = $"?apikey={Uri.EscapeDataString(_options.ApiKey)}&i={Uri.EscapeDataString(imdbId)}&r=json";

        using var response = await httpClient.GetAsync(url, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new HttpRequestException("OMDb rejected the API key (401).");

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        OmdbSeries? result;
        try
        {
            result = JsonSerializer.Deserialize<OmdbSeries>(payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "OMDb returned unparseable JSON for IMDb id {ImdbId}.", imdbId);
            return null;
        }

        if (result is null || !result.IsSuccess)
        {
            logger.LogInformation(
                "OMDb has no usable record for IMDb id {ImdbId} ({Error}).",
                imdbId, result?.Error ?? "no response");
            return null;
        }

        return result;
    }
}
