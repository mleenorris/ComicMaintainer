# API Call Usage Fixes

## Problem Identified
During review of the website code in `templates/index.html`, 19 API calls were found that were calling `response.json()` without first checking `response.ok`. This is incorrect because:

1. **Error Handling**: If the server returns an error status (404, 500, etc.), calling `.json()` without checking the response status can fail or return unexpected data.
2. **Best Practices**: According to Fetch API best practices, you should always check `response.ok` before attempting to parse the response body.
3. **User Experience**: Proper error checking provides better error messages and handling for users.

## Functions Fixed
The following functions were updated to include proper `response.ok` checks:

1. `loadVersion()` - Version display
2. `viewTags()` - View file tags
3. `saveTags()` - Save file tags
4. `processSingleFile()` - Process individual file
5. `renameSingleFile()` - Rename individual file
6. `normalizeSingleFile()` - Normalize individual file
7. `deleteSingleFile()` - Delete individual file
8. `openSettings()` - Load settings (3 API calls fixed)
9. `openAboutModal()` - Load version for about modal
10. `loadLogs()` - Load log files
11. `saveFilenameFormat()` - Save filename format (2 API calls fixed)
12. `resetFilenameFormat()` - Reset filename format to default
13. `updateWatcherStatus()` - Check watcher status
14. `scanUnmarkedFiles()` - Scan for unmarked files
15. `updateWatcherFromSettings()` - Update watcher enabled setting
16. `loadFiles()` - Load file list

## Changes Made

### Before (Incorrect)
```javascript
const response = await fetch('/api/endpoint');
const data = await response.json();  // ❌ No error check
```

### After (Correct)
```javascript
const response = await fetch('/api/endpoint');
if (!response.ok) {
    throw new Error(`HTTP error! status: ${response.status}`);
}
const data = await response.json();  // ✅ Checked response status first
```

## Impact
- **Error Handling**: Better error detection and reporting
- **Debugging**: HTTP status codes are now included in error messages
- **Reliability**: Prevents crashes when API returns error responses
- **Code Quality**: Follows Fetch API best practices

## Statistics
- **Before**: 14 response.ok checks for 40 fetch() calls
- **After**: 33 response.ok checks for 40 fetch() calls
- **Fixed**: 19 missing response.ok checks

## Testing
All fixes follow the same pattern and are wrapped in existing try-catch blocks, so error handling remains consistent with the rest of the codebase.

## Verification
To verify the fixes, you can search the `templates/index.html` file for patterns like:
```javascript
if (!response.ok) {
    throw new Error(`HTTP error! status: ${response.status}`);
}
```

All fetch calls that use `.json()` should now have this check immediately after the fetch.
