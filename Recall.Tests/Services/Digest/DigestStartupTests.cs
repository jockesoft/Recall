using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Recall.Web.Extensions;
using Recall.Web.Services.Digest;

namespace Recall.Tests.Services.Digest;

/// <summary>The digest cannot be switched on half-configured: enabled without a public base URL fails startup.</summary>
[TestFixture]
public class DigestStartupTests
{
    private static void Register(params (string Key, string? Value)[] settings) =>
        new ServiceCollection().AddWeeklyDigest(
            new ConfigurationBuilder()
                .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
                .Build());

    [Test]
    public void ByDefault_TheDigest_Should_BeOff_AndNeedNoBaseUrl()
    {
        var act = () => Register();

        act.Should().NotThrow();
        new DigestOptions().Enabled.Should().BeFalse();
        new SiteOptions().BaseUrl.Should().BeNull("there is no default: it must be set per environment");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("recall.nu")]
    [TestCase("ftp://recall.nu")]
    public void EnabledWithoutAUsableBaseUrl_Should_FailStartup_WithAMessageThatSaysWhatToSet(string? baseUrl)
    {
        var act = () => Register(("Digest:Enabled", "true"), ("Site:BaseUrl", baseUrl));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Digest:Enabled*Site:BaseUrl*Site__BaseUrl*");
    }

    [TestCase("https://recall.nu", "https://recall.nu")]
    [TestCase("https://recall.nu/", "https://recall.nu")]
    [TestCase("http://localhost:7461", "http://localhost:7461")]
    [TestCase("https://example.com/recall/", "https://example.com/recall")]
    public void EnabledWithABaseUrl_Should_Start_AndTheUrlShouldLoseItsTrailingSlash(string baseUrl, string normalized)
    {
        var act = () => Register(("Digest:Enabled", "true"), ("Site:BaseUrl", baseUrl));

        act.Should().NotThrow();
        new SiteOptions { BaseUrl = baseUrl }.NormalizedBaseUrl.Should().Be(normalized);
    }

    [Test]
    public void AnHourOutsideTheDay_Should_FailStartup()
    {
        var act = () => Register(("Digest:HourUtc", "24"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*HourUtc*");
    }
}
