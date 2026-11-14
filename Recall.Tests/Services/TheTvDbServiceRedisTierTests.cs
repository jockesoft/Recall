using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Recall.Tests.Infrastructure.Caching;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure;
using Recall.Web.Infrastructure.Caching;
using Recall.Web.Infrastructure.Persistence.TvdbCache;
using Recall.Web.Services;
using Recall.Web.Services.External.TheTvDb;

namespace Recall.Tests.Services;

/// <summary>
/// <see cref="TheTvDbService"/> over the real Redis-tier wrapper
/// (<see cref="DistributedCacheJson"/>) on an in-memory distributed cache that
/// can be switched off, with the Postgres snapshots mocked and a strict
/// TheTVDB client, so any request to TheTVDB fails the test.
/// </summary>
[TestFixture]
public class TheTvDbServiceRedisTierTests
{
    private SwitchableCache _redis = null!;
    private Mock<ITheTvDbApiClient> _api = null!;
    private Mock<ITvdbSnapshotStore> _store = null!;
    private TheTvDbService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _redis = new SwitchableCache();
        _api = new Mock<ITheTvDbApiClient>(MockBehavior.Strict);
        _store = new Mock<ITvdbSnapshotStore>();
        var cache = new DistributedCacheJson(_redis, NullLogger<DistributedCacheJson>.Instance);
        _sut = new TheTvDbService(_api.Object, cache, _store.Object, NullLogger<TheTvDbService>.Instance);
    }

    // ---- empty Redis: Postgres, no TheTVDB, refilled -----------------------------

    [Test]
    public async Task EmptyRedis_SeriesAggregate_ComesFromPostgres_AndRefillsRedis()
    {
        var snapshot = CacheSamples.Series(78957);
        _store.Setup(s => s.GetSeriesAggregateAsync(78957, "eng", It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);

        var first = await _sut.GetSeriesAggregateByIdAsync(78957);
        var second = await _sut.GetSeriesAggregateByIdAsync(78957);

        first.Should().BeEquivalentTo(snapshot);
        second.Should().BeEquivalentTo(snapshot);
        _store.Verify(s => s.GetSeriesAggregateAsync(78957, "eng", It.IsAny<CancellationToken>()), Times.Once,
            "the second read is served by the entry the first one wrote back");
        _redis.Keys.Should().Equal("series:aggregate:v2:78957:eng");
    }

    [Test]
    public async Task EmptyRedis_CachedOnlySeriesRead_ComesFromPostgres_AndRefillsRedis()
    {
        _store.Setup(s => s.GetSeriesAggregateAsync(78957, "eng", It.IsAny<CancellationToken>())).ReturnsAsync(CacheSamples.Series(78957));

        await _sut.GetCachedSeriesAggregateAsync(78957);
        var second = await _sut.GetCachedSeriesAggregateAsync(78957);

        second!.Name.Should().Be("Gold Rush");
        _store.Verify(s => s.GetSeriesAggregateAsync(78957, "eng", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task EmptyRedis_MovieAggregate_ComesFromPostgres_AndRefillsRedis()
    {
        _store.Setup(s => s.GetMovieAggregateAsync(7, "eng", It.IsAny<CancellationToken>())).ReturnsAsync(CacheSamples.Movie(7));

        await _sut.GetMovieAggregateByIdAsync(7);
        var second = await _sut.GetCachedMovieAggregateAsync(7);

        second.Should().BeEquivalentTo(CacheSamples.Movie(7));
        _store.Verify(s => s.GetMovieAggregateAsync(7, "eng", It.IsAny<CancellationToken>()), Times.Once);
        _redis.Keys.Should().Equal("movie:aggregate:v2:7:eng");
    }

    [Test]
    public async Task EmptyRedis_Episode_ComesFromPostgres_AndRefillsRedis()
    {
        _store.Setup(s => s.GetEpisodeExtendedAsync(2001, It.IsAny<CancellationToken>())).ReturnsAsync(CacheSamples.Episode(2001));

        await _sut.GetEpisodeDetailsAsync(2001);
        var second = await _sut.GetEpisodeDetailsAsync(2001);

        second.Should().BeEquivalentTo(CacheSamples.Episode(2001));
        _store.Verify(s => s.GetEpisodeExtendedAsync(2001, It.IsAny<CancellationToken>()), Times.Once);
        _redis.Keys.Should().Equal("episode:extended:v3:2001:eng");
    }

    // ---- an old key version is never read -------------------------------------

    [Test]
    public async Task OldVersionKey_IsIgnored_AndTheNewKeyIsWritten()
    {
        // An entry as the cache held it before 2026-11-14: v1 key, plain JSON.
        var oldCopy = CacheSamples.Series(78957) with { Name = "Old copy" };
        await _redis.SetStringAsync("series:aggregate:v1:78957:eng",
            JsonSerializer.Serialize(oldCopy, RecallJsonOptions.Web));
        _store.Setup(s => s.GetSeriesAggregateAsync(78957, "eng", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CacheSamples.Series(78957));

        var result = await _sut.GetSeriesAggregateByIdAsync(78957);

        result!.Name.Should().Be("Gold Rush");
        _redis.Keys.Should().BeEquivalentTo(["series:aggregate:v1:78957:eng", "series:aggregate:v2:78957:eng"],
            "the old entry is left alone to expire on its own");
        _redis.Reads.Should().Equal("series:aggregate:v2:78957:eng");
    }

    /// <summary>An in-memory <see cref="IDistributedCache"/> that records what it is asked.</summary>
    private sealed class SwitchableCache : IDistributedCache
    {
        private readonly MemoryDistributedCache _inner = new(Options.Create(new MemoryDistributedCacheOptions()));
        private readonly HashSet<string> _keys = [];

        public List<string> Reads { get; } = [];
        public IReadOnlyCollection<string> Keys => _keys;

        public byte[]? Get(string key) => throw new NotSupportedException();
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new NotSupportedException();
        public void Refresh(string key) => throw new NotSupportedException();
        public void Remove(string key) => throw new NotSupportedException();

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            Reads.Add(key);
            return _inner.GetAsync(key, token);
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            _keys.Add(key);
            return _inner.SetAsync(key, value, options, token);
        }

        public Task RefreshAsync(string key, CancellationToken token = default) => throw new NotSupportedException();

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            _keys.Remove(key);
            return _inner.RemoveAsync(key, token);
        }
    }
}
