using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Pages.Account;
using Recall.Web.Services;
using Recall.Web.Services.Stats;

namespace Recall.Tests.Pages;

[TestFixture]
public sealed class StatsModelTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private Mock<IStatsService> _stats = null!;
    private StatsModel _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.UserId).Returns(UserId);

        _stats = new Mock<IStatsService>();
        Returns(UserStats.Empty);

        _sut = new StatsModel(currentUser.Object, _stats.Object, NullLogger<StatsModel>.Instance)
            .WithTempData().WithHttpContext();
    }

    private void Returns(UserStats stats) =>
        _stats.Setup(x => x.GetAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync(stats);

    private static IReadOnlyList<StatsMonth> Months(params (int Minutes, int Episodes, int Movies)[] latest)
    {
        // Twelve months ending October 2026; the given values fill the last ones.
        var months = Enumerable.Range(0, 12)
            .Select(i => new StatsMonth(new DateOnly(2025, 11, 1).AddMonths(i), 0, 0, 0))
            .ToList();

        for (var i = 0; i < latest.Length; i++)
        {
            var index = 12 - latest.Length + i;
            months[index] = months[index] with { Minutes = latest[i].Minutes, Episodes = latest[i].Episodes, Movies = latest[i].Movies };
        }

        return months;
    }

    [Test]
    public async Task ANewUser_Should_GetTheEmptyState()
    {
        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Stats.IsEmpty.Should().BeTrue();
        _sut.LoadFailed.Should().BeFalse();
        _sut.GapsText.Should().BeNull();
    }

    [Test]
    public async Task APopulatedUser_Should_GetALeadInWords_AndAChart()
    {
        Returns(UserStats.Empty with
        {
            Totals = new StatsTotals(60 * 24 * 33, Episodes: 815, Movies: 3, SeriesFinished: 2),
            Months = Months((300, 5, 0), (600, 9, 1))
        });

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Lead.Should().Be("1 month, 3 days watched, across 815 episodes and 3 movies.");
        _sut.Stats.HasChart.Should().BeTrue();
        _sut.MonthBarPercent(_sut.Stats.Months[^1]).Should().Be(100);
        _sut.MonthBarPercent(_sut.Stats.Months[^2]).Should().Be(50);
        _sut.MonthBarPercent(_sut.Stats.Months[0]).Should().Be(0);
    }

    [Test]
    public async Task TheLead_Should_NotClaimATime_WhenNothingWatchedHasAKnownLength()
    {
        Returns(UserStats.Empty with { Totals = new StatsTotals(0, Episodes: 0, Movies: 2, SeriesFinished: 0) });

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Lead.Should().Be("2 movies watched.");
    }

    [Test]
    public async Task TheBars_Should_FallBackToCounts_WhenNoMonthHasAnyWatchTime()
    {
        Returns(UserStats.Empty with
        {
            Totals = new StatsTotals(0, 6, 0, 0),
            Months = Months((0, 2, 0), (0, 4, 0))
        });

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.MonthBarPercent(_sut.Stats.Months[^1]).Should().Be(100);
        _sut.MonthBarPercent(_sut.Stats.Months[^2]).Should().Be(50);
    }

    [Test]
    public async Task OnlyBulkOrImportedHistory_Should_ExplainTheEmptyChart_AndSayWhatIsLeftOut()
    {
        // The sparse state: totals and top lists, nothing with a trustworthy date.
        Returns(UserStats.Empty with
        {
            Totals = new StatsTotals(40_000, Episodes: 815, Movies: 2, SeriesFinished: 3),
            Months = Months(),
            Undated = new StatsUndated(Episodes: 815, Movies: 2)
        });

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.Stats.IsEmpty.Should().BeFalse();
        _sut.Stats.HasChart.Should().BeFalse();
        _sut.UndatedText.Should().Be("815 episodes and 2 movies");
        _sut.NoChartText.Should().Contain("The 815 episodes and 2 movies you marked several at once or imported");
        _sut.NoChartText.Should().Contain("counted in the totals above");
    }

    [Test]
    public async Task DatedHistoryOlderThanAYear_Should_SayNothingWasMarkedLately()
    {
        Returns(UserStats.Empty with { Totals = new StatsTotals(500, 10, 0, 0), Months = Months() });

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.NoChartText.Should().EndWith("Nothing has been marked in the last twelve months.");
    }

    [Test]
    public void MonthText_Should_SayTheMonthInWords_ForScreenReaders()
    {
        StatsModel.MonthText(new StatsMonth(new DateOnly(2026, 10, 1), 580, 12, 1))
            .Should().Be("October 2026: 12 episodes, 1 movie, 9 h 40 min");
        StatsModel.MonthText(new StatsMonth(new DateOnly(2026, 9, 1), 45, 1, 0))
            .Should().Be("September 2026: 1 episode, 45 min");
        StatsModel.MonthText(new StatsMonth(new DateOnly(2026, 8, 1), 0, 0, 2))
            .Should().Be("August 2026: 2 movies");
        StatsModel.MonthText(new StatsMonth(new DateOnly(2026, 7, 1), 0, 0, 0))
            .Should().Be("July 2026: nothing");
    }

    [TestCase(3, 0, 0, "Counted without a length: 3 titles whose details aren't loaded yet.")]
    [TestCase(1, 2, 0, "Counted without a length: 1 title whose details aren't loaded yet and 2 episodes with no known length.")]
    [TestCase(1, 2, 1, "Counted without a length: 1 title whose details aren't loaded yet, 2 episodes with no known length and 1 movie with no known length.")]
    public async Task Gaps_Should_BeSaidInOneSentence(int notCached, int episodes, int movies, string expected)
    {
        Returns(UserStats.Empty with
        {
            Totals = new StatsTotals(10, 5, 5, 0),
            Gaps = new StatsGaps(notCached, episodes, movies)
        });

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.GapsText.Should().Be(expected);
    }

    [Test]
    public async Task AFailure_Should_BeA500_WithNothingMadeUp()
    {
        _stats.Setup(x => x.GetAsync(UserId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is away"));

        await _sut.OnGetAsync(CancellationToken.None);

        _sut.LoadFailed.Should().BeTrue();
        _sut.Response.StatusCode.Should().Be(500);
        _sut.Stats.Should().BeSameAs(UserStats.Empty);
    }
}
