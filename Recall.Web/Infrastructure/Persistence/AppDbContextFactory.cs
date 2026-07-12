using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace Recall.Web.Infrastructure.Persistence;

public class AppDbContextFactory //: IDesignTimeDbContextFactory<AppDbContext>
{
/*    public AppDbContext CreateDbContext(string[] args)
    {
        // Build config manually
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";

        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = config.GetConnectionString("DefaultConnection");

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Connection string not found in configuration");

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.EnableDynamicJson();
        var dataSource = dataSourceBuilder.Build();

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseNpgsql(dataSource, npgsql =>
        {
            npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
        });

        return new AppDbContext(optionsBuilder.Options);
    }*/
}