# Fix: Batch Jobs Lost on Page Refresh (Chrome Mobile)

## Issue Description

**Problem:** When starting a batch job (like "Process Unmarked Files") on Chrome mobile and then refreshing the page, the job appears to be lost and stops running on the server. The logs also stop showing progress.

**Impact:** Users lose progress when accidentally refreshing the page during long-running batch operations.

## Root Cause Analysis

### Primary Issue: Old Streaming API Used for Unmarked Operations

The "Process Unmarked Files" button (and related operations like "Rename Unmarked" and "Normalize Unmarked") were using the **old Server-Sent Events (SSE) streaming API** instead of the new async job system.

**How the Old Streaming API Worked:**
```
User clicks button → POST to /api/process-unmarked?stream=true
→ Flask generates response with generator function
→ Generator processes files one by one, yielding progress updates
→ Frontend reads stream and displays progress
```

**The Problem:**
When the user refreshes the page (or the browser closes the connection for any reason):
1. HTTP connection is closed
2. Python generator function stops executing
3. File processing stops immediately
4. No job tracking or resumption mechanism exists

### Secondary Issue: Active Job Not Set Immediately

Even for operations that used the new async job API, there was a race condition:
1. Job created via `/api/jobs/process-all`
2. Job starts in background thread
3. Frontend receives job_id
4. Frontend calls `pollJobStatus()` which sets active job on server
5. **If page refreshes before step 4, active job is not tracked**

### Tertiary Issue: Active Job Not Auto-Cleared on Completion

When jobs completed/failed/cancelled, the active job reference in `preferences.db` was only cleared by the frontend. If the frontend never completed the polling (browser crash, network issues), the stale reference remained.

## Solution

### 1. Created New Async Job Endpoints for Unmarked Operations

Added three new endpoints that use the async job system:
- `/api/jobs/process-unmarked` - Full metadata processing
- `/api/jobs/rename-unmarked` - Rename based on metadata
- `/api/jobs/normalize-unmarked` - Normalize metadata only

**How the New Async Job API Works:**
```
User clicks button → POST to /api/jobs/process-unmarked
→ Job created in SQLite (jobs.db)
→ Active job set in preferences.db IMMEDIATELY
→ Job submitted to ThreadPoolExecutor
→ Returns job_id to frontend
→ Frontend polls for status every 500ms
→ Job continues in background even if connection closes
```

### 2. Set Active Job Immediately on Job Creation

Modified all job creation endpoints to set the active job in `preferences.db` **immediately** when the job is created, before returning the job_id to the frontend.

**Before:**
```python
# web_app.py - async_process_all_files()
job_id = job_manager.create_job(files)
job_manager.start_job(job_id, process_item, files)
return jsonify({'job_id': job_id, 'total_items': len(files)})
# Active job set later by frontend in pollJobStatus()
```

**After:**
```python
# web_app.py - async_process_all_files()
job_id = job_manager.create_job(files)
set_active_job(job_id, 'Processing Files...')  # ← Set immediately
logging.info(f"[API] Set active job {job_id} on server")
job_manager.start_job(job_id, process_item, files)
return jsonify({'job_id': job_id, 'total_items': len(files)})
```

### 3. Auto-Clear Active Job on Completion/Failure/Cancellation

Added automatic cleanup of active job references when jobs finish:

**New Method in JobManager:**
```python
def _clear_active_job_if_current(self, job_id: str):
    """Clear active job from preferences if it matches this job_id"""
    try:
        from preferences_store import get_active_job, clear_active_job
        active_job = get_active_job()
        
        if active_job and active_job.get('job_id') == job_id:
            clear_active_job()
            logging.info(f"[JOB {job_id}] Cleared active job from preferences")
    except Exception as e:
        logging.error(f"[JOB {job_id}] Error clearing active job: {e}")
```

**Called automatically when:**
- Job completes successfully
- Job fails with an error
- Job is cancelled by user

### 4. Updated Frontend to Use New Async Endpoints

Modified frontend functions to use the new async job endpoints:

**Before (Streaming API):**
```javascript
async function processUnmarkedFiles() {
    const response = await fetch('/api/process-unmarked?stream=true', {
        method: 'POST'
    });
    
    const reader = response.body.getReader();
    // Read stream and process results...
}
```

**After (Async Job API):**
```javascript
async function processUnmarkedFiles() {
    const response = await fetch('/api/jobs/process-unmarked', {
        method: 'POST'
    });
    
    const data = await response.json();
    const jobId = data.job_id;
    
    // Poll for status - job continues even if page refreshes
    await pollJobStatus(jobId, 'Processing Unmarked Files...');
}
```

## Technical Architecture

### Job Persistence
- **jobs.db**: SQLite database storing job state (status, progress, results)
- **preferences.db**: SQLite database storing active job reference
- Both databases are shared across all Gunicorn workers

### Job Execution
- Jobs run in ThreadPoolExecutor (one per worker process)
- Default: 2 Gunicorn workers × 4 threads = 8 concurrent file processing threads
- Jobs continue running even if HTTP connection closes

### Job Resumption
- On page load, `checkAndResumeActiveJob()` checks for active job
- If found and still running, automatically resumes polling
- If completed, shows results and clears active job
- If not found (404), clears stale active job reference

## Files Modified

### Backend Changes
1. **web_app.py**
   - Added `/api/jobs/process-unmarked` endpoint
   - Added `/api/jobs/rename-unmarked` endpoint
   - Added `/api/jobs/normalize-unmarked` endpoint
   - Modified `/api/jobs/process-all` to set active job immediately
   - Modified `/api/jobs/process-selected` to set active job immediately

2. **job_manager.py**
   - Added `_clear_active_job_if_current()` method
   - Modified `_process_job()` to auto-clear active job on completion
   - Modified `_process_job()` to auto-clear active job on failure
   - Modified `cancel_job()` to auto-clear active job on cancellation

### Frontend Changes
3. **templates/index.html**
   - Modified `processUnmarkedFiles()` to use async job API
   - Modified `renameUnmarkedFiles()` to use async job API
   - Modified `normalizeUnmarkedFiles()` to use async job API

## Testing Checklist

### Page Refresh Scenarios
- [x] Start "Process Unmarked Files" → Refresh page → Job continues and resumes
- [x] Start "Rename Unmarked Files" → Refresh page → Job continues and resumes
- [x] Start "Normalize Unmarked Files" → Refresh page → Job continues and resumes
- [x] Start "Process All Files" → Refresh page → Job continues and resumes
- [x] Start "Process Selected Files" → Refresh page → Job continues and resumes

### Job Completion Scenarios
- [x] Job completes → Active job auto-cleared
- [x] Job fails → Active job auto-cleared
- [x] Job cancelled → Active job auto-cleared
- [x] Job completed during page refresh → Shows results on reload

### Edge Cases
- [x] Refresh immediately after starting job → Active job already set
- [x] Multiple tabs → All see same job state from server
- [x] Browser crash during job → Job continues, resumes on browser restart
- [x] Network interruption → Job continues, polling resumes when network restored

## Migration Notes

### Old Streaming API Endpoints (Still Available)
The old streaming API endpoints are still available for backward compatibility:
- `/api/process-unmarked?stream=true`
- `/api/rename-unmarked?stream=true`
- `/api/normalize-unmarked?stream=true`
- `/api/process-selected?stream=true`
- `/api/rename-selected?stream=true`
- `/api/normalize-selected?stream=true`

However, the frontend now uses the new async job endpoints by default.

### Deprecation Plan
Consider removing old streaming endpoints in a future release after confirming the new async job system works reliably across all use cases.

## Performance Considerations

### Resource Usage
- Each job uses one ThreadPoolExecutor with configurable workers (default: 4)
- SQLite databases use WAL mode for better concurrent access
- Job cleanup runs every 5 minutes, removing jobs older than 24 hours

### Scalability
- Multiple Gunicorn workers share job state via SQLite
- Each worker has its own ThreadPoolExecutor
- Jobs are processed by the worker that created them
- Other workers can query job status from shared database

## Monitoring and Debugging

### Log Messages
```
# Job creation
[API] Request to process unmarked files (async)
[API] Found 10 unmarked files to process
[API] Set active job <job_id> on server
[API] Created and started job <job_id> for 10 unmarked files

# Job processing
[JOB <job_id>] Starting processing of 10 items with 4 workers
[BATCH] Processed unmarked file: /path/to/file.cbz -> /path/to/renamed.cbz
[JOB <job_id>] Progress: 5/10 items processed (5 success, 0 errors)

# Job completion
[JOB <job_id>] Completed: 10 succeeded, 0 failed out of 10 items
[JOB <job_id>] Cleared active job from preferences (job completed/failed)

# Job resumption
[JOB RESUME] Found active job <job_id> on server
[JOB RESUME] Job <job_id> status: processing, 5/10 items processed
[JOB RESUME] Resuming job <job_id>
```

### Database Inspection
```bash
# Check jobs
sqlite3 /Config/jobs.db "SELECT * FROM jobs ORDER BY created_at DESC LIMIT 5;"

# Check active job
sqlite3 /Config/preferences.db "SELECT * FROM active_job;"

# Check job results
sqlite3 /Config/jobs.db "SELECT * FROM job_results WHERE job_id = '<job_id>';"
```

## Known Limitations

1. **Worker Process Restart**: If the Gunicorn worker that's running a job is killed/restarted, the job stops (ThreadPoolExecutor is not persistent across process restarts)

2. **Job State Not Recoverable**: Jobs cannot be automatically restarted after container restart (would require more complex state management)

3. **Single Active Job**: Only one batch job is tracked as "active" at a time (by design, to prevent UI confusion)

## Future Improvements

1. **Job Recovery**: Implement job recovery mechanism that can restart interrupted jobs after container restart

2. **Multiple Active Jobs**: Allow tracking multiple concurrent batch jobs

3. **Job Priority**: Add job priority/queuing system for better resource management

4. **Progress Streaming**: Implement WebSocket-based real-time progress updates (more efficient than polling)

5. **Job History UI**: Add a dedicated job history page to view all completed/failed jobs
