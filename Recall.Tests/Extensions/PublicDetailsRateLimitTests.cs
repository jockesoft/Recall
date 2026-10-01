using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Recall.Web.Extensions;

namespace Recall.Tests.Extensions;

/// <summary>
/// The partition function behind the <c>public-details</c> policy, driven
/// through a real <see cref="PartitionedRateLimiter"/>.
/// </summary>
[TestFixture]
public class PublicDetailsRateLimitTests
{
    private const int Limit = InfrastructureServiceCollectionExtensions.PublicDetailsPermitsPerMinute;

    private PartitionedRateLimiter<HttpContext> _limiter = null!;

    [SetUp]
    public void SetUp() =>
        _limiter = PartitionedRateLimiter.Create<HttpContext, string>(
            InfrastructureServiceCollectionExtensions.PublicDetailsPartition);

    [TearDown]
    public void TearDown() => _limiter.Dispose();

    private static HttpContext Anonymous(string ip) => new DefaultHttpContext
    {
        Connection = { RemoteIpAddress = IPAddress.Parse(ip) }
    };

    private static HttpContext SignedIn(string ip)
    {
        var context = Anonymous(ip);
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "Cookies"));
        return context;
    }

    private bool TryAcquire(HttpContext context)
    {
        using var lease = _limiter.AttemptAcquire(context);
        return lease.IsAcquired;
    }

    [Test]
    public void Anonymous_Should_BeAllowedUpToTheLimit_ThenRejected()
    {
        for (var i = 0; i < Limit; i++)
            TryAcquire(Anonymous("198.51.100.9")).Should().BeTrue();

        TryAcquire(Anonymous("198.51.100.9")).Should().BeFalse();
    }

    [Test]
    public void Anonymous_Should_BeCountedPerClientIp()
    {
        for (var i = 0; i < Limit; i++)
            TryAcquire(Anonymous("198.51.100.9"));

        TryAcquire(Anonymous("198.51.100.9")).Should().BeFalse();
        TryAcquire(Anonymous("198.51.100.10")).Should().BeTrue("another visitor has their own allowance");
    }

    [Test]
    public void SignedIn_Should_NeverBeLimited_EvenFromAnAddressThatIsOverTheLimit()
    {
        for (var i = 0; i < Limit + 1; i++)
            TryAcquire(Anonymous("198.51.100.9"));

        for (var i = 0; i < Limit * 3; i++)
            TryAcquire(SignedIn("198.51.100.9")).Should().BeTrue();
    }

    [Test]
    public void Rejection_Should_SayWhenToComeBack()
    {
        for (var i = 0; i < Limit; i++)
            TryAcquire(Anonymous("198.51.100.9"));

        using var rejected = _limiter.AttemptAcquire(Anonymous("198.51.100.9"));

        rejected.IsAcquired.Should().BeFalse();
        rejected.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter).Should().BeTrue(
            "the 429 response carries this as its Retry-After header");
        retryAfter.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(1));
    }

    [Test]
    public void TheThreePublicDetailsPages_Should_CarryThePolicy()
    {
        foreach (var page in new[]
                 {
                     typeof(Recall.Web.Pages.Series.DetailsModel),
                     typeof(Recall.Web.Pages.Episodes.DetailsModel),
                     typeof(Recall.Web.Pages.Movies.DetailsModel)
                 })
        {
            var attribute = page.GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: false)
                .Cast<EnableRateLimitingAttribute>()
                .SingleOrDefault();

            attribute.Should().NotBeNull($"{page.FullName} is reachable anonymously and can trigger TheTVDB fetches");
            attribute!.PolicyName.Should().Be(InfrastructureServiceCollectionExtensions.PublicDetailsPolicy);
        }
    }
}
