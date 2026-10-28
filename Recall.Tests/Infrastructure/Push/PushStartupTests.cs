using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Push;

namespace Recall.Tests.Infrastructure.Push;

/// <summary>
/// Push cannot be switched on half-configured: enabled without a VAPID key pair
/// that belongs together and a mailto: subject, startup fails and says what to set.
/// </summary>
[TestFixture]
public class PushStartupTests
{
    private static readonly VapidKeys.KeyPair Keys = VapidKeys.Generate();

    private static void Register(params (string Key, string? Value)[] settings) =>
        new ServiceCollection().AddPush(
            new ConfigurationBuilder()
                .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
                .Build());

    [Test]
    public void ByDefault_Push_Should_BeOff_AndNeedNoKeys()
    {
        var act = () => Register();

        act.Should().NotThrow();
        new PushOptions().Enabled.Should().BeFalse();
    }

    [Test]
    public void EnabledWithoutKeys_Should_FailStartup_WithAMessageThatSaysWhatToSet()
    {
        var act = () => Register(("Push:Enabled", "true"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Push:Enabled*Push:PublicKey*generate-vapid-keys*Push__PublicKey*Push__PrivateKey*Push__Subject*");
    }

    [TestCase("not-a-key", null, "*PublicKey is not*")]
    [TestCase(null, "not-a-key", "*PrivateKey is not*")]
    public void EnabledWithAMalformedKey_Should_FailStartup(string? publicKey, string? privateKey, string message)
    {
        var act = () => Register(
            ("Push:Enabled", "true"),
            ("Push:PublicKey", publicKey ?? Keys.PublicKey),
            ("Push:PrivateKey", privateKey ?? Keys.PrivateKey),
            ("Push:Subject", "mailto:admin@recall.nu"));

        act.Should().Throw<InvalidOperationException>().WithMessage(message);
    }

    [Test]
    public void EnabledWithKeysFromTwoDifferentPairs_Should_FailStartup()
    {
        var other = VapidKeys.Generate();

        var act = () => Register(
            ("Push:Enabled", "true"),
            ("Push:PublicKey", Keys.PublicKey),
            ("Push:PrivateKey", other.PrivateKey),
            ("Push:Subject", "mailto:admin@recall.nu"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*not the two halves of one key pair*");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("admin@recall.nu")]
    [TestCase("https://recall.nu")]
    [TestCase("mailto:")]
    public void EnabledWithoutAMailtoSubject_Should_FailStartup(string? subject)
    {
        var act = () => Register(
            ("Push:Enabled", "true"),
            ("Push:PublicKey", Keys.PublicKey),
            ("Push:PrivateKey", Keys.PrivateKey),
            ("Push:Subject", subject));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Push:Subject*mailto:*");
    }

    [Test]
    public void EnabledWithAGeneratedPairAndASubject_Should_Start()
    {
        var act = () => Register(
            ("Push:Enabled", "true"),
            ("Push:PublicKey", Keys.PublicKey),
            ("Push:PrivateKey", Keys.PrivateKey),
            ("Push:Subject", "mailto:admin@recall.nu"));

        act.Should().NotThrow();
    }

    [Test]
    public void Generate_Should_GiveKeysInTheFormBrowsersAndPushServicesExpect()
    {
        var keys = VapidKeys.Generate();

        keys.PublicKey.Should().HaveLength(87).And.MatchRegex("^[A-Za-z0-9_-]+$", "base64url without padding");
        keys.PrivateKey.Should().HaveLength(43).And.MatchRegex("^[A-Za-z0-9_-]+$");
        keys.Should().NotBe(VapidKeys.Generate(), "every call makes a new pair");
        VapidKeys.Problem(new PushOptions { PublicKey = keys.PublicKey, PrivateKey = keys.PrivateKey, Subject = "mailto:a@b.c" })
            .Should().BeNull();
    }
}
