# Testing Guide: Page Refresh Protection for Batch Jobs

This document provides a manual testing guide to verify that batch jobs are properly protected from accidental page refreshes.

## Issue Being Fixed

**Problem:** When users refresh the page during an active batch processing job, the frontend polling connection is lost, even though the backend job continues running.

**Solution:** Added a `beforeunload` event handler that warns users before navigating away from a page with an active batch job. Jobs automatically resume when the user returns.

## Prerequisites

1. Docker container running with ComicMaintainer
2. Some comic files (`.cbz` or `.cbr`) in the watched directory
3. Web browser with JavaScript console open (F12)

## Test Scenarios

### Test 1: Warning Appears During Active Job

**Steps:**
1. Open the web interface at `http://localhost:5000`
2. Click "Process All" or "Process Selected" button
3. Observe the progress modal appears and job starts
4. Immediately try to refresh the page (Ctrl+R or F5)

**Expected Result:**
- Browser shows a warning dialog: "A batch processing job is still running..."
- Dialog offers "Leave" and "Stay" options
- Console shows: `[JOB <job_id>] Starting to poll job status`
- Active job is stored on server in `/Config/preferences.db`

**Actual Result:** ✅ / ❌ (fill in after testing)

---

### Test 2: User Cancels Refresh (Stays on Page)

**Steps:**
1. Start a batch processing job
2. Try to refresh the page
3. Click "Cancel" or "Stay on Page" in the browser warning

**Expected Result:**
- Page does NOT refresh
- Progress modal continues showing
- Job continues processing normally
- Progress updates continue to appear

**Actual Result:** ✅ / ❌ (fill in after testing)

---

### Test 3: User Proceeds with Refresh (Job Resumes)

**Steps:**
1. Start a batch processing job
2. Try to refresh the page
3. Click "Leave" or "Refresh" in the browser warning
4. Wait for page to reload

**Expected Result:**
- Page refreshes
- Progress modal automatically reopens
- Console shows: `[JOB RESUME] Found active job <job_id> on server`
- Console shows: `[JOB RESUME] Resuming job <job_id>`
- Job continues from where it left off
- Progress updates resume

**Actual Result:** ✅ / ❌ (fill in after testing)

---

### Test 4: No Warning After Job Completes

**Steps:**
1. Start a batch processing job
2. Wait for job to complete successfully
3. Observe "Completed" message appears
4. Try to refresh the page

**Expected Result:**
- No warning dialog appears
- Page refreshes normally
- No active job found on server
- Console shows: `[JOB RESUME] No active job found on server`

**Actual Result:** ✅ / ❌ (fill in after testing)

---

### Test 5: Job Completed While User Was Away

**Steps:**
1. Start a batch processing job with many files
2. Try to refresh the page and proceed
3. Wait on the refreshed page until job completes
4. Refresh page again

**Expected Result:**
- On first refresh: Job resumes and continues
- On second refresh (after completion): No warning, job results shown
- Console shows: `[JOB RESUME] Job <job_id> already completed`
- File list is refreshed to show processed files

**Actual Result:** ✅ / ❌ (fill in after testing)

---

### Test 6: Multiple Browser Tabs

**Steps:**
1. Open web interface in Tab 1
2. Start a batch processing job in Tab 1
3. Open same URL in Tab 2
4. Try to refresh Tab 2

**Expected Result:**
- Warning appears in both tabs (they share server state)
- Job can be monitored from either tab
- Refreshing either tab resumes polling
- Both tabs see the same active job from server

**Actual Result:** ✅ / ❌ (fill in after testing)

---

## Console Logging

During testing, check the browser console for these log messages:

**Job Start:**
```
[BATCH] Request to process X files (async)
[BATCH] Found X files to process
[API] Created and started job <job_id> for X files
[JOB <job_id>] Starting to poll job status
```

**Job Progress:**
```
[JOB <job_id>] Progress: X/Y items processed
[BATCH] Processed file: <filename>
```

**Job Completion:**
```
[JOB <job_id>] Job completed: X/Y files succeeded
```

**Job Resumption After Refresh:**
```
[JOB RESUME] Found active job <job_id> on server
[JOB RESUME] Job <job_id> status: processing, X/Y items processed
[JOB RESUME] Resuming job <job_id>
```

**No Active Job:**
```
[JOB RESUME] No active job found on server
```

## Edge Cases

### Edge Case 1: Server Restart During Job
**Scenario:** Server restarts while job is running

**Expected:** Job persists in SQLite database, automatically resumes when server comes back online

### Edge Case 2: Browser Crash
**Scenario:** Browser crashes during job execution

**Expected:** Job continues in backend, active job tracked on server, resumes when browser reopens and navigates to the page

### Edge Case 3: Network Interruption
**Scenario:** Network connection drops during polling

**Expected:** Polling errors logged, server keeps job ID in preferences.db, resumes when connection restored

## Verification Checklist

- [ ] Warning dialog appears when refreshing during active job
- [ ] Warning does NOT appear when no job is active
- [ ] User can cancel refresh and stay on page
- [ ] User can proceed with refresh and job auto-resumes
- [ ] Job state persists across page refreshes
- [ ] Multiple tabs share the same job state
- [ ] Console logging shows proper state transitions
- [ ] Jobs complete successfully despite refreshes

## Notes

- The warning is a browser feature and may look different across browsers
- The exact wording of buttons ("Leave", "Stay", "Cancel") varies by browser
- Active job state is stored server-side at `/Config/preferences.db`, so it works across all browsers and devices
- Jobs are stored in SQLite at `/Config/jobs.db`
- User preferences (theme, perPage) are also stored in `/Config/preferences.db`
