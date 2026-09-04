using AwesomeAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using Recall.Web.Services.Health;

namespace Recall.Tests.Services;

[TestFixture]
public sealed class DbHealthCheckTests
{
    private static Task<HealthCheckResult> RunAsync(bool reachable)
    {
        var probe = new Mock<IDbHealthProbe>();
        probe.Setup(x => x.IsDatabaseReachableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(reachable);

        var sut = new DbHealthCheck(probe.Object);
        return sut.CheckHealthAsync(new HealthCheckContext());
    }

    [Test]
    public async Task ReportsHealthy_WhenTheDatabaseIsReachable()
    {
        var result = await RunAsync(reachable: true);
        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Test]
    public async Task ReportsUnhealthy_WhenTheDatabaseIsUnreachable()
    {
        var result = await RunAsync(reachable: false);
        result.Status.Should().Be(HealthStatus.Unhealthy);
    }
}
