using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Caching;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Middleware;
using Recall.Web.Services.External.TheTvDb;
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

builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/Account/Login";
    o.LogoutPath = "/Account/Logout";
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

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseNpgsql(dataSource, npgsqlOptions =>
    {
        npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
    });

    options.ConfigureWarnings(w =>
        w.Throw(RelationalEventId.MultipleCollectionIncludeWarning));
});

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
builder.Services.AddApplicationServices();

builder.Services.AddScoped<IAppUserRepository, AppUserRepository>();

var app = builder.Build();

app.UseSerilogRequestLogging();

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

app.UseSession();
app.UseAuthentication();
app.UseMiddleware<DevAuthMiddleware>();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapStaticAssets();
app.MapRazorPages()
    .WithStaticAssets()
    .CacheOutput();

app.Run();