using AwesomeAssertions;
using Recall.Web.Infrastructure.Display;

namespace Recall.Tests.Infrastructure.Display;

[TestFixture]
public sealed class InitialsTests
{
    [TestCase("Bryan Cranston", "BC")]
    [TestCase("dev-user", "DU")]
    [TestCase("joakim.fredlund", "JF")]
    [TestCase("some_long_user_name", "SN")]
    [TestCase("madonna", "M")]
    [TestCase("Jean-Claude Van Damme", "JD")]
    [TestCase("  åsa   öberg ", "ÅÖ")]
    [TestCase("42 1337", "?")]
    [TestCase("", "?")]
    [TestCase(null, "?")]
    public void Of_Should_TakeTheFirstAndLastWord(string? name, string expected)
    {
        Initials.Of(name).Should().Be(expected);
    }
}
