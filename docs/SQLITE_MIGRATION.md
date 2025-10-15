# SQLite Backend Migration

## Overview

Job state has been migrated from in-memory storage to a shared SQLite database. This change enables:

- **Multi-worker support**: Multiple Gunicorn workers can now safely share job information
- **Persistence**: Jobs survive server restarts
- **Reliability**: No more "Job not found" errors when different workers handle different requests

## What Changed

### Architecture

**Before:**
- Jobs stored in memory (`Dict[str, Job]`)
- Each worker process had its own job dictionary
- Limited to single worker deployment to avoid "Job not found" errors
- Jobs lost on server restart

**After:**
- Jobs stored in SQLite database (`/app/cache/jobs.db`)
- All worker processes share the same database
- Supports multiple workers (default: 2, configurable via `GUNICORN_WORKERS`)
- Jobs persist across restarts

### New Components

1. **`job_store.py`** - SQLite database operations
   - Thread-safe database access with WAL mode
   - Automatic schema initialization per process
   - CRUD operations for jobs and results

2. **Updated `job_manager.py`**
   - Removed in-memory `self.jobs` dictionary
   - All operations delegated to `job_store`
   - Maintains same public API

3. **Updated `start.sh`**
   - Changed from 1 to 2 Gunicorn workers by default
   - Added `GUNICORN_WORKERS` environment variable

## Database Schema

### `jobs` table
```sql
CREATE TABLE jobs (
    job_id TEXT PRIMARY KEY,
    status TEXT NOT NULL,
    total_items INTEGER NOT NULL,
    processed_items INTEGER DEFAULT 0,
    error TEXT,
    created_at REAL NOT NULL,
    started_at REAL,
    completed_at REAL
)
```

### `job_results` table
```sql
CREATE TABLE job_results (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    job_id TEXT NOT NULL,
    item TEXT NOT NULL,
    success INTEGER NOT NULL,
    error TEXT,
    details TEXT,
    FOREIGN KEY (job_id) REFERENCES jobs(job_id) ON DELETE CASCADE
)
```

### Indexes
- `idx_job_results_job_id` - Fast lookup of results by job
- `idx_jobs_status` - Fast filtering by job status
- `idx_jobs_completed_at` - Fast cleanup of old jobs

## Configuration

### Environment Variables

- **`GUNICORN_WORKERS`** (default: `2`) - Number of worker processes
  - Recommended: 2-4 for most deployments
  - Can increase for high-traffic installations

- **`/Config`** - Directory for persistent data (hardcoded, no env var needed)
  - Job database stored as `/Config/jobs.db`
  - Should be mounted to host for persistence

### Example Docker Run

```bash
docker run -d \
  -v /path/to/comics:/watched_dir \
  -v /path/to/cache:/Config \
  -e WATCHED_DIR=/watched_dir \
  -e GUNICORN_WORKERS=4 \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

## Migration Path

### Existing Deployments

No action required! The migration is automatic:

1. On first startup, `job_store.py` creates the SQLite database
2. Any in-flight jobs from before the upgrade will be lost (expected)
3. New jobs will be stored in the database
4. Multiple workers can be enabled immediately

### Rollback

To rollback to the previous version:

1. Stop the container
2. Deploy the previous version
3. Set `GUNICORN_WORKERS=1` to avoid issues
4. Old jobs will not be recovered (they're in the database)

## Performance Considerations

### Database Performance

- **WAL Mode**: Enables concurrent reads and writes
- **Indexes**: Optimize common queries (status, completion time)
- **Connection Pooling**: Thread-local connections reduce overhead

### Scaling Recommendations

| Library Size | Workers | Thread Pool | Notes |
|--------------|---------|-------------|-------|
| Small (<1K)  | 1-2     | 2-4 threads | Minimal overhead |
| Medium (1-10K) | 2-4   | 4-8 threads | Good balance |
| Large (>10K) | 4-8     | 4-8 threads | High concurrency |

## Testing

All changes have been thoroughly tested:

- ✅ SQLite operations (create, read, update, delete)
- ✅ JobManager integration with SQLite backend
- ✅ Multi-process job sharing
- ✅ Job persistence across manager instances
- ✅ Concurrent job execution
- ✅ Cleanup of old jobs

## Troubleshooting

### "Database is locked" errors

If you see database lock errors:
- Check disk I/O performance
- Reduce number of workers
- Ensure `/Config` is on a fast filesystem

### Jobs not appearing

If jobs aren't showing up:
- Verify all workers can access `/Config`
- Check database file permissions
- Look for errors in `/Config/Log/ComicMaintainer.log`

### Performance issues

If performance degrades:
- Monitor database size (should auto-cleanup old jobs)
- Check worker count matches system resources
- Review thread pool size in job_manager.py

## Future Enhancements

Potential improvements leveraging the SQLite backend:

- [ ] Job priority queues
- [ ] Job scheduling (run at specific times)
- [ ] Job history and statistics
- [ ] Admin UI for job management
- [ ] Export/import job data
- [ ] Job templates for common operations

## Technical Details

### Thread Safety

- Each thread gets its own database connection (thread-local storage)
- WAL mode allows concurrent reads and single writer
- Connections initialized lazily on first use

### Database Maintenance

- Old jobs (>1 hour) automatically cleaned up every 5 minutes
- Cleanup handled by background thread in JobManager
- Manual cleanup via DELETE endpoint still available

### Error Handling

- Database errors logged but don't crash the application
- Failed operations return False/None as appropriate
- Corrupted database files can be deleted (will auto-recreate)

## References

- [SQLite WAL Mode](https://www.sqlite.org/wal.html)
- [Thread-Safe SQLite](https://www.sqlite.org/threadsafe.html)
- [ASYNC_PROCESSING.md](./ASYNC_PROCESSING.md) - Async processing documentation
