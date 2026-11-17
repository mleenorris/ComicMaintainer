using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>Replaces a file's cover with an issue's cover from ComicVine.</summary>
public interface IIssueCoverService
{
    /// <summary>
    /// Downloads the cover of ComicVine issue <paramref name="comicVineIssueId"/>, one the cover
    /// picker listed (see <see cref="IComicVineIssueDetailsService.GetCoverCandidatesAsync"/>), and
    /// makes it page 1 of the CBZ at <paramref name="filePath"/>.
    /// </summary>
    Task<IssueCoverReplaceResult> ReplaceWithComicVineCoverAsync(
        string filePath,
        int comicVineIssueId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The cover of ComicVine issue <paramref name="comicVineIssueId"/> for the cover picker to
    /// show: its small copy when <paramref name="thumbnail"/> is set and ComicVine has one,
    /// otherwise the full one. ComicVine refuses to show its images on other sites, so the
    /// picker cannot link to them: the server fetches them. Null when no cover is stored for
    /// the issue.
    /// </summary>
    Task<IssueCoverImage?> GetCandidateImageAsync(
        int comicVineIssueId,
        bool thumbnail,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IIssueCoverService"/>. Only a cover stored for an issue ComicVine listed is
/// ever downloaded, never an address from the request, and the download gets every check a
/// provider image gets (see <see cref="ISeriesImageStore.FetchImageAsync"/>).
/// </summary>
public sealed class IssueCoverService : IIssueCoverService
{
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly IFileStoreService _fileStore;
    private readonly ISeriesImageStore _imageStore;
    private readonly IIssueCoverWriter _writer;
    private readonly ILogger<IssueCoverService> _logger;

    public IssueCoverService(
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        IFileStoreService fileStore,
        ISeriesImageStore imageStore,
        IIssueCoverWriter writer,
        ILogger<IssueCoverService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _fileStore = fileStore;
        _imageStore = imageStore;
        _writer = writer;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IssueCoverReplaceResult> ReplaceWithComicVineCoverAsync(
        string filePath,
        int comicVineIssueId,
        CancellationToken cancellationToken = default)
    {
        if (await _fileStore.GetFileAsync(filePath, cancellationToken) is not { } file)
        {
            return new IssueCoverReplaceResult(false, false, "The file is not in the library");
        }

        var (coverUrl, _) = await StoredCoverUrlsAsync(comicVineIssueId, cancellationToken);
        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            return new IssueCoverReplaceResult(false, false, "ComicVine has no cover for that issue");
        }

        var download = await DownloadAsync(comicVineIssueId, coverUrl, cancellationToken);
        if (download.Bytes is not { } image || download.ContentType is not { } contentType)
        {
            return new IssueCoverReplaceResult(false, false, download.Error);
        }

        try
        {
            return await _writer.ReplaceFirstPageAsync(file.FilePath, image, contentType, cancellationToken) switch
            {
                IssueCoverWriteOutcome.Replaced => new IssueCoverReplaceResult(true, true, null),
                IssueCoverWriteOutcome.Unchanged => new IssueCoverReplaceResult(true, false, null),
                IssueCoverWriteOutcome.NotWritable => new IssueCoverReplaceResult(false, false, "Only a CBZ's cover can be replaced; convert this file to CBZ first"),
                IssueCoverWriteOutcome.NoPages => new IssueCoverReplaceResult(false, false, "The archive has no pages"),
                IssueCoverWriteOutcome.NotFound => new IssueCoverReplaceResult(false, false, "The file is gone"),
                _ => new IssueCoverReplaceResult(false, false, "The downloaded cover is not a JPEG, PNG or WebP image"),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArchiveLimitExceededException)
        {
            _logger.LogWarning(ex, "Could not replace the cover of {Path}", LoggingHelper.SanitizePathForLog(file.FilePath));
            return new IssueCoverReplaceResult(false, false, $"The archive could not be rewritten: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<IssueCoverImage?> GetCandidateImageAsync(
        int comicVineIssueId,
        bool thumbnail,
        CancellationToken cancellationToken = default)
    {
        var (coverUrl, thumbUrl) = await StoredCoverUrlsAsync(comicVineIssueId, cancellationToken);
        var url = thumbnail && !string.IsNullOrWhiteSpace(thumbUrl) ? thumbUrl : coverUrl;
        return string.IsNullOrWhiteSpace(url) ? null : await DownloadAsync(comicVineIssueId, url, cancellationToken);
    }

    /// <summary>The cover addresses stored for an issue ComicVine listed; nulls when there are none.</summary>
    private async Task<(string? CoverUrl, string? ThumbUrl)> StoredCoverUrlsAsync(int comicVineIssueId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var stored = await db.ComicVineIssues.AsNoTracking()
            .Where(issue => issue.Id == comicVineIssueId)
            .Select(issue => new { issue.CoverUrl, issue.CoverThumbUrl })
            .FirstOrDefaultAsync(cancellationToken);
        return (stored?.CoverUrl, stored?.CoverThumbUrl);
    }

    /// <summary>
    /// Downloads a stored cover with every check a provider image gets (see
    /// <see cref="ISeriesImageStore.FetchImageAsync"/>).
    /// </summary>
    private async Task<IssueCoverImage> DownloadAsync(int comicVineIssueId, string url, CancellationToken cancellationToken)
    {
        try
        {
            var (bytes, contentType) = await _imageStore.FetchImageAsync(url, cancellationToken);
            return new IssueCoverImage(bytes, contentType, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or OperationCanceledException or ArgumentException)
        {
            _logger.LogWarning("Could not download the ComicVine cover of issue {IssueId}: {Reason}", comicVineIssueId, LoggingHelper.SanitizeForLog(ex.Message));
            return new IssueCoverImage(null, null, $"The cover could not be downloaded: {ex.Message}");
        }
    }
}
