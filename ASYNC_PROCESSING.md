# Asynchronous File Processing

This document describes the asynchronous file processing feature implemented for ComicMaintainer.

## Overview

The application now supports asynchronous file processing, allowing multiple files to be processed concurrently in the background. This significantly improves performance for large libraries and keeps the web interface responsive during processing operations.

## Architecture

### Components

1. **Job Manager** (`job_manager.py`)
   - Manages background processing jobs
   - Uses ThreadPoolExecutor for concurrent execution (default: 4 workers)
   - Tracks job status (queued, processing, completed, failed, cancelled)
   - Provides job lifecycle management (create, start, monitor, cancel, delete)
   - Automatic cleanup of old completed jobs (after 1 hour)
   - Job state stored in SQLite database (`job_store.py`) for cross-process sharing

2. **API Endpoints** (`web_app.py`)
   - `POST /api/jobs/process-all` - Start processing all files
   - `POST /api/jobs/process-selected` - Start processing selected files
   - `GET /api/jobs/<job_id>` - Get job status and results
   - `GET /api/jobs` - List all jobs
   - `DELETE /api/jobs/<job_id>` - Delete a job
   - `POST /api/jobs/<job_id>/cancel` - Cancel a running job

3. **Web UI** (`templates/index.html`)
   - Uses async endpoints by default for "Process All" and "Process Selected" buttons
   - Polls job status every 500ms for progress updates
   - Displays real-time progress in modal
   - **Automatically resumes active jobs on page load** - if you close the browser and return, the job will continue from where it left off
   - Maintains backward compatibility with streaming endpoints

## Key Features

### Concurrent Processing
- Multiple files are processed simultaneously using a thread pool
- Default of 4 concurrent workers (configurable in job_manager.py)
- I/O-bound operations (reading/writing comic archives) benefit from parallelism

### Non-Blocking Operations
- Processing jobs run in background threads
- Web interface remains fully responsive during processing
- Multiple jobs can run simultaneously

### Progress Tracking
- Real-time progress updates via polling
- Detailed results for each processed file
- Success/failure tracking with error messages

### Job Management
- Jobs persist in SQLite database (survive server restarts and page reloads)
- Can query job status at any time
- Can cancel running jobs
- Automatic cleanup of old completed jobs (after 24 hours)
- **Active jobs automatically resume when you reload the page** - stored in browser localStorage

## Job Resumption on Page Load

When you start a batch processing job, the job ID is stored in the browser's localStorage. If you close the browser tab or refresh the page while a job is running, the application will automatically:

1. Check localStorage for any active job ID
2. Query the server for the job's current status
3. Resume displaying progress if the job is still running
4. Show completion/failure messages if the job finished while you were away
5. Clean up localStorage once the job is complete

This means you can:
- Close your browser and come back later - the job continues on the server
- Refresh the page without losing progress
- Open multiple tabs - they'll all show the same job progress

**Example behavior:**
- Start "Process All Files" (100 files)
- Close the browser after 30 seconds
- Wait 1 minute
- Reopen the page
- Progress modal will automatically appear showing current status (e.g., "Processing... 75/100")

## Usage

### Starting a Job

**Process all files:**
```javascript
const response = await fetch('/api/jobs/process-all', {
    method: 'POST'
});
const { job_id, total_items } = await response.json();
```

**Process selected files:**
```javascript
const response = await fetch('/api/jobs/process-selected', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
        files: ['path/to/file1.cbz', 'path/to/file2.cbz']
    })
});
const { job_id, total_items } = await response.json();
```

### Monitoring Progress

```javascript
async function pollJobStatus(jobId) {
    while (true) {
        const response = await fetch(`/api/jobs/${jobId}`);
        const status = await response.json();
        
        console.log(`Progress: ${status.processed_items}/${status.total_items}`);
        
        if (status.status === 'completed') {
            console.log('Job completed!');
            break;
        }
        
        await new Promise(resolve => setTimeout(resolve, 500));
    }
}
```

### Job Status Response

```json
{
    "job_id": "uuid-here",
    "status": "processing",
    "total_items": 100,
    "processed_items": 45,
    "progress": 0.45,
    "results": [
        {
            "item": "file1.cbz",
            "success": true,
            "error": null,
            "details": {
                "original": "/path/to/file1.cbz",
                "final": "/path/to/renamed_file1.cbz"
            }
        }
    ],
    "error": null,
    "created_at": 1234567890.0,
    "started_at": 1234567891.0,
    "completed_at": null
}
```

## Performance Benefits

### Before (Synchronous Processing)
- Files processed one at a time
- Web request blocks until all files complete
- Processing 100 files × 2 seconds each = 200 seconds
- Web interface unresponsive during processing

### After (Asynchronous Processing)
- Files processed concurrently (4 at a time by default)
- Web request returns immediately with job ID
- Processing 100 files ÷ 4 workers × 2 seconds each = 50 seconds
- Web interface remains fully responsive
- **~75% faster for large batches**

## Backward Compatibility

The original synchronous/streaming endpoints remain available:
- `/api/process-all?stream=true`
- `/api/process-selected?stream=true`

These can still be used if needed, but the async endpoints are recommended for better performance and user experience.

## Deployment Architecture

The application uses:
- **Multiple Gunicorn worker processes** (default: 2, configurable via `GUNICORN_WORKERS` env var)
- **4 ThreadPoolExecutor threads per worker** (configurable) - Provides concurrent file processing
- **SQLite database** - Shared job state storage across all worker processes

Job state is stored in a SQLite database (`/app/cache/jobs.db`), which enables:
- Multiple Gunicorn workers can safely share job information
- Jobs survive server restarts
- No "Job not found" errors when different workers handle different requests
- Better scalability with horizontal scaling of workers

## Configuration

### Adjusting Gunicorn Workers

To change the number of Gunicorn worker processes, set the `GUNICORN_WORKERS` environment variable:

```bash
export GUNICORN_WORKERS=4  # Default is 2
```

**Recommendations:**
- For small libraries: 1-2 workers
- For medium libraries: 2-4 workers  
- For large libraries with high traffic: 4-8 workers

### Adjusting Thread Pool Size

The number of concurrent workers can be configured using the `MAX_WORKERS` environment variable:

```bash
docker run -d \
  -v <host_dir_to_watch>:/watched_dir \
  -e WATCHED_DIR=/watched_dir \
  -e MAX_WORKERS=8 \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

**Default:** 4 workers

**Recommendations:**
- For CPU-bound systems: 2-4 threads
- For systems with fast storage: 4-8 threads
- For systems with slow storage: 2-4 threads

### Adjusting Polling Interval

To change how often the UI polls for updates, modify `templates/index.html`:

```javascript
const pollInterval = 500; // milliseconds
```

**Recommendations:**
- Faster polling (250-500ms): More responsive, more API calls
- Slower polling (1000-2000ms): Less responsive, fewer API calls

## Testing

### Unit Tests
```bash
python3 /tmp/test_job_manager.py
```

### API Tests
```bash
python3 /tmp/test_async_api.py
```

### Integration Tests
```bash
python3 /tmp/test_integration.py
```

## Technical Details

### Thread Safety
- Job state protected by threading locks
- Thread-safe job creation and updates
- Safe for multiple concurrent web requests

### Error Handling
- Individual file errors don't stop the job
- Failed files tracked in results
- Job continues processing remaining files
- Fatal errors mark job as failed

### Persistence and Storage
- Job state stored in SQLite database (`/Config/jobs.db`)
- Jobs survive server restarts
- **Active jobs automatically resume when you reload the browser page** - job ID stored in localStorage
- Old completed jobs automatically cleaned up after 24 hours
- Can manually delete jobs via API
- Thread-safe database access with WAL mode for concurrent operations
- Multiple Gunicorn workers share job state via database

## Future Enhancements

Potential improvements for future versions:
- [x] Persistent job storage (survive server restarts) - ✅ Implemented with SQLite
- [x] Multi-worker support - ✅ Implemented with shared SQLite backend
- [ ] Job priority levels
- [ ] Configurable worker pools per job type
- [ ] WebSocket support for push notifications
- [ ] Job scheduling and queuing
- [ ] Resource throttling and rate limiting
