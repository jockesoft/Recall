using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure;
using Recall.Web.Infrastructure.Caching;

namespace Recall.Tests.Infrastructure.Caching;

[TestFixture]
public class DistributedCacheJsonTests
{
    private MemoryDistributedCache _memory = null!;
    private RecordingLogger<DistributedCacheJson> _logger = null!;
    private DistributedCacheJson _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _memory = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        _logger = new RecordingLogger<DistributedCacheJson>();
        _sut = new DistributedCacheJson(_memory, _logger);
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
}
