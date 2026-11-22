using AwesomeAssertions;
using Recall.Web.Pages.Shared;

namespace Recall.Tests.Pages;

/// <summary>
/// The navbar account menu's accessible name starts with the initials it shows
/// (WCAG 2.5.3, label in name; axe's label-content-name-mismatch), then says
/// what it is.
/// </summary>
[TestFixture]
public sealed class AvatarModelTests
{
    [TestCase("dev-user", "DU, account menu")]
    [TestCase("Bryan Cranston", "BC, account menu")]
    [TestCase("madonna", "M, account menu")]
    [TestCase("42 1337", "Account menu")]
    [TestCase(null, "Account menu")]
    public void AccountMenuLabel_Should_StartWithTheVisibleInitials(string? name, string expected)
    {
        AvatarModel.AccountMenuLabel(name).Should().Be(expected);
    }
}
