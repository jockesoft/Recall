using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Recall.Web.Infrastructure.External.Omdb;

namespace Recall.Tests.Infrastructure.External.Omdb;

[TestFixture]
public class OmdbRequestBudgetTests
{
    [Test]
    public void TryAcquire_Should_AllowUpToTheConfiguredDailyLimit_ThenReject()
    {
        var sut = new OmdbRequestBudget(Options.Create(new OmdbOptions { MaxRequestsPerDay = 3 }));

        sut.TryAcquire().Should().BeTrue();
        sut.TryAcquire().Should().BeTrue();
        sut.TryAcquire().Should().BeTrue();
        sut.TryAcquire().Should().BeFalse("the daily budget is exhausted after the configured limit");
    }

    [Test]
    public void TryAcquire_Should_TreatNonPositiveLimit_AsAtLeastOne()
    {
        var sut = new OmdbRequestBudget(Options.Create(new OmdbOptions { MaxRequestsPerDay = 0 }));

        sut.TryAcquire().Should().BeTrue("a misconfigured non-positive limit must not block every call");
        sut.TryAcquire().Should().BeFalse();
    }
}
