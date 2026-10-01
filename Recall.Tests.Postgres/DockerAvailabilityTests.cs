using AwesomeAssertions;
using DotNet.Testcontainers.Builders;
using Recall.Tests.Postgres;

// Deliberately outside the Recall.Tests.Postgres namespace: PostgresSuite's
// setup covers that namespace, and these must run when Docker is absent too.
namespace Recall.Tests.PostgresSupport;

[TestFixture]
public sealed class DockerAvailabilityTests
{
    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("false", false)]
    [TestCase("0", false)]
    [TestCase("true", true)]
    [TestCase("1", true)]
    public void IsCiValue_Should_ReadTheCiVariable(string? value, bool expected)
    {
        DockerAvailability.IsCiValue(value).Should().Be(expected);
    }

    [Test]
    public void IsUnreachable_Should_BeTrue_WhenDockerCannotBeReached_EvenIfWrapped()
    {
        var unavailable = new DockerUnavailableException("Docker is either not running or misconfigured.");

        DockerAvailability.IsUnreachable(unavailable).Should().BeTrue();
        DockerAvailability.IsUnreachable(new InvalidOperationException("outer", unavailable)).Should().BeTrue();
    }

    [Test]
    public void IsUnreachable_Should_BeFalse_ForAnyOtherFailure()
    {
        // A failed image pull or a broken migration must fail the run, not hide it as "ignored".
        DockerAvailability.IsUnreachable(new InvalidOperationException("image pull failed")).Should().BeFalse();
        DockerAvailability.IsUnreachable(new TimeoutException()).Should().BeFalse();
    }

    [Test]
    public void Describe_Should_SayWhatDidNotRunAndWhy()
    {
        var unavailable = new DockerUnavailableException("no socket");

        DockerAvailability.Describe(unavailable, isCi: false)
            .Should().Contain("Docker is not reachable")
            .And.Contain(PostgresSuite.Image)
            .And.Contain("Start Docker")
            .And.Contain("no socket");

        DockerAvailability.Describe(unavailable, isCi: true)
            .Should().Contain("this is a failure rather than an ignored suite");
    }
}
