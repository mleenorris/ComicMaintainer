using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Caching.Memory;

namespace ComicMaintainer.WebApi.Opds;

/// <summary>
/// Turns page and cover images into what an OPDS client may expect, a few at a time. Decoding an image takes real
/// memory (up to about 400 MB at the encoder's pixel limit), and a client that prefetches pages of a library in a
/// format it needs converted asks for many at once, so conversions wait for one of a small number of slots.
/// </summary>
/// <remarks>
/// Pages that had to be converted are kept for a while in a small cache bounded by size, keyed by the file and the
/// page's ETag (which changes with the file), so a client that asks for a page again, or two clients reading the
/// same issue, do not pay for the conversion twice. Covers are not converted here at all: the cover cache keeps a
/// JPEG encoding of its own.
/// </remarks>
public sealed class OpdsImageConverter : IDisposable
{
    /// <summary>How many images are converted at the same time, across every client.</summary>
    public const int MaxConcurrentConversions = 2;

    /// <summary>How much converted page data is kept: 32 MiB.</summary>
    public const long ConvertedPageCacheBytes = 32L * 1024 * 1024;

    // A converted page nobody has asked for in this long is dropped.
    private static readonly TimeSpan ConvertedPageSlidingExpiration = TimeSpan.FromMinutes(10);

    // The quality images OPDS does not allow are converted to JPEG at.
    private const int ConvertedQuality = 90;

    private readonly SemaphoreSlim _slots = new(MaxConcurrentConversions, MaxConcurrentConversions);
    private readonly Func<byte[], (byte[] Data, string ContentType)?> _toJpeg;
    private readonly MemoryCache _converted = new(new MemoryCacheOptions { SizeLimit = ConvertedPageCacheBytes });
    private int _conversions;

    public OpdsImageConverter()
        : this(data => CoverThumbnailEncoder.TryCreateThumbnail(data, int.MaxValue, "jpeg", ConvertedQuality))
    {
    }

    /// <summary>Creates the converter around another conversion, for tests that control how long one takes.</summary>
    public OpdsImageConverter(Func<byte[], (byte[] Data, string ContentType)?> toJpeg)
    {
        _toJpeg = toJpeg;
    }

    /// <summary>How many images have been converted (for tests).</summary>
    public int ConversionCount => Volatile.Read(ref _conversions);

    /// <summary>
    /// The image as a client may expect it: unchanged when it is already of <paramref name="target"/>, otherwise
    /// converted to JPEG. An image that cannot be converted is passed through unchanged.
    /// </summary>
    public async Task<(byte[] Data, string ContentType)> ConvertAsync(byte[] data, string contentType, string target, CancellationToken cancellationToken)
    {
        if (string.Equals(contentType, target, StringComparison.OrdinalIgnoreCase))
        {
            return (data, contentType);
        }

        await _slots.WaitAsync(cancellationToken);
        try
        {
            Interlocked.Increment(ref _conversions);
            return await Task.Run(() => _toJpeg(data), cancellationToken) ?? (data, contentType);
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>
    /// A page converted for a client earlier, under <paramref name="key"/> (the file and the page's ETag), if it is
    /// still kept.
    /// </summary>
    public bool TryGetConvertedPage(string key, out (byte[] Data, string ContentType) page) =>
        _converted.TryGetValue(key, out page);

    /// <summary>
    /// Converts a page like <see cref="ConvertAsync"/> and keeps the result under <paramref name="key"/> when it was
    /// actually converted, for <see cref="TryGetConvertedPage"/>.
    /// </summary>
    public async Task<(byte[] Data, string ContentType)> ConvertPageAsync(string key, byte[] data, string contentType, string target, CancellationToken cancellationToken)
    {
        var converted = await ConvertAsync(data, contentType, target, cancellationToken);
        if (!ReferenceEquals(converted.Data, data) && converted.Data.Length <= ConvertedPageCacheBytes / 4)
        {
            _converted.Set(key, converted, new MemoryCacheEntryOptions
            {
                Size = converted.Data.Length,
                SlidingExpiration = ConvertedPageSlidingExpiration,
            });
        }

        return converted;
    }

    public void Dispose()
    {
        _converted.Dispose();
        _slots.Dispose();
    }
}
