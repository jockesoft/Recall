using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Recall.Web.Infrastructure.Authentication;

namespace Recall.Web.Services.Authentication;

public sealed class TurnstileVerifier(
    HttpClient httpClient,
    IOptions<TurnstileOptions> options,
    ILogger<TurnstileVerifier> logger) : ITurnstileVerifier
{
    private const string VerifyEndpoint = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    private readonly TurnstileOptions _options = options.Value;

    public async Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
            return true;

        if (string.IsNullOrWhiteSpace(token))
            return false;

        var form = new Dictionary<string, string>
        {
            ["secret"] = _options.SecretKey!,
            ["response"] = token
        };
        if (!string.IsNullOrWhiteSpace(remoteIp))
        {
            form["remoteip"] = remoteIp;
        }

        try
        {
            using var response = await httpClient.PostAsync(
                VerifyEndpoint, new FormUrlEncodedContent(form), cancellationToken);

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<TurnstileSiteVerifyResponse>(cancellationToken);
            if (result?.Success != true)
            {
                logger.LogWarning(
                    "Turnstile challenge failed: {ErrorCodes}",
                    result?.ErrorCodes is { Length: > 0 } codes ? string.Join(", ", codes) : "(none reported)");
            }

            return result?.Success == true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cloudflare being unreachable shouldn't be the reason a human
            // can't sign in — but it also shouldn't be treated as a pass, so
            // log it and fail closed like an actual failed challenge.
            logger.LogWarning(ex, "Turnstile verification request failed.");
            return false;
        }
    }

    private sealed class TurnstileSiteVerifyResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("error-codes")]
        public string[]? ErrorCodes { get; set; }
    }
}
