using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Quartz;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Filter;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Infrastructure.Timers;
using Recall.Web.Services.Discover;
using Recall.Web.Services.External.TheTvDb;
using Recall.Web.Services.Stats;

namespace Recall.Tests.Services.Discover;

/// <summary>
/// The Discover job (<see cref="DiscoverListRefresher"/>): a run fetches only
/// when a fixed list is due (daily, or never fetched), then every due list,
/// the genre lists only for genres in some user's top two; it stores what
/// passes the quality filter, keeps the stored list when a fetch fails or
/// comes back empty, and logs one line with the request total.
/// </summary>
[TestFixture]
public sealed class DiscoverListRefresherTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 5, 0, 0, DateTimeKind.Utc);
    private static readonly string[] FixedKeys = ["series-new-2026", "series-popular-2026", "movie-year-2026", "series-alltime", "movie-alltime"];

    private Mock<ITheTvDbApiClient> _api = null!;
    private Mock<IDiscoverListRepository> _lists = null!;
    private Mock<IAppUserRepository> _users = null!;
    private Mock<IStatsService> _stats = null!;
    private Dictionary<string, DateTime> _fetched = null!;
    private Dictionary<string, IReadOnlyList<DiscoverTitle>> _saved = null!;
    private RecordingLogger<DiscoverListRefresher> _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _api = new Mock<ITheTvDbApiClient>(MockBehavior.Strict);
        _lists = new Mock<IDiscoverListRepository>();
        _users = new Mock<IAppUserRepository>();
        _stats = new Mock<IStatsService>();
        _fetched = new Dictionary<string, DateTime>();
        _saved = new Dictionary<string, IReadOnlyList<DiscoverTitle>>();
        _logger = new RecordingLogger<DiscoverListRefresher>();

        _lists.Setup(x => x.GetFetchedUtcAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _fetched);
        _lists.Setup(x => x.SaveAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<DiscoverTitle>>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback((string key, IReadOnlyList<DiscoverTitle> titles, DateTime _, CancellationToken _) => _saved[key] = titles)
            .Returns(Task.CompletedTask);
        _users.Setup(x => x.GetIdsWithWatchesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        // Every filter request answers with one good title, unless a test says otherwise.
        _api.Setup(x => x.FilterSeriesAsync(It.IsAny<TitleFilter>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>())).ReturnsAsync([Record(1)]);
        _api.Setup(x => x.FilterMoviesAsync(It.IsAny<TitleFilter>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>())).ReturnsAsync([Record(2)]);
    }

    [Test]
    public async Task AnEmptyTable_Should_FetchEveryFixedList()
    {
        var result = await Sut().RunAsync();

        result.Due.Should().BeTrue();
        result.Refreshed.Should().Be(5);
        _saved.Keys.Should().BeEquivalentTo(FixedKeys);
        _api.Verify(x => x.FilterSeriesAsync(new TitleFilter(TitleFilterSort.FirstAired, 2026, null), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Once);
        _api.Verify(x => x.FilterMoviesAsync(new TitleFilter(TitleFilterSort.Score, 2026, null), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ListsFetchedWithinADay_Should_NotBeFetchedAgain()
    {
        foreach (var key in FixedKeys)
            _fetched[key] = Now.AddHours(-5);

        var result = await Sut().RunAsync();

        result.Should().Be(DiscoverRefreshResult.NotDue);
        _api.VerifyNoOtherCalls();
        _users.Verify(x => x.GetIdsWithWatchesAsync(It.IsAny<CancellationToken>()), Times.Never, "a run that is not due does no per-user work");
    }

    [Test]
    public async Task OneDueList_Should_BeTheOnlyOneFetched()
    {
        foreach (var key in FixedKeys)
            _fetched[key] = Now.AddHours(-5);
        _fetched["movie-alltime"] = Now.AddHours(-23);

        var result = await Sut().RunAsync();

        result.Refreshed.Should().Be(1);
        _saved.Keys.Should().Equal("movie-alltime");
    }

    [Test]
    public async Task GenreLists_Should_BeFetchedForTheGenresInSomeUsersTopTwo_Once()
    {
        var (alice, bob, carol) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        _users.Setup(x => x.GetIdsWithWatchesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([alice, bob, carol]);
        _stats.Setup(x => x.GetAsync(alice, It.IsAny<CancellationToken>())).ReturnsAsync(Stats("Crime", "Drama", "Comedy"));
        _stats.Setup(x => x.GetAsync(bob, It.IsAny<CancellationToken>())).ReturnsAsync(Stats("Drama", "Reality"));
        _stats.Setup(x => x.GetAsync(carol, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("cache down"));

        var result = await Sut().RunAsync();

        result.Users.Should().Be(3);
        result.Genres.Should().Be(3, "Crime, Drama and Reality: Comedy is only Alice's third");
        _saved.Keys.Except(FixedKeys).Should().BeEquivalentTo(
            "series-genre-14", "movie-genre-14", "series-genre-12", "movie-genre-12", "series-genre-3", "movie-genre-3");
        result.Refreshed.Should().Be(11, "five fixed lists and two for each of three genres; a user whose genres fail costs nothing");
    }

    [Test]
    public async Task OnlyTitlesThatPassTheQualityFilter_Should_BeStored_OncePerTitle()
    {
        _api.Setup(x => x.FilterSeriesAsync(It.IsAny<TitleFilter>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            Record(1),
            Record(1),                              // the same title twice
            Record(2, image: null),                 // no poster
            Record(3, score: 40),                   // too obscure
            Record(4, year: "9696"),                // junk year
            new FilterRecordDto { Id = 5, Name = "" }, // no name
            Record(6)
        ]);

        await Sut().RunAsync();

        _saved["series-popular-2026"].Select(t => t.TvdbId).Should().Equal(1, 6);
    }

    [Test]
    public async Task AnOrderedList_Should_KeepItsFirst150()
    {
        _api.Setup(x => x.FilterSeriesAsync(It.IsAny<TitleFilter>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(1, 500).Select(i => Record(i)).ToList());

        await Sut().RunAsync();

        _saved["series-popular-2026"].Should().HaveCount(150);
        _saved["series-new-2026"].Should().HaveCount(500);
    }

    [Test]
    public async Task AFailedFetch_Should_KeepTheStoredList_AndNotStopTheOthers()
    {
        _api.Setup(x => x.FilterSeriesAsync(It.Is<TitleFilter>(f => f.Year == null), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TheTvDbApiException("down", 503));

        var result = await Sut().RunAsync();

        result.Failed.Should().Be(1);
        result.Refreshed.Should().Be(4);
        _saved.Should().NotContainKey("series-alltime", "the list stored before stays as it was");
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("could not refresh series-alltime"));
    }

    [Test]
    public async Task EveryFilterRequest_Should_GetTheListAttemptTimeout_FromTheOptions()
    {
        await Sut().RunAsync();

        _api.Verify(x => x.FilterSeriesAsync(It.IsAny<TitleFilter>(), TimeSpan.FromSeconds(30), It.IsAny<CancellationToken>()), Times.Exactly(3));
        _api.Verify(x => x.FilterMoviesAsync(It.IsAny<TitleFilter>(), TimeSpan.FromSeconds(30), It.IsAny<CancellationToken>()), Times.Exactly(2));

        await Sut(new DiscoverOptions { FilterAttemptTimeoutSeconds = 45 }).RunAsync();

        _api.Verify(x => x.FilterSeriesAsync(It.IsAny<TitleFilter>(), TimeSpan.FromSeconds(45), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Test]
    public async Task AListThatTimesOut_Should_KeepTheStoredList_AndNotEndTheRun()
    {
        // What the client throws when every attempt ran out: a cancellation, but not the job's.
        _api.Setup(x => x.FilterSeriesAsync(It.Is<TitleFilter>(f => f.Year == null), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("attempt timeout", new TimeoutException()));

        var result = await Sut().RunAsync();

        result.Failed.Should().Be(1);
        result.Refreshed.Should().Be(4, "the other lists are still fetched");
        _saved.Should().NotContainKey("series-alltime");
    }

    [Test]
    public async Task TheJobsOwnCancellation_Should_EndTheRun()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _api.Setup(x => x.FilterSeriesAsync(It.IsAny<TitleFilter>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cancelled.Token));

        var act = () => Sut().RunAsync(cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task AnEmptyAnswer_Should_KeepTheStoredList()
    {
        _api.Setup(x => x.FilterMoviesAsync(It.IsAny<TitleFilter>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>())).ReturnsAsync([Record(9, score: 1)]);

        var result = await Sut().RunAsync();

        result.Failed.Should().Be(2, "both movie lists came back with nothing usable");
        _saved.Keys.Should().NotContain(["movie-year-2026", "movie-alltime"]);
    }

    [Test]
    public async Task ListsNoLongerNeeded_Should_BeRemovedOnceAMonthOld()
    {
        await Sut().RunAsync();

        _lists.Verify(x => x.DeleteUnusedAsync(
            It.Is<IReadOnlyCollection<string>>(keep => keep.OrderBy(k => k).SequenceEqual(FixedKeys.OrderBy(k => k))),
            Now.AddDays(-30), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ARun_Should_LogOneLine_WithTheRequestTotal()
    {
        await Sut().RunAsync();

        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information).Which.Message.Should().Be(
            "DiscoverListsTimer: 0 TheTVDB request(s) this run: 5 list(s) refreshed, 0 failed, 0 genre(s) from 0 user(s), 0 unused list(s) removed.");
    }

    [Test]
    public async Task TheJob_Should_NeverLetAnExceptionEscape()
    {
        var refresher = new Mock<IDiscoverListRefresher>();
        refresher.Setup(x => x.RunAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));
        var job = new DiscoverListsTimer(refresher.Object, NullLogger<DiscoverListsTimer>.Instance);

        var act = async () => await job.Execute(Mock.Of<IJobExecutionContext>());

        await act.Should().NotThrowAsync("an exception must not escape into the scheduler");
        refresher.Verify(x => x.RunAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private DiscoverListRefresher Sut(DiscoverOptions? options = null) => new(
        _api.Object, _lists.Object, _users.Object, _stats.Object,
        Options.Create(options ?? new DiscoverOptions()), new FixedTimeProvider(Now), _logger);

    private static FilterRecordDto Record(int id, string? image = "/banners/x.jpg", double score = 5000, string year = "2026") => new()
    {
        Id = id, Name = $"Title {id}", Image = image, Year = year, FirstAired = "2026-03-01", Score = score,
        Status = new StatusDto { Name = "Continuing" }
    };

    private static UserStats Stats(params string[] genres) =>
        UserStats.Empty with
        {
            TopGenres = genres.Select((name, i) => new StatsGenreRow(name, 1000 - i, 3)).ToList(),
            GenreCoverage = new StatsGenreCoverage(10, 10)
        };
}
