using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;

namespace Recall.Web.Infrastructure.Caching;

/// <summary>
/// The byte format of a value in the Redis tier: one format byte, the length of
/// the JSON as a little-endian <see cref="int"/>, then the JSON compressed with
/// Brotli. A series aggregate is mostly repeated field names and image URLs, so
/// it shrinks to about a quarter (221 KB to 58 KB for the largest in the dev
/// cache), which is what every Redis read sends over the wire.
/// </summary>
/// <remarks>
/// Brotli at quality 4 was chosen by measurement (2026-11-14, dev cache): about
/// the size of GZip's <c>Optimal</c> level (24% of the JSON for series
/// aggregates, against 24%), compressed faster (1.3 ms against 1.5 ms for the
/// largest series) and decompressed about as fast (0.3 ms), and it has span
/// APIs, so a read decompresses into one pooled buffer with no streams.
/// Quality 11 is a fifth smaller still but takes 180 ms to compress.
/// The Postgres snapshot tables keep plain <c>jsonb</c>; this is the Redis
/// format only. Changing it means a new format byte or new cache key versions
/// (<see cref="TvdbCacheKeys"/>), never reading old bytes as the new format.
/// </remarks>
public static class CachePayload
{
    /// <summary>Brotli, quality 4, window 22. A plain JSON value starts with <c>{</c> (0x7B), never this.</summary>
    public const byte BrotliFormat = 0x01;

    public const int Quality = 4;
    private const int Window = 22;
    private const int HeaderLength = 5;

    /// <summary>
    /// A declared length past this is treated as damage rather than allocated.
    /// The largest series aggregate in production is under 1 MB.
    /// </summary>
    public const int MaxJsonBytes = 64 * 1024 * 1024;

    public static byte[] Serialize<T>(T value, JsonSerializerOptions options)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, options);
        var maxCompressed = BrotliEncoder.GetMaxCompressedLength(json.Length);
        var buffer = ArrayPool<byte>.Shared.Rent(HeaderLength + maxCompressed);
        try
        {
            buffer[0] = BrotliFormat;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(1, 4), json.Length);
            if (!BrotliEncoder.TryCompress(json, buffer.AsSpan(HeaderLength, maxCompressed), out var written, Quality, Window))
                throw new InvalidOperationException("Brotli compression of a cache payload failed.");

            return buffer.AsSpan(0, HeaderLength + written).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// False when <paramref name="stored"/> is not in this format (an old
    /// uncompressed value, damage, a declared length that does not match what
    /// decompresses), so the caller can treat it as a miss. A <see cref="JsonException"/>
    /// from a payload that decompresses but does not deserialize still throws.
    /// </summary>
    public static bool TryDeserialize<T>(byte[] stored, JsonSerializerOptions options, out T? value)
    {
        value = default;
        if (stored.Length < HeaderLength || stored[0] != BrotliFormat)
            return false;

        var length = BinaryPrimitives.ReadInt32LittleEndian(stored.AsSpan(1, 4));
        if (length is <= 0 or > MaxJsonBytes)
            return false;

        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            if (!BrotliDecoder.TryDecompress(stored.AsSpan(HeaderLength), buffer.AsSpan(0, length), out var written)
                || written != length)
                return false;

            value = JsonSerializer.Deserialize<T>(buffer.AsSpan(0, length), options);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
