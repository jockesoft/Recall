using AwesomeAssertions;
using Recall.Web.Infrastructure.Authentication;

namespace Recall.Tests.Infrastructure.Authentication;

[TestFixture]
public sealed class LocalReturnUrlTests
{
    [TestCase("/")]
    [TestCase("/Series/Details/81189")]
    [TestCase("/Search?q=the+bear&type=series")]
    [TestCase("/Library?section=watching#watching-card-3")]
    public void APathOnThisSite_Should_BeKept(string url) => LocalReturnUrl.Normalize(url).Should().Be(url);

    [TestCase("https://evil.example/")]
    [TestCase("http://evil.example")]
    [TestCase("//evil.example/Series/Details/1")]
    [TestCase("/\\evil.example")]
    [TestCase("\\\\evil.example")]
    [TestCase("javascript:alert(1)")]
    [TestCase("Series/Details/1")]
    [TestCase("~/Series/Details/1")]
    [TestCase("/Series\r\nLocation: https://evil.example")]
    [TestCase("/Series\tDetails")]
    [TestCase("")]
    [TestCase(" /Series/Details/1")]
    [TestCase(null)]
    public void AnythingElse_Should_BeDropped(string? url) => LocalReturnUrl.Normalize(url).Should().BeNull();

    [Test]
    public void AnAddressLongerThanTheColumn_Should_BeDropped()
    {
        LocalReturnUrl.Normalize("/" + new string('a', LocalReturnUrl.MaxLength - 1)).Should().NotBeNull();
        LocalReturnUrl.Normalize("/" + new string('a', LocalReturnUrl.MaxLength)).Should().BeNull();
    }
}
