using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Middleware;
using Recall.Web.Services.External.TheTvDb;
using Recall.Web.Services.Health;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext();
});

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddControllers().AddViewLocalization();
builder.Services.AddAntiforgery();
builder.Services.AddHttpContextAccessor();

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

builder.Services.AddCookieAuthentication();
builder.Services.AddAppSession();
builder.Services.AddAuthorization();

builder.Services.AddRedisCache(builder.Configuration);
builder.Services.AddPostgres(builder.Configuration);

#if DEBUG
builder.Services.AddDatabaseDeveloperPageExceptionFilter();
#endif

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
builder.Services.AddWatchlistImport();

// Deep health probe for uptime monitoring — GET /health returns 200 "Healthy"
// while Postgres answers a bare SELECT 1, 503 "Unhealthy" otherwise.
builder.Services.AddHealthChecks()
    .AddCheck<DbHealthCheck>("database", tags: ["ready"]);
builder.Services.AddPasswordlessAuth(builder.Configuration);
builder.Services.AddLoginRateLimiting();

builder.Services.AddScoped<IAppUserRepository, AppUserRepository>();

builder.Services.AddScheduledJobs();

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
    .WithStaticAssets();

app.Run();
