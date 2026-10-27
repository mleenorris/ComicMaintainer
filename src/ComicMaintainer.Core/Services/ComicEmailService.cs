using ComicMaintainer.Core.Telemetry;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.ErrorReporting;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
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
    private readonly IAzw3ConversionService _azw3Converter;
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

    /// <summary>
    /// Serializes the processor claiming a pending delivery against a cancel
    /// request deciding whether a delivery can be cancelled outright. Without
    /// it a delivery could be marked cancelled after the processor loaded it
    /// as pending, and then be sent anyway.
    /// </summary>
    private readonly SemaphoreSlim _deliveryStateLock = new(1, 1);

    /// <summary>
    /// Deliveries the processor has claimed and not finished yet, by id. A
    /// cancel request for one of these asks the processor to stop instead of
    /// changing the row underneath it.
    /// </summary>
    private readonly ConcurrentDictionary<int, InFlightDelivery> _inFlight = new();

    /// <summary>
    /// How long a cancel request waits for a delivery that is being prepared
    /// to stop before reporting it as still stopping.
    /// </summary>
    private static readonly TimeSpan InFlightCancelWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Longest single sleep while waiting for a rate-limit slot, so a changed
    /// or removed limit takes effect promptly.
    /// </summary>
    private static readonly TimeSpan MaxRateLimitPoll = TimeSpan.FromSeconds(30);

    /// <summary>Ticks of <see cref="RateLimitedUntil"/>; 0 when nothing is held back.</summary>
    private long _rateLimitedUntilTicks;

    /// <summary>
    /// An explicitly marked chapter/issue number ("Ch 12", "#007", "issue 3").
    /// Preferred over <see cref="TrailingNumberPattern"/> so a year or volume in
    /// the name cannot be mistaken for the issue number.
    /// </summary>
    private static readonly Regex MarkedIssueNumberPattern = new(
        @"(?:\b(?:ch|chapter|issue)\b\s*\.?\s*|#\s*)(\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Fallback for unmarked file names: the last number in the name once
    /// bracketed qualifiers such as "(2016)" or "[HD]" have been removed, which
    /// is where the issue number sits in "Series 005.cbz".
    /// </summary>
    private static readonly Regex TrailingNumberPattern = new(
        @"(\d+(?:\.\d+)?)(?!.*\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>Bracketed qualifiers stripped before the fallback match.</summary>
    private static readonly Regex BracketedQualifierPattern = new(
        @"[\(\[\{][^\)\]\}]*[\)\]\}]",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Extensions a series cover sidecar can have, in the order they are
    /// preferred. Mirrors the extensions <see cref="ISeriesFolderCoverWriter"/>
    /// writes.
    /// </summary>
    private static readonly string[] SeriesFolderCoverExtensions = { ".jpg", ".png", ".webp" };

    private readonly ComicMaintainerMetrics? _metrics;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Cancelled once the application starts shutting down. A delivery waiting
    /// for a rate-limit slot then stays pending instead of holding the shutdown
    /// back until the slot frees up.
    /// </summary>
    private readonly CancellationToken _applicationStopping;

    public ComicEmailService(
        IDbContextFactory<ComicMaintainerDbContext> dbFactory,
        IComicEmailSender sender,
        IEpubConversionService epubConverter,
        IAzw3ConversionService azw3Converter,
        IComicEmailQueue queue,
        ISeriesMetadataCacheService seriesCache,
        ISeriesImageStore seriesImages,
        IOptionsMonitor<AppSettings> settings,
        ILogger<ComicEmailService> logger,
        ComicMaintainerMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IHostApplicationLifetime? applicationLifetime = null)
    {
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _applicationStopping = applicationLifetime?.ApplicationStopping ?? CancellationToken.None;
        _dbFactory = dbFactory;
        _sender = sender;
        _epubConverter = epubConverter;
        _azw3Converter = azw3Converter;
        _queue = queue;
        _seriesCache = seriesCache;
        _seriesImages = seriesImages;
        _settings = settings;
        _logger = logger;
    }

    public bool IsConfigured => _sender.IsConfigured;

    public DateTime? RateLimitedUntil
    {
        get
        {
            var ticks = Volatile.Read(ref _rateLimitedUntilTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

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

        // Includes the member issues of condensed deliveries, so an issue that
        // already went out inside a condensed book is not sent again on its own.
        var delivered = skipAlreadyDelivered
            ? await GetDeliveredPathsAsync(db, device.Id, cancellationToken)
            : null;

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

            if (delivered is not null && delivered.Contains(fullPath))
            {
                skipped[rawPath] = "Already delivered to this device";
                continue;
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

    public async Task<EmailQueueResult> QueueCondensedFilesAsync(
        IEnumerable<string> filePaths,
        int deviceId,
        string? condenseMode,
        int? issuesPerBook,
        string source,
        bool skipAlreadyDelivered,
        bool preserveIssueOrder = false,
        string? deliveryFormat = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Email delivery is not configured. Set the SMTP host and the from address in Settings.");
        }

        var mode = EmailCondenseMode.NormalizeOrThrow(condenseMode, nameof(condenseMode));
        if (!EmailCondenseMode.IsCondensing(mode))
        {
            throw new ArgumentException(
                "Condensed delivery requires the 'count' or 'all' condense mode.",
                nameof(condenseMode));
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

        var format = EmailDeliveryFormat.NormalizeCondensedOrThrow(
            deliveryFormat,
            device.DeliveryFormat,
            nameof(deliveryFormat));

        var skipped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<ComicEmailDeliveryEntity> queued;

        // The dedupe read and the inserts it guards must not interleave with a
        // concurrent request for the same issues, or both would queue.
        await _queueLock.WaitAsync(cancellationToken);
        try
        {
            var candidates = ResolveIssues(filePaths, skipped);

            if (skipAlreadyDelivered && candidates.Count > 0)
            {
                candidates = await FilterAlreadyDeliveredAsync(db, device.Id, candidates, skipped, cancellationToken);
            }

            var perBook = EmailCondenseMode.NormalizeIssuesPerBookOrThrow(
                mode,
                issuesPerBook,
                candidates.Count,
                nameof(issuesPerBook));

            var maxBytes = GetMaxAttachmentBytes();
            queued = new List<ComicEmailDeliveryEntity>();
            foreach (var group in BuildBooks(candidates, preserveIssueOrder, perBook))
            {
                var displayName = await BuildCondensedNameAsync(group, cancellationToken);

                // The plan endpoint disables Send for an oversized book, but a
                // direct API call would otherwise queue it and only fail after
                // the worker has rebuilt it through every compression tier.
                var estimated = EstimateCondensedSize(group);
                if (estimated > maxBytes)
                {
                    throw new InvalidOperationException(
                        $"Condensed book \"{displayName}\" is about {FormatMegabytes(estimated)} which exceeds the {FormatMegabytes(maxBytes)} attachment limit. Download it instead, or condense fewer issues per book.");
                }

                var delivery = new ComicEmailDeliveryEntity
                {
                    FilePath = group[0],
                    DeviceId = device.Id,
                    DeviceName = device.Name,
                    DeviceEmail = device.EmailAddress,
                    // Only a generated book can carry several issues; the source
                    // archives themselves cannot be merged.
                    DeliveryFormat = format,
                    Status = EmailDeliveryStatus.Pending,
                    Source = normalizedSource,
                    CondensedFilePaths = group.Count > 1 ? JsonSerializer.Serialize(group) : null,
                    DisplayName = displayName,
                    CreatedAt = DateTime.UtcNow
                };

                db.ComicEmailDeliveries.Add(delivery);
                queued.Add(delivery);
            }

            if (queued.Count > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        finally
        {
            _queueLock.Release();
        }

        foreach (var delivery in queued)
        {
            _queue.Enqueue(delivery.Id);
        }

        if (queued.Count > 0)
        {
            _logger.LogInformation(
                "Queued {Count} condensed comic email(s) to {DeviceName} as {Format}",
                queued.Count,
                LoggingHelper.SanitizeForLog(device.Name),
                format);
        }

        return new EmailQueueResult(queued.Select(ToDto).ToList(), skipped);
    }

    public async Task<CondensePlanDto> PlanCondensedDeliveryAsync(
        IEnumerable<string> filePaths,
        string? condenseMode,
        int? issuesPerBook,
        bool preserveIssueOrder = false,
        int? deviceId = null,
        bool skipAlreadyDelivered = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        var mode = EmailCondenseMode.NormalizeOrThrow(condenseMode, nameof(condenseMode));
        if (!EmailCondenseMode.IsCondensing(mode))
        {
            throw new ArgumentException(
                "Condensed delivery requires the 'count' or 'all' condense mode.",
                nameof(condenseMode));
        }

        var skipped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var issues = ResolveIssues(filePaths, skipped);

        // The plan must describe the books the send would actually queue, so it
        // drops the same already-delivered issues the queueing path does.
        if (skipAlreadyDelivered && deviceId is int device && issues.Count > 0)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            issues = await FilterAlreadyDeliveredAsync(db, device, issues, skipped, cancellationToken);
        }

        var perBook = EmailCondenseMode.NormalizeIssuesPerBookOrThrow(
            mode,
            issuesPerBook,
            issues.Count,
            nameof(issuesPerBook));

        var maxBytes = GetMaxAttachmentBytes();
        var books = new List<CondensedBookDto>();
        foreach (var group in BuildBooks(issues, preserveIssueOrder, perBook))
        {
            var estimated = EstimateCondensedSize(group);
            books.Add(new CondensedBookDto(
                await BuildCondensedNameAsync(group, cancellationToken),
                group,
                estimated,
                estimated > maxBytes));
        }

        return new CondensePlanDto(mode, issues.Count, perBook, maxBytes, books, skipped);
    }

    public async Task<CondensedBookFile> CreateCondensedBookAsync(
        IEnumerable<string> filePaths,
        string? condenseMode,
        int? issuesPerBook,
        int bookIndex,
        bool preserveIssueOrder = false,
        int? deviceId = null,
        bool skipAlreadyDelivered = false,
        IProgress<EpubConversionProgress>? progress = null,
        string? deliveryFormat = null,
        CancellationToken cancellationToken = default)
    {
        var format = await ResolveCondensedFormatAsync(deliveryFormat, deviceId, cancellationToken);

        var plan = await PlanCondensedDeliveryAsync(
            filePaths,
            condenseMode,
            issuesPerBook,
            preserveIssueOrder,
            deviceId,
            skipAlreadyDelivered,
            cancellationToken);

        if (bookIndex < 0 || bookIndex >= plan.Books.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bookIndex),
                $"Book {bookIndex + 1} does not exist; the selection condenses into {plan.Books.Count} book(s).");
        }

        var book = plan.Books[bookIndex];
        var workDirectory = Path.Combine(GetTempDirectory(), "email-download", Guid.NewGuid().ToString("N"));

        // A download has no mail size budget, so the pages keep their original
        // quality however large the condensed book turns out to be.
        var options = await BuildEpubOptionsAsync(book.Files[0], maxSizeBytes: null, cancellationToken) with
        {
            Title = book.DisplayName,
            OutputFileName = book.DisplayName,
            Progress = progress
        };

        try
        {
            var bookPath = string.Equals(format, EmailDeliveryFormat.Azw3, StringComparison.Ordinal)
                ? await _azw3Converter.ConvertToAzw3Async(book.Files, workDirectory, options, cancellationToken)
                : await _epubConverter.ConvertToEpubAsync(book.Files, workDirectory, options, cancellationToken);
            return new CondensedBookFile(bookPath, Path.GetFileName(bookPath));
        }
        catch
        {
            // Only the caller of a successful conversion registers the cleanup
            // of this directory, so a failed or cancelled download has to remove
            // it here or it is left behind for good.
            TryDeleteDirectory(workDirectory);
            throw;
        }
    }

    /// <summary>
    /// Resolves the format a condensed book is generated in. A download can be
    /// requested without a device, in which case there is no default to inherit
    /// and EPUB is used.
    /// </summary>
    public async Task<string> ResolveCondensedFormatAsync(
        string? deliveryFormat,
        int? deviceId,
        CancellationToken cancellationToken)
    {
        string? deviceDefault = null;
        var inheritsDevice = string.IsNullOrWhiteSpace(deliveryFormat) ||
            string.Equals(deliveryFormat.Trim(), EmailDeliveryFormat.Device, StringComparison.OrdinalIgnoreCase);

        if (inheritsDevice && deviceId is int id)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            deviceDefault = await db.EreaderDevices
                .AsNoTracking()
                .Where(d => d.Id == id)
                .Select(d => d.DeliveryFormat)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return EmailDeliveryFormat.NormalizeCondensedOrThrow(
            deliveryFormat,
            deviceDefault,
            nameof(deliveryFormat));
    }

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Failed to clean up the condensed book work directory {Directory}",
                LoggingHelper.SanitizePathForLog(directory));
        }
    }

    /// <summary>
    /// Validates every requested path and returns the issues that can be
    /// condensed, in the order they were supplied.
    /// </summary>
    private List<string> ResolveIssues(IEnumerable<string> filePaths, Dictionary<string, string> skipped)
    {
        var resolved = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawPath in filePaths)
        {
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

            if (seen.Add(fullPath))
            {
                resolved.Add(fullPath);
            }
        }

        return resolved;
    }

    /// <summary>
    /// Splits the resolved issues into the books a condensed send produces.
    /// A book never mixes series: an explicit selection is grouped by the
    /// series folder (and read in natural order inside it) before it is chunked,
    /// so an EPUB cannot carry pages from one series under another's title.
    /// When <paramref name="preserveIssueOrder"/> is set the caller has already
    /// supplied one series in reading order (a send-series request), and that
    /// order is authoritative even when the series spans several folders.
    /// </summary>
    private static List<List<string>> BuildBooks(
        IReadOnlyList<string> issues,
        bool preserveIssueOrder,
        int perBook)
    {
        var books = new List<List<string>>();
        foreach (var series in GroupBySeries(issues, preserveIssueOrder))
        {
            books.AddRange(Chunk(series, perBook));
        }

        return books;
    }

    private static List<List<string>> GroupBySeries(IReadOnlyList<string> issues, bool preserveIssueOrder)
    {
        if (issues.Count == 0)
        {
            return new List<List<string>>();
        }

        if (preserveIssueOrder)
        {
            return new List<List<string>> { issues.ToList() };
        }

        var comparer = new NaturalStringComparer();
        return issues
            .GroupBy(p => Path.GetDirectoryName(p) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, comparer)
            .Select(g => g.OrderBy(Path.GetFileName, comparer).ToList())
            .ToList();
    }

    /// <summary>
    /// Drops the issues already queued or sent to the device, recording why in
    /// <paramref name="skipped"/>. Used by both the queueing and the planning
    /// path so a plan describes exactly the books a send would create.
    /// </summary>
    private static async Task<List<string>> FilterAlreadyDeliveredAsync(
        ComicMaintainerDbContext db,
        int deviceId,
        IReadOnlyList<string> candidates,
        Dictionary<string, string> skipped,
        CancellationToken cancellationToken)
    {
        var delivered = await GetDeliveredPathsAsync(db, deviceId, cancellationToken);
        var remaining = new List<string>(candidates.Count);
        foreach (var path in candidates)
        {
            if (delivered.Contains(path))
            {
                skipped[path] = "Already delivered to this device";
                continue;
            }

            remaining.Add(path);
        }

        return remaining;
    }

    /// <summary>
    /// Every library path already queued or sent to a device, including the
    /// members of condensed deliveries, so a condensed send does not re-deliver
    /// an issue that went out inside an earlier book.
    /// </summary>
    private static async Task<HashSet<string>> GetDeliveredPathsAsync(
        ComicMaintainerDbContext db,
        int deviceId,
        CancellationToken cancellationToken)
    {
        var rows = await db.ComicEmailDeliveries
            .AsNoTracking()
            .Where(d => d.DeviceId == deviceId &&
                        (d.Status == EmailDeliveryStatus.Sent || d.Status == EmailDeliveryStatus.Pending))
            .Select(d => new { d.FilePath, d.CondensedFilePaths })
            .ToListAsync(cancellationToken);

        var delivered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            delivered.Add(row.FilePath);
            foreach (var member in DeserializeCondensedPaths(row.CondensedFilePaths))
            {
                delivered.Add(member);
            }
        }

        return delivered;
    }

    private static IReadOnlyList<string> DeserializeCondensedPaths(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            // A delivery row whose member list cannot be read is treated as a
            // single-issue delivery rather than failing the whole request.
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<List<string>> Chunk(IReadOnlyList<string> items, int size)
    {
        var chunkSize = Math.Max(1, size);
        for (var start = 0; start < items.Count; start += chunkSize)
        {
            yield return items.Skip(start).Take(chunkSize).ToList();
        }
    }

    /// <summary>
    /// Approximates the size of the EPUB a group of issues condenses into.
    /// Pages keep their original (already compressed) bytes, so the source
    /// archives plus the XHTML/container overhead is a close upper estimate.
    /// </summary>
    private static long EstimateCondensedSize(IReadOnlyList<string> files)
    {
        long total = 0;
        foreach (var file in files)
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable right now; the conversion would fail anyway.
            }
        }

        // ~2% for the per-page XHTML wrappers, manifest and container entries.
        return (long)(total * 1.02) + 64 * 1024;
    }

    private long GetMaxAttachmentBytes() =>
        (long)Math.Max(1, _settings.CurrentValue.EmailMaxAttachmentMegabytes) * 1024 * 1024;

    private static string FormatMegabytes(long bytes) =>
        $"{bytes / (1024d * 1024d):0.#} MB";

    /// <summary>
    /// Names a condensed book after the series and the range it covers, e.g.
    /// "Berserk 001-005". Falls back to the first file name when no issue
    /// numbers can be read from the file names.
    /// </summary>
    private async Task<string> BuildCondensedNameAsync(
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        var first = Path.GetFileNameWithoutExtension(files[0]);
        if (files.Count == 1)
        {
            return first;
        }

        var series = await ResolveSeriesTitleAsync(files[0], cancellationToken)
            ?? Path.GetFileName(Path.GetDirectoryName(files[0]))
            ?? first;

        var firstIssue = ExtractIssueLabel(files[0]);
        var lastIssue = ExtractIssueLabel(files[^1]);

        var name = firstIssue is not null && lastIssue is not null
            ? $"{series} {firstIssue}-{lastIssue}"
            : $"{series} ({files.Count} issues)";

        return Truncate(name.Trim(), 512);
    }

    /// <summary>
    /// Reads the issue number out of a file name, zero padded so condensed book
    /// names sort the way the issues do ("001-005" before "006-010"). An
    /// explicitly marked number ("#001", "Ch 12") always wins; otherwise the
    /// last number outside any bracketed qualifier is used so "Series (2016)
    /// 005.cbz" yields 005 rather than the year.
    /// </summary>
    private static string? ExtractIssueLabel(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        var match = MarkedIssueNumberPattern.Match(name);
        if (!match.Success)
        {
            match = TrailingNumberPattern.Match(BracketedQualifierPattern.Replace(name, " "));
        }

        if (!match.Success)
        {
            return null;
        }

        var raw = match.Groups[1].Value;
        var separator = raw.IndexOf('.');
        var whole = separator < 0 ? raw : raw[..separator];
        var fraction = separator < 0 ? string.Empty : raw[separator..];

        return whole.TrimStart('0').PadLeft(3, '0') is { Length: > 0 } padded
            ? padded + fraction
            : "000" + fraction;
    }

    private async Task<string?> ResolveSeriesTitleAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            var folderTitle = Path.GetFileName(Path.GetDirectoryName(filePath));
            if (string.IsNullOrWhiteSpace(folderTitle))
            {
                return null;
            }

            var record = await _seriesCache.GetByTitleAsync(folderTitle, cancellationToken);
            if (record is null)
            {
                return folderTitle;
            }

            return string.IsNullOrWhiteSpace(record.ResolvedSeriesName)
                ? record.CanonicalTitle
                : record.ResolvedSeriesName;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Could not resolve the series name for {FilePath}; naming the condensed book after its folder",
                LoggingHelper.SanitizePathForLog(filePath));
            return null;
        }
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

        // Claim the delivery under the state lock: a cancel request either
        // finishes before the claim (and the row is no longer pending) or sees
        // the delivery in flight and asks it to stop.
        ComicEmailDeliveryEntity? delivery;
        var inFlight = new InFlightDelivery();
        await _deliveryStateLock.WaitAsync(cancellationToken);
        try
        {
            delivery = await db.ComicEmailDeliveries.FirstOrDefaultAsync(d => d.Id == deliveryId, cancellationToken);
            if (delivery is null)
            {
                _logger.LogWarning("Comic email delivery {DeliveryId} no longer exists", deliveryId);
                return;
            }

            if (!string.Equals(delivery.Status, EmailDeliveryStatus.Pending, StringComparison.Ordinal) ||
                !_inFlight.TryAdd(deliveryId, inFlight))
            {
                return;
            }
        }
        finally
        {
            _deliveryStateLock.Release();
        }

        try
        {
            using var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, inFlight.Token);
            await DeliverAsync(db, delivery, inFlight, preparation.Token, cancellationToken);
        }
        finally
        {
            // Removed only after the outcome has been saved, so a cancel
            // request that no longer finds the delivery in flight reads its
            // final status from the database. Taken under the state lock so a
            // bulk cancel that has already loaded this row as pending still
            // finds it in flight instead of overwriting the saved outcome.
            await _deliveryStateLock.WaitAsync(CancellationToken.None);
            try
            {
                _inFlight.TryRemove(new KeyValuePair<int, InFlightDelivery>(deliveryId, inFlight));
            }
            finally
            {
                _deliveryStateLock.Release();
            }

            inFlight.Complete();
        }
    }

    /// <param name="preparationToken">
    /// Cancelled on shutdown or when the user cancels the delivery. Covers
    /// everything up to handing the message to the mail server.
    /// </param>
    /// <param name="stoppingToken">
    /// Cancelled only on shutdown. A message that is already being sent is not
    /// interrupted by a user cancel.
    /// </param>
    private async Task DeliverAsync(
        ComicMaintainerDbContext db,
        ComicEmailDeliveryEntity delivery,
        InFlightDelivery inFlight,
        CancellationToken preparationToken,
        CancellationToken stoppingToken)
    {
        string? temporaryAttachment = null;
        try
        {
            // A condensed delivery carries several issues; an ordinary one only
            // has FilePath.
            var members = DeserializeCondensedPaths(delivery.CondensedFilePaths);
            var sources = members.Count > 0 ? members : new[] { delivery.FilePath };

            var resolved = new List<string>(sources.Count);
            foreach (var source in sources)
            {
                if (!TryResolveLibraryPath(source, out var resolvedPath) || !File.Exists(resolvedPath))
                {
                    throw new FileNotFoundException($"File not found: {Path.GetFileName(source)}");
                }

                resolved.Add(resolvedPath);
            }

            // Wait before converting so a delivery held back for hours does not
            // keep a generated book on disk, and a cancel during the wait
            // wastes no work.
            if (!await WaitForSendSlotAsync(preparationToken))
            {
                if (inFlight.CancelRequested)
                {
                    // Cancelled by the user as well: record that below.
                    throw new OperationCanceledException(preparationToken);
                }

                _logger.LogInformation(
                    "Application is stopping; email delivery {DeliveryId} stays pending instead of waiting for the rate limit",
                    delivery.Id);
                return;
            }

            var maxBytes = GetMaxAttachmentBytes();

            var attachmentPath = resolved[0];
            var isAzw3 = string.Equals(delivery.DeliveryFormat, EmailDeliveryFormat.Azw3, StringComparison.OrdinalIgnoreCase);
            if (resolved.Count > 1 ||
                isAzw3 ||
                string.Equals(delivery.DeliveryFormat, EmailDeliveryFormat.Epub, StringComparison.OrdinalIgnoreCase))
            {
                var workDirectory = Path.Combine(GetTempDirectory(), "email", Guid.NewGuid().ToString("N"));
                // Hand the converter the mail size budget so an oversized book is
                // recompressed rather than rejected below.
                var options = await BuildEpubOptionsAsync(resolved[0], maxBytes, preparationToken);
                if (!string.IsNullOrWhiteSpace(delivery.DisplayName))
                {
                    options = options with { Title = delivery.DisplayName, OutputFileName = delivery.DisplayName };
                }

                if (isAzw3)
                {
                    attachmentPath = resolved.Count == 1
                        ? await _azw3Converter.ConvertToAzw3Async(resolved[0], workDirectory, options, preparationToken)
                        : await _azw3Converter.ConvertToAzw3Async(resolved, workDirectory, options, preparationToken);
                }
                else
                {
                    attachmentPath = resolved.Count == 1
                        ? await _epubConverter.ConvertToEpubAsync(resolved[0], workDirectory, options, preparationToken)
                        : await _epubConverter.ConvertToEpubAsync(resolved, workDirectory, options, preparationToken);
                }

                temporaryAttachment = attachmentPath;
            }

            var attachmentLength = new FileInfo(attachmentPath).Length;
            if (attachmentLength <= 0)
            {
                throw new InvalidOperationException(
                    $"Generated attachment for {Path.GetFileName(delivery.FilePath)} is empty; nothing was sent.");
            }

            if (attachmentLength > maxBytes)
            {
                throw new InvalidOperationException(
                    $"Attachment is {attachmentLength / (1024 * 1024)} MB which exceeds the {_settings.CurrentValue.EmailMaxAttachmentMegabytes} MB limit.");
            }

            // Past this point the delivery can no longer be cancelled.
            preparationToken.ThrowIfCancellationRequested();
            if (!inFlight.TryBeginSending())
            {
                throw new OperationCanceledException(preparationToken);
            }

            var displayName = string.IsNullOrWhiteSpace(delivery.DisplayName)
                ? Path.GetFileNameWithoutExtension(delivery.FilePath)
                : delivery.DisplayName;
            await _sender.SendAsync(
                new ComicEmailMessage(
                    delivery.DeviceEmail,
                    displayName,
                    $"{displayName} sent from ComicMaintainer.",
                    attachmentPath,
                    Path.GetFileName(attachmentPath),
                    GetAttachmentContentType(attachmentPath)),
                stoppingToken);

            delivery.Status = EmailDeliveryStatus.Sent;
            delivery.SentAt = _timeProvider.GetUtcNow().UtcDateTime;
            _metrics?.RecordEmailSend(success: true);
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
        catch (Exception) when (inFlight.CancelRequested)
        {
            // Whatever the work in progress threw once it was asked to stop —
            // usually an OperationCanceledException, but a converter can
            // surface the abort differently — the user cancelled it.
            delivery.Status = EmailDeliveryStatus.Cancelled;
            delivery.ErrorMessage = null;
            _logger.LogInformation(
                "Cancelled email delivery {DeliveryId} of {FilePath} before it was sent",
                delivery.Id,
                LoggingHelper.SanitizePathForLog(delivery.FilePath));
        }
        catch (OperationCanceledException)
        {
            // Shutdown: leave the delivery pending.
            throw;
        }
        catch (Exception ex)
        {
            delivery.Status = EmailDeliveryStatus.Failed;
            delivery.ErrorMessage = Truncate(ex.Message, 1024);
            _metrics?.RecordEmailSend(success: false);

            // A rejected delivery is almost always the remote SMTP server or the
            // recipient address, both of which the operator controls; the
            // failure is already surfaced on the delivery row.
            using (_logger.BeginScope(ErrorReportLogProperties.Exclude("email delivery rejected by the remote server")))
            {
                _logger.LogError(
                    ex,
                    "Failed to email {FilePath} to device {DeviceId}",
                    LoggingHelper.SanitizePathForLog(delivery.FilePath),
                    delivery.DeviceId);
            }
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

    public async Task<ComicEmailDeliveryPage> GetDeliveriesPageAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, 500);
        var skip = Math.Max(0, offset);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var total = await db.ComicEmailDeliveries.CountAsync(cancellationToken);
        var pending = await db.ComicEmailDeliveries
            .CountAsync(d => d.Status == EmailDeliveryStatus.Pending, cancellationToken);
        var deliveries = await db.ComicEmailDeliveries
            .AsNoTracking()
            .OrderByDescending(d => d.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return new ComicEmailDeliveryPage(
            deliveries.Select(ToDto).ToList(),
            total,
            skip,
            take,
            pending,
            RateLimitedUntil);
    }

    public async Task<EmailDeliveryCancelResult> CancelDeliveryAsync(
        int deliveryId,
        CancellationToken cancellationToken = default)
    {
        var (direct, inFlight) = await CancelUnclaimedDeliveryAsync(deliveryId, cancellationToken);
        if (inFlight is null)
        {
            return direct!;
        }

        var request = inFlight.TryRequestCancel();
        if (request == InFlightCancelRequest.Sending)
        {
            return new EmailDeliveryCancelResult(
                EmailDeliveryCancelOutcome.Sending,
                await GetDeliveryDtoAsync(deliveryId, cancellationToken));
        }

        if (request == InFlightCancelRequest.Requested)
        {
            _logger.LogInformation("Stopping email delivery {DeliveryId}, which is being prepared", deliveryId);
            await WaitForInFlightAsync(new[] { inFlight }, cancellationToken);
        }

        if (inFlight.Completion.IsCompleted)
        {
            // The processor has finished with it. A delivery it left pending —
            // on shutdown, possibly after accepting this request — is no
            // longer being worked on and is cancelled directly.
            var (reconciled, _) = await CancelUnclaimedDeliveryAsync(deliveryId, cancellationToken);
            if (reconciled?.Outcome == EmailDeliveryCancelOutcome.Cancelled)
            {
                return reconciled;
            }
        }

        var latest = await GetDeliveryDtoAsync(deliveryId, cancellationToken);
        if (latest is null)
        {
            return new EmailDeliveryCancelResult(EmailDeliveryCancelOutcome.NotFound, null);
        }

        var outcome = latest.Status switch
        {
            EmailDeliveryStatus.Cancelled => EmailDeliveryCancelOutcome.Cancelled,
            EmailDeliveryStatus.Pending => EmailDeliveryCancelOutcome.Stopping,
            _ => EmailDeliveryCancelOutcome.AlreadyFinished,
        };
        return new EmailDeliveryCancelResult(outcome, latest);
    }

    public async Task<EmailDeliveryBulkCancelResult> CancelPendingDeliveriesAsync(CancellationToken cancellationToken = default)
    {
        var (cancelled, claimed) = await CancelUnclaimedPendingDeliveriesAsync(null, cancellationToken);

        var requests = claimed
            .Select(c => (c.Id, c.Delivery, Request: c.Delivery.TryRequestCancel()))
            .ToList();
        var stopping = requests
            .Where(r => r.Request == InFlightCancelRequest.Requested)
            .Select(r => r.Delivery)
            .ToList();
        if (stopping.Count > 0)
        {
            await WaitForInFlightAsync(stopping, cancellationToken);
        }

        var sending = 0;
        var finishedIds = new List<int>();
        var outcomeIds = new List<int>();
        foreach (var (id, inFlight, request) in requests)
        {
            if (request == InFlightCancelRequest.Sending)
            {
                sending++;
                continue;
            }

            outcomeIds.Add(id);
            if (inFlight.Completion.IsCompleted)
            {
                finishedIds.Add(id);
            }
        }

        // The processor has finished with these. Any it left pending — on
        // shutdown, possibly after accepting this request — are no longer
        // being worked on and are cancelled directly.
        if (finishedIds.Count > 0)
        {
            await CancelUnclaimedPendingDeliveriesAsync(finishedIds, cancellationToken);
        }

        var stillStopping = 0;
        if (outcomeIds.Count > 0)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var statuses = await db.ComicEmailDeliveries
                .AsNoTracking()
                .Where(d => outcomeIds.Contains(d.Id))
                .Select(d => d.Status)
                .ToListAsync(cancellationToken);
            cancelled += statuses.Count(s => s == EmailDeliveryStatus.Cancelled);
            stillStopping = statuses.Count(s => s == EmailDeliveryStatus.Pending);
        }

        _logger.LogInformation(
            "Cancelled {Cancelled} pending email deliveries ({Stopping} still stopping, {Sending} already being sent)",
            cancelled,
            stillStopping,
            sending);
        return new EmailDeliveryBulkCancelResult(cancelled, stillStopping, sending);
    }

    /// <summary>
    /// Cancels the delivery directly when the processor is not working on it.
    /// Runs under the state lock, so the processor can neither claim it nor
    /// finish with it meanwhile.
    /// </summary>
    /// <returns>
    /// The result, or the delivery the processor is working on (and no
    /// result) when it is in flight.
    /// </returns>
    private async Task<(EmailDeliveryCancelResult? Result, InFlightDelivery? InFlight)> CancelUnclaimedDeliveryAsync(
        int deliveryId,
        CancellationToken cancellationToken)
    {
        await _deliveryStateLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var delivery = await db.ComicEmailDeliveries.FirstOrDefaultAsync(d => d.Id == deliveryId, cancellationToken);
            if (delivery is null)
            {
                return (new EmailDeliveryCancelResult(EmailDeliveryCancelOutcome.NotFound, null), null);
            }

            if (!string.Equals(delivery.Status, EmailDeliveryStatus.Pending, StringComparison.Ordinal))
            {
                return (new EmailDeliveryCancelResult(EmailDeliveryCancelOutcome.AlreadyFinished, ToDto(delivery)), null);
            }

            if (_inFlight.TryGetValue(deliveryId, out var inFlight))
            {
                return (null, inFlight);
            }

            delivery.Status = EmailDeliveryStatus.Cancelled;
            delivery.ErrorMessage = null;
            await db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Cancelled pending email delivery {DeliveryId} of {FilePath}",
                delivery.Id,
                LoggingHelper.SanitizePathForLog(delivery.FilePath));
            return (new EmailDeliveryCancelResult(EmailDeliveryCancelOutcome.Cancelled, ToDto(delivery)), null);
        }
        finally
        {
            _deliveryStateLock.Release();
        }
    }

    /// <summary>
    /// Cancels pending deliveries the processor is not working on, under the
    /// state lock so none can be claimed or finished meanwhile.
    /// </summary>
    /// <param name="ids">Deliveries to consider; null for every pending one.</param>
    /// <returns>How many were cancelled, and those the processor is working on.</returns>
    private async Task<(int Cancelled, List<(int Id, InFlightDelivery Delivery)> Claimed)> CancelUnclaimedPendingDeliveriesAsync(
        List<int>? ids,
        CancellationToken cancellationToken)
    {
        var cancelled = 0;
        var claimed = new List<(int Id, InFlightDelivery Delivery)>();

        await _deliveryStateLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var query = db.ComicEmailDeliveries.Where(d => d.Status == EmailDeliveryStatus.Pending);
            if (ids is not null)
            {
                query = query.Where(d => ids.Contains(d.Id));
            }

            foreach (var delivery in await query.ToListAsync(cancellationToken))
            {
                if (_inFlight.TryGetValue(delivery.Id, out var inFlight))
                {
                    claimed.Add((delivery.Id, inFlight));
                    continue;
                }

                delivery.Status = EmailDeliveryStatus.Cancelled;
                delivery.ErrorMessage = null;
                cancelled++;
            }

            if (cancelled > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        finally
        {
            _deliveryStateLock.Release();
        }

        return (cancelled, claimed);
    }

    /// <summary>
    /// Waits, for at most <see cref="InFlightCancelWait"/>, for deliveries that
    /// were asked to stop to record their outcome.
    /// </summary>
    private async Task WaitForInFlightAsync(IEnumerable<InFlightDelivery> deliveries, CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(deliveries.Select(d => d.Completion))
                .WaitAsync(InFlightCancelWait, _timeProvider, cancellationToken);
        }
        catch (TimeoutException)
        {
            // Reported to the caller as still stopping.
        }
    }

    private async Task<ComicEmailDeliveryDto?> GetDeliveryDtoAsync(int deliveryId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var delivery = await db.ComicEmailDeliveries
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == deliveryId, cancellationToken);
        return delivery is null ? null : ToDto(delivery);
    }

    /// <summary>
    /// Holds the delivery back until the configured rate limit allows another
    /// comic email, publishing the time it is waiting for through
    /// <see cref="RateLimitedUntil"/>. The limits are re-read at least every
    /// <see cref="MaxRateLimitPoll"/>, so raising or removing one releases the
    /// delivery promptly.
    /// </summary>
    /// <returns>
    /// True once the delivery may be sent; false when the application started
    /// stopping while the delivery still had to wait.
    /// </returns>
    private async Task<bool> WaitForSendSlotAsync(CancellationToken cancellationToken)
    {
        var announced = false;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _applicationStopping);
        try
        {
            while (true)
            {
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var nextSlot = await GetNextSendSlotAsync(now, cancellationToken);
                if (nextSlot is not DateTime slot || slot <= now)
                {
                    return true;
                }

                if (_applicationStopping.IsCancellationRequested)
                {
                    return false;
                }

                var slotUtc = DateTime.SpecifyKind(slot, DateTimeKind.Utc);
                Volatile.Write(ref _rateLimitedUntilTicks, slotUtc.Ticks);
                if (!announced)
                {
                    announced = true;
                    _logger.LogInformation(
                        "Email rate limit reached; holding the next delivery until {NextSendSlot:O}",
                        slotUtc);
                }

                var remaining = slotUtc - now;
                var delay = remaining < MaxRateLimitPoll
                    ? TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds))
                    : MaxRateLimitPoll;
                try
                {
                    await Task.Delay(delay, _timeProvider, wait.Token);
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested && _applicationStopping.IsCancellationRequested)
                {
                    return false;
                }
            }
        }
        finally
        {
            Volatile.Write(ref _rateLimitedUntilTicks, 0);
        }
    }

    /// <summary>
    /// Earliest time the configured limits allow another comic email, or null
    /// when no limit is configured or none has been reached. Only messages that
    /// were actually sent count; a condensed book is one message.
    /// </summary>
    private async Task<DateTime?> GetNextSendSlotAsync(DateTime now, CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        var minSecondsBetweenSends = Math.Max(0, settings.EmailMinSecondsBetweenSends);
        var maxSendsPerHour = Math.Max(0, settings.EmailMaxSendsPerHour);
        var maxSendsPerDay = Math.Max(0, settings.EmailMaxSendsPerDay);
        if (minSecondsBetweenSends == 0 && maxSendsPerHour == 0 && maxSendsPerDay == 0)
        {
            return null;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var sent = db.ComicEmailDeliveries
            .AsNoTracking()
            .Where(d => d.Status == EmailDeliveryStatus.Sent && d.SentAt != null);

        DateTime? nextSlot = null;
        void Consider(DateTime candidate)
        {
            if (nextSlot is null || candidate > nextSlot)
            {
                nextSlot = candidate;
            }
        }

        if (minSecondsBetweenSends > 0 &&
            await sent.MaxAsync(d => d.SentAt, cancellationToken) is DateTime lastSent)
        {
            Consider(lastSent.AddSeconds(minSecondsBetweenSends));
        }

        // With a limit of N per window, the next message may go out once the
        // N-th most recent message in the window has aged out of it.
        foreach (var (limit, window) in new[] { (maxSendsPerHour, TimeSpan.FromHours(1)), (maxSendsPerDay, TimeSpan.FromDays(1)) })
        {
            if (limit == 0)
            {
                continue;
            }

            var windowStart = now - window;
            var oldestCounted = await sent
                .Where(d => d.SentAt > windowStart)
                .OrderByDescending(d => d.SentAt)
                .Select(d => d.SentAt)
                .Skip(limit - 1)
                .FirstOrDefaultAsync(cancellationToken);
            if (oldestCounted is DateTime counted)
            {
                Consider(counted + window);
            }
        }

        return nextSlot;
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
    /// carry the series cover art and the resolved series name, falling back to
    /// the <c>cover.&lt;ext&gt;</c> sidecar ComicMaintainer writes next to the
    /// issues when the cache holds no image of its own. Best-effort: when
    /// neither is available the EPUB falls back to the first comic page as its
    /// cover.
    /// </summary>
    private async Task<EpubConversionOptions> BuildEpubOptionsAsync(
        string fullPath,
        long? maxSizeBytes,
        CancellationToken cancellationToken)
    {
        string? imagePath = null;
        string? seriesTitle = null;

        try
        {
            // The library groups a series by the name of its containing folder,
            // which is also how the cache record is keyed.
            var folderTitle = Path.GetFileName(Path.GetDirectoryName(fullPath));
            if (!string.IsNullOrWhiteSpace(folderTitle))
            {
                var record = await _seriesCache.GetByTitleAsync(folderTitle, cancellationToken);
                if (record is not null)
                {
                    imagePath = string.IsNullOrWhiteSpace(record.LocalImageFile)
                        ? null
                        : _seriesImages.ResolveAbsolutePath(record.LocalImageFile);

                    seriesTitle = string.IsNullOrWhiteSpace(record.ResolvedSeriesName)
                        ? record.CanonicalTitle
                        : record.ResolvedSeriesName;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Could not resolve cached series artwork for {FilePath}; trying series folder cover",
                LoggingHelper.SanitizePathForLog(fullPath));
        }

        try
        {
            // Resolve the sidecar independently so a cache lookup failure
            // cannot prevent the book from opening on the series image.
            imagePath ??= ResolveSeriesFolderCover(fullPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Could not resolve series folder cover for {FilePath}; sending EPUB without series cover",
                LoggingHelper.SanitizePathForLog(fullPath));
        }

        return new EpubConversionOptions(imagePath, seriesTitle, maxSizeBytes);
    }

    /// <summary>
    /// Finds the series cover sidecar sitting next to an issue. Mirrors the
    /// names <see cref="ISeriesFolderCoverWriter"/> writes, which is also the
    /// convention other comic library managers follow.
    /// </summary>
    private static string? ResolveSeriesFolderCover(string fullPath)
    {
        var folder = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        foreach (var extension in SeriesFolderCoverExtensions)
        {
            var candidate = Path.Combine(folder, "cover" + extension);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
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
        var settings = _settings.CurrentValue;
        return LibraryPathValidator.TryResolveWithin(
            path,
            out fullPath,
            settings.WatchedDirectory,
            settings.DuplicateDirectory);
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
            _logger.LogWarning(ex, "Failed to clean up the temporary converted attachment");
        }
    }

    private static string GetAttachmentContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".epub" => "application/epub+zip",
            ".azw3" => "application/x-mobi8-ebook",
            ".cbz" => "application/vnd.comicbook+zip",
            ".cbr" => "application/vnd.comicbook-rar",
            _ => "application/octet-stream"
        };

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static ComicEmailDeliveryDto ToDto(ComicEmailDeliveryEntity entity) => new(
        entity.Id,
        entity.FilePath,
        string.IsNullOrWhiteSpace(entity.DisplayName) ? Path.GetFileName(entity.FilePath) : entity.DisplayName,
        entity.DeviceId,
        entity.DeviceName,
        entity.DeviceEmail,
        entity.DeliveryFormat,
        entity.Status,
        entity.Source,
        entity.ErrorMessage,
        entity.CreatedAt,
        entity.SentAt,
        Math.Max(1, DeserializeCondensedPaths(entity.CondensedFilePaths).Count));

    /// <summary>What became of a request to stop an in-flight delivery.</summary>
    private enum InFlightCancelRequest
    {
        /// <summary>The processor was asked to stop.</summary>
        Requested,

        /// <summary>The message is already being sent and cannot be recalled.</summary>
        Sending,

        /// <summary>The processor has already finished with the delivery.</summary>
        Finished,
    }

    /// <summary>
    /// A delivery the processor is working on. Cancellation is only possible
    /// while it is being prepared (waiting for a rate-limit slot or being
    /// converted); once the message is handed to the mail server it can no
    /// longer be stopped. The state change is atomic, so a cancel request and
    /// the start of sending can never both win.
    /// </summary>
    private sealed class InFlightDelivery
    {
        private const int Preparing = 0;
        private const int CancelRequestedState = 1;
        private const int SendingState = 2;
        private const int FinishedState = 3;

        // Never disposed: a cancel request may still reach it after the
        // processor has finished, and it owns no timer or wait handle.
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state = Preparing;

        public CancellationToken Token => _cancellation.Token;

        /// <summary>Completes once the processor has finished with the delivery.</summary>
        public Task Completion => _completion.Task;

        public bool CancelRequested => Volatile.Read(ref _state) == CancelRequestedState;

        /// <summary>Asks the processor to stop.</summary>
        public InFlightCancelRequest TryRequestCancel()
        {
            var previous = Interlocked.CompareExchange(ref _state, CancelRequestedState, Preparing);
            switch (previous)
            {
                case Preparing:
                    // Callbacks run on the thread pool rather than inline on the
                    // caller's thread.
                    _ = _cancellation.CancelAsync();
                    return InFlightCancelRequest.Requested;
                case CancelRequestedState:
                    return InFlightCancelRequest.Requested;
                case SendingState:
                    return InFlightCancelRequest.Sending;
                default:
                    return InFlightCancelRequest.Finished;
            }
        }

        /// <summary>
        /// Marks the delivery as being sent. False when it was cancelled first.
        /// </summary>
        public bool TryBeginSending()
            => Interlocked.CompareExchange(ref _state, SendingState, Preparing) != CancelRequestedState;

        public void Complete()
        {
            // Completion first, so a request that finds the delivery finished
            // also finds Completion done. Only a delivery that was neither
            // cancelled nor sent changes state.
            _completion.TrySetResult();
            Interlocked.CompareExchange(ref _state, FinishedState, Preparing);
        }
    }
}
