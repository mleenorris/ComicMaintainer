using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Default <see cref="ICoverFingerprintService"/>: a file's cover from the cover cache (which renders
/// it once), a ComicVine cover through the image store's checked download.
/// </summary>
public sealed class CoverFingerprintService : ICoverFingerprintService
{
    private readonly IFileCoverCacheService _covers;
    private readonly ISeriesImageStore _images;
    private readonly ILogger<CoverFingerprintService> _logger;

    public CoverFingerprintService(IFileCoverCacheService covers, ISeriesImageStore images, ILogger<CoverFingerprintService> logger)
    {
        _covers = covers;
        _images = images;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ulong?> OfFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            return CoverFingerprint.Of((await _covers.GetOrCreateAsync(filePath, cancellationToken))?.Data);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Could not read the cover of {Path} for its fingerprint", LoggingHelper.SanitizePathForLog(filePath));
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<ulong?> OfImageAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            var (bytes, _) = await _images.FetchImageAsync(url, cancellationToken);
            return CoverFingerprint.Of(bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug("Could not download a ComicVine cover for its fingerprint: {Reason}", LoggingHelper.SanitizeForLog(ex.Message));
            return null;
        }
    }
}
