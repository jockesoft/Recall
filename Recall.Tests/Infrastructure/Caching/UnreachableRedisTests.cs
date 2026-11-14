using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Caching;

namespace Recall.Tests.Infrastructure.Caching;

/// <summary>
/// The real StackExchange.Redis cache, as <c>AddRedisCache</c> registers it,
/// pointed at a port nothing listens on: what the app meets when the Redis
/// container is stopped. The connection failure must reach
/// <see cref="DistributedCacheJson"/> as an exception it turns into a miss.
/// </summary>
[TestFixture]
public class UnreachableRedisTests
{
    [Test]
    public async Task RedisNotListening_ReadIsAMiss_WriteIsSkipped_AndLaterCallsDoNotWait()
    {
        var services = new ServiceCollection();
        services.AddStackExchangeRedisCache(o =>
        {
            // As AddRedisCache sets it up; the short connect timeout only makes
            // the first, connecting call fail sooner than the default 5 s.
            o.ConfigurationOptions = RedisConnection.Options($"127.0.0.1:{ClosedPort()},connectTimeout=500");
            o.InstanceName = "tvdb:";
        });
        await using var provider = services.BuildServiceProvider();
        var logger = new RecordingLogger<DistributedCacheJson>();
        var sut = new DistributedCacheJson(provider.GetRequiredService<IDistributedCache>(), TimeProvider.System, logger);

        var read = await sut.GetAsync<SeriesAggregate>("series:aggregate:v2:1:eng");
        var write = () => sut.SetAsync("series:aggregate:v2:1:eng", CacheSamples.Series(1), TimeSpan.FromDays(7));

        read.Should().BeNull();
        await write.Should().NotThrowAsync();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
            await sut.GetAsync<SeriesAggregate>($"series:aggregate:v2:{i}:eng");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(250),
            "while bypassed, a read does not try to connect");
    }

    [Test]
    public async Task RedisNotListening_FailsFast_OnceTheFirstConnectHasFailed()
    {
        // Without BacklogPolicy.FailFast each of these waited 5 s in the client's backlog.
        var services = new ServiceCollection();
        services.AddStackExchangeRedisCache(o =>
            o.ConfigurationOptions = RedisConnection.Options($"127.0.0.1:{ClosedPort()},connectTimeout=500"));
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();

        var first = () => cache.GetAsync("k");
        await first.Should().ThrowAsync<Exception>();

        var stopwatch = Stopwatch.StartNew();
        var second = () => cache.GetAsync("k");
        await second.Should().ThrowAsync<Exception>();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
