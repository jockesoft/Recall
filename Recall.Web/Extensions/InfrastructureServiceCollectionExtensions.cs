using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Quartz;
using Recall.Web.Infrastructure.Authentication;
using Recall.Web.Infrastructure.Caching;
using Recall.Web.Infrastructure.Hosting;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Infrastructure.Retention;
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
    /// The Redis-backed distributed cache, wrapped as <see cref="IDistributedCacheJson"/>.
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

        services.AddSingleton<IDistributedCacheJson, DistributedCacheJson>();

        return services;
    }

    /// <summary>
    /// Applies <c>X-Forwarded-For/Proto/Host</c>, but only from the proxies named
    /// in the <c>TrustedProxies</c> section (<see cref="TrustedProxyOptions"/>) —
    /// or, when that section is empty, from loopback and the private ranges. A
    /// request arriving from anywhere else keeps its real connection address, so
    /// a caller that reaches the app without passing through the proxy can't
    /// spoof its way around the per-IP rate limiters with a forged header.
    /// </summary>
    public static IServiceCollection AddTrustedForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(TrustedProxyOptions.SectionName).Get<TrustedProxyOptions>()
                       ?? new TrustedProxyOptions();

        if (settings.ForwardLimit < 1)
        {
            throw new InvalidOperationException(
                $"{TrustedProxyOptions.SectionName}:ForwardLimit must be at least 1 (was {settings.ForwardLimit}).");
        }

        var configuredAddresses = (settings.Addresses ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        var configuredNetworks = (settings.Networks ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();

        // Parsed here rather than inside the options callback so a typo fails
        // startup with a clear message instead of the first request.
        var addresses = configuredAddresses.Select(ParseProxyAddress).ToArray();
        var networks = (configuredAddresses.Length == 0 && configuredNetworks.Length == 0
                ? TrustedProxyOptions.DefaultNetworks
                : configuredNetworks)
            .Select(ParseProxyNetwork)
            .ToArray();

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor |
                ForwardedHeaders.XForwardedProto |
                ForwardedHeaders.XForwardedHost;
            options.ForwardLimit = settings.ForwardLimit;

            // Replace the framework's loopback-only defaults with exactly the set above.
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();

            foreach (var address in addresses)
                options.KnownProxies.Add(address);

            foreach (var network in networks)
                options.KnownIPNetworks.Add(network);
        });

        return services;
    }

    private static IPAddress ParseProxyAddress(string value) =>
        IPAddress.TryParse(value.Trim(), out var address)
            ? address
            : throw new InvalidOperationException(
                $"{TrustedProxyOptions.SectionName}:{nameof(TrustedProxyOptions.Addresses)} contains \"{value}\", which is not an IP address.");

    private static System.Net.IPNetwork ParseProxyNetwork(string value) =>
        System.Net.IPNetwork.TryParse(value.Trim(), out var network)
            ? network
            : throw new InvalidOperationException(
                $"{TrustedProxyOptions.SectionName}:{nameof(TrustedProxyOptions.Networks)} contains \"{value}\", which is not a network in CIDR notation (e.g. 172.18.0.0/16).");

    public static IServiceCollection AddCookieAuthentication(this IServiceCollection services)
    {
        // Re-checks the cookie against the user row every few minutes — see RecallCookieEvents.
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<RecallCookieEvents>();

        services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/Account/Login";
                options.LogoutPath = "/Account/Logout";
                options.AccessDeniedPath = "/Account/Login";
                options.ExpireTimeSpan = TimeSpan.FromDays(SignInCookieDays);
                options.SlidingExpiration = true;
                options.EventsType = typeof(RecallCookieEvents);
                options.Cookie.Name = SignInCookieName;
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

    /// <summary>The sign-in cookie. The Privacy page names it, so the two cannot drift apart.</summary>
    public const string SignInCookieName = "Recall.Auth";

    /// <summary>How long the sign-in cookie lasts without a visit (it is renewed on use).</summary>
    public const int SignInCookieDays = 30;

    /// <summary>Name of the per-IP policy on requests for a sign-in link; see <see cref="LoginEmailPartition"/>.</summary>
    public const string LoginEmailPolicy = "login-email";

    /// <summary>How many sign-in links one client IP may ask for per <see cref="LoginEmailWindow"/>.</summary>
    public const int LoginEmailPermits = 8;

    public static readonly TimeSpan LoginEmailWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Limits requests for a sign-in link, which are the POSTs to the sign-in
    /// page: <see cref="LoginEmailPermits"/> per <see cref="LoginEmailWindow"/>
    /// per client IP. Loading the page (a GET) is never counted, so reloading
    /// it, or following a few links to it, cannot lock anyone out; only asking
    /// for links can.
    /// </summary>
    public static RateLimitPartition<string> LoginEmailPartition(HttpContext httpContext)
    {
        if (!HttpMethods.IsPost(httpContext.Request.Method))
            return RateLimitPartition.GetNoLimiter("page-load");

        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter($"post:{clientIp}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = LoginEmailPermits,
            Window = LoginEmailWindow,
            QueueLimit = 0
        });
    }

    /// <summary>Name of the per-IP policy on the public Details pages; see <see cref="PublicDetailsPartition"/>.</summary>
    public const string PublicDetailsPolicy = "public-details";

    /// <summary>How many Details pages one anonymous client IP may load per minute.</summary>
    public const int PublicDetailsPermitsPerMinute = 60;

    /// <summary>
    /// The app's rate-limit policies:
    /// <list type="bullet">
    /// <item><see cref="LoginEmailPolicy"/> — throttles requests for a sign-in link (POSTs
    /// only) per client IP so the form can't be scripted to spray login emails. Applied via
    /// <c>[EnableRateLimiting]</c> on LoginModel, with a site-wide backstop on the same endpoint.</item>
    /// <item><see cref="PublicDetailsPolicy"/> — bounds what an anonymous client can make the
    /// app fetch from TheTVDB through the public Series/Episodes/Movies Details pages.</item>
    /// </list>
    /// <c>UseRateLimiter</c> must run after authentication: the second policy depends on
    /// knowing whether the request is signed in.
    /// </summary>
    public static IServiceCollection AddRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(PublicDetailsPolicy, PublicDetailsPartition);

            options.AddPolicy(LoginEmailPolicy, LoginEmailPartition);

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

                // Tells a well-behaved crawler when to come back instead of leaving it to guess.
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };
        });

        return services;
    }

    /// <summary>
    /// The public Details pages are open to anonymous visitors and search
    /// engines on purpose, and an uncached series, episode or movie is fetched
    /// from TheTVDB on demand — so without a limit, anyone walking ids could
    /// make the app spend its TheTVDB allowance for them. Anonymous requests
    /// get <see cref="PublicDetailsPermitsPerMinute"/> page loads a minute per
    /// client IP (plenty for a person, and for a crawler pacing itself);
    /// signed-in users are not limited.
    /// </summary>
    public static RateLimitPartition<string> PublicDetailsPartition(HttpContext httpContext)
    {
        if (httpContext.User.Identity?.IsAuthenticated == true)
            return RateLimitPartition.GetNoLimiter("signed-in");

        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter($"anonymous:{clientIp}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = PublicDetailsPermitsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    }

    /// <summary>The scheduled jobs, by the names <c>Jobs:Disabled</c> accepts.</summary>
    public static readonly IReadOnlyList<string> ScheduledJobNames =
    [
        nameof(UpdateTvDbInfoTimer), nameof(UpdateMovieInfoTimer), nameof(MailTimer), nameof(UpdateOmdbInfoTimer),
        nameof(UpdateMovieOmdbInfoTimer), nameof(NewEpisodeNotificationTimer), nameof(WatchlistImportTimer),
        nameof(PruneOldDataTimer)
    ];

    /// <summary>
    /// The jobs switched off with <c>Jobs:Disabled</c> (a list of job class
    /// names, empty by default). It exists for an instance that must not do a
    /// job's work, such as the UI review instances, which must not let the
    /// import job call TheTVDB. A name that is not a job fails startup: a typo
    /// would otherwise leave the job running unnoticed.
    /// </summary>
    public static IReadOnlySet<string> DisabledJobs(IConfiguration configuration)
    {
        var names = configuration.GetSection("Jobs:Disabled").Get<string[]>() ?? [];

        var unknown = names.Where(name => !ScheduledJobNames.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"Jobs:Disabled names a job that does not exist: {string.Join(", ", unknown)}. " +
                $"Known jobs: {string.Join(", ", ScheduledJobNames)}.");
        }

        return names.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Registers the Quartz.NET jobs that keep TVDB/OMDb data fresh, drain the mail
    /// and import queues, raise new-episode notifications and prune old rows, plus
    /// the hosted service that runs them.
    /// </summary>
    public static IServiceCollection AddScheduledJobs(this IServiceCollection services, IConfiguration configuration)
    {
        // PruneOldDataTimer's settings and dependencies.
        services.Configure<RetentionOptions>(configuration.GetSection(RetentionOptions.SectionName));
        services.AddScoped<IDataRetentionRepository, DataRetentionRepository>();
        services.TryAddSingleton(TimeProvider.System);

        var disabledJobs = DisabledJobs(configuration);

        services.AddQuartz(q =>
        {
            // A job named in Jobs:Disabled is not scheduled at all.
            void Schedule<TJob>(Action<ITriggerConfigurator<TJob>> trigger) where TJob : IJob
            {
                var name = typeof(TJob).Name;

                // Keeps ScheduledJobNames honest: a job added here but not there
                // could not be switched off, and would not be reported as known.
                if (!ScheduledJobNames.Contains(name))
                    throw new InvalidOperationException($"{name} is scheduled but missing from {nameof(ScheduledJobNames)}.");

                if (!disabledJobs.Contains(name))
                    q.ScheduleJob<TJob>(trigger);
            }

            Schedule<UpdateTvDbInfoTimer>(trigger => trigger
                .WithIdentity("UpdateTvDbInfoTimer-trigger")
                .StartAt(DateTimeOffset.UtcNow.AddSeconds(10))
                .WithDailyTimeIntervalSchedule(s => s.WithInterval(60, IntervalUnit.Minute))
                .WithDescription("Check for new TVDB info every 60 minutes, but only refresh series and episodes that are due for a refresh."));

            Schedule<UpdateMovieInfoTimer>(trigger => trigger
                .WithIdentity("UpdateMovieInfoTimer-trigger")
                .StartAt(DateTimeOffset.UtcNow.AddSeconds(15))
                .WithDailyTimeIntervalSchedule(s => s.WithInterval(60, IntervalUnit.Minute))
                .WithDescription("Check for new TVDB movie info every 60 minutes, but only refresh movies that are due for a refresh."));

            Schedule<MailTimer>(trigger => trigger
                .WithIdentity("MailTimer-trigger")
                .StartAt(DateTimeOffset.UtcNow.AddSeconds(30))
                .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromMinutes(1)).RepeatForever())
                .WithDescription("Drain the outbound email queue once a minute."));

            Schedule<UpdateOmdbInfoTimer>(trigger => trigger
                .WithIdentity("UpdateOmdbInfoTimer-trigger")
                .StartAt(DateTimeOffset.UtcNow.AddSeconds(20))
                .WithDailyTimeIntervalSchedule(s => s.WithInterval(60, IntervalUnit.Minute))
                .WithDescription("Refresh OMDb data for cached series — at most 30 requests/hour, each series at most monthly."));

            Schedule<UpdateMovieOmdbInfoTimer>(trigger => trigger
                .WithIdentity("UpdateMovieOmdbInfoTimer-trigger")
                .StartAt(DateTimeOffset.UtcNow.AddSeconds(25))
                .WithDailyTimeIntervalSchedule(s => s.WithInterval(60, IntervalUnit.Minute))
                .WithDescription("Refresh OMDb data for cached movies — at most 30 requests/hour, each movie at most monthly."));

            Schedule<NewEpisodeNotificationTimer>(trigger => trigger
                .WithIdentity("NewEpisodeNotificationTimer-trigger")
                .StartAt(DateTimeOffset.UtcNow.AddSeconds(45))
                .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromHours(6)).RepeatForever())
                .WithDescription("Notify users when a series they track has an episode that aired in the last few days."));

            Schedule<WatchlistImportTimer>(trigger => trigger
                .WithIdentity("WatchlistImportTimer-trigger")
                .StartAt(DateTimeOffset.UtcNow.AddSeconds(50))
                .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromMinutes(1)).RepeatForever())
                .WithDescription("Drain the IMDb watchlist import queue at a steady pace, a few rows per minute."));

            Schedule<PruneOldDataTimer>(trigger => trigger
                .WithIdentity("PruneOldDataTimer-trigger")
                .StartAt(DateTimeOffset.UtcNow.AddSeconds(90))
                .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromHours(24)).RepeatForever())
                .WithDescription("Delete settled login tokens, finished emails, read notifications, the notified-episode ledger and completed imports once they pass their retention period."));
        });

        services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

        return services;
    }
}
