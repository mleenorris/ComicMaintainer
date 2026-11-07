using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.WebApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public partial class FilesController : ControllerBase
{
    private readonly IFileStoreService _fileStore;
    private readonly IComicProcessorService _processor;
    private readonly IProcessingHistoryService _historyService;
    private readonly ISeriesLibraryService _seriesLibrary;
    private readonly ILogger<FilesController> _logger;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly IFolderCombineService _folderCombine;
    private readonly IEventBroadcaster? _eventBroadcaster;
    private readonly ISeriesNameResolver? _seriesNameResolver;
    private readonly ScheduledJobsHostedService? _scheduledJobsHostedService;

    public FilesController(
        IFileStoreService fileStore,
        IComicProcessorService processor,
        IProcessingHistoryService historyService,
        ISeriesLibraryService seriesLibrary,
        ILogger<FilesController> logger,
        IOptionsMonitor<AppSettings> settings,
        IFolderCombineService folderCombine,
        IEventBroadcaster? eventBroadcaster = null,
        ISeriesNameResolver? seriesNameResolver = null,
        ScheduledJobsHostedService? scheduledJobsHostedService = null)
    {
        _fileStore = fileStore;
        _processor = processor;
        _historyService = historyService;
        _seriesLibrary = seriesLibrary;
        _logger = logger;
        _settings = settings;
        _folderCombine = folderCombine;
        _eventBroadcaster = eventBroadcaster;
        _seriesNameResolver = seriesNameResolver;
        _scheduledJobsHostedService = scheduledJobsHostedService;
    }

    /// <summary>
    /// Validates that a file path is within the watched directory to prevent path
    /// traversal attacks. Symlinks and junctions are resolved first — on the path
    /// itself and on every ancestor directory — so a link inside the watched
    /// directory cannot reach an external target.
    /// </summary>
    private bool IsPathSafe(string filePath) => TryResolveSafePath(filePath, out _);

    /// <summary>
    /// Link-aware variant of <see cref="IsPathSafe"/> that also returns the
    /// validated physical path to use for subsequent file system operations.
    /// </summary>
    private bool TryResolveSafePath(string filePath, out string resolvedPath) =>
        LibraryPathValidator.TryResolveWithin(
            filePath,
            out resolvedPath,
            _settings.CurrentValue.WatchedDirectory);

    private static string DecodeBase64UrlSafe(string input) =>
        TryDecodeBase64UrlSafe(input, out var decoded) ? decoded : string.Empty;

    /// <summary>
    /// Decodes a URL-safe base64 path key. Returns false for a key that is not valid
    /// base64, so callers can tell it apart from the library root's empty key.
    /// </summary>
    private static bool TryDecodeBase64UrlSafe(string? input, out string decoded)
    {
        decoded = string.Empty;
        if (input is null)
            return false;

        try
        {
            // Convert URL-safe base64 back to standard base64
            var base64 = input.Replace('-', '+').Replace('_', '/');
            // Add padding if necessary
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
            }
            var bytes = Convert.FromBase64String(base64);
            decoded = System.Text.Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }



    /// <summary>
    /// Helper method to log processing history entries
    /// </summary>
    private async Task LogHistoryAsync(string filePath, string action, bool success, string? errorMessage = null)
    {
        await _historyService.AddHistoryEntryAsync(new ProcessingHistoryEntry
        {
            Id = Guid.NewGuid(),
            FilePath = filePath,
            Action = action,
            Timestamp = DateTime.UtcNow,
            Success = success,
            ErrorMessage = errorMessage
        });
    }

    /// <summary>
    /// Helper method to log processing history entries with before/after metadata
    /// </summary>
    private async Task LogHistoryWithChangesAsync(
        string filePath, 
        string action, 
        bool success, 
        string? errorMessage,
        string? beforeFilename,
        string? afterFilename,
        ComicMetadata? beforeMetadata,
        ComicMetadata? afterMetadata)
    {
        await _historyService.AddHistoryEntryAsync(new ProcessingHistoryEntry
        {
            Id = Guid.NewGuid(),
            FilePath = filePath,
            Action = action,
            Timestamp = DateTime.UtcNow,
            Success = success,
            ErrorMessage = errorMessage,
            BeforeFilename = beforeFilename,
            AfterFilename = afterFilename,
            BeforeTitle = beforeMetadata?.Title,
            AfterTitle = afterMetadata?.Title,
            BeforeSeries = beforeMetadata?.Series,
            AfterSeries = afterMetadata?.Series,
            BeforeIssue = beforeMetadata?.Issue,
            AfterIssue = afterMetadata?.Issue,
            BeforePublisher = beforeMetadata?.Publisher,
            AfterPublisher = afterMetadata?.Publisher,
            BeforeYear = beforeMetadata?.Year,
            AfterYear = afterMetadata?.Year,
            BeforeVolume = beforeMetadata?.Volume,
            AfterVolume = afterMetadata?.Volume
        });
    }
}

public class PatchMetadataRequest
{
    public ComicMetadata? Metadata { get; set; }
    public List<string>? LockFields { get; set; }
}
