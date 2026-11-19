namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// The fingerprints of covers (see <see cref="Utilities.CoverFingerprint"/>): a library file's, and a
/// ComicVine cover's, to tell which ComicVine volume's issues a file's cover matches.
/// </summary>
public interface ICoverFingerprintService
{
    /// <summary>The fingerprint of the cover of the file at <paramref name="filePath"/>; null when it has none that can be read.</summary>
    Task<ulong?> OfFileAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// The fingerprint of the image at <paramref name="url"/>, downloaded with every check a provider
    /// image gets; null when it cannot be downloaded or read.
    /// </summary>
    Task<ulong?> OfImageAsync(string url, CancellationToken cancellationToken = default);
}
