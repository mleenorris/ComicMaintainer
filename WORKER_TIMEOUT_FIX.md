# Worker Timeout Fix for Large Libraries

## Problem
When processing unmarked files in large libraries, the application experienced WORKER TIMEOUT errors. The issue occurred because:

1. Each file was individually marked with `mark_file_web_modified()`, which called `os.fsync()` to ensure data was written to disk
2. With hundreds or thousands of files, these individual disk writes accumulated and exceeded the gunicorn worker timeout (30 seconds default, 600 seconds configured)
3. The synchronous processing loop blocked the worker, preventing it from responding to health checks

## Solution
The fix addresses the timeout issue through two key improvements:

### 1. Batch Marker Updates
Added `mark_files_web_modified_batch()` function in `markers.py`:
- Marks all files in a single write operation instead of individual writes
- Reduces `os.fsync()` calls from N (number of files) to 1
- Significantly improves performance for large batches

### 2. Streaming Responses
Updated endpoints to support Server-Sent Events (SSE):
- `/api/process-unmarked?stream=true`
- `/api/rename-unmarked?stream=true`
- `/api/normalize-unmarked?stream=true`

Benefits:
- Worker can send incremental progress updates
- Prevents worker timeout by keeping connection alive
- Better user experience with real-time progress
- Backward compatible (non-streaming mode still works)

## Technical Details

### Batch Marker Function
```python
def mark_files_web_modified_batch(filepaths: list):
    """Mark multiple files as modified by the web interface in a single write operation"""
    abs_paths = [os.path.abspath(fp) for fp in filepaths]
    
    with _web_modified_lock:
        web_modified_files = _load_marker_set(WEB_MODIFIED_MARKER_FILE)
        web_modified_files.update(abs_paths)
        _save_marker_set(WEB_MODIFIED_MARKER_FILE, web_modified_files)
    
    logging.info(f"Batch marked {len(abs_paths)} files as web modified")
```

### Streaming Pattern
Each endpoint now:
1. Checks for `stream=true` query parameter
2. Batch marks all files upfront (before processing)
3. Processes files one by one
4. Sends progress updates via SSE
5. Sends final results when complete

### Frontend Updates
JavaScript functions updated to:
1. Use streaming URLs with `?stream=true`
2. Show progress modal with real-time updates
3. Use Server-Sent Events reader to handle chunks
4. Parse JSON progress updates
5. Display success/error counts and file details

## Performance Impact

### Before
- Large library (1000 files): ~1000 fsync calls = TIMEOUT
- No progress feedback until completion
- Worker blocked for entire duration

### After
- Large library (1000 files): ~1 fsync call for markers + processing time
- Real-time progress updates
- Worker stays responsive
- Better error handling per file

## Backward Compatibility
Non-streaming mode is maintained for compatibility:
```javascript
// Old way (still works)
fetch('/api/process-unmarked', { method: 'POST' })

// New way (recommended)
fetch('/api/process-unmarked?stream=true', { method: 'POST' })
```

## Testing
Run the test scripts in `/tmp/` to verify:
- `test_batch_marker.py` - Tests batch marker functionality
- `test_endpoints.py` - Tests endpoint structure
- `test_javascript.py` - Tests JavaScript changes
