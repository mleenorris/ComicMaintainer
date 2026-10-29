using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
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
/// semantics. A real cross-device link error needs two filesystems, so the fallback tests make
/// the overridable File.Replace step throw one (<see cref="ReplaceHookService"/>).
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

    // The classifier is private to ComicProcessorService (no InternalsVisibleTo).
    private static readonly Func<int, bool, bool> IsCrossDeviceLinkHResult = typeof(ComicProcessorService)
        .GetMethod("IsCrossDeviceLinkError", BindingFlags.Static | BindingFlags.NonPublic, new[] { typeof(int), typeof(bool) })!
        .CreateDelegate<Func<int, bool, bool>>();

    private static readonly Func<IOException, bool> IsCrossDeviceLinkException = typeof(ComicProcessorService)
        .GetMethod("IsCrossDeviceLinkError", BindingFlags.Static | BindingFlags.NonPublic, new[] { typeof(IOException) })!
        .CreateDelegate<Func<IOException, bool>>();

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
        _root = Path.Join(Path.GetTempPath(), "cps-replace-cov-" + Guid.NewGuid().ToString("N"));
        _library = Path.Join(_root, "library");
        _tempDir = Path.Join(_root, "temp");
        Directory.CreateDirectory(_library);

        _settings = new AppSettings
        {
            WatchedDirectory = _library,
            DuplicateDirectory = Path.Join(_root, "duplicates"),
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
            // Best-effort cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
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

        var path = WriteBook("Series/book.cbz");
        var backup = path + ".backup";
        await File.WriteAllTextAsync(backup, "stale backup");
        var service = CreateService(FailFirstReplaceWith(CrossDeviceLinkError()));

        var result = await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "New" }, CancellationToken.None);

        Assert.True(result);
        Assert.Equal("New", SeriesOf(path));
        Assert.False(File.Exists(backup));
        Assert.True(_logger.Has(LogLevel.Warning, CrossDeviceWarning));
        Assert.True(_logger.Has(LogLevel.Information, FallbackSucceeded));
        Assert.DoesNotContain(_logger.Entries, e => e.Template == RetryWarning);
        Assert.Equal(1, service.ReplaceCalls);
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task CrossDeviceFallback_TargetAlreadyGone_StillWritesNewArchive()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var path = WriteBook("Series/book.cbz");
        var backup = path + ".backup";
        var service = CreateService(FailFirstReplaceWith(CrossDeviceLinkError()));
        _logger.On(CrossDeviceWarning, _ => File.Delete(path));

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

        var path = WriteBook("Series/book.cbz");
        var before = await File.ReadAllBytesAsync(path);
        var backup = path + ".backup";
        var service = CreateService(FailFirstReplaceWith(CrossDeviceLinkError()));
        _logger.On(CrossDeviceWarning, _ => DeleteTempFiles());
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

        var path = WriteBook("Series/book.cbz");
        var before = await File.ReadAllBytesAsync(path);
        var backup = path + ".backup";
        using var cts = new CancellationTokenSource();
        var service = CreateService(FailFirstReplaceWith(CrossDeviceLinkError()));
        _logger.On(CrossDeviceWarning, _ => DeleteTempFiles());
        _logger.On(FallbackFailedError, _ => cts.Cancel());

        var result = await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "New" }, cts.Token);

        Assert.False(result);
        Assert.True(_logger.Has(LogLevel.Error, RestoreFailedError));
        Assert.False(_logger.Has(LogLevel.Warning, RestoredWarning));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal(before, await File.ReadAllBytesAsync(backup));
    }

    [Fact]
    public async Task ReplaceFailsWithFileExistsError_IsRetriedInsteadOfUsingTheCopyFallback()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var path = WriteBook("Series/book.cbz");
        var fileExists = FileExistsError();
        // EEXIST is errno 17 on Unix, the same value as Windows' ERROR_NOT_SAME_DEVICE.
        Assert.Equal(17, fileExists.HResult);
        var service = CreateService(FailFirstReplaceWith(fileExists));

        var result = await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "New" }, CancellationToken.None);

        Assert.True(result);
        Assert.Equal("New", SeriesOf(path));
        var retry = Assert.Single(_logger.Entries, e => e.Template == RetryWarning);
        Assert.Same(fileExists, retry.Exception);
        Assert.DoesNotContain(_logger.Entries, e => e.Template == CrossDeviceWarning);
        Assert.Equal(2, service.ReplaceCalls);
        Assert.False(File.Exists(path + ".backup"));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task UpdateMetadataAsync_ReplaceErrorMessageMentionsCrossDevice_IsRetriedInsteadOfUsingTheCopyFallback()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var service = CreateService();
        var path = WriteBook("cross-device-lib/Series/book.cbz");
        var backup = path + ".backup";
        Directory.CreateDirectory(backup);
        _logger.On(RetryWarning, _ => Directory.Delete(backup));

        var result = await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "New" }, CancellationToken.None);

        Assert.True(result);
        Assert.Equal("New", SeriesOf(path));
        var retry = Assert.Single(_logger.Entries, e => e.Template == RetryWarning);
        Assert.Contains("cross-device", retry.Exception!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_logger.Entries, e => e.Template == CrossDeviceWarning);
        Assert.False(File.Exists(backup));
        Assert.Empty(TempFiles());
    }

    [Theory]
    [InlineData(18, false, true)] // EXDEV
    [InlineData(17, false, false)] // EEXIST
    [InlineData(21, false, false)] // EISDIR
    [InlineData(unchecked((int)0x80070011), false, false)]
    [InlineData(unchecked((int)0x80070011), true, true)] // HRESULT_FROM_WIN32(ERROR_NOT_SAME_DEVICE)
    [InlineData(unchecked((int)0x80070012), true, false)] // HRESULT_FROM_WIN32(ERROR_NO_MORE_FILES)
    [InlineData(17, true, false)]
    [InlineData(18, true, false)]
    public void IsCrossDeviceLinkError_MatchesOnlyTheCodeOfTheCurrentPlatform(int hresult, bool isWindows, bool expected)
    {
        Assert.Equal(expected, IsCrossDeviceLinkHResult(hresult, isWindows));
    }

    [Fact]
    public void IsCrossDeviceLinkError_IgnoresTheExceptionMessage()
    {
        Assert.True(IsCrossDeviceLinkException(CrossDeviceLinkError()));
        Assert.False(IsCrossDeviceLinkException(FileExistsError()));
        Assert.False(IsCrossDeviceLinkException(
            new IOException("Is a directory : '/library/cross-device/book.cbz.backup'", 21)));
    }

    [Fact]
    public async Task UpdateMetadataAsync_TargetReplacedByDirectoryBeforeSwap_FailsWithoutRetrying()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var service = CreateService((call, target) =>
        {
            if (call == 1)
            {
                File.Delete(target);
                Directory.CreateDirectory(target);
            }
        });
        var path = WriteBook("Series/book.cbz");

        var result = await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "New" }, CancellationToken.None);

        Assert.False(result);
        Assert.Equal(1, service.ReplaceCalls);
        var error = Assert.Single(_logger.Entries, e => e.Template == ReplaceFailedError);
        Assert.IsType<UnauthorizedAccessException>(error.Exception);
        Assert.DoesNotContain(_logger.Entries, e => e.Template == RetryWarning);
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

    private ReplaceHookService CreateService(Action<int, string> beforeReplace)
    {
        var service = new ReplaceHookService(_settings, _logger, _fileStore.Object, _history.Object, beforeReplace);
        _services.Add(service);
        return service;
    }

    private static Action<int, string> FailFirstReplaceWith(Exception error) =>
        (call, _) =>
        {
            if (call == 1)
            {
                throw error;
            }
        };

    /// <summary>
    /// The error File.Replace reports when the temp file and the target live on different
    /// filesystems (EXDEV on Unix, ERROR_NOT_SAME_DEVICE on Windows).
    /// </summary>
    private static IOException CrossDeviceLinkError() =>
        new("Invalid cross-device link", OperatingSystem.IsWindows() ? unchecked((int)0x80070011) : 18);

    /// <summary>A genuine "file already exists" error raised by the OS.</summary>
    private IOException FileExistsError()
    {
        var source = Path.Join(_root, "exists-source");
        var target = Path.Join(_root, "exists-target");
        File.WriteAllText(source, "source");
        File.WriteAllText(target, "target");
        return Assert.Throws<IOException>(() => File.Copy(source, target));
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

    private string WriteBook(string relativePath)
    {
        var path = Path.Join(_library, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var info = zip.CreateEntry("ComicInfo.xml");
            using (var writer = new StreamWriter(info.Open()))
            {
                writer.Write(new XElement("ComicInfo", new XElement("Series", "Old")).ToString());
            }

            var page = zip.CreateEntry("page001.jpg");
            using var stream = page.Open();
            stream.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
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

    /// <summary>
    /// Calls the hook (with the 1-based call number and the target path) before every
    /// File.Replace, so a test can change filesystem state at exactly that point or throw an
    /// error a single test filesystem cannot produce, such as a cross-device link failure.
    /// </summary>
    private sealed class ReplaceHookService : ComicProcessorService
    {
        private readonly Action<int, string> _beforeReplace;

        public ReplaceHookService(
            AppSettings settings,
            ILogger<ComicProcessorService> logger,
            IFileStoreService fileStore,
            IProcessingHistoryService history,
            Action<int, string> beforeReplace)
            : base(new TestOptionsMonitor<AppSettings>(settings), logger, fileStore, history)
        {
            _beforeReplace = beforeReplace;
        }

        public int ReplaceCalls { get; private set; }

        protected override void ReplaceFile(string sourceFile, string destinationFile, string backupFile)
        {
            _beforeReplace(++ReplaceCalls, destinationFile);
            base.ReplaceFile(sourceFile, destinationFile, backupFile);
        }
    }

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
