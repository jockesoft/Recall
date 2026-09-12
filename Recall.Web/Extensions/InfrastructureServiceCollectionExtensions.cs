using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Quartz;
using Recall.Web.Infrastructure.Caching;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Timers;
using Serilog;

namespace Recall.Web.Extensions;

/// <summary>
/// Cross-cutting hosting infrastructure: caching, authentication, the database,
/// rate limiting, and the background job schedule. Domain/application service
/// wiring lives in <see cref="ServiceCollectionExtensions"/>.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// The distributed cache (<see cref="IDistributedCacheJson"/>) plus the raw
    /// <see cref="StackExchange.Redis.IConnectionMultiplexer"/> used for locking.
    /// </summary>
    public static IServiceCollection AddRedisCache(this IServiceCollection services, IConfiguration configuration)
    {
        // appsettings or env var: REDIS_CONNECTION=redis:6379
        var redisConnection = configuration["REDIS_CONNECTION"]
                               ?? configuration.GetConnectionString("RedisConnection");

        if (string.IsNullOrWhiteSpace(redisConnection))
        {
            throw new InvalidOperationException(
                "Redis connection string is not configured. Set the REDIS_CONNECTION environment " +
                "variable or the ConnectionStrings:RedisConnection setting.");
        }

        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redisConnection;
            options.InstanceName = "tvdb:"; // optional prefix
        });

        // Separate from IDistributedCache: used directly for distributed locking.
        services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
            StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnection));

        services.AddSingleton<IDistributedCacheJson, DistributedCacheJson>();

        return services;
    }

    public static IServiceCollection AddCookieAuthentication(this IServiceCollection services)
    {
        services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/Account/Login";
                options.LogoutPath = "/Account/Logout";
                options.AccessDeniedPath = "/Account/Login";
                options.ExpireTimeSpan = TimeSpan.FromDays(30);
                options.SlidingExpiration = true;
                options.Cookie.Name = "Recall.Auth";
                options.Cookie.HttpOnly = true;
                // Lax (not Strict) so the cookie survives the top-level GET navigation
                // from the emailed sign-in link.
                options.Cookie.SameSite = SameSiteMode.Lax;
#if !DEBUG
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
#endif
            });

        return services;
    }

    public static IServiceCollection AddAppSession(this IServiceCollection services)
    {
        services.AddSession(options =>
        {
#if DEBUG
            options.Cookie.Name = "Recall.Dev.App.Session";
#else
            options.Cookie.Name = "Recall.App.Session";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.HttpOnly = true;
#endif
            options.Cookie.IsEssential = true;
            options.IdleTimeout = TimeSpan.FromHours(12);
        });

        return services;
    }

    /// <summary>
    /// The Postgres-backed <see cref="AppDbContext"/>: a singleton-configured scoped
    /// context for per-request repositories, plus a factory for callers that fan out
    /// work in parallel within a single request (e.g. the home dashboard loading many
    /// series aggregates at once) and need their own short-lived context instead of
    /// racing on the scoped instance.
    /// </summary>
    public static IServiceCollection AddPostgres(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "The DefaultConnection connection string is not configured.");
        }

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.EnableDynamicJson();
        var dataSource = dataSourceBuilder.Build();

        void ConfigureAppDbContext(DbContextOptionsBuilder options)
        {
            options.UseNpgsql(dataSource, npgsqlOptions =>
            {
                npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });

            options.ConfigureWarnings(w =>
                w.Throw(RelationalEventId.MultipleCollectionIncludeWarning));
        }

        // optionsLifetime must be singleton so the factory below can share the options.
        services.AddDbContext<AppDbContext>(ConfigureAppDbContext, optionsLifetime: ServiceLifetime.Singleton);
        services.AddDbContextFactory<AppDbContext>(ConfigureAppDbContext);

        return services;
    }

    /// <summary>
    /// Throttles the sign-in form per client IP so it can't be scripted to spray
    /// login emails. Applied via <c>[EnableRateLimiting("login-email")]</c> on LoginModel.
    /// </summary>
    public static IServiceCollection AddLoginRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy("login-email", httpContext =>
            {
                var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter(clientIp, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 8,
                    Window = TimeSpan.FromMinutes(5),
                    QueueLimit = 0
                });
            });

            // Site-wide backstop on the same endpoint: bounds total sign-in POSTs
            // regardless of how many distinct IPs they come from (a botnet spread
            // across many addresses would otherwise sail past the per-IP policy).
            // A single shared bucket, so it only ever applies to that one path.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                HttpMethods.IsPost(httpContext.Request.Method) &&
                httpContext.Request.Path.StartsWithSegments("/Account/Login")
                    ? RateLimitPartition.GetFixedWindowLimiter("login-post-global", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 300,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    })
                    : RateLimitPartition.GetNoLimiter("unrestricted"));

            options.OnRejected = (context, cancellationToken) =>
            {
                Log.Warning(
                    "Rate limit exceeded for {Path} from {RemoteIp}",
                    context.HttpContext.Request.Path,
                    context.HttpContext.Connection.RemoteIpAddress);
                return ValueTask.CompletedTask;
            };
        });

        return services;
    }

    /// <summary>
    /// Registers the Quartz.NET jobs that keep TVDB/OMDb data fresh, drain the mail
    /// queue, and raise new-episode notifications, plus the hosted service that runs them.
    /// </summary>
    public static IServiceCollection AddScheduledJobs(this IServiceCollection services)
    {
        services.AddQuartz(q =>
        {
            q.ScheduleJob<UpdateTvDbInfoTimer>(trigger => trigger
                .WithIdentity("UpdateTvDbInfoTimer-trigger")
                .StartAt(DateTimeOffset.UtcNow.AddSeconds(10))
                .WithDailyTimeIntervalSchedule(s => s.WithInterval(60, IntervalUnit.Minute))
                .WithDescription("Check for new TVDB info every 60 minutes, but only refresh series and episodes that are due for a refresh."));

            q.ScheduleJob<UpdateMovieInfoTimer>(trigger => trigger
                .WithIdentity("UpdateMovieInfoTimer-trigger")
                .StartAt(DateTimeOffset.UtcNow.AddSeconds(15))
                .WithDailyTimeIntervalSchedule(s => s.WithInterval(60, IntervalUnit.Minute))
                .WithDescription("Check for new TVDB movie info every 60 minutes, but only refresh movies that are due for a refresh."));

            q.ScheduleJob<MailTimer>(trigger => trigger
                .WithIdentity("MailTimer-trigger")
                .StartAt(DateTimeOffset.UtcNow.AddSeconds(30))
                .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromMinutes(1)).RepeatForever())
                .WithDescription("Drain the outbound email queue once a minute."));

            q.ScheduleJob<UpdateOmdbInfoTimer>(trigger => trigger
                .WithIdentity("UpdateOmdbInfoTimer-trigger")
                .StartAt(DateTimeOffset.UtcNow.AddSeconds(20))
                .WithDailyTimeIntervalSchedule(s => s.WithInterval(60, IntervalUnit.Minute))
                .WithDescription("Refresh OMDb data for cached series — at most 30 requests/hour, each series at most monthly."));

            q.ScheduleJob<UpdateMovieOmdbInfoTimer>(trigger => trigger
                .WithIdentity("UpdateMovieOmdbInfoTimer-trigger")
                .StartAt(DateTimeOffset.UtcNow.AddSeconds(25))
                .WithDailyTimeIntervalSchedule(s => s.WithInterval(60, IntervalUnit.Minute))
                .WithDescription("Refresh OMDb data for cached movies — at most 30 requests/hour, each movie at most monthly."));

            q.ScheduleJob<NewEpisodeNotificationTimer>(trigger => trigger
                .WithIdentity("NewEpisodeNotificationTimer-trigger")
                .StartAt(DateTimeOffset.UtcNow.AddSeconds(45))
                .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromHours(6)).RepeatForever())
                .WithDescription("Notify users when a series they track has an episode that aired in the last few days."));

            q.ScheduleJob<WatchlistImportTimer>(trigger => trigger
                .WithIdentity("WatchlistImportTimer-trigger")
                .StartAt(DateTimeOffset.UtcNow.AddSeconds(50))
                .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromMinutes(1)).RepeatForever())
                .WithDescription("Drain the IMDb watchlist import queue at a steady pace, a few rows per minute."));
        });

        services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

        return services;
    }
}
