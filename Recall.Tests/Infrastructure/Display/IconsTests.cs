using System.Reflection;
using AwesomeAssertions;
using Recall.Web.Infrastructure.Display;

namespace Recall.Tests.Infrastructure.Display;

/// <summary>
/// An icon class that does not exist renders as nothing, and nobody notices
/// until a user does. This checks every constant in <see cref="Icons"/>
/// against the vendored Phosphor stylesheets.
/// </summary>
[TestFixture]
public sealed class IconsTests
{
    private static IEnumerable<TestCaseData> AllIcons() =>
        typeof(Icons)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => new TestCaseData(f.Name, (string)f.GetRawConstantValue()!).SetName($"Icon_{f.Name}_Should_ExistInPhosphor"));

    [TestCaseSource(nameof(AllIcons))]
    public void Icon_Should_ExistInTheVendoredStylesheet(string name, string cssClass)
    {
        var parts = cssClass.Split(' ');
        parts.Should().HaveCount(2, "an icon is a weight class and an icon class");
        var (weight, icon) = (parts[0], parts[1]);
        weight.Should().BeOneOf("ph", "ph-fill");

        var stylesheet = File.ReadAllText(Path.Combine(
            WebRoot(), "lib", "phosphor-icons", "src", weight == "ph" ? "regular" : "fill", "style.css"));

        stylesheet.Should().Contain($".{weight}.{icon}:before", $"Icons.{name} must name an icon Phosphor has");
    }

    private static string WebRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Recall.sln")))
            directory = directory.Parent;

        directory.Should().NotBeNull("the tests run from inside the repository");
        return Path.Combine(directory!.FullName, "Recall.Web", "wwwroot");
    }
}
