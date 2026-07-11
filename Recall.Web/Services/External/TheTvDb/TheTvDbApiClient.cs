using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Caching;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Common;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Episodes;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Search;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;
using Recall.Web.Mappings;

namespace Recall.Web.Services.External.TheTvDb;

public sealed class TheTvDbApiClient(
    HttpClient httpClient,
    TheTvDbClientState state,
    ILogger<TheTvDbApiClient> logger,
    IDistributedCacheJson cacheJson)
    : ITheTvDbApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<SearchResultDto>> SearchSeriesAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var envelope = await SendAsync<TheTvDbEnvelopeDto<List<SearchResultDto>>>(
            () => new HttpRequestMessage(HttpMethod.Get, $"search?query={Uri.EscapeDataString(query)}&type=series"),
            cancellationToken);

        return envelope.Data!;
    }

    public async Task<SeriesTranslationDataDto?> GetSeriesTranslationByLanguageAsync(
        int seriesId,
        string language,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seriesId);
        
        if (string.IsNullOrWhiteSpace(language)) throw new ArgumentException("Language is required.", nameof(language));

        var envelope = await SendAsync<TheTvDbEnvelopeDto<SeriesTranslationDataDto>>(
            () => new HttpRequestMessage(HttpMethod.Get, $"series/{seriesId}/translations/{Uri.EscapeDataString(language)}"),
            cancellationToken);

        return envelope.Data;
    }

    public async Task<SeriesAggregate?> GetSeriesAggregateByIdAsync(
        int seriesId,
        string language = "eng",
        CancellationToken cancellationToken = default)
    {
        language = language.Trim().ToLowerInvariant();
        var cacheKey = $"series:aggregate:v1:{seriesId}:{language}";

        var cached = await cacheJson.GetAsync<SeriesAggregate>(cacheKey, cancellationToken);
        if (cached is not null)
        {
            logger.LogDebug("Cache hit for series aggregate {SeriesId}, language {Language}.", seriesId, language);
            return cached;
        }

        SeriesDataDto? seriesDto = null;
        SeriesTranslationDataDto? translationDto = null;

        try
        {
            var translationDtoTask = GetSeriesTranslationByLanguageAsync(seriesId, language, cancellationToken);
            var seriesDtoTask = GetSeriesByIdExtendedAsync(seriesId, cancellationToken);
            
            await Task.WhenAll(seriesDtoTask, translationDtoTask);
            (seriesDto, translationDto) = (seriesDtoTask.Result, translationDtoTask.Result);
        }
        catch (TheTvDbApiException ex)
        {
            logger.LogInformation(
                ex,
                "Series/Translation fetch failed for series {SeriesId}, language {Language}. Falling back.",
                seriesId, language);
        }

        if (seriesDto is null)
            return null;
        
        IReadOnlyList<EpisodeDto>? fallbackEpisodes = null;
        if (seriesDto.Episodes is null || seriesDto.Episodes.Count == 0)
        {
            fallbackEpisodes = await LoadEpisodesFromSeasonsAsync(seriesDto, cancellationToken);
        }

        var aggregate = seriesDto.ToAggregate(translationDto, fallbackEpisodes);
        var englishEpisodes = await EnrichEpisodesWithEnglishAsync(aggregate.Episodes, cancellationToken);

        aggregate = aggregate with { Episodes = englishEpisodes };

        var ttl = Jitter(TimeSpan.FromHours(12), 0.10);

        if (aggregate.Status is { KeepUpdated: false, Name: not null } && aggregate.Status.Name.Equals("ended", StringComparison.OrdinalIgnoreCase))
        {
            ttl = Jitter(TimeSpan.FromDays(7), 0.10);
        }
        await cacheJson.SetAsync(cacheKey, aggregate, ttl, cancellationToken);

        return aggregate;
    }

    /// <summary>
    /// Fallback loader: pulls episodes per season when /series/{id}/extended does not include episodes.
    /// Seasons are loaded in parallel (each season's pages are still fetched sequentially, since we
    /// don't know page count up front); overall HTTP concurrency is capped by the shared request throttle.
    /// </summary>
    private async Task<IReadOnlyList<EpisodeDto>> LoadEpisodesFromSeasonsAsync(
        SeriesDataDto seriesDto,
        CancellationToken cancellationToken)
    {
        var seasonNumbers = (seriesDto.Seasons ?? [])
            .Select(s => s.Number)
            .Where(n => n.HasValue)
            .Select(n => n!.Value)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        if (seasonNumbers.Count == 0)
            return [];

        var seasonTasks = seasonNumbers.Select(seasonNumber =>
            LoadEpisodesForSeasonAsync(seriesDto.Id, seasonNumber, cancellationToken));

        var perSeasonResults = await Task.WhenAll(seasonTasks);

        return perSeasonResults
            .SelectMany(episodes => episodes)
            .Where(e => e.Id.HasValue)
            .GroupBy(e => e.Id!.Value)
            .Select(g => g.First())
            .OrderBy(e => e.SeasonNumber ?? int.MaxValue)
            .ThenBy(e => e.Number ?? int.MaxValue)
            .ThenBy(e => e.Id ?? int.MaxValue)
            .ToArray();
    }

    private async Task<List<EpisodeDto>> LoadEpisodesForSeasonAsync(
        int seriesId,
        int seasonNumber,
        CancellationToken cancellationToken)
    {
        var result = new List<EpisodeDto>();

        // Most TV series use "default" season type for normal numbering.
        // If your API requires a different type (e.g. "official"), switch this.
        const string seasonType = "default";

        var page = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TheTvDbEnvelopeDto<EpisodesPageDataDto>? envelope;
            try
            {
                envelope = await SendAsync<TheTvDbEnvelopeDto<EpisodesPageDataDto>>(
                    () => new HttpRequestMessage(
                        HttpMethod.Get,
                        $"series/{seriesId}/episodes/{seasonType}?season={seasonNumber}&page={page}"),
                    cancellationToken);
            }
            catch (TheTvDbApiException ex)
            {
                logger.LogWarning(ex,
                    "Failed loading episodes for series {SeriesId}, season {Season}, page {Page}.",
                    seriesId, seasonNumber, page);
                break;
            }

            var pageEpisodes = envelope.Data?.Episodes ?? new List<EpisodeDto>();
            if (pageEpisodes.Count == 0)
                break;

            result.AddRange(pageEpisodes);

            var hasNext = envelope.Data?.Links?.Next is not null;
            if (!hasNext)
                break;

            page++;
        }

        return result;
    }

    public async Task<SeriesDataDto?> GetSeriesByIdExtendedAsync(int seriesId, CancellationToken cancellationToken = default)
    {
        if (seriesId <= 0)
            throw new ArgumentOutOfRangeException(nameof(seriesId));

        var envelope = await SendAsync<TheTvDbEnvelopeDto<SeriesDataDto>>(
            () => new HttpRequestMessage(HttpMethod.Get, $"series/{seriesId}/extended"),
            cancellationToken);

        return envelope.Data;
    }

    public async Task<EpisodeTranslationDataDto?> GetEpisodeTranslationByLanguageAsync(
        int episodeId,
        string language,
        CancellationToken cancellationToken = default)
    {
        if (episodeId <= 0) throw new ArgumentOutOfRangeException(nameof(episodeId));
        if (string.IsNullOrWhiteSpace(language)) throw new ArgumentException("Language is required.", nameof(language));

        var envelope = await SendAsync<TheTvDbEnvelopeDto<EpisodeTranslationDataDto>>(
            () => new HttpRequestMessage(HttpMethod.Get, $"episodes/{episodeId}/translations/{Uri.EscapeDataString(language)}"),
            cancellationToken);

        return envelope.Data;
    }

    private static TimeSpan Jitter(TimeSpan baseTtl, double pct)
    {
        var factor = 1 + (Random.Shared.NextDouble() * 2 - 1) * pct; // e.g. 0.9..1.1
        var ms = Math.Max(1000, baseTtl.TotalMilliseconds * factor);
        return TimeSpan.FromMilliseconds(ms);
    }
    
    /// <summary>
    /// Fetches the English translation for every episode in parallel (bounded by the shared
    /// request throttle), instead of one HTTP round trip at a time. For a 100+ episode series
    /// this is the difference between minutes and seconds.
    /// </summary>
    private async Task<IReadOnlyList<EpisodeSummary>> EnrichEpisodesWithEnglishAsync(
        IReadOnlyList<EpisodeSummary> episodes,
        CancellationToken cancellationToken)
    {
        var tasks = episodes.Select(ep => EnrichSingleEpisodeAsync(ep, cancellationToken));
        return await Task.WhenAll(tasks);
    }

    private async Task<EpisodeSummary> EnrichSingleEpisodeAsync(EpisodeSummary ep, CancellationToken cancellationToken)
    {
        EpisodeTranslationDataDto? tr = null;
        try
        {
            tr = await GetEpisodeTranslationByLanguageAsync(ep.Id, "eng", cancellationToken);
        }
        catch (TheTvDbApiException ex)
        {
            logger.LogDebug(ex, "Could not load English translation for episode {EpisodeId}", ep.Id);
        }

        return new EpisodeSummary
        {
            Id = ep.Id,
            SeasonNumber = ep.SeasonNumber,
            EpisodeNumber = ep.EpisodeNumber,
            Name = !string.IsNullOrWhiteSpace(tr?.Name) ? tr.Name! : ep.Name,
            Overview = !string.IsNullOrWhiteSpace(tr?.Overview) ? tr.Overview : ep.Overview,
            Aired = ep.Aired,
            RuntimeMinutes = ep.RuntimeMinutes,
            IsMovie = ep.IsMovie,
            FinaleType = ep.FinaleType
        };
    }

    /// <summary>
    /// Sends a request, attaching the current bearer token per-request (never mutating the shared
    /// HttpClient's default headers, which isn't safe under concurrent calls). Throttles overall
    /// concurrency via the shared state, and transparently re-authenticates + retries once on 401.
    /// </summary>
    private async Task<T> SendAsync<T>(Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken)
    {
        await state.RequestThrottle.WaitAsync(cancellationToken);
        try
        {
            return await SendCoreAsync<T>(requestFactory, allowReauth: true, cancellationToken);
        }
        finally
        {
            state.RequestThrottle.Release();
        }
    }

    private async Task<T> SendCoreAsync<T>(Func<HttpRequestMessage> requestFactory, bool allowReauth, CancellationToken cancellationToken)
    {
        var token = await state.GetOrRefreshTokenAsync(httpClient, forceRefresh: false, cancellationToken);

        using var request = requestFactory();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized && allowReauth)
        {
            logger.LogInformation("TheTVDB token rejected (401); re-authenticating and retrying once.");
            await state.GetOrRefreshTokenAsync(httpClient, forceRefresh: true, cancellationToken);
            return await SendCoreAsync<T>(requestFactory, allowReauth: false, cancellationToken);
        }

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