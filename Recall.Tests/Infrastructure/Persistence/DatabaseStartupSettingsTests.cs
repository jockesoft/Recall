using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Recall.Web.Infrastructure.Persistence;

namespace Recall.Tests.Infrastructure.Persistence;

/// <summary>
/// Which environments guard against an empty database, and the two settings'
/// defaults. What the guard does is tested against real PostgreSQL
/// (<c>EmptyDatabaseGuardTests</c> in Recall.Tests.Postgres).
/// </summary>
[TestFixture]
public sealed class DatabaseStartupSettingsTests
{
    [TestCase("Production", true)]
    [TestCase("Development", false)]
    [TestCase("Test", false)]
    [TestCase("Staging", false)]
    public void OnlyProduction_Should_GuardAgainstAnEmptyDatabase(string environment, bool guarded)
    {
        var settings = DatabaseStartup.Settings.From(Configuration(), new Environment(environment));

        settings.Should().Be(new DatabaseStartup.Settings(
            MigrateOnStartup: true, GuardEmptyDatabase: guarded, AllowEmptyDatabase: false));
    }

    [Test]
    public void TheSettings_Should_BeReadFromTheDatabaseSection()
    {
        var configuration = Configuration(
            ("Database:AllowEmptyDatabase", "true"),
            ("Database:MigrateOnStartup", "false"));

        DatabaseStartup.Settings.From(configuration, new Environment("Production")).Should().Be(
            new DatabaseStartup.Settings(MigrateOnStartup: false, GuardEmptyDatabase: true, AllowEmptyDatabase: true));
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Recall.Web";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
