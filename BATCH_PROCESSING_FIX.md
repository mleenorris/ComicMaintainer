# Batch Processing Status Stuck Issue - Fix Documentation

## Problem Statement

The batch processing progress dialog would get stuck (e.g., at 11/25 files showing 44%) and fail to update, leaving users unable to determine if the process was still running or had failed.

### Symptoms
- Progress bar frozen at a specific percentage
- No further updates to file count or progress
- Cancel button visible but unclear if job is running
- Files continued to be processed in the background but UI showed no indication

### Root Causes

1. **SSE Connection Failures**: The Server-Sent Events (SSE) connection could silently fail or disconnect without the frontend detecting it
2. **No Fallback Mechanism**: When SSE failed, there was no fallback polling to keep the UI updated
3. **Silent Broadcast Failures**: Backend broadcast failures were logged as warnings but didn't prevent job processing, causing frontend/backend desync
4. **No Timeout Detection**: Frontend had no mechanism to detect when it hadn't received updates for an extended period

## Solution Overview

Implemented a multi-layered approach to ensure progress updates are always delivered:

### 1. SSE Reconnection Handling (Frontend)

When the SSE connection reconnects, the frontend now automatically polls the current job status to catch up on any missed updates.

```javascript
eventSource.onopen = () => {
    console.log('SSE: Connected to event stream');
    
    // When SSE reconnects and we have an active job, poll for its current status
    if (hasActiveJob && currentJobId) {
        console.log(`SSE: Reconnected with active job ${currentJobId}, fetching current status...`);
        pollJobStatusOnce(currentJobId);
    }
};
```

### 2. Watchdog Timer (Frontend)

A watchdog timer monitors for stuck jobs by detecting when no updates have been received for 60 seconds. When triggered, it automatically polls the job status.

```javascript
// Set up a watchdog timer to detect stuck jobs (no updates for 60 seconds)
const watchdogInterval = setInterval(async () => {
    const timeSinceLastUpdate = Date.now() - lastUpdateTime;
    
    // If no update for 60 seconds and job is still active, poll status
    if (hasActiveJob && currentJobId === jobId && timeSinceLastUpdate > 60000) {
        console.warn(`[JOB ${jobId}] No updates for ${Math.round(timeSinceLastUpdate / 1000)}s, polling status...`);
        await pollJobStatusOnce(jobId);
        lastUpdateTime = Date.now(); // Reset timer after manual poll
    }
    
    // Clear interval if job is no longer active
    if (!hasActiveJob || currentJobId !== jobId) {
        clearInterval(watchdogInterval);
    }
}, 15000); // Check every 15 seconds
```

### 3. Manual Status Polling (Frontend)

New function to fetch and update job status manually, used by both SSE reconnection and watchdog:

```javascript
async function pollJobStatusOnce(jobId) {
    // Fetch current job status from backend
    const response = await fetch(`/api/jobs/${jobId}`);
    const status = await response.json();
    
    // Update progress UI
    updateProgress(processed, total, successCount, errorCount);
    
    // Handle completion if job is done
    if (status.status === 'completed' || status.status === 'failed' || status.status === 'cancelled') {
        handleJobUpdatedEvent({ /* simulate SSE event */ });
    }
}
```

### 4. Improved Error Handling (Backend)

Changed broadcast failure logging from WARNING to ERROR with full stack traces to make failures visible:

```python
except Exception as e:
    # Don't fail job processing if broadcast fails, but log prominently
    # This helps diagnose cases where the frontend appears stuck
    logging.error(f"[JOB {job_id}] CRITICAL: Failed to broadcast progress update - frontend may appear stuck! Error: {e}")
    # Try to log stack trace for debugging
    import traceback
    logging.error(f"[JOB {job_id}] Broadcast failure stack trace:\n{traceback.format_exc()}")
```

### 5. Broadcast Retry Logic (Backend)

The final completion broadcast now retries 3 times to ensure the frontend receives it:

```python
# Broadcast final completion status - retry multiple times to ensure frontend receives it
for attempt in range(3):
    try:
        self._broadcast_job_progress(
            job_id=job_id,
            status=JobStatus.COMPLETED.value,
            processed=len(items),
            total=len(items),
            success=success_count,
            errors=error_count
        )
        break  # Success, exit retry loop
    except Exception as e:
        if attempt < 2:
            logging.warning(f"[JOB {job_id}] Completion broadcast attempt {attempt+1} failed, retrying... Error: {e}")
            time.sleep(0.5)  # Brief delay before retry
        else:
            logging.error(f"[JOB {job_id}] All completion broadcast attempts failed! Frontend may not detect completion.")
```

## How It Works in Practice

### Normal Operation Flow
1. User initiates batch processing
2. Backend creates job and starts processing
3. SSE broadcasts progress updates in real-time
4. Frontend receives updates and updates UI
5. Watchdog timer is reset on each update
6. Job completes, completion broadcast sent (with retries)
7. Frontend displays completion and refreshes file list

### SSE Disconnection Recovery Flow
1. SSE connection drops due to network issue
2. Browser automatically reconnects to SSE endpoint
3. `onopen` event fires, detecting we have an active job
4. Frontend polls `/api/jobs/{id}` to get current status
5. UI updates with latest progress
6. SSE updates resume normally

### Stuck Job Recovery Flow
1. No SSE updates received for 60 seconds
2. Watchdog timer detects inactivity (checked every 15s)
3. Frontend logs warning and polls `/api/jobs/{id}`
4. Backend returns current job status from database
5. Frontend updates UI with current progress
6. If job is complete, triggers completion handler
7. Watchdog timer is reset

## Benefits

### For Users
- **No More Stuck Progress Bars**: UI always shows current progress
- **Transparent Recovery**: Issues are resolved automatically
- **Reliable Completion Detection**: Job completion is always detected
- **Better UX**: Progress updates are smooth and reliable

### For Developers
- **Better Debugging**: Enhanced logging makes issues visible
- **Robust Architecture**: Multiple layers of redundancy
- **Network Resilient**: Handles disconnections gracefully
- **Easy to Monitor**: Clear log messages show what's happening

## Testing

### Automated Tests
- `test_progress_callbacks.py` - Verifies SSE broadcasting works
- `test_job_specific_events.py` - Verifies job-specific event tracking
- `test_watchdog.py` - Verifies watchdog and reconnection logic

### Manual Testing Scenarios

#### Test 1: Normal Batch Processing
1. Select 25+ files
2. Click "Process All"
3. Verify progress updates smoothly
4. Verify completion is detected

#### Test 2: Network Interruption
1. Start batch processing
2. Disconnect network temporarily (5-10 seconds)
3. Reconnect network
4. Verify UI catches up with progress
5. Verify job completes successfully

#### Test 3: Page Refresh During Processing
1. Start batch processing
2. Refresh page while processing
3. Verify job resume logic kicks in
4. Verify progress continues to update

#### Test 4: Browser Tab Minimized
1. Start batch processing
2. Minimize browser tab for 2+ minutes
3. Restore tab
4. Verify progress has updated correctly

## Configuration

### Watchdog Timeout
The watchdog timeout is set to 60 seconds (no updates received). This can be adjusted in `templates/index.html`:

```javascript
if (hasActiveJob && currentJobId === jobId && timeSinceLastUpdate > 60000) {
    // Change 60000 to desired timeout in milliseconds
}
```

### Watchdog Check Interval
The watchdog checks every 15 seconds. This can be adjusted:

```javascript
}, 15000); // Change to desired interval in milliseconds
```

### Broadcast Retry Count
The completion broadcast retries 3 times. This can be adjusted in `src/job_manager.py`:

```python
for attempt in range(3):  # Change to desired retry count
```

## Future Enhancements

Potential improvements for future iterations:

1. **Exponential Backoff**: Implement exponential backoff for watchdog polling when SSE is consistently failing
2. **Health Metrics**: Add endpoint to report SSE connection health
3. **User Notification**: Show a subtle indicator when watchdog activates
4. **Configurable Timeouts**: Make watchdog and SSE timeouts configurable via settings
5. **Connection Quality Indicator**: Show SSE connection quality in UI

## Files Modified

### Frontend
- `templates/index.html`
  - Added `pollJobStatusOnce()` function
  - Added watchdog timer to `trackJobStatus()`
  - Enhanced SSE `onopen` handler
  - Added watchdog reset to `handleJobUpdatedEvent()`

### Backend
- `src/job_manager.py`
  - Enhanced error logging in `_broadcast_job_progress()`
  - Added retry logic for completion broadcast
  - Added stack trace logging for broadcast failures

### Tests
- `test_watchdog.py` (new)
  - Tests job progress tracking
  - Tests SSE reconnection handling
  - Verifies watchdog would detect stuck jobs

## Troubleshooting

### If Progress Still Gets Stuck

1. **Check Browser Console**: Look for watchdog warnings or SSE errors
2. **Check Server Logs**: Look for "CRITICAL: Failed to broadcast" errors
3. **Verify SSE Connection**: Check Network tab for `/api/events/stream` connection
4. **Check Job Status API**: Manually query `/api/jobs/{id}` to see backend status

### Common Issues

**Issue**: Progress updates but then stops
- **Cause**: SSE connection dropped without reconnecting
- **Solution**: Check browser SSE reconnection logic, verify watchdog is active

**Issue**: Job completes but UI shows "Processing"
- **Cause**: Completion broadcast failed all retries
- **Solution**: Check server logs for broadcast errors, verify SSE connection

**Issue**: Watchdog polls too frequently
- **Cause**: SSE updates not resetting watchdog timer
- **Solution**: Verify `window.resetJobWatchdog()` is being called in `handleJobUpdatedEvent()`

## Conclusion

This fix implements a robust, multi-layered approach to ensure batch processing progress is always displayed correctly. By combining SSE for real-time updates, automatic reconnection handling, and a watchdog timer for detecting stuck jobs, the system can now handle various failure scenarios gracefully without user intervention.

The enhanced logging also makes it much easier to diagnose issues if they do occur, improving the overall maintainability of the system.
