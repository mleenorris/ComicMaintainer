# Fix: Ignore Temporary Files in FileWatcherService

## Problem
The application was constantly trying to process temporary files, causing excessive logging and unnecessary processing attempts. The issue is visible in the logs:

```
[WRN] ProcessFileAsync: File not found: .tmp_a9f723d9-9231-4a6e-8737-cc8aceb2a4d6.cbz
[INFO] ProcessFileAsync: Starting processing for file: .tmp_a9f723d9-9231-4a6e-8737-cc8aceb2a4d6.cbz
[WRN] ProcessFileAsync: File not found: .tmp_b0a63162-fbc2-44c1-9c92-5dda06a97515.cbz
[INFO] ProcessFileAsync: Starting processing for file: .tmp_b0a63162-fbc2-44c1-9c92-5dda06a97515.cbz
```

These temporary files (starting with `.tmp_` or ending with `.tmp`) are created by the OS or applications during file operations (move, copy, etc.) and should be ignored by the watcher.

## Root Cause
The `FileWatcherService` was configured to watch all files with the filter `*.*`, and only checked if files were comic files AFTER the file system events were raised. This meant:

1. Every file change triggered an event
2. Temporary files triggered `Created`, `Changed`, `Renamed`, and `Deleted` events
3. Each event logged and attempted processing before checking the file type
4. This caused constant spam in logs and wasted system resources

## Solution
Added an `IsTemporaryFile()` method and early exit checks in all file event handlers to ignore temporary files immediately:

### Changes Made

1. **Added `IsTemporaryFile()` method** in `FileWatcherService.cs`:
   ```csharp
   /// <summary>
   /// Check if a file is a temporary file that should be ignored
   /// </summary>
   private static bool IsTemporaryFile(string path)
   {
       var fileName = Path.GetFileName(path);
       
       // Ignore files starting with .tmp_ (common temporary file pattern)
       if (fileName.StartsWith(".tmp_", StringComparison.OrdinalIgnoreCase))
       {
           return true;
       }
       
       // Ignore files with .tmp extension
       if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
       {
           return true;
       }
       
       return false;
   }
   ```

2. **Added early exit checks** in all event handlers:
   - `OnFileCreated()`: Returns immediately if file is temporary
   - `OnFileChanged()`: Returns immediately if file is temporary  
   - `OnFileRenamed()`: Returns immediately if target file is temporary (allows renames FROM temp files TO comic files)
   - `OnFileDeleted()`: Returns immediately if file is temporary

### Patterns Detected

The fix detects and ignores:
- Files starting with `.tmp_` (e.g., `.tmp_a9f723d9-9231-4a6e-8737-cc8aceb2a4d6.cbz`)
- Files with `.tmp` extension (e.g., `myfile.tmp`)

### Test Coverage

Added 5 new tests to verify the fix:
1. `OnFileCreated_IgnoresTemporaryFilesStartingWithTmpPrefix()` - Verifies .tmp_ files are ignored
2. `OnFileCreated_IgnoresFilesWithTmpExtension()` - Verifies .tmp files are ignored
3. `OnFileChanged_IgnoresTemporaryFiles()` - Verifies changes to temp files are ignored
4. `OnFileDeleted_IgnoresTemporaryFiles()` - Verifies deletions of temp files are ignored
5. `OnFileRenamed_IgnoresWhenTargetIsTemporaryFile()` - Verifies renames to temp files are ignored

All tests pass (18/18 FileWatcherService tests).

## Benefits

1. ✅ **Eliminates log spam** - No more warnings about temporary files not found
2. ✅ **Reduces system load** - No unnecessary processing attempts for temporary files
3. ✅ **Cleaner logs** - Easier to identify actual issues
4. ✅ **Better performance** - Less overhead from file system events
5. ✅ **Minimal code change** - Only 50 lines added, no breaking changes

## Impact

- **Files Changed**: 2
  - `src/ComicMaintainer.Core/Services/FileWatcherService.cs`
  - `tests/ComicMaintainer.Tests/Services/FileWatcherServiceTests.cs`
- **Lines Added**: ~198 (including tests and documentation)
- **Breaking Changes**: None
- **Test Coverage**: 5 new tests, all passing

## Alternative Solutions Considered

1. **Change FileSystemWatcher filter** - Not viable because it only supports a single filter pattern (e.g., `*.cbz`), and we need to watch multiple extensions
2. **Use multiple FileSystemWatcher instances** - Would work but adds complexity and overhead
3. **Filter after IsComicFile check** - Current approach is better because it prevents any processing for temp files

The chosen solution is the most efficient as it exits immediately before any logging or processing occurs.
