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
            File.Replace(tempFile, targetFile, backupPath);
            
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
                File.Replace(tempFile, targetFile, backupPath);
                
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
    private static bool IsCrossDeviceLinkError(IOException ex)
    {
        // EXDEV (errno 18 on Linux/Unix) = Cross-device link
        // On Windows, HResult 0x80070011 = ERROR_NOT_SAME_DEVICE
        const int EXDEV = 18;
        const int ERROR_NOT_SAME_DEVICE = 0x11;
        const int HRESULT_ERROR_CODE_MASK = 0xFFFF;
        
        // Check HResult for Windows (upper 16 bits are facility code, lower 16 bits are error code)
        var hresult = ex.HResult;
        var errorCode = hresult & HRESULT_ERROR_CODE_MASK;
        
        // Check if this is a cross-device error on Windows or Unix
        return errorCode == ERROR_NOT_SAME_DEVICE || 
               errorCode == EXDEV ||
               ex.Message.Contains("cross-device", StringComparison.OrdinalIgnoreCase);
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
}
