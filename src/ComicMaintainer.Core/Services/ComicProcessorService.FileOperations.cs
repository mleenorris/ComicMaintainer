using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;

namespace ComicMaintainer.Core.Services;

public partial class ComicProcessorService
{
    /// <summary>
    /// Delay in milliseconds to allow file handles to be fully released after archive disposal
    /// </summary>
    private const int FileHandleReleaseDelayMs = 100;
    
    /// <summary>
    /// Maximum number of retry attempts for file replacement operations
    /// </summary>
    private const int MaxFileReplaceRetries = 5;
    
    /// <summary>
    /// Initial delay in milliseconds for exponential backoff retry logic
    /// </summary>
    private const int RetryInitialDelayMs = 100;
    
    /// <summary>
    /// Delay in milliseconds before attempting to restore from backup
    /// </summary>
    private const int BackupRestoreDelayMs = 200;

    private async Task ReplaceFileWithRetryAsync(string tempFile, string targetFile, CancellationToken cancellationToken)
    {
        var backupPath = $"{targetFile}.backup";
        Exception? lastException = null;
        
        // First, try the atomic File.Replace operation
        try
        {
            ReplaceFile(tempFile, targetFile, backupPath);
            
            // Clean up backup if replace succeeded
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
            
            return; // Success
        }
        catch (IOException ex) when (IsCrossDeviceLinkError(ex))
        {
            // Cross-device link error - use fallback strategy
            // This is deterministic and won't be resolved by retrying
            _logger.LogWarning("Cross-device link detected, using fallback copy strategy for {File}", LoggingHelper.SanitizePathForLog(targetFile));
            
            try
            {
                // Clean up any existing backup file first to ensure we have a clean state
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
                
                // Create backup by copying the target file
                if (File.Exists(targetFile))
                {
                    File.Copy(targetFile, backupPath, overwrite: true);
                }
                
                // Copy temp file to target
                File.Copy(tempFile, targetFile, overwrite: true);
                
                // Delete temp file
                File.Delete(tempFile);
                
                // Clean up backup
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
                
                _logger.LogInformation("Successfully replaced file using fallback strategy: {File}", LoggingHelper.SanitizePathForLog(targetFile));
                return; // Success
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(fallbackEx, "Fallback strategy failed for {File}, attempting restore from backup", LoggingHelper.SanitizePathForLog(targetFile));
                await TryRestoreBackupAsync(backupPath, targetFile, cancellationToken);
                throw;
            }
        }
        catch (IOException ex)
        {
            lastException = ex;
        }
        catch (Exception ex)
        {
            // For non-IOException, try to restore from backup if it exists
            _logger.LogError(ex, "File replace failed for {File}, attempting restore from backup", LoggingHelper.SanitizePathForLog(targetFile));
            await TryRestoreBackupAsync(backupPath, targetFile, cancellationToken);
            throw;
        }
        
        // If we got here, we have an IOException (but not cross-device) - retry with backoff
        for (int attempt = 0; attempt < MaxFileReplaceRetries; attempt++)
        {
            try
            {
                await DelayWithExponentialBackoffAsync(attempt, lastException, targetFile, cancellationToken);
                
                // Attempt to replace the file
                ReplaceFile(tempFile, targetFile, backupPath);
                
                // Clean up backup if replace succeeded
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
                
                return; // Success
            }
            catch (IOException ex) when (attempt < MaxFileReplaceRetries - 1)
            {
                lastException = ex;
            }
            catch (Exception ex)
            {
                // For non-IOException or final attempt, try to restore from backup if it exists
                _logger.LogError(ex, "File replace failed for {File}, attempting restore from backup", LoggingHelper.SanitizePathForLog(targetFile));
                await TryRestoreBackupAsync(backupPath, targetFile, cancellationToken);
                throw;
            }
        }
        
        // If we exhausted all retries, throw the last exception
        if (lastException != null)
        {
            _logger.LogError(lastException, "Failed to replace file after {MaxRetries} attempts: {File}", MaxFileReplaceRetries, LoggingHelper.SanitizePathForLog(targetFile));
            throw lastException;
        }
    }

    /// <summary>
    /// Replaces <paramref name="destinationFile"/> with <paramref name="sourceFile"/> using
    /// <see cref="File.Replace(string, string, string?)"/>, keeping the previous contents in
    /// <paramref name="backupFile"/>. Virtual so tests can simulate failures that cannot be
    /// provoked on a single test filesystem, such as a cross-device link error.
    /// </summary>
    protected virtual void ReplaceFile(string sourceFile, string destinationFile, string backupFile) =>
        File.Replace(sourceFile, destinationFile, backupFile);

    /// <summary>
    /// Attempts to restore a backup file with proper error handling
    /// </summary>
    private async Task TryRestoreBackupAsync(string backupPath, string targetFile, CancellationToken cancellationToken)
    {
        if (!File.Exists(backupPath))
            return;
            
        try
        {
            // Wait a moment before attempting restore
            await Task.Delay(BackupRestoreDelayMs, cancellationToken);
            
            // Use Move instead of Copy for restore as it's more reliable
            if (File.Exists(targetFile))
            {
                File.Delete(targetFile);
            }
            File.Move(backupPath, targetFile);
            
            _logger.LogWarning("Restored backup file after failed replacement: {File}", LoggingHelper.SanitizePathForLog(targetFile));
        }
        catch (Exception restoreEx)
        {
            _logger.LogError(restoreEx, "Failed to restore backup file: {BackupPath}", LoggingHelper.SanitizePathForLog(backupPath));
        }
    }

    /// <summary>
    /// Delays execution with exponential backoff for retry logic
    /// </summary>
    private async Task DelayWithExponentialBackoffAsync(int attempt, Exception? exception, string targetFile, CancellationToken cancellationToken)
    {
        _logger.LogWarning(exception, "File replace attempt {Attempt} failed for {File}, retrying...", attempt + 1, LoggingHelper.SanitizePathForLog(targetFile));
        
        // Exponential backoff: 100ms, 200ms, 400ms, 800ms
        var delayMs = RetryInitialDelayMs * (int)Math.Pow(2, attempt);
        await Task.Delay(delayMs, cancellationToken);
    }

    /// <summary>
    /// Checks if an IOException is a cross-device link error
    /// </summary>
    private static bool IsCrossDeviceLinkError(IOException ex) =>
        IsCrossDeviceLinkError(ex.HResult, OperatingSystem.IsWindows());

    /// <summary>
    /// Classifies an <see cref="Exception.HResult"/> as a cross-device link error. .NET reports
    /// Win32 errors as HRESULT_FROM_WIN32 on Windows and the raw errno on Unix, so each
    /// platform's code is only matched on that platform: ERROR_NOT_SAME_DEVICE (17) is EEXIST on
    /// Unix, where EXDEV is 18. The message is not inspected because it can contain arbitrary
    /// path text.
    /// </summary>
    private static bool IsCrossDeviceLinkError(int hresult, bool isWindows)
    {
        // HRESULT_FROM_WIN32(ERROR_NOT_SAME_DEVICE)
        const int HRESULT_ERROR_NOT_SAME_DEVICE = unchecked((int)0x80070011);
        // EXDEV on Linux, macOS and the BSDs
        const int EXDEV = 18;

        return isWindows ? hresult == HRESULT_ERROR_NOT_SAME_DEVICE : hresult == EXDEV;
    }

    /// <summary>
    /// Resolves <paramref name="filePath"/> to its physical location (following
    /// symlinks and junctions on the file and every ancestor directory) and
    /// verifies the result is inside the watched or duplicate directory.
    /// </summary>
    private bool TryResolveAllowedPath(string filePath, out string resolvedPath) =>
        LibraryPathValidator.TryResolveWithin(
            filePath,
            out resolvedPath,
            _settings.WatchedDirectory,
            _settings.DuplicateDirectory);

    /// <summary>
    /// Like <see cref="TryResolveAllowedPath"/>, but returns the physical path of the
    /// directory entry itself: only the directories leading to it are resolved, so
    /// deleting the result removes a link rather than the file it points to.
    /// </summary>
    private bool TryResolveAllowedEntry(string filePath, out string entryPath)
    {
        entryPath = string.Empty;
        if (!TryResolveAllowedPath(filePath, out _))
            return false;

        var fullPath = Path.GetFullPath(filePath);
        if (!LibraryPathValidator.TryResolveWithin(
                Path.GetDirectoryName(fullPath),
                out var directory,
                _settings.WatchedDirectory,
                _settings.DuplicateDirectory))
            return false;

        entryPath = Path.Join(directory, Path.GetFileName(fullPath));
        return true;
    }
}
