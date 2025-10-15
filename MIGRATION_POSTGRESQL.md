# Migration Guide: PostgreSQL Job State Backend

This guide explains the changes made to move job state from in-memory storage to PostgreSQL, and how to migrate your existing deployment.

## What Changed

### Before (In-Memory Storage)
- Job state stored in Python dictionary in memory
- Limited to single Gunicorn worker process
- Jobs lost on server restart
- No persistence across deployments

### After (PostgreSQL Backend)
- Job state stored in PostgreSQL database
- Supports multiple Gunicorn workers (default: 2)
- Jobs persist across server restarts
- Survives deployments and container restarts

## Benefits

1. **Persistence**: Jobs and their results are preserved across restarts
2. **Scalability**: Multiple Gunicorn workers can now be used safely
3. **Reliability**: No more "Job not found" errors due to worker routing
4. **Better Performance**: More workers = better handling of concurrent requests

## Migration Steps

### For Docker Compose Users (Recommended)

1. **Backup your existing setup** (if needed):
   ```bash
   # Backup your configuration
   cp -r /path/to/config /path/to/config.backup
   ```

2. **Create `.env` file** from `.env.example`:
   ```bash
   cp .env.example .env
   # Edit .env and set your paths and password
   nano .env
   ```

3. **Stop existing container** (if running):
   ```bash
   docker stop your-container-name
   ```

4. **Pull latest changes and start with Docker Compose**:
   ```bash
   git pull
   docker-compose up -d
   ```

5. **Verify services are running**:
   ```bash
   docker-compose ps
   docker-compose logs app
   ```

### For Standalone Docker Users

1. **Set up PostgreSQL database**:
   - Use managed PostgreSQL service (AWS RDS, DigitalOcean, etc.)
   - Or run PostgreSQL in a separate container
   - Create database: `CREATE DATABASE comicmaintainer;`

2. **Update your docker run command** to include `DATABASE_URL`:
   ```bash
   docker run -d \
     -v /path/to/comics:/watched_dir \
     -v /path/to/config:/config \
     -e WATCHED_DIR=/watched_dir \
     -e CACHE_DIR=/config \
     -e DATABASE_URL=postgresql://user:password@host:5432/comicmaintainer \
     -p 5000:5000 \
     iceburn1/comictagger-watcher:latest
   ```

## Environment Variables

New required variable:
- `DATABASE_URL`: PostgreSQL connection string
  - Format: `postgresql://username:password@host:5432/database`
  - Example: `postgresql://comicmaintainer:mypass@localhost:5432/comicmaintainer`

New optional variable:
- `GUNICORN_WORKERS`: Number of worker processes (default: 2)
  - Increase for better performance on multi-core systems
  - Recommended: 2-4 workers for most deployments

## Database Schema

The database will be automatically initialized on first run with the following table:

```sql
CREATE TABLE jobs (
    job_id VARCHAR(36) PRIMARY KEY,
    status VARCHAR(20) NOT NULL,
    total_items INTEGER NOT NULL,
    processed_items INTEGER DEFAULT 0,
    results TEXT DEFAULT '[]',  -- JSON array
    error TEXT,
    created_at FLOAT NOT NULL,
    started_at FLOAT,
    completed_at FLOAT
);
```

## Backward Compatibility

- All API endpoints remain the same
- Web interface works identically
- No changes to request/response formats
- Existing job management code unchanged

## Troubleshooting

### "DATABASE_URL environment variable not set"
**Solution**: Add `DATABASE_URL` to your environment variables.

For docker-compose:
```yaml
environment:
  DATABASE_URL: postgresql://user:password@db:5432/comicmaintainer
```

For docker run:
```bash
-e DATABASE_URL=postgresql://user:password@host:5432/comicmaintainer
```

### "could not connect to server: Connection refused"
**Solution**: Ensure PostgreSQL is running and accessible.

For docker-compose, check db service:
```bash
docker-compose logs db
docker-compose ps
```

Verify connection from app container:
```bash
docker-compose exec app pg_isready -h db -U comicmaintainer
```

### Jobs from old deployment are gone
**Expected behavior**: Jobs from the in-memory system are not migrated. This is normal.
- Old jobs were stored in memory and lost on restart
- New jobs created after migration will persist in PostgreSQL

### Multiple workers causing issues
If you experience problems with multiple workers:
1. Check database connection pooling
2. Verify DATABASE_URL is correct
3. Check logs: `docker-compose logs app`
4. Temporarily reduce workers: `GUNICORN_WORKERS=1`

## Performance Tuning

### Worker Count Recommendations
- **1-2 cores**: `GUNICORN_WORKERS=2`
- **4+ cores**: `GUNICORN_WORKERS=4`
- **8+ cores**: `GUNICORN_WORKERS=4-8`

Note: Each worker has its own ThreadPoolExecutor with 4 threads, so total concurrency = workers × 4.

### PostgreSQL Connection Pooling
For high-load deployments, consider configuring connection pooling:
- Use PgBouncer between app and PostgreSQL
- Configure SQLAlchemy pool size if needed
- Monitor connection usage

## Rollback

If you need to rollback to the in-memory version:

1. Checkout previous commit:
   ```bash
   git checkout <previous-commit-hash>
   ```

2. Rebuild container:
   ```bash
   docker-compose down
   docker-compose build
   docker-compose up -d
   ```

3. Remove PostgreSQL requirement from environment

Note: You will lose all job history stored in PostgreSQL.

## Questions?

For issues or questions about the migration, please open an issue on GitHub.
