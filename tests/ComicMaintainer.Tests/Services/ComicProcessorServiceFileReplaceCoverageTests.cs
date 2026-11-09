using System.Collections.Concurrent;
using System.IO.Compression;
using System.Xml.Linq;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers how a rewritten archive replaces the original. The new archive is built next to the
/// original and renamed over it in one step: transient failures are retried, every other failure
/// leaves the original byte-for-byte as it was, and there is no copy fallback that could truncate
/// it or a backup that could be restored over it. Failures are injected through the overridable
/// replace step (<see cref="ReplaceHookService"/>) and log hooks.
/// </summary>
public class ComicProcessorServiceFileReplaceCoverageTests : IDisposable
{
    private const string RetryWarning = "File replace attempt {Attempt} failed for {File}, retrying...";
    private const string ReplaceFailedError = "File replace failed for {File}; the original archive is unchanged";

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

        TestIO.TryDeleteDirectory(_root);
    }

    [Fact]
    public async Task UpdateMetadataAsync_BuildsTheNewArchiveNextToTheOriginalAndNotInTheTempDirectory()
    {
        string? source = null;
        var service = CreateService((_, from, _) => source = from);
        var path = WriteBook("Series/book.cbz");

        var result = await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "New" }, CancellationToken.None);

        Assert.True(result);
        Assert.Equal("New", SeriesOf(path));
        Assert.Equal(Path.GetDirectoryName(path), Path.GetDirectoryName(source));
        Assert.True(ArchiveWriteRecoveryService.IsTempPath(source!), source);
        Assert.False(Directory.Exists(_tempDir));
        Assert.Empty(LeftoverFiles(path));
    }

    [Fact]
    public async Task UpdateMetadataAsync_TransientReplaceFailure_IsRetriedAndSucceeds()
    {
        var service = CreateService(FailFirstReplaceWith(new IOException("The process cannot access the file because it is being used by another process.")));
        var path = WriteBook("Series/book.cbz");

        var result = await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "New" }, CancellationToken.None);

        Assert.True(result);
        Assert.Equal("New", SeriesOf(path));
        var retry = Assert.Single(_logger.Entries, e => e.Template == RetryWarning);
        Assert.Equal(1, retry.Properties["Attempt"]);
        Assert.Equal(2, service.ReplaceCalls);
        Assert.Empty(LeftoverFiles(path));
    }

    [Fact]
    public async Task UpdateMetadataAsync_ReplaceKeepsFailing_GivesUpAfterFiveRetriesAndKeepsTheOriginal()
    {
        var service = CreateService(AlwaysFailReplaceWith(() => new IOException("locked")));
        var path = WriteBook("Series/book.cbz");
        var before = await File.ReadAllBytesAsync(path);

        var result = await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "New" }, CancellationToken.None);

        Assert.False(result);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal(
            new object?[] { 1, 2, 3, 4, 5 },
            _logger.Entries.Where(e => e.Template == RetryWarning).Select(e => e.Properties["Attempt"]));
        var error = Assert.Single(_logger.Entries, e => e.Template == ReplaceFailedError);
        Assert.IsAssignableFrom<IOException>(error.Exception);
        Assert.True(_logger.Has(LogLevel.Error, "Error updating metadata for {FilePath}"));
        Assert.Empty(LeftoverFiles(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplaceRetry_CancelledWhileBackingOff_FailsAndKeepsOriginal(bool removeMetadata)
    {
        var service = CreateService(AlwaysFailReplaceWith(() => new IOException("locked")));
        var path = WriteBook("Series/book.cbz");
        var before = await File.ReadAllBytesAsync(path);
        using var cts = new CancellationTokenSource();
        _logger.On(RetryWarning, e =>
        {
            if (Equals(e.Properties["Attempt"], 2))
            {
                cts.Cancel();
            }
        });

        var result = removeMetadata
            ? (await service.RemoveMetadataAsync(path, cts.Token)).Succeeded
            : await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "New" }, cts.Token);

        Assert.False(result);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal(2, _logger.Entries.Count(e => e.Template == RetryWarning));
        Assert.Empty(LeftoverFiles(path));
    }

    [Fact]
    public async Task UpdateMetadataAsync_CrossDeviceError_FailsWithoutCopyingOverTheOriginal()
    {
        // The old fallback for EXDEV copied the original to '<file>.backup' and then truncated and
        // rewrote the original in place. Now a replace that cannot rename simply fails.
        var crossDevice = new IOException("Invalid cross-device link", OperatingSystem.IsWindows() ? unchecked((int)0x80070011) : 18);
        var service = CreateService(AlwaysFailReplaceWith(() => crossDevice));
        var path = WriteBook("Series/book.cbz");
        var before = await File.ReadAllBytesAsync(path);

        var result = await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "New" }, CancellationToken.None);

        Assert.False(result);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".backup"));
        Assert.Empty(LeftoverFiles(path));
    }

    [Fact]
    public async Task UpdateMetadataAsync_TargetDeletedBeforeTheReplace_FailsWithoutRetryingOrRecreatingIt()
    {
        var service = CreateService((call, _, target) =>
        {
            if (call == 1)
            {
                File.Delete(target);
            }
        });
        var path = WriteBook("Series/book.cbz");

        var result = await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "New" }, CancellationToken.None);

        Assert.False(result);
        Assert.False(File.Exists(path));
        Assert.Equal(1, service.ReplaceCalls);
        Assert.DoesNotContain(_logger.Entries, e => e.Template == RetryWarning);
        Assert.Empty(LeftoverFiles(path));
    }

    [Fact]
    public async Task UpdateMetadataAsync_TargetReplacedByDirectoryBeforeSwap_FailsWithoutRetrying()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var service = CreateService((call, _, target) =>
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
        Assert.Single(_logger.Entries, e => e.Template == ReplaceFailedError);
        Assert.DoesNotContain(_logger.Entries, e => e.Template == RetryWarning);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task UpdateMetadataAsync_KeepsThePermissionsOfTheOriginal()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var service = CreateService((_, _, _) => { });
        var path = WriteBook("Series/book.cbz");
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        File.SetUnixFileMode(path, mode);

        Assert.True(await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "New" }, CancellationToken.None));

        Assert.Equal(mode, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task UpdateMetadataAsync_ExistingLeftoverTempFile_IsNeverOverwritten()
    {
        // Temp names are unique and created with CreateNew, so a write never reuses another
        // write's file.
        var service = CreateService((_, _, _) => { });
        var path = WriteBook("Series/book.cbz");
        var unrelated = ArchiveWriteRecoveryService.CreateTempPath(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(unrelated, "another write");

        Assert.True(await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "New" }, CancellationToken.None));

        Assert.Equal("another write", await File.ReadAllTextAsync(unrelated));
    }

    private ReplaceHookService CreateService(Action<int, string, string> beforeReplace)
    {
        var service = new ReplaceHookService(_settings, _logger, _fileStore.Object, _history.Object, beforeReplace);
        _services.Add(service);
        return service;
    }

    private static Action<int, string, string> FailFirstReplaceWith(Exception error) =>
        (call, _, _) =>
        {
            if (call == 1)
            {
                throw error;
            }
        };

    private static Action<int, string, string> AlwaysFailReplaceWith(Func<Exception> error) =>
        (_, _, _) => throw error();

    /// <summary>Files left next to the archive by the write: temp files or backups.</summary>
    private static string[] LeftoverFiles(string path) =>
        Directory.GetFiles(Path.GetDirectoryName(path)!)
            .Where(file => ArchiveWriteRecoveryService.IsTempPath(file) || file.EndsWith(".backup", StringComparison.Ordinal))
            .ToArray();

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
    /// Calls the hook (with the 1-based call number, the temp file and the target path) before
    /// every replace, so a test can change filesystem state at exactly that point or throw an
    /// error a single test filesystem cannot produce, such as a cross-device link failure.
    /// </summary>
    private sealed class ReplaceHookService : ComicProcessorService
    {
        private readonly Action<int, string, string> _beforeReplace;

        public ReplaceHookService(
            AppSettings settings,
            ILogger<ComicProcessorService> logger,
            IFileStoreService fileStore,
            IProcessingHistoryService history,
            Action<int, string, string> beforeReplace)
            : base(
                  new TestOptionsMonitor<AppSettings>(settings),
                  logger,
                  fileStore,
                  history,
                  Mock.Of<IEventBroadcaster>(),
                  Mock.Of<ISeriesMetadataCacheService>(),
                  TestServices.CreateSeriesNameResolver(new TestOptionsMonitor<AppSettings>(settings), NullLogger<SeriesNameResolver>.Instance),
                  Mock.Of<IBatchJobRegistry>(),
                  Mock.Of<IComicEmailService>(),
                  new FileOperationLock())
        {
            _beforeReplace = beforeReplace;
        }

        public int ReplaceCalls { get; private set; }

        protected override void ReplaceFile(string sourceFile, string destinationFile)
        {
            _beforeReplace(++ReplaceCalls, sourceFile, destinationFile);
            base.ReplaceFile(sourceFile, destinationFile);
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
