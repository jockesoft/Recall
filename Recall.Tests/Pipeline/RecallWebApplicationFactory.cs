using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Services;

namespace Recall.Tests.Pipeline;

/// <summary>
/// Starts the real application — <c>Program.cs</c>, the whole middleware
/// pipeline, the Razor views — with everything outside the process swapped out:
/// <list type="bullet">
/// <item>Postgres → one in-memory SQLite database, created from the EF model;</item>
/// <item>Redis → an in-memory distributed cache;</item>
/// <item>TheTVDB → <see cref="TheTvDb"/>, a mock that knows one series;</item>
/// <item>the Quartz scheduler → not started, so no job runs during a test.</item>
/// </list>
/// The environment is <c>Test</c>, which is what makes <c>DevAuthMiddleware</c>
/// stand aside: requests arrive anonymous, exactly as in production.
/// </summary>
public sealed class RecallWebApplicationFactory : WebApplicationFactory<Program>
{
    public const int KnownSeriesId = 42;
    public const string KnownSeriesName = "Pipeline Test Series";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public Mock<ITheTvDbService> TheTvDb { get; } = new();

    public RecallWebApplicationFactory()
    {
        // Kept open for the factory's lifetime: an in-memory SQLite database
        // lives only as long as its connection.
        _connection.Open();

        TheTvDb
            .Setup(x => x.GetSeriesAggregateByIdAsync(KnownSeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesAggregate
            {
                TvdbId = KnownSeriesId,
                Name = KnownSeriesName,
                Slug = "pipeline-test-series",
                Overview = "An overview that only the pipeline tests will ever read.",
                FirstAired = new DateOnly(2020, 1, 1),
                Status = new SeriesStatus { Name = "Ended", KeepUpdated = false },
                Seasons = [new SeasonSummary { Id = 1, Number = 1, Name = "Season 1" }],
                Episodes =
                [
                    new EpisodeSummary { Id = 4201, SeasonNumber = 1, EpisodeNumber = 1, Name = "Pilot Episode", Aired = new DateOnly(2020, 1, 1) }
                ]
            });
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        // UseSetting, not ConfigureAppConfiguration: Program.cs reads configuration
        // while it is still registering services, before later sources are applied.
        builder.UseSetting("Database:MigrateOnStartup", "false");     // the migrations are Postgres-only
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");

        builder.ConfigureServices(services =>
        {
            // ---- Postgres -> SQLite -------------------------------------------------
            RemoveAll(services, d =>
                d.ServiceType == typeof(AppDbContext)
                || d.ServiceType == typeof(DbContextOptions)
                || (d.ServiceType.IsGenericType && d.ServiceType.GetGenericArguments().Contains(typeof(AppDbContext))));

            services.AddDbContext<AppDbContext>(
                options => options.UseSqlite(_connection), optionsLifetime: ServiceLifetime.Singleton);
            services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(_connection));

            // ---- Redis -> memory ----------------------------------------------------
            RemoveAll(services, d => d.ServiceType == typeof(IDistributedCache));
            services.AddSingleton<IDistributedCache>(
                new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));

            // ---- external APIs ------------------------------------------------------
            RemoveAll(services, d => d.ServiceType == typeof(ITheTvDbService));
            services.AddScoped(_ => TheTvDb.Object);

            // ---- background jobs: registered, never started -------------------------
            RemoveAll(services, d =>
                d.ServiceType == typeof(IHostedService)
                && d.ImplementationType?.FullName?.Contains("Quartz", StringComparison.Ordinal) == true);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();

        return host;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
            _connection.Dispose();
    }

    private static void RemoveAll(IServiceCollection services, Func<ServiceDescriptor, bool> match)
    {
        foreach (var descriptor in services.Where(match).ToList())
            services.Remove(descriptor);
    }
}
