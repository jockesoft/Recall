using AwesomeAssertions;
using Recall.Web.Infrastructure.Display;

namespace Recall.Tests.Infrastructure.Display;

[TestFixture]
public sealed class VoteCountTests
{
    [TestCase("16180", "16,180", Description = "an episode's, as OMDb sends it")]
    [TestCase("315,074", "315,074", Description = "a series', with OMDb's own separators")]
    [TestCase("1234567", "1,234,567")]
    [TestCase("987", "987")]
    [TestCase(" 12,345 ", "12,345")]
    public void Format_Should_WriteThousandsSeparators(string raw, string expected) =>
        VoteCount.Format(raw).Should().Be(expected);

    [TestCase(null)]
    [TestCase("")]
    [TestCase("N/A")]
    [TestCase("n/a")]
    public void Format_Should_BeNothing_WithoutACount(string? raw) =>
        VoteCount.Format(raw).Should().BeNull();

    [Test]
    public void Format_Should_LeaveSomethingThatIsNotACount_AsGiven() =>
        VoteCount.Format("lots").Should().Be("lots");
}
