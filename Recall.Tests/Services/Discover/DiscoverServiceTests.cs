using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services.Discover;
using Recall.Web.Services.Stats;

namespace Recall.Tests.Services.Discover;

/// <summary>
/// <see cref="DiscoverService"/>: a page is built from the stored lists the
/// user's genres need, what the user knows, and their Stats genres (which a
/// failure turns into "All-time favourites"); a POST's title is looked up in
/// those same lists. The service has no TheTVDB client to call.
/// </summary>
[TestFixture]
public sealed class DiscoverServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid User = Guid.NewGuid();

    private Mock<IDiscoverListRepository> _lists = null!;
    private Mock<IDiscoverUserRepository> _users = null!;
    private Mock<IStatsService> _stats = null!;
    private IReadOnlyCollection<string>? _keysRead;

    [SetUp]
    public void SetUp()
    {
        _lists = new Mock<IDiscoverListRepository>(MockBehavior.Strict);
        _users = new Mock<IDiscoverUserRepository>(MockBehavior.Strict);
        _stats = new Mock<IStatsService>(MockBehavior.Strict);

        _users.Setup(x => x.GetKnownTitlesAsync(User, It.IsAny<CancellationToken>())).ReturnsAsync(KnownTitles.None);
        _lists.Setup(x => x.GetAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyCollection<string> keys, CancellationToken _) => _keysRead = keys)
            .ReturnsAsync(new Dictionary<string, DiscoverList>
            {
                ["series-genre-14"] = new("series-genre-14", Enumerable.Range(1, 8).Select(i => Title(i, 2024)).ToList(), Now),
                ["series-alltime"] = new("series-alltime", Enumerable.Range(101, 8).Select(i => Title(i, 1999)).ToList(), Now)
            });
    }

    [Test]
    public async Task ThePage_Should_ReadTheListsOfTheUsersGenres()
    {
        _stats.Setup(x => x.GetAsync(User, It.IsAny<CancellationToken>())).ReturnsAsync(Stats("Crime"));

        var page = await Sut().GetPageAsync(User);

        _keysRead.Should().Contain(["series-genre-14", "movie-genre-14", "series-popular-2026"]);
        page.Rows.Select(r => r.Heading).Should().Equal("Popular in Crime");
    }

    [Test]
    public async Task StatsThatFail_Should_GiveAllTimeFavourites()
    {
        _stats.Setup(x => x.GetAsync(User, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("cache down"));

        var page = await Sut().GetPageAsync(User);

        _keysRead.Should().NotContain(k => k.Contains("genre"));
        page.Rows.Select(r => r.Heading).Should().Equal("All-time favourites");
    }

    [Test]
    public async Task Find_Should_ReturnTheTitle_WhenAListThePageReadsHasIt()
    {
        _stats.Setup(x => x.GetAsync(User, It.IsAny<CancellationToken>())).ReturnsAsync(Stats("Crime"));

        (await Sut().FindAsync(User, DiscoverKind.Series, 3))!.Name.Should().Be("Title 3");
        (await Sut().FindAsync(User, DiscoverKind.Movie, 3)).Should().BeNull("a movie with that id was not offered");
        (await Sut().FindAsync(User, DiscoverKind.Series, 999)).Should().BeNull("an id in no list was not offered");
    }

    [Test]
    public async Task Find_Should_RefuseAnIdThatIsNotPositive_WithoutReadingAnything()
    {
        (await Sut().FindAsync(User, DiscoverKind.Series, 0)).Should().BeNull();
        _lists.Verify(x => x.GetAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private DiscoverService Sut() => new(
        _lists.Object, _users.Object, _stats.Object, Options.Create(new DiscoverOptions()),
        new FixedTimeProvider(Now), NullLogger<DiscoverService>.Instance);

    private static DiscoverTitle Title(int id, int year) => new()
    {
        Kind = DiscoverKind.Series, TvdbId = id, Name = $"Title {id}", Year = year, FirstAired = new DateOnly(year, 1, 1),
        ImageUrl = "https://artworks.thetvdb.com/p.jpg", Status = "Ended", Score = 10_000 - id
    };

    private static UserStats Stats(params string[] genres) =>
        UserStats.Empty with
        {
            TopGenres = genres.Select((name, i) => new StatsGenreRow(name, 1000 - i, 3)).ToList(),
            GenreCoverage = new StatsGenreCoverage(10, 10)
        };
}
