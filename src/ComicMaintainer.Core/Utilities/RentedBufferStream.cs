using System.Buffers;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// A read-only stream over the first bytes of an array rented from
/// <see cref="ArrayPool{T}.Shared"/>, which gives the array back when it is disposed. A page is
/// read into one of these, sent, and dropped, so serving pages does not allocate a fresh
/// multi-megabyte array (on the large object heap) per request.
/// </summary>
internal sealed class RentedBufferStream : MemoryStream
{
    private byte[]? _rented;

    public RentedBufferStream(byte[] rented, int length)
        : base(rented, 0, length, writable: false, publiclyVisible: false)
    {
        _rented = rented;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        var rented = Interlocked.Exchange(ref _rented, null);
        if (rented is not null)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>The bytes as a new array of their exact length, for callers that keep them.</summary>
    public byte[] ToExactArray()
    {
        ObjectDisposedException.ThrowIf(_rented is null, this);
        return ToArray();
    }
}
