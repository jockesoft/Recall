using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Recall.Web.Services.Digest;

namespace Recall.Tests.Services.Digest;

[TestFixture]
public sealed class DigestUnsubscribeTokensTests
{
    private IDataProtectionProvider _provider = null!;
    private DigestUnsubscribeTokens _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _provider = new EphemeralDataProtectionProvider();
        _sut = new DigestUnsubscribeTokens(_provider);
    }

    [Test]
    public void AToken_Should_RoundTrip_AndFitInAUrl()
    {
        var userId = Guid.NewGuid();

        var token = _sut.Create(userId);

        token.Should().MatchRegex("^[A-Za-z0-9_-]+$", "it goes into a query string as it is");
        _sut.TryRead(token, out var read).Should().BeTrue();
        read.Should().Be(userId);
    }

    [Test]
    public void AToken_Should_NotExpire_OrCarryAnythingReadable()
    {
        var userId = Guid.NewGuid();
        var token = _sut.Create(userId);

        token.Should().NotContain(userId.ToString("N")[..8], "the user id is encrypted, not just signed");
        // The protector is not time-limited: there is nothing in the token that could run out.
        _sut.TryRead(token, out _).Should().BeTrue();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not-a-token")]
    [TestCase("%%%%")]
    public void Nonsense_Should_BeRejected(string? token)
    {
        _sut.TryRead(token, out var userId).Should().BeFalse();
        userId.Should().Be(Guid.Empty);
    }

    [Test]
    public void ATamperedToken_Should_BeRejected()
    {
        var token = _sut.Create(Guid.NewGuid());
        var flipped = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');

        _sut.TryRead(flipped, out _).Should().BeFalse();
        _sut.TryRead(token + "AAAA", out _).Should().BeFalse();
        _sut.TryRead(new string('A', 4000), out _).Should().BeFalse("an absurdly long value is refused outright");
    }

    [Test]
    public void ATokenIssuedForAnotherPurpose_Should_BeRejected()
    {
        // The same keys, another purpose: what a token from some other feature would be.
        var other = _provider.CreateProtector("Recall.SomethingElse.v1");
        var foreign = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(other.Protect(Guid.NewGuid().ToByteArray()));

        _sut.TryRead(foreign, out _).Should().BeFalse();
    }

    [Test]
    public void ATokenFromAnotherKeyRing_Should_BeRejected()
    {
        var elsewhere = new DigestUnsubscribeTokens(new EphemeralDataProtectionProvider());

        _sut.TryRead(elsewhere.Create(Guid.NewGuid()), out _).Should().BeFalse("if the keys were lost, old links stop working rather than misfire");
    }
}
