using System.Net;
using System.Threading.RateLimiting;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Recall.Web.Extensions;

namespace Recall.Tests.Extensions;

/// <summary>
/// The partition function behind the <c>login-email</c> policy, driven through
/// a real <see cref="PartitionedRateLimiter"/>: it counts requests for a
/// sign-in link (POSTs), never page loads.
/// </summary>
[TestFixture]
public class LoginRateLimitTests
{
    private const int Limit = InfrastructureServiceCollectionExtensions.LoginEmailPermits;

    private PartitionedRateLimiter<HttpContext> _limiter = null!;

    [SetUp]
    public void SetUp() =>
        _limiter = PartitionedRateLimiter.Create<HttpContext, string>(
            InfrastructureServiceCollectionExtensions.LoginEmailPartition);

    [TearDown]
    public void TearDown() => _limiter.Dispose();

    private static HttpContext Request(string method, string ip) => new DefaultHttpContext
    {
        Request = { Method = method, Path = "/Account/Login" },
        Connection = { RemoteIpAddress = IPAddress.Parse(ip) }
    };

    private bool TryAcquire(HttpContext context)
    {
        using var lease = _limiter.AttemptAcquire(context);
        return lease.IsAcquired;
    }

    [Test]
    public void PageLoads_Should_NeverBeLimited()
    {
        for (var i = 0; i < Limit * 10; i++)
            TryAcquire(Request("GET", "198.51.100.9")).Should().BeTrue("reloading the sign-in page must not lock anyone out");
    }

    [Test]
    public void PageLoads_Should_NotUseUpTheAllowanceForLinkRequests()
    {
        for (var i = 0; i < Limit * 10; i++)
            TryAcquire(Request("GET", "198.51.100.9"));

        for (var i = 0; i < Limit; i++)
            TryAcquire(Request("POST", "198.51.100.9")).Should().BeTrue("page loads are not counted against link requests");
    }

    [Test]
    public void LinkRequests_Should_BeAllowedUpToTheLimit_ThenRejected_PerClientIp()
    {
        for (var i = 0; i < Limit; i++)
            TryAcquire(Request("POST", "198.51.100.9")).Should().BeTrue();

        TryAcquire(Request("POST", "198.51.100.9")).Should().BeFalse();
        TryAcquire(Request("POST", "198.51.100.10")).Should().BeTrue("another visitor has their own allowance");
    }

    [Test]
    public void AClientOverTheLimit_Should_StillBeAbleToLoadThePage()
    {
        for (var i = 0; i < Limit + 1; i++)
            TryAcquire(Request("POST", "198.51.100.9"));

        TryAcquire(Request("POST", "198.51.100.9")).Should().BeFalse();
        TryAcquire(Request("GET", "198.51.100.9")).Should().BeTrue();
        TryAcquire(Request("HEAD", "198.51.100.9")).Should().BeTrue();
    }
}
