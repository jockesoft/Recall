using Recall.Web.Infrastructure.External.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.External.TheTvDb;

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

    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddScoped<ITrackedSeriesRepository, TrackedSeriesRepository>();

        return services;
    }
}