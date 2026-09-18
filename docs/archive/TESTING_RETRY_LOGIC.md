# Testing Guide: Batch Job Retry Logic

This guide provides step-by-step instructions for manually testing the improved batch job retry logic.

## Prerequisites

1. Docker installed
2. Some comic files (`.cbz` or `.cbr`) in a test directory
3. Web browser with developer tools (F12)
4. Network simulation tools (optional but helpful)

## Setup

1. **Build and run the container:**
   ```bash
   docker build -t comictagger-watcher .
   docker run -d \
     -v /path/to/comics:/watched_dir \
     -v /path/to/config:/Config \
     -e WATCHED_DIR=/watched_dir \
     -p 5000:5000 \
     --name comic-maintainer \
     comictagger-watcher
   ```

2. **Open browser:** Navigate to `http://localhost:5000`

3. **Open developer tools:** Press F12 and go to Console tab

## Test Cases

### Test 1: Normal Operation (Baseline)

**Purpose:** Verify that normal operation still works correctly.

**Steps:**
1. Click "Process All Files" button
2. Observe progress modal appears
3. Watch console for log messages
4. Wait for job to complete

**Expected Results:**
- ✅ Progress modal shows and updates
- ✅ Console shows: `[JOB xxx] Starting to poll job status`
- ✅ Console shows: `[JOB xxx] Progress: X/Y items processed`
- ✅ Console shows: `[JOB xxx] Job completed: X/Y files succeeded`
- ✅ Success message appears
- ✅ File list refreshes
- ✅ No error messages

**Pass/Fail:** ___________

---

### Test 2: Page Refresh During Active Job

**Purpose:** Verify job resumes after page refresh.

**Steps:**
1. Start "Process All Files"
2. Let it run for 2-3 seconds (some files processed)
3. Press F5 to refresh the page
4. Observe behavior

**Expected Results:**
- ✅ `beforeunload` warning appears: "A batch processing job is still running..."
- ✅ Choose "Leave" to proceed with refresh
- ✅ Page reloads
- ✅ Progress modal automatically reopens
- ✅ Console shows: `[JOB RESUME] Found active job xxx on server`
- ✅ Console shows: `[JOB RESUME] Resuming job xxx`
- ✅ Job continues from where it left off
- ✅ Job completes successfully

**Pass/Fail:** ___________

---

### Test 3: Simulated Server Error (500)

**Purpose:** Verify retry logic works for transient server errors.

**Setup:**
1. Use browser DevTools Network tab
2. Enable "Offline" mode briefly during polling
3. Or use browser extension to throttle/block requests

**Steps:**
1. Start "Process All Files"
2. Enable network throttling/offline mode for 3-5 seconds
3. Disable network throttling/offline mode
4. Observe recovery

**Expected Results:**
- ✅ Console shows: `[JOB xxx] Error during polling: Failed to fetch, retry 1/5`
- ✅ Console shows increasing retry counts (2/5, 3/5, etc.)
- ✅ Exponential backoff visible (longer waits between retries)
- ✅ Once network restored: `[JOB xxx] Progress: X/Y items processed`
- ✅ Job continues and completes successfully
- ✅ No modal closure during retries

**Pass/Fail:** ___________

---

### Test 4: Permanent Network Failure (Max Retries)

**Purpose:** Verify behavior when max retries is exceeded.

**Steps:**
1. Start "Process All Files"
2. Completely disconnect network (disable WiFi, unplug ethernet)
3. Wait for 5 retry attempts (~15 seconds)
4. Observe behavior

**Expected Results:**
- ✅ Console shows 5 retry attempts with increasing delays:
  - `retry 1/5` (wait 500ms)
  - `retry 2/5` (wait 1000ms)
  - `retry 3/5` (wait 2000ms)
  - `retry 4/5` (wait 4000ms)
  - `retry 5/5` (wait 8000ms)
- ✅ Console shows: `[JOB xxx] Giving up after 5 consecutive errors`
- ✅ Error message: "Lost connection to server. Job may still be running. Refresh to check status."
- ✅ Progress modal closes
- ✅ Active job is NOT cleared from server
- ✅ Can refresh to resume once network restored

**Pass/Fail:** ___________

---

### Test 5: Job Recovery After Network Restored

**Purpose:** Verify job can be resumed after network issue.

**Steps:**
1. Start "Process All Files" with many files (50+)
2. Disconnect network after 5-10 files processed
3. Wait for max retries (modal closes)
4. Reconnect network
5. Refresh the page

**Expected Results:**
- ✅ Console shows: `[JOB RESUME] Found active job xxx on server`
- ✅ Console shows: `[JOB RESUME] Resuming job xxx`
- ✅ Progress modal reopens
- ✅ Job continues from where it left off
- ✅ Job completes successfully
- ✅ All files are processed (no duplicates or skips)

**Pass/Fail:** ___________

---

### Test 6: Server Restart During Job

**Purpose:** Verify job survives server restart.

**Steps:**
1. Start "Process All Files" with many files
2. After 10-20 files processed, restart Docker container:
   ```bash
   docker restart comic-maintainer
   ```
3. Wait for server to come back online (~10 seconds)
4. Refresh the browser page

**Expected Results:**
- ✅ Container restarts successfully
- ✅ Job data persists in SQLite database
- ✅ After refresh: `[JOB RESUME] Found active job xxx on server`
- ✅ Progress modal reopens
- ✅ Job continues from where it left off (may re-process last few files)
- ✅ Job completes successfully

**Pass/Fail:** ___________

---

### Test 7: Job Deleted (404 Error)

**Purpose:** Verify proper handling when job no longer exists.

**Steps:**
1. Start "Process All Files"
2. Let it complete
3. Delete job from database:
   ```bash
   docker exec -it comic-maintainer sqlite3 /Config/jobs.db
   DELETE FROM jobs WHERE job_id = '<job_id>';
   .exit
   ```
4. Refresh the page (while active job still tracked)

**Expected Results:**
- ✅ Console shows: `[JOB RESUME] Job xxx not found (404) - was cleaned up`
- ✅ Warning message: "Previous batch processing job is no longer available"
- ✅ Active job is cleared from server
- ✅ No retry attempts (404 is permanent error)
- ✅ Page loads normally

**Pass/Fail:** ___________

---

### Test 8: Multiple Browser Tabs

**Purpose:** Verify job state is shared across tabs.

**Steps:**
1. Open web interface in Tab 1
2. Start "Process All Files" in Tab 1
3. Open same URL in Tab 2
4. Observe both tabs
5. Refresh Tab 2

**Expected Results:**
- ✅ Both tabs show the same active job
- ✅ `beforeunload` warning in Tab 2 when refreshing
- ✅ Tab 2 resumes polling after refresh
- ✅ Both tabs show same progress
- ✅ Job completes in both tabs

**Pass/Fail:** ___________

---

### Test 9: Invalid Response Handling

**Purpose:** Verify handling of malformed API responses.

**Setup:**
This requires modifying backend temporarily to return invalid JSON.

**Expected Results:**
- ✅ Console shows: `[JOB xxx] Invalid job status response, retry 1/5`
- ✅ Retries with exponential backoff
- ✅ After 5 retries: gives up with appropriate message
- ✅ Job not cleared (might still be running)

**Pass/Fail:** ___________

---

## Verification Checklist

After completing all tests, verify:

- [ ] Normal operation still works (no regression)
- [ ] Jobs resume correctly after page refresh
- [ ] Transient errors trigger retry logic
- [ ] Exponential backoff is working (visible in console timings)
- [ ] Max retries (5) is enforced
- [ ] 404 errors are treated as permanent (no retry)
- [ ] 5xx errors are treated as transient (retry)
- [ ] Network errors trigger retry
- [ ] Jobs are NOT cleared when max retries exceeded
- [ ] Jobs CAN be resumed after network restored
- [ ] Server restart doesn't lose job state
- [ ] Multiple tabs share job state correctly
- [ ] Console logs provide clear debugging info
- [ ] User messages are helpful and accurate

## Success Criteria

All tests should pass with:
- ✅ No JavaScript errors in console
- ✅ Jobs complete successfully despite transient errors
- ✅ Proper retry behavior with exponential backoff
- ✅ Appropriate handling of permanent vs transient errors
- ✅ Clear user feedback for all scenarios

## Debugging Tips

### Check Active Job on Server
```bash
docker exec -it comic-maintainer sqlite3 /Config/preferences.db
SELECT * FROM preferences WHERE key = 'active_job_id' OR key = 'active_job_title';
.exit
```

### Check Job Status in Database
```bash
docker exec -it comic-maintainer sqlite3 /Config/jobs.db
SELECT job_id, status, total_items, processed_items FROM jobs ORDER BY created_at DESC LIMIT 5;
.exit
```

### Watch Server Logs
```bash
docker logs -f comic-maintainer
```

### Check Network Requests
- Open DevTools > Network tab
- Filter by "jobs"
- Observe request/response status codes
- Look for retry patterns

## Known Issues

None at this time.

## Reporting Issues

If any test fails, please report:
1. Which test failed
2. Browser and version
3. Console logs (copy all relevant messages)
4. Network tab screenshot
5. Expected vs actual behavior
6. Steps to reproduce

## Notes

- Network simulation tools: Chrome DevTools Network tab, Firefox Network Monitor, or browser extensions
- Some tests may vary slightly based on network speed and server performance
- Exponential backoff timings may not be exact but should be visibly increasing
- Jobs stored in SQLite at `/Config/jobs.db`
- Active job tracked in `/Config/preferences.db`
