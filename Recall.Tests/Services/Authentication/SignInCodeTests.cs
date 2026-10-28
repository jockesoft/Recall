using AwesomeAssertions;
using Recall.Web.Services.Authentication;

namespace Recall.Tests.Services.Authentication;

[TestFixture]
public class SignInCodeTests
{
    [Test]
    public void Generate_Should_GiveSixDigits()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => SignInCode.Generate()).ToList();

        codes.Should().OnlyContain(c => c.Length == 6 && c.All(char.IsAsciiDigit));
        codes.Distinct().Count().Should().BeGreaterThan(190, "random, not a counter");
    }

    [TestCase("482913", "482 913")]
    [TestCase("007001", "007 001")]
    public void Grouped_Should_SplitIntoTwoGroupsOfThree(string code, string grouped)
    {
        SignInCode.Grouped(code).Should().Be(grouped);
    }

    [TestCase("482913", "482913")]
    [TestCase("482 913", "482913")]
    [TestCase(" 4 8 2 9 1 3 ", "482913")]
    [TestCase("482 913", "482913")]
    [TestCase("48291", null)]
    [TestCase("4829134", null)]
    [TestCase("48a913", null)]
    [TestCase("482-913", null)]
    [TestCase("٤٨٢٩١٣", null, Description = "digits, but not ASCII ones")]
    [TestCase("", null)]
    [TestCase(null, null)]
    public void Normalize_Should_DropSpaces_AndAcceptOnlySixAsciiDigits(string? typed, string? expected)
    {
        SignInCode.Normalize(typed).Should().Be(expected);
    }

    [Test]
    public void Matches_Should_AcceptTheCode_ForItsOwnTokenOnly()
    {
        var token = Guid.NewGuid();
        var hash = SignInCode.Hash("482913", token);

        SignInCode.Matches("482913", token, hash).Should().BeTrue();
        SignInCode.Matches("482914", token, hash).Should().BeFalse();
        SignInCode.Matches("482913", Guid.NewGuid(), hash).Should().BeFalse("the hash is salted with the token id");
        SignInCode.Matches("482913", token, "not base64!").Should().BeFalse();
        SignInCode.Hash("482913", Guid.NewGuid()).Should().NotBe(hash);
    }
}
