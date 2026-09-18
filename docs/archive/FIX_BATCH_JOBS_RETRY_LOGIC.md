# Fix: Robust Error Handling for Batch Job Polling

## Issue
**"Batch jobs are still stopping and not resuming when the webpage is refreshed"**

The problem was that when any error occurred during job polling (network issues, server errors, etc.), the frontend would:
1. Stop polling immediately
2. Close the progress modal
3. Clear the active job from the server
4. Give up entirely

This meant that transient network issues or temporary server problems would cause the job tracking to fail, even though the backend job was still running successfully.

## Root Cause

The original `pollJobStatus()` function had a single try-catch block that caught ALL errors and immediately gave up:

```javascript
try {
    while (true) {
        // Poll for status...
        if (!response.ok) {
            throw new Error(...);  // Any error stops everything
        }
    }
} catch (error) {
    // Give up immediately, clear the job
    closeProgressModal();
    await clearActiveJobOnServer();
    hasActiveJob = false;
}
```

This approach didn't distinguish between:
- **Transient errors** (network issues, 500 errors) that could be retried
- **Permanent errors** (404, job deleted) that require giving up

## Solution

Implemented robust error handling with retry logic and exponential backoff:

### 1. pollJobStatus() Function Changes

**Retry Logic:**
- Moved try-catch INSIDE the while loop
- Track consecutive errors
- Retry up to 5 times with exponential backoff
- Reset error counter on successful fetch

**Error Type Distinction:**
```javascript
if (response.status === 404) {
    // Permanent error - job was deleted
    // Clear and exit
}
else if (response.status >= 500) {
    // Transient error - server problem
    // Retry with backoff
}
else {
    // Other client errors
    // Treat as permanent
}
```

**Exponential Backoff:**
- Retry 1: Wait 500ms
- Retry 2: Wait 1000ms (2^1 * 500ms)
- Retry 3: Wait 2000ms (2^2 * 500ms)
- Retry 4: Wait 4000ms (2^3 * 500ms)
- Retry 5: Wait 8000ms (2^4 * 500ms)
- After 5 failures: Give up but DON'T clear the job

**Key Improvement:**
When giving up after max retries, we DON'T clear the active job from the server:
```javascript
if (consecutiveErrors >= maxConsecutiveErrors) {
    showMessage('Lost connection to server. Job may still be running. Refresh to check status.', 'error');
    closeProgressModal();
    // DON'T clear active job - it might still be running
    hasActiveJob = false;
    break;
}
```

This allows the user to refresh and resume polling if the connection is restored.

### 2. checkAndResumeActiveJob() Function Changes

**Better Error Handling:**
- Distinguish between 404, 5xx, and network errors
- Don't clear job on server errors or network errors
- Only clear on 404 (job not found) or permanent errors

**Before:**
```javascript
if (!response.ok) {
    // Always clear the job, regardless of error type
    await clearActiveJobOnServer();
    return;
}
```

**After:**
```javascript
if (response.status === 404) {
    // Job was deleted - clear it
    await clearActiveJobOnServer();
}
else if (response.status >= 500) {
    // Server error - don't clear, user can retry
    showMessage('Server error. Please refresh to try again.', 'error');
    return;  // Job stays active
}
```

## Benefits

✅ **Resilient to network issues**: Transient network problems don't stop job tracking
✅ **Automatic retry**: Up to 5 retries with exponential backoff
✅ **Smart failure handling**: Don't clear jobs that might still be running
✅ **Better user experience**: Jobs continue in background, progress resumes when connection restored
✅ **Clear error messages**: Users know what happened and what to do
✅ **Graceful degradation**: After max retries, tells user to refresh

## Testing Scenarios

### Scenario 1: Transient Network Error
**Setup:**
1. Start a batch job
2. Temporarily disconnect network (unplug cable, disable WiFi)
3. Wait 2-3 seconds
4. Reconnect network

**Expected Result:**
- Console shows retry attempts with exponential backoff
- Polling resumes automatically when connection restored
- Progress updates continue
- Job completes successfully

### Scenario 2: Server Restart During Job
**Setup:**
1. Start a batch job with many files
2. Restart the Docker container
3. Wait for server to come back online
4. Refresh the page

**Expected Result:**
- Job continues running on server (persistent in SQLite)
- After refresh, job is detected and resumed
- Progress modal reopens
- Polling continues from where it left off

### Scenario 3: Server Returns 500 Error
**Setup:**
1. Start a batch job
2. Simulate server error (modify backend to return 500)
3. Observe retry behavior
4. Fix server
5. Observe recovery

**Expected Result:**
- Console shows: "Server error (500), retry X/5"
- Exponential backoff delays between retries
- Once server recovers, polling resumes
- No data loss

### Scenario 4: Job Deleted (404)
**Setup:**
1. Start a batch job
2. Let it complete
3. Wait for cleanup (or manually delete from DB)
4. Refresh page

**Expected Result:**
- Console shows: "Job not found (404)"
- Active job is cleared from server
- Message: "Previous batch processing job is no longer available"
- No polling attempts

### Scenario 5: Maximum Retries Exceeded
**Setup:**
1. Start a batch job
2. Disconnect network permanently
3. Wait for 5 retry attempts

**Expected Result:**
- Console shows 5 retry attempts with increasing delays
- After 5 failures: "Giving up after 5 consecutive errors"
- Message: "Lost connection to server. Job may still be running. Refresh to check status."
- Active job NOT cleared (can resume with refresh)
- Progress modal closes

### Scenario 6: Page Refresh During Retry
**Setup:**
1. Start a batch job
2. Disconnect network
3. Wait for retry attempts to start
4. Refresh page before max retries reached

**Expected Result:**
- New page load detects active job
- Shows modal and resumes polling
- If network still down, retries continue
- If network restored, polling succeeds

## Implementation Details

### Files Modified
- `templates/index.html` (only file changed)

### Functions Modified
1. `pollJobStatus(jobId, title)` - Added retry logic and exponential backoff
2. `checkAndResumeActiveJob()` - Improved error handling

### Lines Changed
- Added: ~66 lines
- Removed: ~22 lines
- Net change: +44 lines

### No Breaking Changes
- Backward compatible with existing functionality
- No API changes
- No database migrations
- No configuration changes

## Monitoring and Debugging

### Console Logs to Watch

**Successful polling:**
```
[JOB abc123] Starting to poll job status
[JOB abc123] Progress: 10/100 items processed
```

**Transient error with retry:**
```
[JOB abc123] Server error (500), retry 1/5
[JOB abc123] Server error (500), retry 2/5
[JOB abc123] Progress: 10/100 items processed  // Recovered!
```

**Network error with retry:**
```
[JOB abc123] Error during polling: Failed to fetch, retry 1/5
[JOB abc123] Error during polling: Failed to fetch, retry 2/5
```

**Max retries exceeded:**
```
[JOB abc123] Error during polling: Failed to fetch, retry 5/5
[JOB abc123] Giving up after 5 consecutive errors
```

**Job not found:**
```
[JOB abc123] Job not found (404) - may have been cleaned up
```

### User Messages

| Scenario | Message | Type |
|----------|---------|------|
| Connection lost, can retry | "Lost connection to server. Job may still be running. Refresh to check status." | Error |
| Job not found | "Job no longer exists on server" | Warning |
| Server error on resume | "Server error checking job status. Please refresh to try again." | Error |
| Network error on resume | "Could not check job status. Please refresh to try again." | Warning |

## Performance Impact

- **Minimal**: Only adds retry logic when errors occur
- **No impact on success path**: Successful polls work exactly as before
- **Slightly slower on transient errors**: Exponential backoff adds delay, but ensures recovery
- **Better UX overall**: Jobs don't fail unnecessarily

## Deployment

No special deployment steps required:
- No database changes
- No configuration changes
- No server restart needed (unless using Docker)
- Just deploy the updated `index.html` file

## Future Enhancements

Consider these improvements for future versions:

1. **Configurable retry count**: Allow users to set max retries
2. **Retry progress indicator**: Show "Retrying... (3/5)" in UI
3. **Network status detection**: Use browser Network Information API
4. **Offline mode**: Detect offline state and pause polling
5. **Server-sent events**: Replace polling with SSE for real-time updates
6. **WebSocket support**: Bi-directional communication for better reliability

## Related Documentation

- [TESTING_PAGE_REFRESH.md](TESTING_PAGE_REFRESH.md) - Manual testing guide
- [FIX_SUMMARY_JOB_RESUMPTION.md](FIX_SUMMARY_JOB_RESUMPTION.md) - Previous fix
- [ASYNC_PROCESSING.md](ASYNC_PROCESSING.md) - Async job architecture
