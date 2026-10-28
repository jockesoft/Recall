using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Recall.Tests.TestSupport;
using Recall.Web.Infrastructure.Push;
using Recall.Web.Services.Push;

namespace Recall.Tests.Services.Push;

/// <summary>
/// The request a push service gets, and what its answer means. The network is
/// a stub: nothing is sent anywhere.
/// </summary>
[TestFixture]
public class WebPushSenderTests
{
    private static readonly VapidKeys.KeyPair Keys = VapidKeys.Generate();
    private static readonly PushPayload Payload = new("Silo: S02E05 is out", "", "/Episodes/Details/1", PushPayload.AppIcon);
    private const string Topic = "33333333333333333333333333333333";

    private static PushTarget Browser()
    {
        // A real browser key pair, so the payload can be encrypted for it.
        using var key = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var q = key.ExportParameters(false).Q;
        return new PushTarget(
            "https://fcm.googleapis.com/fcm/send/abc",
            Base64Url([0x04, .. q.X!, .. q.Y!]),
            Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static WebPushSender CreateSut(StubHttpMessageHandler handler) => new(
        new HttpClient(handler),
        Options.Create(new PushOptions { Enabled = true, PublicKey = Keys.PublicKey, PrivateKey = Keys.PrivateKey, Subject = "mailto:admin@recall.nu" }),
        NullLogger<WebPushSender>.Instance);

    [Test]
    public async Task Send_Should_PostAnEncryptedSignedPush_WithADaysTtl_NormalUrgency_AndTheTopic()
    {
        HttpRequestMessage? sent = null;
        byte[] body = [];
        var handler = new StubHttpMessageHandler(request =>
        {
            sent = request;
            // Read here: the library disposes the encrypted content once the request is done.
            body = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created);
        });

        var result = await CreateSut(handler).SendAsync(Browser(), Payload, Topic);

        result.Should().Be(PushDelivery.Delivered);
        sent!.Method.Should().Be(HttpMethod.Post);
        sent.RequestUri.Should().Be(new Uri("https://fcm.googleapis.com/fcm/send/abc"));
        sent.Headers.GetValues("TTL").Should().Equal("86400");
        // RFC 8030: no Urgency header means "normal"; the library sends one only for the other levels.
        (sent.Headers.TryGetValues("Urgency", out var urgency) ? urgency.Single() : "normal").Should().Be("normal");
        sent.Headers.GetValues("Topic").Should().Equal(Topic);
        sent.Headers.Authorization!.Scheme.Should().Be("vapid");
        sent.Headers.Authorization.Parameter.Should().Contain($"k={Keys.PublicKey}");
        sent.Content!.Headers.ContentEncoding.Should().Equal("aes128gcm");
        System.Text.Encoding.UTF8.GetString(body).Should().NotContain("Silo", "the payload is encrypted for the browser");
        body.Length.Should().BeGreaterThan(86, "an aes128gcm record: 86 bytes of header, then the ciphertext");
    }

    [TestCase(HttpStatusCode.NotFound)]
    [TestCase(HttpStatusCode.Gone)]
    public async Task ASubscriptionThePushServiceNoLongerKnows_Should_BeGone(HttpStatusCode status)
    {
        var result = await CreateSut(new StubHttpMessageHandler(_ => new HttpResponseMessage(status))).SendAsync(Browser(), Payload, Topic);

        result.Should().Be(PushDelivery.Gone);
    }

    [TestCase(HttpStatusCode.BadRequest)]
    [TestCase(HttpStatusCode.Forbidden)]
    [TestCase(HttpStatusCode.TooManyRequests)]
    [TestCase(HttpStatusCode.InternalServerError)]
    public async Task AnyOtherError_Should_BeAFailure_AndKeepTheSubscription(HttpStatusCode status)
    {
        var result = await CreateSut(new StubHttpMessageHandler(_ => new HttpResponseMessage(status))).SendAsync(Browser(), Payload, Topic);

        result.Should().Be(PushDelivery.Failed);
    }

    [Test]
    public async Task NoConnection_Should_BeAFailure_NotAnException()
    {
        var result = await CreateSut(StubHttpMessageHandler.Throwing(new HttpRequestException("no route"))).SendAsync(Browser(), Payload, Topic);

        result.Should().Be(PushDelivery.Failed);
    }
}
