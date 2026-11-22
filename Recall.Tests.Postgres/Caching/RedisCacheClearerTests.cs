using AwesomeAssertions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Recall.Web.Infrastructure.Caching;
using StackExchange.Redis;

namespace Recall.Tests.Postgres.Caching;

/// <summary>
/// <see cref="RedisCacheClearer"/> against a real Valkey (the production image,
/// <see cref="DefaultImage"/>), started for this fixture. It lives in this suite
/// because the suite already needs Docker and reports itself ignored without it.
/// </summary>
[TestFixture]
public class RedisCacheClearerTests
{
    /// <summary>Same image as production (<c>compose.prod.yml</c>) and local development.</summary>
    public const string DefaultImage = "dhi.io/valkey:9.1";

    /// <summary>Overrides <see cref="DefaultImage"/>, e.g. to try another Valkey (or Redis) version.</summary>
    public const string ImageVariable = "RECALL_TEST_VALKEY_IMAGE";

    private IContainer _redis = null!;
    private string _endpoint = null!;
    private ConnectionMultiplexer _admin = null!;

    /// <summary><see cref="ImageVariable"/> when it is set and not blank, else <see cref="DefaultImage"/>.</summary>
    public static string Image =>
        Environment.GetEnvironmentVariable(ImageVariable) is { } image && !string.IsNullOrWhiteSpace(image)
            ? image.Trim()
            : DefaultImage;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        // The hardened image keeps Valkey's protected mode on, which refuses every
        // client that is not on loopback while no password is set. Testcontainers
        // connects through a mapped port, so it is switched off here (the
        // production container has a password instead). The image's entrypoint
        // runs valkey-server with arguments that start with a dash.
        _redis = new ContainerBuilder(Image)
            .WithCommand("--protected-mode", "no")
            .WithPortBinding(6379, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
            .Build();
        await _redis.StartAsync();
        _endpoint = $"{_redis.Hostname}:{_redis.GetMappedPublicPort(6379)}";
        _admin = await ConnectionMultiplexer.ConnectAsync($"{_endpoint},allowAdmin=true");
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        await _admin.DisposeAsync();
        await _redis.DisposeAsync();
    }

    [SetUp]
    public async Task FlushAsync()
    {
        await _admin.GetServer(_endpoint).FlushAllDatabasesAsync();
        await _admin.GetServer(_endpoint).ExecuteAsync("CONFIG", "RESETSTAT");
    }

    [Test]
    public async Task RemovesEveryKeyUnderThePrefix_InTheConnectionsDatabase_AndNothingElse()
    {
        var db0 = _admin.GetDatabase(0);
        var db1 = _admin.GetDatabase(1);
        // More than one SCAN page and several UNLINK batches.
        for (var i = 0; i < 1_234; i++)
            await db0.HashSetAsync($"tvdb:series:aggregate:v2:{i}:eng", "data", "x");
        await db0.StringSetAsync("other:keep", "1");
        await db0.StringSetAsync("tvdbx:not-the-prefix", "1");
        await db1.HashSetAsync("tvdb:series:aggregate:v2:1:eng", "data", "x");
        await using var clearer = new RedisCacheClearer(_endpoint, TvdbCacheKeys.InstancePrefix);

        var removed = await clearer.ClearAsync();

        removed.Should().Be(1_234);
        (await KeysAsync(0)).Should().BeEquivalentTo(["other:keep", "tvdbx:not-the-prefix"]);
        (await KeysAsync(1)).Should().ContainSingle("another database is left alone");
    }

    [Test]
    public async Task UsesScanAndUnlink_NeverKeysDelOrFlush()
    {
        for (var i = 0; i < 600; i++)
            await _admin.GetDatabase().StringSetAsync($"tvdb:k{i}", "1");
        await _admin.GetServer(_endpoint).ExecuteAsync("CONFIG", "RESETSTAT");
        await using var clearer = new RedisCacheClearer(_endpoint, TvdbCacheKeys.InstancePrefix);

        await clearer.ClearAsync();

        var stats = (await _admin.GetServer(_endpoint).InfoAsync("commandstats"))
            .SelectMany(g => g).Select(p => p.Key).ToList();
        stats.Should().Contain(["cmdstat_scan", "cmdstat_unlink"]);
        stats.Should().NotContain(["cmdstat_keys", "cmdstat_del", "cmdstat_flushdb", "cmdstat_flushall"]);
    }

    [Test]
    public async Task UsesTheDefaultDatabaseOfItsConnectionString()
    {
        await _admin.GetDatabase(0).StringSetAsync("tvdb:a", "1");
        await _admin.GetDatabase(1).StringSetAsync("tvdb:a", "1");
        await using var clearer = new RedisCacheClearer($"{_endpoint},defaultDatabase=1", TvdbCacheKeys.InstancePrefix);

        (await clearer.ClearAsync()).Should().Be(1);

        (await KeysAsync(0)).Should().ContainSingle();
        (await KeysAsync(1)).Should().BeEmpty();
    }

    [Test]
    public async Task WorksWithThePasswordInTheConnectionString_AsInProduction()
    {
        // Production runs Valkey with --requirepass and REDIS_CONNECTION carries
        // password=...; the clearer's own connection and the distributed cache
        // (configured as AddRedisCache configures it) must both authenticate.
        const string password = "test-valkey-password";
        await using var secured = new ContainerBuilder(Image)
            .WithCommand("--requirepass", password)
            .WithPortBinding(6379, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
            .Build();
        await secured.StartAsync();
        var connectionString = $"{secured.Hostname}:{secured.GetMappedPublicPort(6379)},password={password}";

        using var cache = new RedisCache(new RedisCacheOptions
        {
            ConfigurationOptions = RedisConnection.Options(connectionString),
            InstanceName = TvdbCacheKeys.InstancePrefix,
        });
        await cache.SetAsync("series:aggregate:v2:1:eng", [1, 2, 3], new DistributedCacheEntryOptions());
        (await cache.GetAsync("series:aggregate:v2:1:eng")).Should().Equal(1, 2, 3);

        await using var other = await ConnectionMultiplexer.ConnectAsync(connectionString);
        await other.GetDatabase().StringSetAsync("other:keep", "1");
        await using var clearer = new RedisCacheClearer(connectionString, TvdbCacheKeys.InstancePrefix);

        (await clearer.ClearAsync()).Should().Be(1);
        (await other.GetDatabase().KeyExistsAsync("other:keep")).Should().BeTrue();
    }

    [Test]
    public async Task NothingToRemove_ReturnsZero()
    {
        await using var clearer = new RedisCacheClearer(_endpoint, TvdbCacheKeys.InstancePrefix);

        (await clearer.ClearAsync()).Should().Be(0);
    }

    private async Task<List<string>> KeysAsync(int database)
    {
        var keys = new List<string>();
        await foreach (var key in _admin.GetServer(_endpoint).KeysAsync(database, "*"))
            keys.Add(key!);
        return keys;
    }
}
