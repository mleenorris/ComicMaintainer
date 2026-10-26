using System.Collections.Concurrent;
using System.IO.Compression;
using System.Xml.Linq;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers the retry, restore and cross-device fallback paths that replace an archive on disk
/// after its ComicInfo.xml was rewritten. Failures are provoked with real filesystem state
/// (a directory squatting on the backup or target path) and log hooks that change that state
/// at precise points of the algorithm. The Linux-only tests rely on POSIX unlink/rename
/// semantics; the "cross-device" folder name makes the resulting IOException message match
/// the fallback's message-based detection.
/// </summary>
public class ComicProcessorServiceFileReplaceCoverageTests : IDisposable
{
    private const string RetryWarning = "File replace attempt {Attempt} failed for {File}, retrying...";
    private const string ReplaceFailedError = "File replace failed for {File}, attempting restore from backup";
    private const string CrossDeviceWarning = "Cross-device link detected, using fallback copy strategy for {File}";
    private const string FallbackSucceeded = "Successfully replaced file using fallback strategy: {File}";
    private const string FallbackFailedError = "Fallback strategy failed for {File}, attempting restore from backup";
    private const string RestoredWarning = "Restored backup file after failed replacement: {File}";
    private const string RestoreFailedError = "Failed to restore backup file: {BackupPath}";
    private const string LargeEntryWarning = "Skipping large entry {Entry} ({Size} bytes) to prevent memory issues";

    private readonly string _root;
    private readonly string _library;
    private readonly string _tempDir;
    private readonly AppSettings _settings;
    private readonly HookLogger _logger = new();
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IProcessingHistoryService> _history = new();
    private readonly List<ComicProcessorService> _services = new();

    public ComicProcessorServiceFileReplaceCoverageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cps-replace-cov-" + Guid.NewGuid().ToString("N"));
        _library = Path.Combine(_root, "library");
        _tempDir = Path.Combine(_root, "temp");
        Directory.CreateDirectory(_library);

        _settings = new AppSettings
        {
            WatchedDirectory = _library,
            DuplicateDirectory = Path.Combine(_root, "duplicates"),
            TempFileDirectory = _tempDir,
            FilenameFormat = "{series} - Chapter {issue}",
            IssueNumberPadding = 4,
        };
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task UpdateMetadataAsync_BackupPathBlockedOnFirstAttempt_SucceedsOnRetry()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var service = CreateService();
        var path = WriteBook("Series/book.cbz");
        var backup = path + ".backup";
        Directory.CreateDirectory(backup);
        _logger.On(RetryWarning, _ => Directory.Delete(backup));

        var result = await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "New" }, CancellationToken.None);

        Assert.True(result);
        Assert.Equal("New", SeriesOf(path));
        Assert.False(Directory.Exists(backup));
        Assert.False(File.Exists(backup));
        var retry = Assert.Single(_logger.Entries, e => e.Template == RetryWarning);
        Assert.Equal(1, retry.Properties["Attempt"]);
        Assert.IsAssignableFrom<IOException>(retry.Exception);
        Assert.Empty(TempFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplaceRetry_CancelledWhileBackingOff_FailsAndKeepsOriginal(bool removeMetadata)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var service = CreateService();
        var path = WriteBook("Series/book.cbz");
        var before = await File.ReadAllBytesAsync(path);
        Directory.CreateDirectory(path + ".backup");
        using var cts = new CancellationTokenSource();
        _logger.On(RetryWarning, e =>
        {
            if (Equals(e.Properties["Attempt"], 2))
            {
                cts.Cancel();
            }
        });

        var result = removeMetadata
            ? await service.RemoveMetadataAsync(path, cts.Token)
            : await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "New" }, cts.Token);

        Assert.False(result);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal(2, _logger.Entries.Count(e => e.Template == RetryWarning));
        var error = Assert.Single(_logger.Entries, e => e.Template == ReplaceFailedError);
        Assert.IsAssignableFrom<OperationCanceledException>(error.Exception);
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task UpdateMetadataAsync_BackupPathPermanentlyBlocked_GivesUpAfterFiveAttempts()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var service = CreateService();
        var path = WriteBook("Series/book.cbz");
        var before = await File.ReadAllBytesAsync(path);
        Directory.CreateDirectory(path + ".backup");

        var result = await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "New" }, CancellationToken.None);

        Assert.False(result);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal(
            new object?[] { 1, 2, 3, 4, 5 },
            _logger.Entries.Where(e => e.Template == RetryWarning).Select(e => e.Properties["Attempt"]));
        var error = Assert.Single(_logger.Entries, e => e.Template == ReplaceFailedError);
        Assert.IsAssignableFrom<IOException>(error.Exception);
        Assert.True(_logger.Has(LogLevel.Error, "Error updating metadata for {FilePath}"));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task CrossDeviceFallback_ReplacesFileAndDiscardsStaleBackup()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var service = CreateService();
        var path = WriteBook("cross-device-lib/Series/book.cbz");
        var backup = path + ".backup";
        Directory.CreateDirectory(backup);
        _logger.On(CrossDeviceWarning, _ =>
        {
            Directory.Delete(backup);
            File.WriteAllText(backup, "stale backup");
        });

        var result = await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "New" }, CancellationToken.None);

        Assert.True(result);
        Assert.Equal("New", SeriesOf(path));
        Assert.False(File.Exists(backup));
        Assert.True(_logger.Has(LogLevel.Information, FallbackSucceeded));
        Assert.False(_logger.Entries.Any(e => e.Template == RetryWarning));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task CrossDeviceFallback_TargetAlreadyGone_StillWritesNewArchive()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var service = CreateService();
        var path = WriteBook("cross-device-lib/Series/book.cbz");
        var backup = path + ".backup";
        Directory.CreateDirectory(backup);
        _logger.On(CrossDeviceWarning, _ =>
        {
            Directory.Delete(backup);
            File.Delete(path);
        });

        var result = await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "New" }, CancellationToken.None);

        Assert.True(result);
        Assert.Equal("New", SeriesOf(path));
        Assert.False(File.Exists(backup));
        Assert.Empty(TempFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossDeviceFallback_CopyFails_RestoresOriginalFromBackup(bool targetMissingAtRestore)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var service = CreateService();
        var path = WriteBook("cross-device-lib/Series/book.cbz");
        var before = await File.ReadAllBytesAsync(path);
        var backup = path + ".backup";
        Directory.CreateDirectory(backup);
        _logger.On(CrossDeviceWarning, _ =>
        {
            Directory.Delete(backup);
            DeleteTempFiles();
        });
        if (targetMissingAtRestore)
        {
            _logger.On(FallbackFailedError, _ => File.Delete(path));
        }

        var result = await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "New" }, CancellationToken.None);

        Assert.False(result);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(backup));
        var fallbackError = Assert.Single(_logger.Entries, e => e.Template == FallbackFailedError);
        Assert.IsAssignableFrom<FileNotFoundException>(fallbackError.Exception);
        Assert.True(_logger.Has(LogLevel.Warning, RestoredWarning));
        Assert.False(_logger.Has(LogLevel.Error, RestoreFailedError));
    }

    [Fact]
    public async Task CrossDeviceFallback_RestoreCancelled_LeavesBackupInPlace()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var service = CreateService();
        var path = WriteBook("cross-device-lib/Series/book.cbz");
        var before = await File.ReadAllBytesAsync(path);
        var backup = path + ".backup";
        Directory.CreateDirectory(backup);
        using var cts = new CancellationTokenSource();
        _logger.On(CrossDeviceWarning, _ =>
        {
            Directory.Delete(backup);
            DeleteTempFiles();
        });
        _logger.On(FallbackFailedError, _ => cts.Cancel());

        var result = await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "New" }, cts.Token);

        Assert.False(result);
        Assert.True(_logger.Has(LogLevel.Error, RestoreFailedError));
        Assert.False(_logger.Has(LogLevel.Warning, RestoredWarning));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal(before, await File.ReadAllBytesAsync(backup));
    }

    [Fact]
    public async Task UpdateMetadataAsync_TargetReplacedByDirectoryBeforeSwap_FailsWithoutRetrying()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var service = CreateService();
        var path = WriteBook("Series/large.cbz", includeLargeEntry: true);
        _logger.On(LargeEntryWarning, _ =>
        {
            File.Delete(path);
            Directory.CreateDirectory(path);
        });

        var result = await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "New" }, CancellationToken.None);

        Assert.False(result);
        var error = Assert.Single(_logger.Entries, e => e.Template == ReplaceFailedError);
        Assert.IsType<UnauthorizedAccessException>(error.Exception);
        Assert.False(_logger.Entries.Any(e => e.Template == RetryWarning));
        Assert.True(Directory.Exists(path));
        Assert.Empty(TempFiles());
    }

    private ComicProcessorService CreateService()
    {
        var service = new ComicProcessorService(
            new TestOptionsMonitor<AppSettings>(_settings),
            _logger,
            _fileStore.Object,
            _history.Object);
        _services.Add(service);
        return service;
    }

    private string[] TempFiles() =>
        Directory.Exists(_tempDir) ? Directory.GetFiles(_tempDir, ".tmp_*.cbz") : Array.Empty<string>();

    private void DeleteTempFiles()
    {
        foreach (var file in TempFiles())
        {
            File.Delete(file);
        }
    }

    private string WriteBook(string relativePath, bool includeLargeEntry = false)
    {
        var path = Path.Combine(_library, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var info = zip.CreateEntry("ComicInfo.xml");
            using (var writer = new StreamWriter(info.Open()))
            {
                writer.Write(new XElement("ComicInfo", new XElement("Series", "Old")).ToString());
            }

            var page = zip.CreateEntry("page001.jpg");
            using (var stream = page.Open())
            {
                stream.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
            }

            if (includeLargeEntry)
            {
                var big = zip.CreateEntry("big.bin", CompressionLevel.Fastest);
                using var stream = big.Open();
                stream.Write(new byte[(10 * 1024 * 1024) + 1]);
            }
        }

        return path;
    }

    private static string? SeriesOf(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("ComicInfo.xml");
        if (entry == null)
        {
            return null;
        }

        using var stream = entry.Open();
        return XElement.Load(stream).Element("Series")?.Value;
    }

    private sealed record LogEntry(LogLevel Level, string Template, IReadOnlyDictionary<string, object?> Properties, Exception? Exception);

    private sealed class HookLogger : ILogger<ComicProcessorService>
    {
        private readonly ConcurrentDictionary<string, Action<LogEntry>> _hooks = new();

        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public void On(string template, Action<LogEntry> hook) => _hooks[template] = hook;

        public bool Has(LogLevel level, string template) =>
            Entries.Any(e => e.Level == level && e.Template == template);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var template = string.Empty;
            var properties = new Dictionary<string, object?>();
            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    if (pair.Key == "{OriginalFormat}")
                    {
                        template = pair.Value?.ToString() ?? string.Empty;
                    }
                    else
                    {
                        properties[pair.Key] = pair.Value;
                    }
                }
            }

            var entry = new LogEntry(logLevel, template, properties, exception);
            Entries.Enqueue(entry);
            if (_hooks.TryGetValue(template, out var hook))
            {
                hook(entry);
            }
        }

        private sealed class NoopScope : IDisposable
        {
            public static readonly NoopScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
