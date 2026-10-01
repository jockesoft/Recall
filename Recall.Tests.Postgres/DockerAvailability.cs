using DotNet.Testcontainers.Builders;

namespace Recall.Tests.Postgres;

/// <summary>
/// Decides what an unreachable Docker means for this suite: ignored on a
/// developer machine, a failure in CI.
/// </summary>
public static class DockerAvailability
{
    /// <summary>True when the <c>CI</c> environment variable is set, as GitHub Actions does.</summary>
    public static bool IsCi => IsCiValue(Environment.GetEnvironmentVariable("CI"));

    public static bool IsCiValue(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Equals("false", StringComparison.OrdinalIgnoreCase)
        && value != "0";

    /// <summary>
    /// True only for "no Docker to talk to". Anything else that goes wrong while
    /// starting the container (a failed image pull, a container that exits) is a
    /// real failure and must not be turned into an ignored suite.
    /// </summary>
    public static bool IsUnreachable(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DockerUnavailableException)
                return true;
        }

        return false;
    }

    public static string Describe(Exception exception, bool isCi) =>
        "Docker is not reachable, so the PostgreSQL tests in Recall.Tests.Postgres did not run. " +
        $"They start a {PostgresSuite.Image} container through Testcontainers. " +
        (isCi
            ? "The CI environment variable is set, so this is a failure rather than an ignored suite. "
            : "Start Docker and run the tests again. ") +
        $"Docker said: {exception.GetBaseException().Message}";
}
