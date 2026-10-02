using AwesomeAssertions;
using Recall.Web.Infrastructure.Display;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Tests.Infrastructure.Display;

[TestFixture]
public sealed class ImportStatusDisplayTests
{
    [Test]
    public void EveryStatus_Should_HaveWordsOfItsOwn_NeverTheEnumName()
    {
        foreach (var status in Enum.GetValues<WatchlistImportItemStatus>())
        {
            var display = ImportStatusDisplay.For(status);

            display.Label.Should().NotBeNullOrWhiteSpace();
            display.BadgeClass.Should().StartWith("tvdb-badge--");
            display.Icon.Should().StartWith("ph");
        }

        ImportStatusDisplay.For(WatchlistImportItemStatus.AlreadyInLibrary).Label.Should().Be("Already had it");
        ImportStatusDisplay.For(WatchlistImportItemStatus.NotFound).Label.Should().Be("No match");
    }

    [TestCase(WatchlistImportItemStatus.Imported, "tvdb-badge--ok")]
    [TestCase(WatchlistImportItemStatus.AlreadyInLibrary, "tvdb-badge--neutral")]
    [TestCase(WatchlistImportItemStatus.NotFound, "tvdb-badge--amber")]
    [TestCase(WatchlistImportItemStatus.Unsupported, "tvdb-badge--neutral")]
    [TestCase(WatchlistImportItemStatus.Failed, "tvdb-badge--danger")]
    public void Outcomes_Should_CarryTheirStatusColour(WatchlistImportItemStatus status, string badgeClass)
    {
        ImportStatusDisplay.For(status).BadgeClass.Should().Be(badgeClass);
    }

    [Test]
    public void Outcomes_Should_BeEveryStatusExceptWaiting()
    {
        ImportStatusDisplay.Outcomes.Should().BeEquivalentTo(
            Enum.GetValues<WatchlistImportItemStatus>().Where(s => s != WatchlistImportItemStatus.Pending));
    }
}
