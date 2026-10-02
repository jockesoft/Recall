using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Recall.Web.Infrastructure.Authentication;
using Recall.Web.Infrastructure.Retention;
using Recall.Web.Pages;

namespace Recall.Tests.Pages;

[TestFixture]
public class PrivacyModelTests
{
    private static PrivacyModel Create(Dictionary<string, string?>? settings = null, RetentionOptions? retention = null) =>
        new(Options.Create(retention ?? new RetentionOptions()),
            Options.Create(new LoginTokenOptions { TokenLifetimeMinutes = 20 }),
            new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build());

    [Test]
    public void ThePage_Should_StateWhatTheConfigurationSays()
    {
        var sut = Create(retention: new RetentionOptions { EmailDays = 45 });

        sut.Retention.EmailDays.Should().Be(45);
        sut.SignInLinkMinutes.Should().Be(20);
        sut.SignInCookieName.Should().Be("Recall.Auth");
        sut.SignInCookieDays.Should().Be(30);
    }

    [Test]
    public void After_Should_GiveThePeriod_OrSayTheCleanUpIsOff()
    {
        PrivacyModel.After(30, "sending").Should().Be("deleted 30 days after sending");
        PrivacyModel.After(1, "sending").Should().Be("deleted 1 day after sending");
        PrivacyModel.After(0, "sending").Should().StartWith("kept", "a period of zero switches that clean-up off, and the page must not claim otherwise");
    }

    [Test]
    public void LogRetentionDays_Should_ComeFromTheDailyFileSink()
    {
        var sut = Create(new()
        {
            ["Serilog:WriteTo:0:Name"] = "Console",
            ["Serilog:WriteTo:1:Name"] = "File",
            ["Serilog:WriteTo:1:Args:rollingInterval"] = "Day",
            ["Serilog:WriteTo:1:Args:retainedFileCountLimit"] = "30"
        });

        sut.LogRetentionDays.Should().Be(30);
    }

    [Test]
    public void LogRetentionDays_Should_BeUnknown_WhenTheConfigurationDoesNotSay()
    {
        Create().LogRetentionDays.Should().BeNull();
        Create(new()
        {
            ["Serilog:WriteTo:0:Name"] = "File",
            ["Serilog:WriteTo:0:Args:rollingInterval"] = "Hour",
            ["Serilog:WriteTo:0:Args:retainedFileCountLimit"] = "30"
        }).LogRetentionDays.Should().BeNull("thirty hourly files are not thirty days");
    }
}
