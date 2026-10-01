using System.Net;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Recall.Web.Extensions;

namespace Recall.Tests.Extensions;

/// <summary>
/// Runs the real <see cref="ForwardedHeadersMiddleware"/> against the options
/// <c>AddTrustedForwardedHeaders</c> produces, so these assert the behavior
/// that matters — whose headers end up believed — not just the option values.
/// </summary>
[TestFixture]
public class TrustedForwardedHeadersTests
{
    private const string ClientIp = "198.51.100.9";

    [Test]
    public async Task Default_Should_IgnoreForwardedHeaders_FromAPublicAddress()
    {
        var context = await InvokeAsync(Configure(), remoteIp: "203.0.113.7", forwardedFor: ClientIp);

        context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("203.0.113.7"),
            "a caller that reaches the app directly must not be able to choose its own rate-limit key");
        context.Request.Scheme.Should().Be("http");
        context.Request.Host.Value.Should().Be("internal:8701");
    }

    [TestCase("127.0.0.1")]
    [TestCase("::1")]
    [TestCase("172.18.0.1")]
    [TestCase("10.0.0.5")]
    [TestCase("192.168.1.10")]
    [TestCase("fd00::1")]
    public async Task Default_Should_ApplyForwardedHeaders_FromLoopbackAndPrivateRanges(string proxyIp)
    {
        var context = await InvokeAsync(Configure(), remoteIp: proxyIp, forwardedFor: ClientIp);

        context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse(ClientIp));
        context.Request.Scheme.Should().Be("https");
        context.Request.Host.Value.Should().Be("recall.nu");
    }

    [Test]
    public async Task Default_Should_ApplyForwardedHeaders_FromAnIPv4MappedProxyAddress()
    {
        // What a dual-stack Kestrel listener reports for an IPv4 peer.
        var context = await InvokeAsync(Configure(), remoteIp: "::ffff:172.18.0.1", forwardedFor: ClientIp);

        context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse(ClientIp));
    }

    [Test]
    public async Task Should_UseOnlyTheProxysOwnEntry_WhenTheClientPrependsAForgedOne()
    {
        var context = await InvokeAsync(Configure(), remoteIp: "172.18.0.1", forwardedFor: $"1.2.3.4, {ClientIp}");

        context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse(ClientIp),
            "with ForwardLimit 1 only the entry the trusted proxy appended is read");
    }

    [Test]
    public async Task ConfiguredNetworks_Should_ReplaceTheDefaults()
    {
        var options = Configure(("TrustedProxies:Networks:0", "10.1.0.0/16"));

        var fromConfigured = await InvokeAsync(options, remoteIp: "10.1.2.3", forwardedFor: ClientIp);
        var fromDefaultRange = await InvokeAsync(options, remoteIp: "172.18.0.1", forwardedFor: ClientIp);
        var fromLoopback = await InvokeAsync(options, remoteIp: "127.0.0.1", forwardedFor: ClientIp);

        fromConfigured.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse(ClientIp));
        fromDefaultRange.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("172.18.0.1"));
        fromLoopback.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("127.0.0.1"));
    }

    [Test]
    public async Task ConfiguredAddress_Should_BeTrusted_AndReplaceTheDefaults()
    {
        var options = Configure(("TrustedProxies:Addresses:0", "203.0.113.50"));

        var fromConfigured = await InvokeAsync(options, remoteIp: "203.0.113.50", forwardedFor: ClientIp);
        var fromDefaultRange = await InvokeAsync(options, remoteIp: "172.18.0.1", forwardedFor: ClientIp);

        fromConfigured.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse(ClientIp));
        fromDefaultRange.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("172.18.0.1"));
    }

    [Test]
    public async Task BlankEntries_Should_BeIgnored_SoTheDefaultsStillApply()
    {
        // An env var left in place but emptied, e.g. TrustedProxies__Networks__0=
        var options = Configure(("TrustedProxies:Networks:0", " "), ("TrustedProxies:Addresses:0", ""));

        var context = await InvokeAsync(options, remoteIp: "172.18.0.1", forwardedFor: ClientIp);

        context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse(ClientIp));
    }

    [Test]
    public async Task EmptyJsonArrays_Should_FallBackToTheDefaults()
    {
        // The shape appsettings.json ships with.
        const string json = """{ "TrustedProxies": { "Addresses": [], "Networks": [], "ForwardLimit": 1 } }""";
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var trusted = await InvokeAsync(Configure(configuration), remoteIp: "172.18.0.1", forwardedFor: ClientIp);
        var untrusted = await InvokeAsync(Configure(configuration), remoteIp: "203.0.113.7", forwardedFor: ClientIp);

        trusted.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse(ClientIp));
        untrusted.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("203.0.113.7"));
    }

    [Test]
    public async Task ForwardLimit_Should_WalkAProxyChain_WhenEveryHopIsTrusted()
    {
        // CDN (203.0.113.50) -> reverse proxy (172.18.0.1) -> app.
        var options = Configure(
            ("TrustedProxies:Networks:0", "172.16.0.0/12"),
            ("TrustedProxies:Addresses:0", "203.0.113.50"),
            ("TrustedProxies:ForwardLimit", "2"));

        var context = await InvokeAsync(options, remoteIp: "172.18.0.1", forwardedFor: $"{ClientIp}, 203.0.113.50");

        context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse(ClientIp));
    }

    [Test]
    public void Should_Throw_WhenANetworkIsNotCidr()
    {
        var act = () => Configure(("TrustedProxies:Networks:0", "172.18.0.0/99"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*TrustedProxies:Networks*172.18.0.0/99*");
    }

    [Test]
    public void Should_Throw_WhenAnAddressIsNotAnIpAddress()
    {
        var act = () => Configure(("TrustedProxies:Addresses:0", "my-proxy"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*TrustedProxies:Addresses*my-proxy*");
    }

    [Test]
    public void Should_Throw_WhenForwardLimitIsBelowOne()
    {
        var act = () => Configure(("TrustedProxies:ForwardLimit", "0"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*ForwardLimit*");
    }

    private static IOptions<ForwardedHeadersOptions> Configure(params (string Key, string Value)[] settings) =>
        Configure(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build());

    private static IOptions<ForwardedHeadersOptions> Configure(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddTrustedForwardedHeaders(configuration);

        return services.BuildServiceProvider().GetRequiredService<IOptions<ForwardedHeadersOptions>>();
    }

    private static async Task<HttpContext> InvokeAsync(
        IOptions<ForwardedHeadersOptions> options, string remoteIp, string forwardedFor)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("internal:8701");
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-Host"] = "recall.nu";

        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, options);
        await middleware.Invoke(context);

        return context;
    }
}
