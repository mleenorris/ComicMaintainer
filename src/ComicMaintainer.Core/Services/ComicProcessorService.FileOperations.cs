using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
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
    private bool TryResolveAllowedEntry(string filePath, out string entryPath) =>
        LibraryPathValidator.TryResolveEntryWithin(
            filePath,
            out entryPath,
            _settings.WatchedDirectory,
            _settings.DuplicateDirectory);

    /// <summary>
    /// The path of <paramref name="entryPath"/> as seen through the opened directory, so it
    /// keeps naming the entry in the checked directory even if that directory is moved or
    /// replaced by a link afterwards. On macOS that is the opened directory's current path.
    /// On Windows the opened directory cannot be moved, so the path itself is used.
    /// </summary>
    private static string GetEntryPathInDirectory(SafeFileHandle? directoryHandle, string entryPath)
    {
        if (directoryHandle is null || directoryHandle.IsInvalid)
        {
            return entryPath;
        }

        if (OperatingSystem.IsLinux())
        {
            return Path.Join($"/proc/self/fd/{directoryHandle.DangerousGetHandle().ToInt32()}", Path.GetFileName(entryPath));
        }

        return OperatingSystem.IsMacOS()
            ? Path.Join(NativeFileSystem.ReadMacDirectoryPath(directoryHandle.DangerousGetHandle().ToInt32()), Path.GetFileName(entryPath))
            : entryPath;
    }

    /// <summary>
    /// Opens the directory holding <paramref name="entryPath"/> so <see cref="DeleteFileInDirectory"/>
    /// deletes from the directory that was checked. On Linux and macOS the entry is deleted
    /// relative to the opened directory; on Windows the handle denies delete sharing, which keeps
    /// the directory and its ancestors from being renamed or replaced while it is open. Other
    /// platforms cannot pin the directory, so deleting fails there.
    /// </summary>
    private static SafeFileHandle OpenDeleteDirectory(string entryPath)
    {
        if (OperatingSystem.IsLinux())
        {
            return OpenRenameDirectory(entryPath)!;
        }

        if (OperatingSystem.IsMacOS())
        {
            return OpenMacDirectory(entryPath);
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Deleting files is only supported on Linux, macOS and Windows.");
        }

        var directoryPath = Path.GetDirectoryName(entryPath)
            ?? throw new IOException("Cannot determine the directory of the file.");
        // Share access is only checked for opens that request more than attributes, so
        // listing access is needed for the missing FILE_SHARE_DELETE to block renames.
        var handle = NativeFileSystem.CreateWindowsFile(
            directoryPath,
            NativeFileSystem.FILE_LIST_DIRECTORY | NativeFileSystem.FILE_READ_ATTRIBUTES,
            NativeFileSystem.FILE_SHARE_READ_WRITE,
            IntPtr.Zero,
            NativeFileSystem.OPEN_EXISTING,
            NativeFileSystem.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);
        try
        {
            if (handle.IsInvalid)
            {
                throw new IOException($"Could not open the directory: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
            }

            // The handle follows links, so a directory that was replaced by a link (or a
            // junction) before it was opened resolves somewhere else and is refused. Both
            // paths are spelled as the file system stores them, so they are compared exactly.
            // Trimming keeps the separator of a root such as "D:\", which the handle reports too.
            if (!string.Equals(
                    Path.TrimEndingDirectorySeparator(NativeFileSystem.ReadWindowsFinalPath(handle)),
                    Path.TrimEndingDirectorySeparator(directoryPath),
                    StringComparison.Ordinal))
            {
                throw new IOException("The directory changed while it was being opened.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Deletes <paramref name="entryPath"/> from the directory opened by
    /// <see cref="OpenDeleteDirectory"/>, after checking that directory is still inside the
    /// allowed directories. Like <see cref="File.Delete"/>, a missing entry is not an error.
    /// </summary>
    private void DeleteFileInDirectory(SafeFileHandle? directoryHandle, string entryPath)
    {
        if (directoryHandle is null || directoryHandle.IsInvalid)
        {
            throw new IOException("The directory handle is unavailable.");
        }

        if (OperatingSystem.IsWindows())
        {
            var openedDirectory = NativeFileSystem.ReadWindowsFinalPath(directoryHandle);
            if (!LibraryPathValidator.IsWithin(openedDirectory, _settings.WatchedDirectory) &&
                !LibraryPathValidator.IsWithin(openedDirectory, _settings.DuplicateDirectory))
            {
                throw new IOException("The directory is outside the allowed directories.");
            }

            File.Delete(entryPath);
            return;
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Deleting files is only supported on Linux, macOS and Windows.");
        }

        var directoryDescriptor = directoryHandle.DangerousGetHandle().ToInt32();
        var currentDirectory = OperatingSystem.IsLinux()
            ? ReadLinuxDirectoryPath(directoryDescriptor)
            : NativeFileSystem.ReadMacDirectoryPath(directoryDescriptor);
        if (!LibraryPathValidator.IsWithin(currentDirectory, _settings.WatchedDirectory) &&
            !LibraryPathValidator.IsWithin(currentDirectory, _settings.DuplicateDirectory))
        {
            throw new IOException("The directory is outside the allowed directories.");
        }

        if (UnlinkAt(directoryDescriptor, Path.GetFileName(entryPath), 0) != 0)
        {
            const int ENOENT = 2;
            var error = Marshal.GetLastPInvokeError();
            if (error != ENOENT)
            {
                throw new IOException($"Could not delete the file: {new Win32Exception(error).Message}");
            }
        }
    }

    /// <summary>
    /// Opens the directory holding <paramref name="entryPath"/> on macOS, checking that it is
    /// the directory that was resolved rather than something it was replaced by meanwhile.
    /// </summary>
    private static SafeFileHandle OpenMacDirectory(string entryPath)
    {
        var directoryPath = Path.GetDirectoryName(entryPath)
            ?? throw new IOException("Cannot determine the directory of the file.");
        var descriptor = NativeFileSystem.OpenUnixDirectory(
            directoryPath, NativeFileSystem.MAC_O_DIRECTORY | NativeFileSystem.MAC_O_CLOEXEC);
        if (descriptor < 0)
        {
            throw new IOException($"Could not open the directory: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }

        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            // The open follows links, so a directory that was replaced by a link before it
            // was opened resolves somewhere else and is refused. Both paths are spelled as
            // the file system stores them, so a differently cased request still matches.
            if (!string.Equals(
                    Path.TrimEndingDirectorySeparator(NativeFileSystem.ReadMacDirectoryPath(descriptor)),
                    Path.TrimEndingDirectorySeparator(directoryPath),
                    StringComparison.Ordinal))
            {
                throw new IOException("The directory changed while it was being opened.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle? OpenRenameDirectory(string sourcePath)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var directoryPath = Path.GetDirectoryName(sourcePath)
            ?? throw new IOException("Cannot determine the source directory.");
        var directoryFlag = UsesArmOpenFlags ? 0x4000 : 0x10000;
        var descriptor = NativeFileSystem.OpenUnixDirectory(directoryPath, directoryFlag | 0x80000 | 0x200000);
        if (descriptor < 0)
        {
            throw new IOException($"Could not open the source directory: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }

        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            if (!string.Equals(ReadLinuxDirectoryPath(descriptor), directoryPath, StringComparison.Ordinal))
            {
                throw new IOException("The source directory changed while it was being opened.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private string MoveFileInRenameDirectory(SafeFileHandle? directoryHandle, string sourcePath, string targetPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            File.Move(sourcePath, targetPath);
            return targetPath;
        }

        if (directoryHandle is null || directoryHandle.IsInvalid)
        {
            throw new IOException("The source directory handle is unavailable.");
        }

        var directoryDescriptor = directoryHandle.DangerousGetHandle().ToInt32();
        var currentDirectory = ReadLinuxDirectoryPath(directoryDescriptor);
        if (!LibraryPathValidator.IsWithin(currentDirectory, _settings.WatchedDirectory) &&
            !LibraryPathValidator.IsWithin(currentDirectory, _settings.DuplicateDirectory))
        {
            throw new IOException("The source directory is outside the allowed directories.");
        }

        var result = RenameLinuxNoReplace(
            directoryDescriptor,
            Path.GetFileName(sourcePath),
            directoryDescriptor,
            Path.GetFileName(targetPath),
            1);
        if (result != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is not (22 or 38 or 95))
            {
                throw RenameError(error);
            }

            OnLinuxRenameFallback();
            MoveLinuxFileWithoutReplacing(
                directoryDescriptor,
                currentDirectory,
                Path.GetFileName(sourcePath),
                Path.GetFileName(targetPath));
        }

        return Path.Combine(ReadLinuxDirectoryPath(directoryDescriptor), Path.GetFileName(targetPath));
    }

    /// <summary>
    /// Renames <paramref name="sourceName"/> to <paramref name="targetName"/> inside the opened
    /// directory on a filesystem without an atomic no-overwrite rename, such as NFS, ZFS before
    /// 2.2 or a FUSE filesystem like an Unraid user share. The target is created atomically and
    /// never replaces an existing file: as a hard link to the opened source or, where hard links
    /// are unavailable, as an exclusively created copy. The source name is then removed only while
    /// it still names that file, so a file that replaced it in the meantime is kept.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private void MoveLinuxFileWithoutReplacing(int directory, string directoryPath, string sourceName, string targetName)
    {
        _logger.LogDebug(
            "Atomic no-overwrite rename is not supported for {FileName}; moving it without replacing an existing file",
            LoggingHelper.SanitizeForLog(sourceName));

        // O_PATH | O_NOFOLLOW | O_CLOEXEC: holds the file itself without following a link or opening it for I/O.
        using var source = OpenLinuxFile(directory, sourceName, 0x200000 | LinuxNoFollowFlag | 0x80000);
        var sourceLink = $"/proc/self/fd/{source.DangerousGetHandle().ToInt32()}";
        var sourceFile = GetLinuxFileStatus(source);
        if ((sourceFile.Mode & 0xF000) != 0x8000)
        {
            throw new IOException("Only regular files can be renamed.");
        }

        LinuxFileStatus? createdTarget = null;
        SafeFileHandle? copy = null;
        try
        {
            // AT_FDCWD and AT_SYMLINK_FOLLOW on the /proc link: links exactly the file that is held.
            if (LinkLinux(-100, sourceLink, directory, targetName, 0x400) == 0)
            {
                createdTarget = sourceFile;
            }
            else
            {
                var error = Marshal.GetLastPInvokeError();

                // EPERM, EACCES, EXDEV, EMLINK, ENOSYS, EOPNOTSUPP: no hard link here, but a copy can work.
                if (error is not (1 or 13 or 18 or 31 or 38 or 95))
                {
                    throw RenameError(error);
                }

                _logger.LogDebug(
                    "Hard links are not available for {FileName} ({Reason}); copying it instead",
                    LoggingHelper.SanitizeForLog(sourceName),
                    new Win32Exception(error).Message);

                // O_RDONLY | O_CLOEXEC on the /proc link reopens the held file for reading.
                using var reader = OpenLinuxFile(-100, sourceLink, 0x80000);

                // O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC: fails rather than replace or follow anything already there.
                copy = OpenLinuxFile(directory, targetName, 0x1 | 0x40 | 0x80 | 0x80000, sourceFile.Mode & 0x1FFu);
                createdTarget = GetLinuxFileStatus(copy);
                CopyLinuxFile(reader, copy, (UnixFileMode)(sourceFile.Mode & 0x1FF), sourceName);
            }

            if (!RemoveLinuxNameIfUnchanged(directory, directoryPath, sourceName, sourceFile))
            {
                throw new IOException("The file was moved or deleted while it was being renamed.");
            }
        }
        catch (Exception) when (createdTarget is { } target)
        {
            try
            {
                RemoveLinuxNameIfUnchanged(directory, directoryPath, targetName, target);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not remove {FileName} after its rename failed", LoggingHelper.SanitizeForLog(targetName));
            }

            throw;
        }
        finally
        {
            copy?.Dispose();
        }
    }

    [SupportedOSPlatform("linux")]
    private void CopyLinuxFile(SafeFileHandle source, SafeFileHandle target, UnixFileMode permissions, string name)
    {
        var buffer = new byte[81920];
        long offset = 0;
        int read;
        while ((read = RandomAccess.Read(source, buffer, offset)) > 0)
        {
            RandomAccess.Write(target, buffer.AsSpan(0, read), offset);
            offset += read;
        }

        try
        {
            // Keep what a rename keeps: the exact permissions (creating the file applied the umask)
            // and the modification time.
            File.SetUnixFileMode(target, permissions);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not keep the permissions or modification time of {FileName}", LoggingHelper.SanitizeForLog(name));
        }

        RandomAccess.FlushToDisk(target);
    }

    /// <summary>
    /// Removes <paramref name="name"/> from the opened directory if it still names
    /// <paramref name="file"/>. The name is first moved to a private one and checked there, so a
    /// file that has replaced it is never deleted: that file is linked back under its name or, if
    /// that is not possible without risking another file, kept under the private name.
    /// </summary>
    /// <returns><see langword="false"/> if <paramref name="name"/> no longer exists.</returns>
    private bool RemoveLinuxNameIfUnchanged(int directory, string directoryPath, string name, in LinuxFileStatus file)
    {
        // AT_SYMLINK_NOFOLLOW: a link that took the name is a different file, never its target.
        var error = StatLinuxFile(directory, name, 0x100, out var current);
        if (error == 2)
        {
            return false;
        }

        if (error != 0)
        {
            throw RenameError(error);
        }

        if (!current.IsSameFile(file))
        {
            return true;
        }

        var privateName = $".tmp_{Guid.NewGuid():N}";
        if (RenameLinux(directory, name, directory, privateName) != 0)
        {
            error = Marshal.GetLastPInvokeError();
            if (error == 2)
            {
                return false;
            }

            throw RenameError(error);
        }

        // Nothing below may throw: the caller would undo the move while the name is set aside.
        var replaced = StatLinuxFile(directory, privateName, 0x100, out current) != 0 || !current.IsSameFile(file);
        if (replaced && LinkLinux(directory, privateName, directory, name, 0) != 0)
        {
            var reason = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
            _logger.LogWarning(
                "A file replaced {FileName} while it was being renamed and could not be put back without risking another file ({Reason}); it was kept as {TemporaryPath}",
                LoggingHelper.SanitizeForLog(name),
                reason,
                LoggingHelper.SanitizeForLog(Path.Join(directoryPath, privateName)));
            return true;
        }

        if (UnlinkLinux(directory, privateName, 0) != 0)
        {
            var reason = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
            _logger.LogWarning(
                "Could not remove {TemporaryPath} after renaming {FileName}: {Reason}",
                LoggingHelper.SanitizeForLog(Path.Join(directoryPath, privateName)),
                LoggingHelper.SanitizeForLog(name),
                reason);
        }

        return true;
    }

    private static IOException RenameError(int error) =>
        new($"Could not rename the file: {new Win32Exception(error).Message}");

    private static string ReadLinuxDirectoryPath(int descriptor)
    {
        var buffer = new byte[4096];
        var length = ReadLinuxLink($"/proc/self/fd/{descriptor}", buffer, (nuint)buffer.Length);
        if (length <= 0 || length >= buffer.Length)
        {
            throw new IOException($"Could not resolve the opened directory: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }

        var path = Encoding.UTF8.GetString(buffer, 0, (int)length);
        return path;
    }

    private string GetAllowedLogicalPath(string resolvedPath)
    {
        foreach (var allowedDirectory in new[] { _settings.WatchedDirectory, _settings.DuplicateDirectory })
        {
            if (string.IsNullOrWhiteSpace(allowedDirectory))
            {
                continue;
            }

            var logicalRoot = Path.GetFullPath(allowedDirectory);
            var resolvedRoot = LibraryPathValidator.ResolveRealPath(logicalRoot);
            if (!LibraryPathValidator.IsWithin(resolvedPath, resolvedRoot))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(resolvedRoot, resolvedPath);
            var logicalPath = Path.GetFullPath(Path.Combine(logicalRoot, relativePath));
            if (TryResolveAllowedPath(logicalPath, out var verifiedPath) &&
                string.Equals(verifiedPath, resolvedPath, LibraryPathValidator.PathComparison))
            {
                return logicalPath;
            }
        }

        throw new IOException("The renamed file does not resolve within a configured library directory.");
    }

    [DllImport("libc", EntryPoint = "readlink", SetLastError = true)]
    private static extern nint ReadLinuxLink(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [Out] byte[] buffer,
        nuint bufferSize);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAt(
        int directory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int flags);

    protected virtual int RenameLinuxNoReplace(
        int sourceDirectory,
        string sourceName,
        int targetDirectory,
        string targetName,
        uint flags) =>
        RenameLinuxNoReplaceNative(sourceDirectory, sourceName, targetDirectory, targetName, flags);

    protected virtual void OnLinuxRenameFallback()
    {
    }

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameLinuxNoReplaceNative(
        int sourceDirectory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sourceName,
        int targetDirectory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string targetName,
        uint flags);

    /// <summary>
    /// Whether open(2) uses the ARM and PowerPC values of O_DIRECTORY (0x4000) and O_NOFOLLOW
    /// (0x8000); the other Linux architectures use 0x10000 and 0x20000.
    /// </summary>
    private static bool UsesArmOpenFlags =>
        RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Armv6 or Architecture.Arm64 or Architecture.Ppc64le;

    private static int LinuxNoFollowFlag => UsesArmOpenFlags ? 0x8000 : 0x20000;

    private static SafeFileHandle OpenLinuxFile(int directory, string path, int flags, uint mode = 0)
    {
        var descriptor = OpenLinuxFileNative(directory, path, flags, mode);
        if (descriptor < 0)
        {
            throw RenameError(Marshal.GetLastPInvokeError());
        }

        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    private static LinuxFileStatus GetLinuxFileStatus(SafeFileHandle file)
    {
        // AT_EMPTY_PATH: the descriptor itself.
        var error = StatLinuxFile(file.DangerousGetHandle().ToInt32(), string.Empty, 0x1000, out var status);
        return error == 0 ? status : throw RenameError(error);
    }

    /// <returns>0, or the error that prevented identifying the file.</returns>
    private static int StatLinuxFile(int directory, string path, int flags, out LinuxFileStatus status)
    {
        // STATX_TYPE | STATX_MODE | STATX_INO, without which two files cannot be told apart.
        if (StatLinuxFileNative(directory, path, flags, 0x103, out status) != 0)
        {
            return Marshal.GetLastPInvokeError();
        }

        return (status.Mask & 0x103) == 0x103 ? 0 : 61;
    }

    protected virtual int LinkLinux(
        int sourceDirectory,
        string sourceName,
        int targetDirectory,
        string targetName,
        int flags) =>
        LinkLinuxNative(sourceDirectory, sourceName, targetDirectory, targetName, flags);

    protected virtual int RenameLinux(
        int sourceDirectory,
        string sourceName,
        int targetDirectory,
        string targetName) =>
        RenameLinuxNative(sourceDirectory, sourceName, targetDirectory, targetName);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenLinuxFileNative(
        int directory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mode);

    [DllImport("libc", EntryPoint = "linkat", SetLastError = true)]
    private static extern int LinkLinuxNative(
        int sourceDirectory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sourceName,
        int targetDirectory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string targetName,
        int flags);

    [DllImport("libc", EntryPoint = "renameat", SetLastError = true)]
    private static extern int RenameLinuxNative(
        int sourceDirectory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sourceName,
        int targetDirectory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string targetName);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkLinux(
        int directory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int StatLinuxFileNative(
        int directory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        out LinuxFileStatus status);

    /// <summary>The fields of Linux's 256-byte <c>struct statx</c> that identify a file.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFileStatus
    {
        [FieldOffset(0)]
        public uint Mask;

        [FieldOffset(28)]
        public ushort Mode;

        [FieldOffset(32)]
        public ulong Inode;

        [FieldOffset(136)]
        public uint DeviceMajor;

        [FieldOffset(140)]
        public uint DeviceMinor;

        public readonly bool IsSameFile(in LinuxFileStatus other) =>
            Inode == other.Inode && DeviceMajor == other.DeviceMajor && DeviceMinor == other.DeviceMinor;
    }
}
