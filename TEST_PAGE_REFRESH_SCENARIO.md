# Test Scenario: Page Refresh During Batch Job

This document provides step-by-step instructions to manually test the page refresh fix.

## Prerequisites

1. Docker container running with ComicMaintainer
2. At least 10-20 comic files (`.cbz` or `.cbr`) in the watched directory
3. Some files marked as processed, some unmarked (for testing "Process Unmarked")
4. Chrome browser (mobile or desktop)
5. Browser developer tools open (F12) to view console logs

## Test Case 1: Process Unmarked Files with Page Refresh

### Setup
```bash
# Mark some files as processed, leave others unmarked
# This creates a mix for testing
```

### Steps
1. Open web interface at `http://localhost:5000`
2. Open browser developer console (F12)
3. Click on "Process Unmarked" button in the toolbar
4. Confirm the action
5. Observe:
   - Progress modal appears showing "Processing Unmarked Files..."
   - Console shows: `[BATCH] Created job <job_id> for X unmarked files`
   - Progress updates appear in the modal
6. **Wait 2-3 seconds** (let some files process)
7. **Refresh the page** (Ctrl+R or F5)
8. Observe:
   - Browser may show a warning about leaving the page
   - Click "Leave" or confirm the refresh
9. Wait for page to reload
10. Observe:
    - Progress modal automatically reappears
    - Console shows: `[JOB RESUME] Found active job <job_id> on server`
    - Console shows: `[JOB RESUME] Resuming job <job_id>`
    - Progress continues from where it left off
    - Files continue being processed

### Expected Console Output

**Before Refresh:**
```
[BATCH] Starting process unmarked files request...
[BATCH] Created job abc-123-def for 10 unmarked files
[JOB abc-123-def] Starting to poll job status: Processing Unmarked Files...
```

**After Refresh:**
```
[JOB RESUME] Found active job abc-123-def on server
[JOB RESUME] Job abc-123-def status: processing, 5/10 items processed
[JOB RESUME] Resuming job abc-123-def
[JOB abc-123-def] Starting to poll job status: Processing Unmarked Files...
```

**On Completion:**
```
[JOB abc-123-def] Job completed: 10/10 files succeeded
Processed 10 of 10 files successfully!
```

### Expected Server Logs

Check Docker logs:
```bash
docker logs <container-name> --tail 50
```

**Before Refresh:**
```
[API] Request to process unmarked files (async)
[API] Found 10 unmarked files to process
[API] Set active job abc-123-def on server
[API] Created and started job abc-123-def for 10 unmarked files
[JOB abc-123-def] Starting processing of 10 items with 4 workers
[BATCH] Processed unmarked file: /Comics/file1.cbz -> /Comics/Series #001.cbz
[BATCH] Processed unmarked file: /Comics/file2.cbz -> /Comics/Series #002.cbz
[JOB abc-123-def] Progress: 2/10 items processed (2 success, 0 errors)
```

**During Refresh (Important - logs should continue!):**
```
[BATCH] Processed unmarked file: /Comics/file3.cbz -> /Comics/Series #003.cbz
[BATCH] Processed unmarked file: /Comics/file4.cbz -> /Comics/Series #004.cbz
[JOB abc-123-def] Progress: 4/10 items processed (4 success, 0 errors)
```

**After Resume:**
```
# Frontend resumes polling, no new job created
[BATCH] Processed unmarked file: /Comics/file5.cbz -> /Comics/Series #005.cbz
...
[JOB abc-123-def] Progress: 10/10 items processed (10 success, 0 errors)
[JOB abc-123-def] Completed: 10 succeeded, 0 failed out of 10 items
[JOB abc-123-def] Cleared active job from preferences (job completed/failed)
```

### Success Criteria

✅ **PASS** if:
- Server logs show continuous processing during page refresh
- Job completes successfully
- All unmarked files are processed
- Progress modal resumes after page reload
- No duplicate processing of files

❌ **FAIL** if:
- Server logs stop during page refresh
- Job appears "lost" after refresh
- Files are processed twice
- Progress modal doesn't reappear after reload

## Test Case 2: Process All Files with Immediate Refresh

This tests the race condition fix where page refreshes immediately after starting the job.

### Steps
1. Open web interface
2. Click "Process All" button
3. **IMMEDIATELY refresh the page** (within 1 second)
4. Observe job resumes correctly

### Expected Behavior
- Active job is set on server **before** job_id is returned to frontend
- Even with immediate refresh, job is tracked and resumes
- Console shows: `[JOB RESUME] Found active job <job_id> on server`

## Test Case 3: Multiple Browser Tabs

### Steps
1. Open web interface in Tab 1
2. Start "Process Unmarked" in Tab 1
3. Open web interface in Tab 2 (new tab, same URL)
4. Observe both tabs show the same active job
5. Refresh Tab 2
6. Observe Tab 2 resumes the job
7. Wait for job to complete
8. Observe both tabs show completion message

### Expected Behavior
- Both tabs see the same job (shared server state)
- Either tab can be refreshed and resume
- Job completes successfully regardless of which tab is active

## Test Case 4: Browser Crash Simulation

### Steps
1. Start "Process Unmarked Files"
2. Close the entire browser (not just the tab)
3. Wait 10-20 seconds
4. Reopen browser
5. Navigate to web interface
6. Observe job status

### Expected Behavior
- Job continues running on server (check Docker logs)
- When page loads, progress modal appears automatically
- Console shows: `[JOB RESUME] Found active job <job_id> on server`
- Job resumes from current progress

## Test Case 5: Job Completion While Away

### Steps
1. Start "Process Unmarked Files" with 5-10 files
2. Immediately refresh the page
3. Wait for job to complete on server
4. Refresh page again after completion
5. Observe completion message

### Expected Behavior
- Console shows: `[JOB RESUME] Job <job_id> already completed`
- Message shows: "Batch processing completed: X of Y files processed successfully"
- File list is refreshed to show processed files
- Active job is cleared from server

## Test Case 6: Network Interruption Simulation

### Steps
1. Start "Process Unmarked Files"
2. Open browser Network tab
3. While job is running, enable "Offline" mode in Network tab
4. Wait 5-10 seconds
5. Disable "Offline" mode
6. Observe polling resumes

### Expected Behavior
- Console shows retry attempts with exponential backoff
- Console shows: `[JOB <job_id>] Error during polling: <error>, retry X/5`
- After network restored, polling resumes automatically
- Job continues without interruption

## Common Issues and Troubleshooting

### Issue: Job doesn't resume after refresh
**Check:**
1. Browser console for errors
2. Server logs for job status
3. Database: `sqlite3 /Config/preferences.db "SELECT * FROM active_job;"`
4. Database: `sqlite3 /Config/jobs.db "SELECT * FROM jobs ORDER BY created_at DESC LIMIT 5;"`

### Issue: Job appears to run twice
**Check:**
- This should NOT happen with the fix
- If it does, check for duplicate job creation in server logs
- File markers should prevent duplicate processing

### Issue: Progress doesn't update after resume
**Check:**
- Console for polling errors
- Network tab for API request status
- Server logs for job progress

## Verification Commands

### Check Active Job in Database
```bash
docker exec <container-name> sqlite3 /Config/preferences.db "SELECT * FROM active_job;"
```

### Check Recent Jobs
```bash
docker exec <container-name> sqlite3 /Config/jobs.db "SELECT job_id, status, total_items, processed_items, created_at FROM jobs ORDER BY created_at DESC LIMIT 5;"
```

### Check Job Results
```bash
docker exec <container-name> sqlite3 /Config/jobs.db "SELECT * FROM job_results WHERE job_id = '<job_id>';"
```

### Monitor Real-Time Logs
```bash
docker logs -f <container-name>
```

## Performance Metrics

Track these metrics during testing:

1. **Job Start Latency**: Time from button click to job creation
   - Expected: < 500ms

2. **Resume Latency**: Time from page load to job resume
   - Expected: < 1 second

3. **Processing Rate**: Files processed per minute
   - Expected: Varies by file size and ComicTagger processing time

4. **Database Response Time**: Job status query time
   - Expected: < 50ms (SQLite is very fast for this workload)

## Regression Testing

Ensure old functionality still works:

- [ ] Single file operations still work
- [ ] "Process All Files" still works
- [ ] "Process Selected Files" still works
- [ ] File marking/unmarking still works
- [ ] Settings page still works
- [ ] Tag editing still works

## Chrome Mobile Specific Testing

Since the issue was reported on Chrome mobile:

1. Use Chrome DevTools device emulation (F12 → Toggle device toolbar)
2. Select "iPhone" or "Android" device
3. Run all test cases above
4. Pay special attention to:
   - Touch interactions
   - Mobile Chrome's aggressive connection management
   - Background tab behavior
   - Pull-to-refresh gesture

### Chrome Mobile Quirks
- Chrome mobile may close connections more aggressively
- Background tabs may have reduced priority
- Network conditions may vary (3G/4G/WiFi)
- Pull-to-refresh can trigger page reload

## Test Results Template

Date: ________________
Tester: ________________
Browser: Chrome ________ (Desktop/Mobile)
Container Version: ________________

| Test Case | Pass/Fail | Notes |
|-----------|-----------|-------|
| TC1: Process Unmarked with Refresh | ⬜ | |
| TC2: Immediate Refresh | ⬜ | |
| TC3: Multiple Tabs | ⬜ | |
| TC4: Browser Crash | ⬜ | |
| TC5: Completion While Away | ⬜ | |
| TC6: Network Interruption | ⬜ | |

Overall Result: ⬜ PASS / ⬜ FAIL

Additional Notes:
_____________________________________________
_____________________________________________
_____________________________________________
