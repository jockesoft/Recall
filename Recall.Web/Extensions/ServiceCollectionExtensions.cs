using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Recall.Web.Infrastructure.Authentication;
using Recall.Web.Infrastructure.External;
using Recall.Web.Infrastructure.External.Omdb;
using Recall.Web.Infrastructure.External.TheTvDb;
using Recall.Web.Infrastructure.Mail;
using Recall.Web.Infrastructure.Persistence.OmdbCache;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Infrastructure.Persistence.TvdbCache;
using Recall.Web.Services.Digest;
using Recall.Web.Services;
using Recall.Web.Services.Authentication;
using Recall.Web.Services.External.Omdb;
using Recall.Web.Services.External.TheTvDb;
using Recall.Web.Services.Favorites;
using Recall.Web.Services.Health;
using Recall.Web.Services.Import;
using Recall.Web.Services.Notifications;
using Recall.Web.Services.Sitemap;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTheTvDb(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TheTvDbOptions>(configuration.GetSection(TheTvDbOptions.SectionName));

        // Must outlive the transient typed client: it holds the bearer token and
        // the shared request throttle (see TheTvDbClientState).
        services.TryAddSingleton<TheTvDbClientState>();

        services.AddHttpClient<ITheTvDbApiClient, TheTvDbApiClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TheTvDbOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
            client.DefaultRequestHeaders.Accept.Add(new("application/json"));
            // One attempt, body included. Retries are run by TheTvDbApiClient around
            // each throttled attempt (see ExternalHttpResilience), not inside this client.
            client.Timeout = ExternalHttpResilience.TheTvDbAttemptTimeout;
        });

        services.AddTheTvDbRetryPipeline();

        services.AddScoped<ITheTvDbService, TheTvDbService>();
        return services;
    }

    /// <summary>
    /// OMDb enrichment: the typed API client plus the snapshot stores. Series and
    /// movies are fetched by the hourly OMDb jobs; an episode is fetched on demand
    /// the first time a signed-in user opens it (never for an anonymous request).
    /// Every caller draws on the shared <see cref="IOmdbRequestBudget"/>.
    /// </summary>
    public static IServiceCollection AddOmdb(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OmdbOptions>(configuration.GetSection(OmdbOptions.SectionName));

        services.AddHttpClient<IOmdbApiClient, OmdbApiClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<OmdbOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
            client.DefaultRequestHeaders.Accept.Add(new("application/json"));
            // Overall budget for one call, its single retry included.
            client.Timeout = ExternalHttpResilience.OmdbOverallTimeout;
        }).AddOmdbResilience();

        services.AddScoped<IOmdbSnapshotStore, OmdbSnapshotStore>();
        services.AddScoped<IEpisodeOmdbSnapshotStore, EpisodeOmdbSnapshotStore>();
        services.AddScoped<IMovieOmdbSnapshotStore, MovieOmdbSnapshotStore>();
        services.AddSingleton<IOmdbRequestBudget, OmdbRequestBudget>();
        return services;
    }

    /// <summary>
    /// The weekly email digest: its options, and the check that it cannot be
    /// switched on half-configured. Digest emails are sent by a background job,
    /// which has no request to build links from, so an enabled digest without
    /// <c>Site:BaseUrl</c> would mail out links that lead nowhere. That fails
    /// startup instead.
    /// </summary>
    public static IServiceCollection AddWeeklyDigest(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DigestOptions>(configuration.GetSection(DigestOptions.SectionName));
        services.Configure<SiteOptions>(configuration.GetSection(SiteOptions.SectionName));
        services.AddSingleton<IDigestUnsubscribeTokens, DigestUnsubscribeTokens>();
        services.AddScoped<IDigestRepository, DigestRepository>();
        services.AddScoped<IDigestComposer, DigestComposer>();
        services.AddScoped<IWeeklyDigestService, WeeklyDigestService>();

        var digest = configuration.GetSection(DigestOptions.SectionName).Get<DigestOptions>() ?? new DigestOptions();
        var site = configuration.GetSection(SiteOptions.SectionName).Get<SiteOptions>() ?? new SiteOptions();

        if (digest.Enabled && site.NormalizedBaseUrl is null)
        {
            throw new InvalidOperationException(
                "Digest:Enabled is true but Site:BaseUrl is not set to an absolute http(s) address " +
                "(for example https://recall.nu). The weekly digest needs it for the links in its emails. " +
                "Set Site__BaseUrl, or set Digest__Enabled=false.");
        }

        if (digest.HourUtc is < 0 or > 23)
            throw new InvalidOperationException($"Digest:HourUtc must be between 0 and 23, but is {digest.HourUtc}.");

        return services;
    }

    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        // The clock every air-date check reads (AirDate.Today); tests substitute a fixed one.
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddScoped<ITrackedSeriesRepository, TrackedSeriesRepository>();
        services.AddScoped<IEpisodeWatchRepository, EpisodeWatchRepository>();
        services.AddScoped<ILikeRepository, LikeRepository>();
        services.AddScoped<IMovieWatchRepository, MovieWatchRepository>();
        services.AddScoped<ITrackedMovieRepository, TrackedMovieRepository>();
        services.AddScoped<IMovieTrackingService, MovieTrackingService>();
        services.AddScoped<IRatingRepository, RatingRepository>();
        services.AddScoped<IWatchProgressService, WatchProgressService>();
        // When a series in the queue counts as "haven't watched in a while" (ContinueWatchingOrder.Arrange).
        services.AddOptions<LibraryOptions>().BindConfiguration(LibraryOptions.SectionName);
        services.AddScoped<IWatchTimeService, WatchTimeService>();
        services.AddScoped<IFavoritesService, FavoritesService>();
        services.AddScoped<ITvdbSnapshotStore, TvdbSnapshotStore>();
        services.AddScoped<ISitemapService, SitemapService>();
        services.AddScoped<IDbHealthProbe, DbHealthProbe>();

        return services;
    }

    /// <summary>
    /// In-app notifications: the repository plus <see cref="NotificationService"/>.
    /// The app resolves the service to read/mark notifications; the
    /// <c>NewEpisodeNotificationTimer</c> job resolves it to raise them.
    /// </summary>
    public static IServiceCollection AddNotifications(this IServiceCollection services)
    {
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<INotificationService, NotificationService>();

        return services;
    }

    /// <summary>
    /// Outbound mail: the queue repository plus <see cref="MailService"/>, which
    /// both the app (to enqueue) and the <c>MailTimer</c> job (to send) resolve.
    /// </summary>
    public static IServiceCollection AddMail(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MailOptions>(configuration.GetSection(MailOptions.SectionName));
        services.AddScoped<IEmailRepository, EmailRepository>();
        services.AddScoped<IMailService, MailService>();

        return services;
    }

    /// <summary>
    /// Bulk import of an IMDb list export: the queue repository plus
    /// <see cref="WatchlistImportService"/>, which both the upload page (to
    /// enqueue) and the <c>WatchlistImportTimer</c> job (to drain) resolve.
    /// </summary>
    public static IServiceCollection AddWatchlistImport(this IServiceCollection services)
    {
        services.AddScoped<IWatchlistImportRepository, WatchlistImportRepository>();
        services.AddScoped<IWatchlistImportService, WatchlistImportService>();

        return services;
    }

    /// <summary>
    /// Passwordless (magic-link) sign-in: the token repository plus
    /// <see cref="IPasswordlessAuthService"/>, backed by the in-memory
    /// <see cref="ILoginAbuseGuard"/> (per-address/site-wide volumetric caps) and
    /// the Cloudflare Turnstile CAPTCHA verifier used on the login page. Cookie
    /// authentication itself is wired up separately, in <c>AddCookieAuthentication</c>.
    /// </summary>
    public static IServiceCollection AddPasswordlessAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LoginTokenOptions>(configuration.GetSection(LoginTokenOptions.SectionName));
        services.Configure<TurnstileOptions>(configuration.GetSection(TurnstileOptions.SectionName));
        services.AddScoped<ILoginTokenRepository, LoginTokenRepository>();
        services.AddScoped<IPasswordlessAuthService, PasswordlessAuthService>();
        services.AddSingleton<ILoginAbuseGuard, LoginAbuseGuard>();
        // No retry (it's a POST, and a human can just resubmit the form) — but
        // don't let a slow Cloudflare hold the sign-in request for the default 100 s.
        services.AddHttpClient<ITurnstileVerifier, TurnstileVerifier>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        return services;
    }
}