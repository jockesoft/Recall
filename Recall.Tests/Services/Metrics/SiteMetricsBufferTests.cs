using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Recall.Tests.TestSupport;
using Recall.Web.Services.Metrics;

namespace Recall.Tests.Services.Metrics;

[TestFixture]
public sealed class SiteMetricsBufferTests
{
    private static readonly DateTimeOffset LateEvening = new(2026, 11, 27, 23, 59, 0, TimeSpan.Zero);

    private FixedTimeProvider _clock = null!;
    private SiteMetricsBuffer _buffer = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new FixedTimeProvider(LateEvening);
        _buffer = new SiteMetricsBuffer(_clock, NullLogger<SiteMetricsBuffer>.Instance);
    }

    [Test]
    public void Increment_Should_CountByUtcDateAndKey()
    {
        _buffer.Increment(SiteMetricKeys.LinkSent);
        _buffer.Increment(SiteMetricKeys.LinkSent);
        _buffer.Increment(SiteMetricKeys.CrawlerView);
        _clock.Now = LateEvening.AddMinutes(2); // past midnight UTC: a new day
        _buffer.Increment(SiteMetricKeys.LinkSent);

        _buffer.Drain().Should().BeEquivalentTo(new[]
        {
            new SiteMetricCount(new DateOnly(2026, 11, 27), SiteMetricKeys.LinkSent, 2),
            new SiteMetricCount(new DateOnly(2026, 11, 27), SiteMetricKeys.CrawlerView, 1),
            new SiteMetricCount(new DateOnly(2026, 11, 28), SiteMetricKeys.LinkSent, 1)
        });
    }

    [Test]
    public void Increment_Should_IgnoreAKeyThatIsNotKnown_SoNothingFromARequestCanBecomeAKey()
    {
        _buffer.Increment("view./series/42?ref=mail.anon");
        _buffer.Increment("203.0.113.7");

        _buffer.Pending().Should().BeEmpty();
    }

    [Test]
    public void Drain_Should_EmptyTheBuffer_AndRestoreShouldPutCountsBack()
    {
        _buffer.Increment(SiteMetricKeys.NewAccount);
        var drained = _buffer.Drain();

        _buffer.Pending().Should().BeEmpty();
        _buffer.Increment(SiteMetricKeys.NewAccount);
        _buffer.Restore(drained);

        _buffer.Drain().Should().Equal(new SiteMetricCount(new DateOnly(2026, 11, 27), SiteMetricKeys.NewAccount, 2));
    }

    [Test]
    public async Task Increment_Should_LoseNothing_WhenCountedFromManyThreadsWhileDraining()
    {
        var drained = new List<SiteMetricCount>();
        using var stop = new CancellationTokenSource();
        var drainer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                drained.AddRange(_buffer.Drain());
                await Task.Yield();
            }
        });

        Parallel.For(0, 20_000, _ => _buffer.Increment(SiteMetricKeys.CrawlerView));
        await stop.CancelAsync();
        await drainer;
        drained.AddRange(_buffer.Drain());

        drained.Sum(count => count.Count).Should().Be(20_000);
    }

    [Test]
    public void EveryKnownKey_Should_BeShort_AndMadeOfFixedWordsOnly()
    {
        foreach (var key in SiteMetricKeys.All)
        {
            key.Length.Should().BeLessThanOrEqualTo(64, "the column holds 64 characters");
            key.Should().MatchRegex("^[a-z]+(\\.[a-z-]+)+$", "a key is made of fixed words, never of anything from a request");
        }
    }
}
