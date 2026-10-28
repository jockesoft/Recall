using System.Net;
using System.Text.Json;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.Extensions.Options;
using Recall.Web.Infrastructure;
using Recall.Web.Infrastructure.Push;

namespace Recall.Web.Services.Push;

/// <summary>
/// <see cref="IPushSender"/> over Lib.Net.Http.WebPush: the payload encrypted
/// with <c>aes128gcm</c> for the browser's keys, and the request signed with
/// the VAPID key pair from <see cref="PushOptions"/>. A typed client; its
/// <see cref="HttpClient"/> has a short timeout and follows no redirects.
/// </summary>
public sealed class WebPushSender(
    HttpClient httpClient,
    IOptions<PushOptions> options,
    ILogger<WebPushSender> logger) : IPushSender
{
    /// <summary>How long a push service keeps a push for a device that is off: a day, after which the news is old.</summary>
    public static readonly TimeSpan TimeToLive = TimeSpan.FromHours(24);

    public async Task<PushDelivery> SendAsync(
        PushTarget target, PushPayload payload, string topic, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        using var vapid = new VapidAuthentication(settings.PublicKey, settings.PrivateKey) { Subject = settings.Subject.Trim() };
        var client = new PushServiceClient(httpClient) { DefaultAuthentication = vapid, AutoRetryAfter = false };

        var subscription = new PushSubscription { Endpoint = target.Endpoint };
        subscription.SetKey(PushEncryptionKeyName.P256DH, target.P256dh);
        subscription.SetKey(PushEncryptionKeyName.Auth, target.Auth);

        var message = new PushMessage(JsonSerializer.Serialize(payload, RecallJsonOptions.Web))
        {
            // The push service keeps only the newest push per topic for a device
            // it cannot reach yet, so a backlog arrives as one notification.
            Topic = topic,
            TimeToLive = (int)TimeToLive.TotalSeconds,
            Urgency = PushMessageUrgency.Normal
        };

        try
        {
            await client.RequestPushMessageDeliveryAsync(subscription, message, cancellationToken);
            return PushDelivery.Delivered;
        }
        catch (PushServiceClientException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return PushDelivery.Gone;
        }
        catch (PushServiceClientException ex)
        {
            logger.LogWarning("Push service {Host} refused a push: {Status} {Body}",
                new Uri(target.Endpoint).Host, (int)ex.StatusCode, ex.Body);
            return PushDelivery.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Push to {Host} failed.", new Uri(target.Endpoint).Host);
            return PushDelivery.Failed;
        }
    }
}
