using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Quartz;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Caching;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Infrastructure.Timers;
using Recall.Web.Middleware;
using Recall.Web.Services.External.TheTvDb;
using Recall.Web.Services.Health;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// appsettings or env var: REDIS_CONNECTION=redis:6379
var redisConnection = builder.Configuration["REDIS_CONNECTION"] 
                      ?? builder.Configuration.GetConnectionString("RedisConnection");

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConnection;
    options.InstanceName = "tvdb:"; // optional prefix
});

Console.WriteLine("Redis connection string: " + redisConnection);

builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext();
});

builder.Services
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

// Add services to the container.
builder.Services.AddRazorPages();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost;

    // If your proxy is internal/docker/network-local and not explicitly listed,
    // clear these so forwarded headers are accepted.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddSession(options =>
{
#if DEBUG
    options.Cookie.Name = "Recall.Dev.App.Session";
#else
    options.Cookie.Name = "Recall.App.Session";
    // You might want to only set the application cookies over a secure connection:
// Remove below line if on http
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.HttpOnly = true;
#endif
    options.Cookie.IsEssential = true;
    options.IdleTimeout = TimeSpan.FromHours(12);
});

builder.Services.AddControllers().AddViewLocalization();
builder.Services.AddAntiforgery();

var dataSourceBuilder = new NpgsqlDataSourceBuilder(
    builder.Configuration.GetConnectionString("DefaultConnection"));

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

// The scoped AppDbContext still serves the per-request repositories. Its options
// must be singleton so the singleton factory below can share them.
builder.Services.AddDbContext<AppDbContext>(
    ConfigureAppDbContext,
    optionsLifetime: ServiceLifetime.Singleton);

// A factory lets callers that fan out work in parallel within a single request
// (e.g. the home dashboard loading many series aggregates at once) each take
// their own short-lived context instead of racing on the scoped instance.
builder.Services.AddDbContextFactory<AppDbContext>(ConfigureAppDbContext);

#if DEBUG
builder.Services.AddDatabaseDeveloperPageExceptionFilter();
#endif

builder.Services.AddAuthorization();

// Redis multiplexer for locking (separate from IDistributedCache)
builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(sp =>
{
    var cfg = redisConnection ?? "localhost:6379";
    return StackExchange.Redis.ConnectionMultiplexer.Connect(cfg);
});

builder.Services.AddSingleton<IDistributedCacheJson, DistributedCacheJson>();

builder.Services.AddSingleton<TheTvDbClientState>();
builder.Services.AddHttpClient<ITheTvDbApiClient, TheTvDbApiClient>(client =>
{
    client.BaseAddress = new Uri("https://api4.thetvdb.com/v4/");
});
// Add TheTVDB integration
builder.Services.AddTheTvDb(builder.Configuration);
builder.Services.AddOmdb(builder.Configuration);
builder.Services.AddApplicationServices();
builder.Services.AddNotifications();
builder.Services.AddMail(builder.Configuration);

// Deep health probe for uptime monitoring — GET /health returns 200 "Healthy"
// while Postgres answers a bare SELECT 1, 503 "Unhealthy" otherwise.
builder.Services.AddHealthChecks()
    .AddCheck<DbHealthCheck>("database", tags: ["ready"]);
builder.Services.AddPasswordlessAuth(builder.Configuration);

// Throttle the sign-in form per client IP so it can't be scripted to spray
// login emails. Applied via [EnableRateLimiting("login-email")] on LoginModel.
builder.Services.AddRateLimiter(options =>
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

builder.Services.AddScoped<IAppUserRepository, AppUserRepository>();

// Add the required Quartz.NET services
builder.Services.AddQuartz(q =>
{
    q.ScheduleJob<UpdateTvDbInfoTimer>(trigger => trigger
        .WithIdentity("UpdateTvDbInfoTimer-trigger")
        .StartAt(DateTimeOffset.UtcNow.AddSeconds(10))
        .WithDailyTimeIntervalSchedule(60, IntervalUnit.Minute)
        .WithDescription("Check for new TVDB info every 60 minutes, but only refresh series and episodes that are due for a refresh."));

    q.ScheduleJob<MailTimer>(trigger => trigger
        .WithIdentity("MailTimer-trigger")
        .StartAt(DateTimeOffset.UtcNow.AddSeconds(30))
        .WithSimpleSchedule(s => s.WithIntervalInMinutes(1).RepeatForever())
        .WithDescription("Drain the outbound email queue once a minute."));

    q.ScheduleJob<UpdateOmdbInfoTimer>(trigger => trigger
        .WithIdentity("UpdateOmdbInfoTimer-trigger")
        .StartAt(DateTimeOffset.UtcNow.AddSeconds(20))
        .WithDailyTimeIntervalSchedule(60, IntervalUnit.Minute)
        .WithDescription("Refresh OMDb data for cached series — at most 30 requests/hour, each series at most monthly."));

    q.ScheduleJob<NewEpisodeNotificationTimer>(trigger => trigger
        .WithIdentity("NewEpisodeNotificationTimer-trigger")
        .StartAt(DateTimeOffset.UtcNow.AddSeconds(45))
        .WithSimpleSchedule(s => s.WithIntervalInHours(6).RepeatForever())
        .WithDescription("Notify users when a series they track has an episode that aired in the last few days."));
});

// Add the Quartz.NET hosted service

builder.Services.AddQuartzHostedService(
    q => q.WaitForJobsToComplete = true);

var app = builder.Build();

// Bring the database schema up to the current model before serving traffic.
await app.MigrateDatabaseAsync();

//app.UseSerilogRequestLogging();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage(); // or app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            var exceptionHandlerPathFeature =
                context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();

            var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
            logger.LogError(exceptionHandlerPathFeature?.Error, "Unhandled exception");

            context.Response.Redirect("/Error");
        });
    });
    
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.UseStaticFiles(); // important for runtime-created files
app.UseRouting();

app.UseRateLimiter();

app.UseSession();
app.UseAuthentication();

#if DEBUG
app.UseMiddleware<DevAuthMiddleware>();
#endif

app.UseAuthorization();

// Anonymous, and the middleware sets its own no-store cache headers.
// /health = readiness (runs the DB check); /health/live = liveness (no checks,
// just "is the app serving requests?") so a container healthcheck won't restart
// the process during a transient DB outage.
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapStaticAssets();
app.MapRazorPages()
    .WithStaticAssets()
    .CacheOutput();

app.Run();