using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services.Metrics;

namespace Recall.Tests.Services.Metrics;

/// <summary>
/// The buffer's counts reach <c>site_metric_daily</c> (SQLite here): added to
/// the day's row, kept when a write fails, and written at shutdown.
/// </summary>
[TestFixture]
public sealed class SiteMetricsFlusherTests
{
    private static readonly DateOnly Day = new(2026, 11, 27);

    private SqliteConnection _connection = null!;
    private ServiceProvider _services = null!;
    private SiteMetricsBuffer _buffer = null!;

    [SetUp]
    public async Task SetUpAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
        services.AddScoped<ISiteMetricRepository, SiteMetricRepository>();
        _services = services.BuildServiceProvider();

        await using (var scope = _services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();

        _buffer = new SiteMetricsBuffer(new FixedTimeProvider(new DateTimeOffset(Day, new TimeOnly(12, 0), TimeSpan.Zero)),
            NullLogger<SiteMetricsBuffer>.Instance);
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private SiteMetricsFlusher Flusher(IServiceScopeFactory? scopes = null) => new(
        _buffer, scopes ?? _services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
        NullLogger<SiteMetricsFlusher>.Instance);

    private async Task<Dictionary<string, long>> StoredAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().SiteMetrics
            .Where(x => x.Date == Day)
            .ToDictionaryAsync(x => x.Key, x => x.Count);
    }

    [Test]
    public async Task FlushAsync_Should_WriteTheCounts_AndAddToTheDaysRowOnTheNextFlush()
    {
        using var flusher = Flusher();
        _buffer.Increment(SiteMetricKeys.LinkSent);
        _buffer.Increment(SiteMetricKeys.LinkSent);
        _buffer.Increment(SiteMetricKeys.CrawlerView);

        (await flusher.FlushAsync(CancellationToken.None)).Should().Be(2);
        _buffer.Increment(SiteMetricKeys.LinkSent);
        await flusher.FlushAsync(CancellationToken.None);

        (await StoredAsync()).Should().BeEquivalentTo(new Dictionary<string, long>
        {
            [SiteMetricKeys.LinkSent] = 3,
            [SiteMetricKeys.CrawlerView] = 1
        });
        _buffer.Pending().Should().BeEmpty();
    }

    [Test]
    public async Task FlushAsync_Should_KeepTheCounts_WhenTheWriteFails_AndWriteThemNextTime()
    {
        var failing = new Mock<ISiteMetricRepository>();
        failing.Setup(x => x.AddAsync(It.IsAny<IReadOnlyCollection<SiteMetricCount>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database down"));
        var failingServices = new ServiceCollection().AddScoped(_ => failing.Object).BuildServiceProvider();

        _buffer.Increment(SiteMetricKeys.NewAccount);
        using (var down = Flusher(failingServices.GetRequiredService<IServiceScopeFactory>()))
            (await down.FlushAsync(CancellationToken.None)).Should().Be(0);

        _buffer.Pending().Should().ContainSingle(c => c.Key == SiteMetricKeys.NewAccount && c.Count == 1);

        using var up = Flusher();
        await up.FlushAsync(CancellationToken.None);
        (await StoredAsync()).Should().Equal(new Dictionary<string, long> { [SiteMetricKeys.NewAccount] = 1 });
        await failingServices.DisposeAsync();
    }

    [Test]
    public async Task StopAsync_Should_WriteWhatIsLeft_SoAShutdownLosesNothing()
    {
        using var flusher = Flusher();
        await flusher.StartAsync(CancellationToken.None);

        _buffer.Increment(SiteMetricKeys.CodeRedeemed);
        _buffer.Increment(SiteMetricKeys.PageView(SitePage.Series, signedIn: false));
        await flusher.StopAsync(CancellationToken.None);

        (await StoredAsync()).Should().BeEquivalentTo(new Dictionary<string, long>
        {
            [SiteMetricKeys.CodeRedeemed] = 1,
            ["view.series.anon"] = 1
        });
    }

    [Test]
    public async Task TheTable_Should_HoldADateAKeyAndACount_AndNothingElse()
    {
        await using var scope = _services.CreateAsyncScope();
        var entity = scope.ServiceProvider.GetRequiredService<AppDbContext>().Model.FindEntityType("Recall.Web.Infrastructure.Persistence.Entities.SiteMetricDailyEntity")!;

        entity.GetProperties().Select(p => p.GetColumnName()).Should().BeEquivalentTo("date", "key", "count");
        entity.GetForeignKeys().Should().BeEmpty("the counts belong to nobody");
    }
}
