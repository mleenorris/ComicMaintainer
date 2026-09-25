using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Queues and performs comic email deliveries to saved ereader devices,
/// optionally converting each issue to EPUB first.
/// </summary>
public class ComicEmailService : IComicEmailService
{
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbFactory;
    private readonly IComicEmailSender _sender;
    private readonly IEpubConversionService _epubConverter;
    private readonly IComicEmailQueue _queue;
    private readonly ISeriesMetadataCacheService _seriesCache;
    private readonly ISeriesImageStore _seriesImages;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<ComicEmailService> _logger;

    /// <summary>
    /// Serializes the "is this file already queued/sent?" check with the insert
    /// that follows it. Without it two concurrent send requests can both see no
    /// handled row and create duplicate deliveries for the same file/device;
    /// the single background consumer only serializes the SMTP work.
    /// </summary>
    private readonly SemaphoreSlim _queueLock = new(1, 1);

    public ComicEmailService(
        IDbContextFactory<ComicMaintainerDbContext> dbFactory,
        IComicEmailSender sender,
        IEpubConversionService epubConverter,
        IComicEmailQueue queue,
        ISeriesMetadataCacheService seriesCache,
        ISeriesImageStore seriesImages,
        IOptionsMonitor<AppSettings> settings,
        ILogger<ComicEmailService> logger)
    {
        _dbFactory = dbFactory;
        _sender = sender;
        _epubConverter = epubConverter;
        _queue = queue;
        _seriesCache = seriesCache;
        _seriesImages = seriesImages;
        _settings = settings;
        _logger = logger;
    }

    public bool IsConfigured => _sender.IsConfigured;

    public async Task<EmailQueueResult> QueueFilesAsync(
        IEnumerable<string> filePaths,
        int deviceId,
        string? deliveryFormat,
        string source,
        bool skipAlreadyDelivered,
        int? subscriptionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Email delivery is not configured. Set the SMTP host and the from address in Settings.");
        }

        var normalizedSource = string.Equals(source, EmailDeliverySource.Auto, StringComparison.OrdinalIgnoreCase)
            ? EmailDeliverySource.Auto
            : EmailDeliverySource.Manual;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var device = await db.EreaderDevices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (device is null)
        {
            throw new InvalidOperationException($"Ereader device {deviceId} was not found.");
        }

        var format = EmailDeliveryFormat.NormalizeOrThrow(deliveryFormat, device.DeliveryFormat, nameof(deliveryFormat));

        var skipped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<ComicEmailDeliveryEntity> queued;

        // The dedupe read and the insert it guards must not interleave with a
        // concurrent request for the same file/device, or both would queue.
        await _queueLock.WaitAsync(cancellationToken);
        try
        {
            queued = await CreateDeliveriesAsync(
                db,
                filePaths,
                device,
                format,
                normalizedSource,
                subscriptionId,
                skipAlreadyDelivered,
                skipped,
                cancellationToken);
        }
        finally
        {
            _queueLock.Release();
        }

        if (queued.Count > 0)
        {
            foreach (var delivery in queued)
            {
                _queue.Enqueue(delivery.Id);
            }

            _logger.LogInformation(
                "Queued {Count} comic email(s) to {DeviceName} as {Format}",
                queued.Count,
                LoggingHelper.SanitizeForLog(device.Name),
                format);
        }

        return new EmailQueueResult(queued.Select(ToDto).ToList(), skipped);
    }

    /// <summary>
    /// Validates each requested path and inserts a pending delivery row for it.
    /// Always called while <see cref="_queueLock"/> is held so the dedupe check
    /// and the insert cannot be interleaved by a concurrent request.
    /// </summary>
    private async Task<List<ComicEmailDeliveryEntity>> CreateDeliveriesAsync(
        ComicMaintainerDbContext db,
        IEnumerable<string> filePaths,
        EreaderDeviceEntity device,
        string format,
        string normalizedSource,
        int? subscriptionId,
        bool skipAlreadyDelivered,
        Dictionary<string, string> skipped,
        CancellationToken cancellationToken)
    {
        var queued = new List<ComicEmailDeliveryEntity>();

        foreach (var rawPath in filePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            if (!TryResolveLibraryPath(rawPath, out var fullPath))
            {
                skipped[rawPath] = "File is outside the comic library";
                continue;
            }

            if (!File.Exists(fullPath))
            {
                skipped[rawPath] = "File not found";
                continue;
            }

            if (!ComicFileExtensions.IsComicArchive(fullPath))
            {
                skipped[rawPath] = "Not a supported comic archive";
                continue;
            }

            if (skipAlreadyDelivered)
            {
                var alreadyHandled = await db.ComicEmailDeliveries.AnyAsync(
                    d => d.FilePath == fullPath &&
                         d.DeviceId == device.Id &&
                         (d.Status == EmailDeliveryStatus.Sent || d.Status == EmailDeliveryStatus.Pending),
                    cancellationToken);

                if (alreadyHandled)
                {
                    skipped[rawPath] = "Already delivered to this device";
                    continue;
                }
            }

            var delivery = new ComicEmailDeliveryEntity
            {
                FilePath = fullPath,
                DeviceId = device.Id,
                DeviceName = device.Name,
                DeviceEmail = device.EmailAddress,
                DeliveryFormat = format,
                Status = EmailDeliveryStatus.Pending,
                Source = normalizedSource,
                SubscriptionId = subscriptionId,
                CreatedAt = DateTime.UtcNow
            };

            db.ComicEmailDeliveries.Add(delivery);
            queued.Add(delivery);
        }

        if (queued.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return queued;
    }

    public async Task<int> QueueAutoSendAsync(
        string filePath,
        string? seriesTitle,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return 0;
        }

        // A subscription may have been created from either the metadata series
        // name or the folder-derived title the library groups by, so match both.
        var keys = BuildSeriesKeyCandidates(filePath, seriesTitle);
        if (keys.Count == 0)
        {
            return 0;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var subscriptions = await (
            from subscription in db.SeriesEmailSubscriptions.AsNoTracking()
            join device in db.EreaderDevices.AsNoTracking() on subscription.DeviceId equals device.Id
            where keys.Contains(subscription.NormalizedSeriesKey) && subscription.Enabled
            select new { subscription, device })
            .ToListAsync(cancellationToken);

        if (subscriptions.Count == 0)
        {
            return 0;
        }

        if (!IsConfigured)
        {
            _logger.LogWarning(
                "Series {SeriesKey} is marked for automatic email delivery but SMTP is not configured; skipping",
                LoggingHelper.SanitizeForLog(subscriptions[0].subscription.NormalizedSeriesKey));
            return 0;
        }

        var queuedCount = 0;
        foreach (var row in subscriptions)
        {
            var format = string.Equals(row.subscription.DeliveryFormat, EmailDeliveryFormat.Device, StringComparison.OrdinalIgnoreCase)
                ? row.device.DeliveryFormat
                : row.subscription.DeliveryFormat;

            try
            {
                var result = await QueueFilesAsync(
                    new[] { filePath },
                    row.device.Id,
                    format,
                    EmailDeliverySource.Auto,
                    skipAlreadyDelivered: true,
                    subscriptionId: row.subscription.Id,
                    cancellationToken);

                queuedCount += result.Queued.Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    ex,
                    "Failed to queue automatic delivery of {FilePath} to device {DeviceId}",
                    LoggingHelper.SanitizePathForLog(filePath),
                    row.device.Id);
            }
        }

        // LastSentAt is deliberately not stamped here: the message has only been
        // queued. It is set in ProcessDeliveryAsync, per subscription, once the
        // delivery it produced has actually been sent.
        return queuedCount;
    }

    public async Task ProcessDeliveryAsync(int deliveryId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var delivery = await db.ComicEmailDeliveries.FirstOrDefaultAsync(d => d.Id == deliveryId, cancellationToken);
        if (delivery is null)
        {
            _logger.LogWarning("Comic email delivery {DeliveryId} no longer exists", deliveryId);
            return;
        }

        if (!string.Equals(delivery.Status, EmailDeliveryStatus.Pending, StringComparison.Ordinal))
        {
            return;
        }

        string? temporaryAttachment = null;
        try
        {
            if (!TryResolveLibraryPath(delivery.FilePath, out var fullPath) || !File.Exists(fullPath))
            {
                throw new FileNotFoundException($"File not found: {Path.GetFileName(delivery.FilePath)}");
            }

            var attachmentPath = fullPath;
            if (string.Equals(delivery.DeliveryFormat, EmailDeliveryFormat.Epub, StringComparison.OrdinalIgnoreCase))
            {
                var workDirectory = Path.Combine(GetTempDirectory(), "email", Guid.NewGuid().ToString("N"));
                var options = await BuildEpubOptionsAsync(fullPath, cancellationToken);
                attachmentPath = await _epubConverter.ConvertToEpubAsync(fullPath, workDirectory, options, cancellationToken);
                temporaryAttachment = attachmentPath;
            }

            var maxBytes = (long)Math.Max(1, _settings.CurrentValue.EmailMaxAttachmentMegabytes) * 1024 * 1024;
            var attachmentLength = new FileInfo(attachmentPath).Length;
            if (attachmentLength > maxBytes)
            {
                throw new InvalidOperationException(
                    $"Attachment is {attachmentLength / (1024 * 1024)} MB which exceeds the {_settings.CurrentValue.EmailMaxAttachmentMegabytes} MB limit.");
            }

            var displayName = Path.GetFileNameWithoutExtension(delivery.FilePath);
            await _sender.SendAsync(
                new ComicEmailMessage(
                    delivery.DeviceEmail,
                    displayName,
                    $"{displayName} sent from ComicMaintainer.",
                    attachmentPath,
                    Path.GetFileName(attachmentPath),
                    GetAttachmentContentType(attachmentPath)),
                cancellationToken);

            delivery.Status = EmailDeliveryStatus.Sent;
            delivery.SentAt = DateTime.UtcNow;
            delivery.ErrorMessage = null;

            if (delivery.SubscriptionId is int subscriptionId)
            {
                // The message is already out; do not let a cancellation here
                // lose the sent status or the timestamp.
                var subscription = await db.SeriesEmailSubscriptions
                    .FirstOrDefaultAsync(s => s.Id == subscriptionId, CancellationToken.None);
                if (subscription is not null)
                {
                    subscription.LastSentAt = delivery.SentAt;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            delivery.Status = EmailDeliveryStatus.Failed;
            delivery.ErrorMessage = Truncate(ex.Message, 1024);
            _logger.LogError(
                ex,
                "Failed to email {FilePath} to device {DeviceId}",
                LoggingHelper.SanitizePathForLog(delivery.FilePath),
                delivery.DeviceId);
        }
        finally
        {
            CleanupTemporaryAttachment(temporaryAttachment);
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    public async Task SendTestEmailAsync(int deviceId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var device = await db.EreaderDevices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (device is null)
        {
            throw new InvalidOperationException($"Ereader device {deviceId} was not found.");
        }

        await _sender.SendAsync(
            new ComicEmailMessage(
                device.EmailAddress,
                "ComicMaintainer test email",
                "This is a test message from ComicMaintainer. Your email delivery settings are working."),
            cancellationToken);
    }

    public async Task<IReadOnlyList<ComicEmailDeliveryDto>> GetRecentDeliveriesAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, 500);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var deliveries = await db.ComicEmailDeliveries
            .AsNoTracking()
            .OrderByDescending(d => d.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        return deliveries.Select(ToDto).ToList();
    }

    /// <summary>
    /// Normalized series keys a file could be subscribed under: its metadata
    /// series name and the name of the folder that contains it (the title the
    /// library groups series by).
    /// </summary>
    private List<string> BuildSeriesKeyCandidates(string filePath, string? seriesTitle)
    {
        var keys = new List<string>(2);

        void Add(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return;
            }

            var key = _seriesCache.NormalizeKey(title);
            if (!string.IsNullOrWhiteSpace(key) && !keys.Contains(key, StringComparer.Ordinal))
            {
                keys.Add(key);
            }
        }

        Add(seriesTitle);

        try
        {
            Add(Path.GetFileName(Path.GetDirectoryName(filePath)));
        }
        catch (ArgumentException)
        {
            // Malformed path: the metadata-derived key (if any) is still usable.
        }

        return keys;
    }

    /// <summary>
    /// Looks up the cached series record for a file so the generated EPUB can
    /// carry the series cover art and the resolved series name. Best-effort:
    /// a miss (or any lookup failure) simply produces an EPUB that falls back
    /// to the first comic page as its cover.
    /// </summary>
    private async Task<EpubConversionOptions?> BuildEpubOptionsAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        try
        {
            // The library groups a series by the name of its containing folder,
            // which is also how the cache record is keyed.
            var folderTitle = Path.GetFileName(Path.GetDirectoryName(fullPath));
            if (string.IsNullOrWhiteSpace(folderTitle))
            {
                return null;
            }

            var record = await _seriesCache.GetByTitleAsync(folderTitle, cancellationToken);
            if (record is null)
            {
                return null;
            }

            var imagePath = string.IsNullOrWhiteSpace(record.LocalImageFile)
                ? null
                : _seriesImages.ResolveAbsolutePath(record.LocalImageFile);

            var seriesTitle = string.IsNullOrWhiteSpace(record.ResolvedSeriesName)
                ? record.CanonicalTitle
                : record.ResolvedSeriesName;

            if (imagePath is null && string.IsNullOrWhiteSpace(seriesTitle))
            {
                return null;
            }

            return new EpubConversionOptions(imagePath, seriesTitle);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Could not resolve series artwork for {FilePath}; sending EPUB without series cover",
                LoggingHelper.SanitizePathForLog(fullPath));
            return null;
        }
    }

    private string GetTempDirectory()
    {
        var configured = _settings.CurrentValue.TempFileDirectory;
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetTempPath(), "ComicMaintainer")
            : configured;
    }

    /// <summary>
    /// Resolves a caller-supplied path and confirms it lives inside the watched
    /// or duplicate directory, so a delivery request can never be used to email
    /// arbitrary files off the host. Symlinks and junctions are resolved first —
    /// both on the file itself and on every ancestor directory — so a link
    /// inside the library cannot smuggle in an external target.
    /// </summary>
    private bool TryResolveLibraryPath(string path, out string fullPath)
    {
        fullPath = string.Empty;

        try
        {
            var resolved = ResolveRealPath(Path.GetFullPath(path));
            var settings = _settings.CurrentValue;

            if (!IsWithin(resolved, settings.WatchedDirectory) &&
                !IsWithin(resolved, settings.DuplicateDirectory))
            {
                return false;
            }

            fullPath = resolved;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the physical path a (possibly linked) path ultimately refers to,
    /// resolving each path segment from the root down. Segments that do not
    /// exist, or are not links, are kept verbatim.
    /// </summary>
    private static string ResolveRealPath(string fullPath)
    {
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent))
        {
            // Root of the volume: nothing above it can be a link.
            return ResolveLinkTarget(fullPath) ?? fullPath;
        }

        var candidate = Path.Combine(ResolveRealPath(parent), Path.GetFileName(fullPath));
        return ResolveLinkTarget(candidate) ?? candidate;
    }

    private static string? ResolveLinkTarget(string path)
    {
        try
        {
            FileSystemInfo? target = Directory.Exists(path)
                ? Directory.ResolveLinkTarget(path, returnFinalTarget: true)
                : File.Exists(path)
                    ? File.ResolveLinkTarget(path, returnFinalTarget: true)
                    : null;

            return target is null ? null : Path.GetFullPath(target.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Unresolvable (broken or cyclic link, or no permission): treat the
            // path as-is; the containment check below still has to pass.
            return null;
        }
    }

    private static bool IsWithin(string fullPath, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var root = ResolveRealPath(Path.GetFullPath(directory)).TrimEnd(Path.DirectorySeparatorChar);
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private void CleanupTemporaryAttachment(string? attachmentPath)
    {
        if (string.IsNullOrEmpty(attachmentPath))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(attachmentPath);
            if (File.Exists(attachmentPath))
            {
                File.Delete(attachmentPath);
            }

            if (!string.IsNullOrEmpty(directory) &&
                Directory.Exists(directory) &&
                !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to clean up temporary EPUB attachment");
        }
    }

    private static string GetAttachmentContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".epub" => "application/epub+zip",
            ".cbz" => "application/vnd.comicbook+zip",
            ".cbr" => "application/vnd.comicbook-rar",
            _ => "application/octet-stream"
        };

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static ComicEmailDeliveryDto ToDto(ComicEmailDeliveryEntity entity) => new(
        entity.Id,
        entity.FilePath,
        Path.GetFileName(entity.FilePath),
        entity.DeviceId,
        entity.DeviceName,
        entity.DeviceEmail,
        entity.DeliveryFormat,
        entity.Status,
        entity.Source,
        entity.ErrorMessage,
        entity.CreatedAt,
        entity.SentAt);
}
