using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Recall.Web.Extensions;

namespace Recall.Tests.Extensions;

/// <summary>
/// <c>Jobs:Disabled</c>: a job named there is not scheduled; a name that is no
/// job fails startup. (That every scheduled job is in the list of known names
/// is checked when the jobs are registered, so by every pipeline test.)
/// </summary>
[TestFixture]
public class DisabledJobsTests
{
    private static IConfiguration Configuration(params string[] disabled) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(disabled.Select((name, i) => new KeyValuePair<string, string?>($"Jobs:Disabled:{i}", name)))
            .Build();

    [Test]
    public void ByDefault_NoJob_Should_BeDisabled()
    {
        InfrastructureServiceCollectionExtensions.DisabledJobs(Configuration()).Should().BeEmpty();
        InfrastructureServiceCollectionExtensions.ScheduledJobNames.Should().HaveCount(8).And.OnlyHaveUniqueItems();
    }

    [Test]
    public void ANamedJob_Should_BeDisabled_WhateverTheCase()
    {
        var disabled = InfrastructureServiceCollectionExtensions.DisabledJobs(Configuration("watchlistimporttimer"));

        disabled.Contains("WatchlistImportTimer").Should().BeTrue();
        disabled.Should().HaveCount(1);
    }

    [Test]
    public void AnUnknownName_Should_FailStartup_RatherThanLeaveTheJobRunning()
    {
        var act = () => InfrastructureServiceCollectionExtensions.DisabledJobs(Configuration("WatchlistImportTimr"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*WatchlistImportTimr*");
    }

    [Test]
    public void Registration_Should_AcceptADisabledJob_AndRejectAnUnknownOne()
    {
        var register = (IConfiguration configuration) =>
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScheduledJobs(configuration);
            using var provider = services.BuildServiceProvider();
            // Quartz runs the scheduling callback when its options are built.
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Quartz.QuartzOptions>>().Value.Should().NotBeNull();
        };

        register.Invoking(r => r(Configuration("WatchlistImportTimer"))).Should().NotThrow();
        register.Invoking(r => r(Configuration("Nope"))).Should().Throw<InvalidOperationException>();
    }
}
