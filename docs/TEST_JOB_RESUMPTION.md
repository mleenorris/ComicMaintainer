# Test Plan: Job Resumption on Page Load

## Issue Fixed
Jobs were not automatically resuming on page load because the `checkAndResumeActiveJob()` async function was not being awaited in the `DOMContentLoaded` event handler.

## Changes Made
1. Made the `DOMContentLoaded` event handler async
2. Added `await` keyword before `checkAndResumeActiveJob()` call

## Test Scenarios

### Scenario 1: Job Still Processing
**Setup:**
1. Start a batch processing job (Process All Files)
2. Let it run for a few seconds (not complete)
3. Close the browser tab/window
4. Reopen the page

**Expected Result:**
- Console should show: `[JOB RESUME] Found active job <job_id> in localStorage, checking status...`
- Progress modal should appear with title "Resuming Job..." or the original job title
- Progress should continue from where it left off
- Files should continue processing
- When complete, success message should appear

### Scenario 2: Job Completed While Away
**Setup:**
1. Start a small batch processing job
2. Close the browser tab/window immediately
3. Wait for job to complete on the server (check logs or database)
4. Reopen the page

**Expected Result:**
- Console should show: `[JOB RESUME] Job <job_id> already completed`
- Success message should appear: "Batch processing completed: X of Y files processed successfully"
- Job ID should be cleared from localStorage
- File list should be refreshed to show updated status

### Scenario 3: Job Failed While Away
**Setup:**
1. Start a batch processing job
2. Simulate a failure (e.g., delete files being processed)
3. Close the browser tab/window
4. Reopen the page

**Expected Result:**
- Console should show: `[JOB RESUME] Job <job_id> already failed: <error>`
- Error message should appear
- Job ID should be cleared from localStorage

### Scenario 4: Job Cancelled
**Setup:**
1. Start a batch processing job
2. Cancel it using the Cancel button
3. Close the browser tab/window
4. Reopen the page

**Expected Result:**
- Console should show: `[JOB RESUME] Job <job_id> was cancelled`
- Warning message should appear: "Batch processing was cancelled"
- Job ID should be cleared from localStorage

### Scenario 5: Job Not Found (Cleaned Up)
**Setup:**
1. Start a batch processing job
2. Let it complete
3. Wait for job cleanup (24 hours or manually delete from database)
4. Reopen the page (with old job ID still in localStorage)

**Expected Result:**
- Console should show: `[JOB RESUME] Job <job_id> not found on server (HTTP 404)`
- Warning message should appear: "Previous batch processing job is no longer available"
- Job ID should be cleared from localStorage

### Scenario 6: No Active Job
**Setup:**
1. Clear localStorage or start fresh
2. Load the page

**Expected Result:**
- Console should show: `[JOB RESUME] No active job found in localStorage`
- No progress modal or messages
- Normal page load

## Manual Testing Steps

1. **Build and run the Docker container:**
   ```bash
   docker build -t comictagger-watcher .
   docker run -d \
     -v /path/to/comics:/watched_dir \
     -v /path/to/config:/Config \
     -e WATCHED_DIR=/watched_dir \
     -p 5000:5000 \
     comictagger-watcher
   ```

2. **Open browser and navigate to:** `http://localhost:5000`

3. **Test each scenario above**

4. **Check browser console** for log messages (F12 > Console)

5. **Verify localStorage** (F12 > Application > Local Storage > http://localhost:5000)
   - Look for `activeJobId` and `activeJobTitle` keys

6. **Check server logs** for job processing status:
   ```bash
   docker logs -f <container_id>
   ```

7. **Check SQLite database** (if needed):
   ```bash
   docker exec -it <container_id> sqlite3 /Config/jobs.db
   SELECT * FROM jobs ORDER BY created_at DESC LIMIT 5;
   .exit
   ```

## Automated Testing (Future)
Consider adding JavaScript unit tests using Jest or similar framework to test:
- `checkAndResumeActiveJob()` function
- `pollJobStatus()` function
- localStorage interactions
- API response handling

## Success Criteria
✅ Jobs automatically resume when page is reloaded while processing
✅ Completed jobs show appropriate success/failure messages
✅ No duplicate processing of the same job
✅ localStorage is properly cleaned up after job completion
✅ Console logs provide clear debugging information
✅ No JavaScript errors in the browser console
