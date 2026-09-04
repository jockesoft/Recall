using Microsoft.Extensions.Options;
using Recall.Web.Infrastructure.Authentication;
using Recall.Web.Infrastructure.External.Omdb;
using Recall.Web.Infrastructure.External.TheTvDb;
using Recall.Web.Infrastructure.Mail;
using Recall.Web.Infrastructure.Persistence.OmdbCache;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Infrastructure.Persistence.TvdbCache;
using Recall.Web.Services;
using Recall.Web.Services.Authentication;
using Recall.Web.Services.External.Omdb;
using Recall.Web.Services.External.TheTvDb;
using Recall.Web.Services.Favorites;
using Recall.Web.Services.Health;
using Recall.Web.Services.Notifications;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTheTvDb(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TheTvDbOptions>(configuration.GetSection(TheTvDbOptions.SectionName));

        services.AddHttpClient<ITheTvDbApiClient, TheTvDbApiClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TheTvDbOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
            client.DefaultRequestHeaders.Accept.Add(new("application/json"));
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddScoped<ITheTvDbService, TheTvDbService>();
        return services;
    }

    /// <summary>
    /// OMDb enrichment: the typed API client plus the snapshot store. Fetching is
    /// driven by <c>UpdateOmdbInfoTimer</c>; nothing calls OMDb on a request path.
    /// </summary>
    public static IServiceCollection AddOmdb(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OmdbOptions>(configuration.GetSection(OmdbOptions.SectionName));

        services.AddHttpClient<IOmdbApiClient, OmdbApiClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<OmdbOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
            client.DefaultRequestHeaders.Accept.Add(new("application/json"));
            client.Timeout = TimeSpan.FromSeconds(20);
        });

        services.AddScoped<IOmdbSnapshotStore, OmdbSnapshotStore>();
        return services;
    }

    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddScoped<ITrackedSeriesRepository, TrackedSeriesRepository>();
        services.AddScoped<IEpisodeWatchRepository, EpisodeWatchRepository>();
        services.AddScoped<ILikeRepository, LikeRepository>();
        services.AddScoped<IWatchProgressService, WatchProgressService>();
        services.AddScoped<IWatchTimeService, WatchTimeService>();
        services.AddScoped<IFavoritesService, FavoritesService>();
        services.AddScoped<ITvdbSnapshotStore, TvdbSnapshotStore>();
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
    /// Passwordless (magic-link) sign-in: the token repository plus
    /// <see cref="IPasswordlessAuthService"/>. Cookie authentication itself is
    /// wired up separately in <c>Program.cs</c>.
    /// </summary>
    public static IServiceCollection AddPasswordlessAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LoginTokenOptions>(configuration.GetSection(LoginTokenOptions.SectionName));
        services.AddScoped<ILoginTokenRepository, LoginTokenRepository>();
        services.AddScoped<IPasswordlessAuthService, PasswordlessAuthService>();

        return services;
    }
}