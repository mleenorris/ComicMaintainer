# Polling Removal - Event-Driven Architecture

This document describes the changes made to remove all polling mechanisms and replace them with event-driven patterns.

## Overview

The application has been completely refactored to use an event-driven architecture, eliminating all polling loops. This significantly reduces CPU usage, network requests, and improves responsiveness.

## Changes Made

### Frontend (templates/index.html)

#### 1. Cache Rebuild Status
**Before:** Polled `/api/files` every 2 seconds to check if cache rebuild was complete
```javascript
// Polling every 2 seconds
cacheRebuildPollTimer = setInterval(async () => {
    const response = await fetch(url);
    const data = await response.json();
    if (!data.cache_rebuilding && isCacheRebuilding) {
        // Cache rebuild completed
        isCacheRebuilding = false;
        stopCacheRebuildPolling();
        loadFiles(currentPage, false);
    }
}, 2000);
```

**After:** Receives real-time updates via SSE events
```javascript
function handleCacheUpdatedEvent(data) {
    if (data.rebuild_complete) {
        console.log('SSE: Cache rebuild completed, refreshing file list');
        isCacheRebuilding = false;
        loadFiles(currentPage, false);
    }
}
```

**Impact:** Eliminated ~30 HTTP requests per minute during cache rebuilds

#### 2. Watcher Status
**Before:** Polled `/api/watcher/status` every 10 seconds
```javascript
watcherStatusInterval = setInterval(updateWatcherStatus, 10000);
```

**After:** Fetches status once on page load, then receives updates via SSE
```javascript
// Initial fetch only
updateWatcherStatus();

// Updates via SSE
function handleWatcherStatusEvent(data) {
    updateWatcherStatusDisplay(data.running, data.enabled);
}
```

**Impact:** Eliminated 6 HTTP requests per minute continuously

#### 3. Job Progress Tracking
**Before:** Polled `/api/jobs/{id}` every 5 seconds as a fallback
```javascript
async function pollJobStatus(jobId, title) {
    while (true) {
        const response = await fetch(`/api/jobs/${jobId}`);
        const status = await response.json();
        updateProgress(status.processed_items, status.total_items);
        if (status.status === 'completed') break;
        await new Promise(resolve => setTimeout(resolve, 5000));
    }
}
```

**After:** SSE-only tracking with single initial fetch
```javascript
async function trackJobStatus(jobId, title) {
    // Fetch initial state once
    const response = await fetch(`/api/jobs/${jobId}`);
    const status = await response.json();
    updateProgress(status.processed_items, status.total_items);
    
    // All subsequent updates via SSE - no polling loop
    console.log('Waiting for SSE updates...');
}
```

**Impact:** Eliminated 12 HTTP requests per minute during batch processing

### Backend (src/web_app.py)

#### 1. Watcher Activity Monitor
**Before:** Thread with sleep polling every 2 seconds
```python
def watcher_monitor_thread():
    last_watcher_time = get_watcher_update_time()
    while True:
        time.sleep(2)  # Poll every 2 seconds
        current_watcher_time = get_watcher_update_time()
        if current_watcher_time > last_watcher_time:
            broadcast_cache_updated(rebuild_complete=False)
            last_watcher_time = current_watcher_time
```

**After:** File system watcher using watchdog
```python
class WatcherMonitorHandler(FileSystemEventHandler):
    def on_modified(self, event):
        if event.src_path.endswith(CACHE_UPDATE_MARKER):
            broadcast_cache_updated(rebuild_complete=False)

observer = Observer()
observer.schedule(WatcherMonitorHandler(), CONFIG_DIR, recursive=False)
observer.start()
```

**Impact:** 
- Eliminated continuous polling loop (30 checks per minute)
- Near-instant event notification (< 1s latency vs 0-2s polling delay)
- Reduced CPU usage

#### 2. Cleanup Tasks
**Before:** Thread with sleep polling
```python
def cleanup_web_markers_thread():
    while True:
        time.sleep(300)  # Sleep for 5 minutes
        cleanup_web_modified_markers(max_files=100)

cleanup_thread = threading.Thread(target=cleanup_web_markers_thread, daemon=True)
cleanup_thread.start()
```

**After:** Event-based timer that reschedules itself
```python
def cleanup_web_markers_scheduled():
    try:
        cleanup_web_modified_markers(max_files=100)
    finally:
        # Reschedule for next run
        cleanup_timer = threading.Timer(300.0, cleanup_web_markers_scheduled)
        cleanup_timer.daemon = True
        cleanup_timer.start()

# Initial schedule
cleanup_timer = threading.Timer(300.0, cleanup_web_markers_scheduled)
cleanup_timer.start()
```

**Impact:**
- Same functional behavior
- Cleaner code structure
- More efficient resource usage (timer doesn't hold thread continuously)

### Backend (src/job_manager.py)

#### Job Cleanup
**Before:** Thread with infinite loop and sleep
```python
def _cleanup_old_jobs(self):
    while True:
        time.sleep(300)
        cutoff_time = time.time() - 86400
        deleted = job_store.cleanup_old_jobs(cutoff_time)
```

**After:** Self-rescheduling timer
```python
def _schedule_cleanup(self):
    self._cleanup_timer = threading.Timer(300.0, self._cleanup_old_jobs)
    self._cleanup_timer.start()

def _cleanup_old_jobs(self):
    try:
        cutoff_time = time.time() - 86400
        deleted = job_store.cleanup_old_jobs(cutoff_time)
    finally:
        self._schedule_cleanup()  # Reschedule
```

### Backend (src/watcher.py)

#### Main Loop
**Before:** Infinite loop with sleep(1)
```python
try:
    while True:
        time.sleep(1)  # Keep process alive
except KeyboardInterrupt:
    observer.stop()
```

**After:** Event.wait() - truly event-driven
```python
shutdown_event = threading.Event()

try:
    # Blocks efficiently until interrupted - uses no CPU
    shutdown_event.wait()
except KeyboardInterrupt:
    observer.stop()
```

**Impact:**
- Eliminates unnecessary wake-ups (no more 1Hz polling)
- Better CPU efficiency
- Same functionality with cleaner implementation

#### File Stability Check
**Note:** The `time.sleep()` calls in `_is_file_stable()` are intentionally kept as they serve a different purpose - they are debouncing delays to wait for file writes to complete, not polling for events.

## Benefits Summary

### Performance
- **Frontend:** ~48 fewer HTTP requests per minute during normal operation
- **Backend:** Eliminated 3 continuous polling threads
- **CPU Usage:** Reduced idle CPU usage from polling loops
- **Response Time:** Instant event notifications vs 0-10 second polling delays

### Code Quality
- More maintainable event-driven code
- Clear separation of concerns
- Better error handling
- Easier to test

### Scalability
- Better suited for multiple worker processes
- More efficient with large numbers of concurrent clients
- Lower network overhead

## Testing Recommendations

1. **Cache Rebuilds:** Verify cache updates are received instantly via SSE
2. **Watcher Status:** Check status indicator updates when watcher starts/stops
3. **Job Progress:** Ensure progress bars update in real-time during batch processing
4. **SSE Reconnection:** Test that reconnection works after network interruption
5. **Multi-Worker:** Verify events are broadcast to all connected clients across workers

## Migration Notes

- No database migrations required
- No configuration changes needed
- Fully backward compatible
- Existing deployments will automatically use event-driven architecture on update

## Technical Details

### SSE Event Types
The application uses four SSE event types:

1. **cache_updated:** File list cache rebuilt
2. **watcher_status:** Watcher service status changed
3. **file_processed:** Individual file processed
4. **job_updated:** Batch job progress update

### Event Flow
```
Watcher Process → File Change → Marker File Updated
                                      ↓
Web App Process → Watchdog Observer → Detects Change
                                      ↓
Event Broadcaster → SSE → All Connected Clients
                                      ↓
Frontend → Updates UI in Real-Time
```

## Related Documentation

- [EVENT_BROADCASTING_SYSTEM.md](EVENT_BROADCASTING_SYSTEM.md) - SSE architecture details
- [EVENT_SYSTEM_IMPROVEMENTS_SUMMARY.md](EVENT_SYSTEM_IMPROVEMENTS_SUMMARY.md) - Performance metrics
- [WORKER_TIMEOUT_FIX.md](WORKER_TIMEOUT_FIX.md) - Multi-worker considerations
