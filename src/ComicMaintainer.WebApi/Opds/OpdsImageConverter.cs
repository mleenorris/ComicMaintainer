using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.WebApi.Opds;

/// <summary>
/// Turns page and cover images into what an OPDS client may expect, a few at a time. Decoding an image takes real
/// memory (up to about 400 MB at the encoder's pixel limit), and a client that prefetches pages of a library in a
/// format it needs converted asks for many at once, so conversions wait for one of a small number of slots.
/// </summary>
public sealed class OpdsImageConverter
{
    /// <summary>How many images are converted at the same time, across every client.</summary>
    public const int MaxConcurrentConversions = 2;

    // The quality images OPDS does not allow are converted to JPEG at.
    private const int ConvertedQuality = 90;

    private readonly SemaphoreSlim _slots = new(MaxConcurrentConversions, MaxConcurrentConversions);
    private readonly Func<byte[], (byte[] Data, string ContentType)?> _toJpeg;

    public OpdsImageConverter()
        : this(data => CoverThumbnailEncoder.TryCreateThumbnail(data, int.MaxValue, "jpeg", ConvertedQuality))
    {
    }

    /// <summary>Creates the converter around another conversion, for tests that control how long one takes.</summary>
    public OpdsImageConverter(Func<byte[], (byte[] Data, string ContentType)?> toJpeg)
    {
        _toJpeg = toJpeg;
    }

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
            return await Task.Run(() => _toJpeg(data), cancellationToken) ?? (data, contentType);
        }
        finally
        {
            _slots.Release();
        }
    }
}
