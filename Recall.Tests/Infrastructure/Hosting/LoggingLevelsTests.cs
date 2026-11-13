using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Recall.Tests.TestSupport;
using Serilog.Extensions.Logging;

namespace Recall.Tests.Infrastructure.Hosting;

/// <summary>
/// The rule in appsettings.json: framework categories that log once per
/// request, query or outbound call stay at Warning in every environment, while
/// startup and shutdown and Recall's own lines get through. Checked through a
/// Serilog logger factory with the shipped levels, as <c>UseSerilog</c> builds it.
/// </summary>
[TestFixture]
public class LoggingLevelsTests
{
    /// <summary>Framework categories that write a line per request, query or outbound call at Information.</summary>
    private static readonly string[] PerRequestCategories =
    [
        "Microsoft.EntityFrameworkCore.Database.Command",
        "System.Net.Http.HttpClient.ITheTvDbApiClient.LogicalHandler",
        "System.Net.Http.HttpClient.ITheTvDbApiClient.ClientHandler",
        "System.Net.Http.HttpClient.IOmdbApiClient.ClientHandler",
        "Microsoft.AspNetCore.Hosting.Diagnostics",
        "Microsoft.AspNetCore.Routing.EndpointMiddleware",
        "Polly"
    ];

    /// <summary>Categories whose Information lines are kept: startup and shutdown, and the app's own job summaries.</summary>
    private static readonly string[] KeptCategories =
    [
        "Microsoft.Hosting.Lifetime",
        "Recall.Web.Infrastructure.Timers.UpdateTvDbInfoTimer",
        "Recall.Web.Infrastructure.Timers.NewEpisodeNotificationTimer"
    ];

    private static IEnumerable<TestCaseData> Cases(string[] categories) =>
        from environment in ShippedSerilog.Environments
        from category in categories
        select new TestCaseData(environment, category);

    private static IEnumerable<TestCaseData> PerRequestCases() => Cases(PerRequestCategories);

    private static IEnumerable<TestCaseData> KeptCases() => Cases(KeptCategories);

    private static IReadOnlyList<LogLevel> Written(string? environment, string category)
    {
        var sink = new InMemorySink();
        using (var factory = new SerilogLoggerFactory(ShippedSerilog.CreateLogger(sink, environment), dispose: true))
        {
            var logger = factory.CreateLogger(category);
            logger.LogInformation("information");
            logger.LogWarning("warning");
            logger.LogError("error");
        }

        return sink.Events
            .Select(e => e.RenderMessage() switch
            {
                "information" => LogLevel.Information,
                "warning" => LogLevel.Warning,
                _ => LogLevel.Error
            })
            .ToList();
    }

    /// <summary>UseSerilog ignores Microsoft's Logging section, so a level set there would look effective and do nothing.</summary>
    [TestCase("appsettings.json")]
    [TestCase("appsettings.Development.json")]
    [TestCase("appsettings.Production.json")]
    public void AppSettings_Should_HaveNoLoggingSection(string file)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(file, optional: false)
            .Build();

        configuration.GetSection("Logging").Exists().Should().BeFalse(
            "log levels live only under Serilog:MinimumLevel");
    }

    [TestCaseSource(nameof(PerRequestCases))]
    public void PerRequestFrameworkCategory_Should_LogWarningsAndErrorsOnly(string? environment, string category)
    {
        Written(environment, category).Should().Equal(LogLevel.Warning, LogLevel.Error);
    }

    [TestCaseSource(nameof(KeptCases))]
    public void StartupAndRecallCategories_Should_KeepTheirInformationLines(string? environment, string category)
    {
        Written(environment, category).Should().Equal(LogLevel.Information, LogLevel.Warning, LogLevel.Error);
    }
}
