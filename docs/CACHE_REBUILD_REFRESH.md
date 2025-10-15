# Cache Rebuild Auto-Refresh Feature

## Summary
Implemented automatic file list refresh when asynchronous cache rebuild completes. Previously, when the cache was rebuilding in the background, users had to manually refresh to see the updated file list.

## Changes Made

### Backend Changes (web_app.py)
- Modified `/api/files` endpoint to include `cache_rebuilding` flag in response
- The flag reflects the current state of `enriched_file_cache['rebuild_in_progress']`

### Frontend Changes (templates/index.html)
- Added cache rebuild polling mechanism that:
  1. Detects when cache rebuild starts
  2. Polls every 2 seconds to check if rebuild is complete
  3. Automatically refreshes file list when rebuild completes
  4. Cleans up polling timer on page unload

## How It Works

1. **Cache Rebuild Detection**: When `/api/files` is called and a cache rebuild is in progress, the response includes `cache_rebuilding: true`

2. **Polling Activation**: Frontend detects the rebuild flag and starts polling every 2 seconds

3. **Completion Detection**: When polling detects `cache_rebuilding: false`, it automatically calls `loadFiles()` to refresh the list

4. **Cleanup**: Polling timer is cleared when rebuild completes or when user navigates away

## User Experience Improvements

- **Automatic Updates**: Users no longer need to manually refresh after cache rebuilds
- **Seamless Experience**: File list updates automatically when new files are processed
- **No Interruption**: Polling happens in background without blocking UI interactions
- **Resource Efficient**: Polling only active during cache rebuilds (typically 2-5 seconds for large libraries)

## Testing Scenarios

1. **Manual Refresh**: Click Refresh button → cache rebuilds → list auto-updates
2. **New Files**: Watcher processes files → cache rebuilds on next request → list auto-updates
3. **Page Navigation**: Navigate during rebuild → polling continues → updates on current page
4. **Page Unload**: Close/navigate away → polling timer properly cleaned up

## Performance Impact

- Minimal: Polling only occurs during cache rebuilds (rare events)
- 2-second polling interval is sufficient for typical rebuild times
- No impact on normal file list requests
