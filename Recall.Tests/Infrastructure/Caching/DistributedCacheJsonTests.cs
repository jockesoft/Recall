using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure;
using Recall.Web.Infrastructure.Caching;

namespace Recall.Tests.Infrastructure.Caching;

[TestFixture]
public class DistributedCacheJsonTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private MemoryDistributedCache _memory = null!;
    private FixedTimeProvider _clock = null!;
    private RecordingLogger<DistributedCacheJson> _logger = null!;
    private DistributedCacheJson _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _memory = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        _clock = new FixedTimeProvider(Now);
        _logger = new RecordingLogger<DistributedCacheJson>();
        _sut = new DistributedCacheJson(_memory, _clock, _logger);
    }

    // ---- compressed round trips ------------------------------------------------

    [Test]
    public async Task SeriesAggregate_RoundTrips_Compressed() =>
        await AssertRoundTripAsync(CacheSamples.Series(), "series:aggregate:v2:78957:eng");

    [Test]
    public async Task MovieAggregate_RoundTrips_Compressed() =>
        await AssertRoundTripAsync(CacheSamples.Movie(), "movie:aggregate:v2:7:eng");

    [Test]
    public async Task EpisodeExtended_RoundTrips_Compressed() =>
        await AssertRoundTripAsync(CacheSamples.Episode(), "episode:extended:v3:2001:eng");

    [Test]
    public async Task SeriesExtended_RoundTrips_Compressed() =>
        await AssertRoundTripAsync(CacheSamples.SeriesExtended(), "series:extended:v3:78957");

    private async Task AssertRoundTripAsync<T>(T value, string key)
    {
        await _sut.SetAsync(key, value, TimeSpan.FromDays(7));

        var stored = (await _memory.GetAsync(key))!;
        var json = JsonSerializer.SerializeToUtf8Bytes(value, RecallJsonOptions.Web);
        stored[0].Should().Be(CachePayload.BrotliFormat);
        stored.Length.Should().BeLessThan(json.Length, "the value is stored compressed, not as JSON");

        var read = await _sut.GetAsync<T>(key);

        read.Should().BeEquivalentTo(value);
        JsonSerializer.SerializeToUtf8Bytes(read, RecallJsonOptions.Web).Should().Equal(json,
            "the value read back serializes to exactly the JSON that went in");
    }

    [Test]
    public void LargeSeriesAggregate_ShrinksToUnderAThird()
    {
        var series = CacheSamples.Series();
        var json = JsonSerializer.SerializeToUtf8Bytes(series, RecallJsonOptions.Web);

        var stored = CachePayload.Serialize(series, RecallJsonOptions.Web);

        stored.Length.Should().BeLessThan(json.Length / 3);
    }

    // ---- values that are not in the format -------------------------------------

    [Test]
    public async Task UncompressedJson_AsTheOldEntriesWere_IsAMiss()
    {
        // What the cache held before 2026-11-14: UTF-8 JSON via SetStringAsync.
        await _memory.SetStringAsync("series:aggregate:v2:78957:eng",
            JsonSerializer.Serialize(CacheSamples.Series(), RecallJsonOptions.Web));

        var read = await _sut.GetAsync<SeriesAggregate>("series:aggregate:v2:78957:eng");

        read.Should().BeNull();
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
    }

    [Test]
    public async Task TruncatedPayload_IsAMiss()
    {
        var stored = CachePayload.Serialize(CacheSamples.Series(), RecallJsonOptions.Web);
        await _memory.SetAsync("k", stored[..(stored.Length / 2)], new DistributedCacheEntryOptions());

        (await _sut.GetAsync<SeriesAggregate>("k")).Should().BeNull();
    }

    [Test]
    public async Task PayloadWhoseDeclaredLengthIsWrong_IsAMiss()
    {
        var stored = CachePayload.Serialize(CacheSamples.Movie(), RecallJsonOptions.Web);
        stored[1] ^= 0x01; // the length's low byte
        await _memory.SetAsync("k", stored, new DistributedCacheEntryOptions());

        (await _sut.GetAsync<MovieAggregate>("k")).Should().BeNull();
    }

    [Test]
    public async Task PayloadThatDecompressesButIsNotJson_IsAMiss()
    {
        var stored = CachePayload.Serialize("not an object", RecallJsonOptions.Web);
        await _memory.SetAsync("k", stored, new DistributedCacheEntryOptions());

        (await _sut.GetAsync<MovieAggregate>("k")).Should().BeNull();
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
    }

    [Test]
    public void CachePayload_RejectsAnAbsurdDeclaredLength_WithoutAllocatingIt()
    {
        byte[] stored = [CachePayload.BrotliFormat, 0xFF, 0xFF, 0xFF, 0x7F, 0x00];

        CachePayload.TryDeserialize<SeriesAggregate>(stored, RecallJsonOptions.Web, out var value).Should().BeFalse();
        value.Should().BeNull();
    }

    // ---- Redis failing -----------------------------------------------------------

    [Test]
    public async Task FailingRedis_ReadIsAMiss_WriteIsSkipped_AndNothingThrows()
    {
        var failing = FailingCache();
        var sut = new DistributedCacheJson(failing.Object, _clock, _logger);

        var read = await sut.GetAsync<SeriesAggregate>("series:aggregate:v2:1:eng");
        var write = () => sut.SetAsync("series:aggregate:v2:1:eng", CacheSamples.Series(1), TimeSpan.FromDays(7));
        var remove = () => sut.RemoveAsync("series:aggregate:v2:1:eng");

        read.Should().BeNull();
        await write.Should().NotThrowAsync();
        await remove.Should().NotThrowAsync();
    }

    [Test]
    public async Task FailingRedis_IsBypassedForHalfAMinute_ThenTriedAgain()
    {
        var failing = FailingCache();
        var sut = new DistributedCacheJson(failing.Object, _clock, _logger);

        await sut.GetAsync<SeriesAggregate>("a");
        await sut.GetAsync<SeriesAggregate>("b");
        await sut.SetAsync("c", CacheSamples.Movie(), TimeSpan.FromDays(7));
        _clock.Now += DistributedCacheJson.BypassFor - TimeSpan.FromSeconds(1);
        await sut.GetAsync<SeriesAggregate>("d");

        failing.Verify(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        failing.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()), Times.Never);

        _clock.Now += TimeSpan.FromSeconds(1);
        await sut.GetAsync<SeriesAggregate>("e");

        failing.Verify(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task FailingRedis_LogsOneWarningPerOutage()
    {
        var failing = FailingCache();
        var sut = new DistributedCacheJson(failing.Object, _clock, _logger);

        await sut.GetAsync<SeriesAggregate>("a");
        _clock.Now += DistributedCacheJson.BypassFor; // still down when the bypass ends
        await sut.GetAsync<SeriesAggregate>("b");

        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("bypassed");
    }

    [Test]
    public async Task RedisThatComesBack_IsUsedAgain()
    {
        var flaky = new Mock<IDistributedCache>();
        flaky.SetupSequence(c => c.GetAsync("k", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Redis timed out"))
            .ReturnsAsync(CachePayload.Serialize(CacheSamples.Movie(), RecallJsonOptions.Web));
        var sut = new DistributedCacheJson(flaky.Object, _clock, _logger);

        (await sut.GetAsync<MovieAggregate>("k")).Should().BeNull();
        _clock.Now += DistributedCacheJson.BypassFor;

        (await sut.GetAsync<MovieAggregate>("k")).Should().BeEquivalentTo(CacheSamples.Movie());
    }

    [Test]
    public async Task CallersOwnCancellation_StillPropagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cache = new Mock<IDistributedCache>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));
        var sut = new DistributedCacheJson(cache.Object, _clock, _logger);

        var read = () => sut.GetAsync<SeriesAggregate>("k", cts.Token);

        await read.Should().ThrowAsync<OperationCanceledException>();
    }

    private static Mock<IDistributedCache> FailingCache()
    {
        var failing = new Mock<IDistributedCache>();
        var down = new InvalidOperationException("It was not possible to connect to the redis server(s).");
        failing.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(down);
        failing.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>())).ThrowsAsync(down);
        failing.Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(down);
        return failing;
    }
}
