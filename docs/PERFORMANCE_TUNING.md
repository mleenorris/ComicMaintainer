# Performance Tuning Guide

This guide provides recommendations for optimizing ComicMaintainer's performance based on your specific use case and system resources.

## Table of Contents
- [Environment Variables](#environment-variables)
- [Database Performance](#database-performance)
- [File Processing Performance](#file-processing-performance)
- [Web Interface Performance](#web-interface-performance)
- [System Resource Optimization](#system-resource-optimization)
- [Monitoring Performance](#monitoring-performance)

## Environment Variables

### Core Performance Settings

#### `MAX_WORKERS`
Number of concurrent worker threads for file processing.

**Default:** `4`

**Recommendations:**
- **CPU-bound systems:** 2-4 workers
- **Systems with fast storage (SSD/NVMe):** 4-8 workers
- **Systems with slow storage (HDD/Network):** 2-4 workers
- **Low memory systems:** 2-3 workers

**Example:**
```bash
docker run -e MAX_WORKERS=6 ...
```

#### `GUNICORN_WORKERS`
Number of Gunicorn worker processes for the web interface.

**Default:** `2`

**Recommendations:**
- **Single user:** 1-2 workers
- **Multiple concurrent users:** 2-4 workers
- **High traffic:** 4-8 workers (2 × CPU cores)

**Example:**
```bash
docker run -e GUNICORN_WORKERS=4 ...
```

#### `DB_CACHE_SIZE_MB`
SQLite cache size in megabytes.

**Default:** `64` (64MB)

**Recommendations:**
- **Small libraries (<1000 files):** 32MB
- **Medium libraries (1000-5000 files):** 64MB (default)
- **Large libraries (5000-20000 files):** 128MB
- **Very large libraries (>20000 files):** 256MB

**Example:**
```bash
docker run -e DB_CACHE_SIZE_MB=128 ...
```

**Note:** Higher cache sizes improve read performance but use more RAM.

### Log Settings

#### `LOG_MAX_BYTES`
Maximum log file size in bytes before rotation.

**Default:** `5242880` (5MB)

**Recommendations:**
- **Production systems:** 5-10MB
- **Debug/troubleshooting:** 10-20MB
- **Storage-constrained systems:** 1-5MB

**Example:**
```bash
docker run -e LOG_MAX_BYTES=10485760 ...  # 10MB
```

## Database Performance

### SQLite Optimizations

The application automatically configures SQLite with optimal settings:

1. **WAL Mode:** Enabled for better concurrent access
2. **Synchronous Mode:** NORMAL (balanced performance/safety with WAL)
3. **Memory-Mapped I/O:** 256MB for faster reads
4. **Temp Store:** In-memory for temporary tables
5. **Cache Size:** Configurable via `DB_CACHE_SIZE_MB`

### Database Maintenance

For optimal performance, occasionally vacuum the database:

```bash
# Access the container
docker exec -it comictagger-watcher sh

# Vacuum the database
sqlite3 /Config/store/comicmaintainer.db "VACUUM;"
```

**When to vacuum:**
- After removing large numbers of files
- After significant marker data cleanup
- Monthly for large libraries

## File Processing Performance

### Optimization Strategies

1. **Early Exit for Normalized Files**
   - Files already in correct format are skipped without modification
   - Reduces unnecessary I/O and metadata updates

2. **Regex Pattern Caching**
   - Chapter number patterns are pre-compiled
   - Improves parsing performance for large batches

3. **File Stability Detection**
   - Optimized to 2 checks (4 seconds) instead of 3 (6 seconds)
   - Still reliable for most file transfer scenarios
   - Reduces latency for file processing

### Processing Large Batches

For processing thousands of files:

1. **Increase worker count:**
   ```bash
   docker run -e MAX_WORKERS=8 ...
   ```

2. **Increase database cache:**
   ```bash
   docker run -e DB_CACHE_SIZE_MB=128 ...
   ```

3. **Use async job processing:**
   - Use the web interface "Process All Files" button
   - Jobs are persistent and survive page refreshes
   - Real-time progress updates via SSE

## Web Interface Performance

### Frontend Optimizations

The web interface includes several performance optimizations:

1. **Pagination:** 100 files per page (configurable)
2. **Search Debouncing:** 300ms delay reduces API calls by 87%
3. **Server-Side Processing:** Filtering, sorting, and searching on backend
4. **Real-time Updates:** Server-Sent Events (SSE) instead of polling

### Response Optimization

The application automatically:
- Disables JSON pretty-printing (smaller responses)
- Skips JSON key sorting (faster serialization)
- Adds cache control headers
- Uses WAL mode for faster database reads

### Browser Performance

For best performance:
1. Use modern browsers (Chrome, Firefox, Safari, Edge)
2. Enable hardware acceleration
3. Close unused tabs when processing large libraries
4. Use dark mode to reduce GPU load (optional)

## System Resource Optimization

### Memory Usage

**Typical memory usage:**
- Base application: ~100-200MB
- Per worker thread: ~50-100MB
- Database cache: Configured via `DB_CACHE_SIZE_MB`

**Total RAM recommendation:**
- Minimum: 512MB
- Recommended: 1GB+
- Optimal: 2GB+ for large libraries

### CPU Usage

**CPU recommendations:**
- Minimum: 1 core
- Recommended: 2+ cores
- Optimal: 4+ cores for concurrent processing

### Storage Performance

**Storage recommendations:**
- **SSD/NVMe:** Optimal performance, use higher `MAX_WORKERS` (6-8)
- **HDD:** Lower `MAX_WORKERS` (2-4) to avoid disk thrashing
- **Network storage:** Lower `MAX_WORKERS` (2-3), increase stability checks if needed

### Docker Resource Limits

Example docker-compose configuration:

```yaml
services:
  comictagger-watcher:
    # ... other settings ...
    deploy:
      resources:
        limits:
          cpus: '4'      # Limit CPU usage
          memory: 2G     # Limit memory usage
        reservations:
          cpus: '1'      # Reserve minimum CPU
          memory: 512M   # Reserve minimum memory
```

## Monitoring Performance

### Log Analysis

Check logs for performance insights:

```bash
# View recent log entries
docker logs comictagger-watcher --tail 100

# View logs in real-time
docker logs -f comictagger-watcher

# Access log file directly
docker exec -it comictagger-watcher cat /Config/Log/ComicMaintainer.log
```

### Health Checks

Use the health endpoint to monitor application status:

```bash
# Check health
curl http://localhost:5000/health

# Example response:
{
  "status": "healthy",
  "watched_dir": "/watched_dir",
  "watched_dir_accessible": true,
  "database_accessible": true,
  "watcher_running": true,
  "file_count": 1234,
  "version": "1.0.0"
}
```

### Performance Metrics

Key metrics to monitor:

1. **File processing time:** Check logs for individual file processing duration
2. **Batch job completion time:** Monitor job progress in web interface
3. **Database query time:** Enable debug logging to see query performance
4. **Memory usage:** Use `docker stats comictagger-watcher`
5. **CPU usage:** Use `docker stats comictagger-watcher`

### Debug Mode

Enable detailed logging for performance analysis:

```bash
docker run -e DEBUG_MODE=true ...
```

**Warning:** Debug mode generates verbose logs and may impact performance. Use only for troubleshooting.

## Performance Benchmarks

### Typical Performance (SSD, 4 workers)

| Library Size | Initial Scan | Batch Processing | Web UI Load |
|--------------|--------------|------------------|-------------|
| 100 files    | <1 second    | ~30 seconds      | <100ms      |
| 1,000 files  | 1-2 seconds  | ~5 minutes       | <300ms      |
| 5,000 files  | 3-5 seconds  | ~20 minutes      | <500ms      |
| 10,000 files | 5-10 seconds | ~40 minutes      | <1 second   |

**Note:** Processing times vary based on:
- File sizes (larger archives take longer)
- Metadata complexity
- Storage speed
- System resources
- Number of files already normalized

## Troubleshooting Performance Issues

### Slow File Processing

**Symptoms:** Files take a long time to process

**Solutions:**
1. Check storage speed (HDD vs SSD)
2. Reduce `MAX_WORKERS` if CPU/disk is saturated
3. Check if files are actually being modified or skipped (normalized)
4. Enable debug logging to identify bottlenecks

### Slow Web Interface

**Symptoms:** Web UI is sluggish or unresponsive

**Solutions:**
1. Increase `GUNICORN_WORKERS`
2. Increase `DB_CACHE_SIZE_MB`
3. Reduce pagination size (if customized)
4. Check browser console for JavaScript errors
5. Clear browser cache

### High Memory Usage

**Symptoms:** Container uses excessive RAM

**Solutions:**
1. Reduce `MAX_WORKERS`
2. Reduce `DB_CACHE_SIZE_MB`
3. Reduce `GUNICORN_WORKERS`
4. Set Docker memory limits
5. Vacuum database to reclaim space

### High CPU Usage

**Symptoms:** CPU constantly at 100%

**Solutions:**
1. Reduce `MAX_WORKERS`
2. Check for stuck jobs (cancel and retry)
3. Ensure watcher is not processing same files repeatedly
4. Check if files are already normalized (should skip processing)

## Best Practices

### For Small Libraries (<1000 files)
```bash
docker run \
  -e MAX_WORKERS=2 \
  -e GUNICORN_WORKERS=2 \
  -e DB_CACHE_SIZE_MB=32 \
  ...
```

### For Medium Libraries (1000-5000 files)
```bash
docker run \
  -e MAX_WORKERS=4 \
  -e GUNICORN_WORKERS=2 \
  -e DB_CACHE_SIZE_MB=64 \
  ...
```

### For Large Libraries (>5000 files)
```bash
docker run \
  -e MAX_WORKERS=6 \
  -e GUNICORN_WORKERS=4 \
  -e DB_CACHE_SIZE_MB=128 \
  ...
```

### For Production Systems
```bash
docker run \
  -e MAX_WORKERS=4 \
  -e GUNICORN_WORKERS=4 \
  -e DB_CACHE_SIZE_MB=64 \
  -e LOG_MAX_BYTES=10485760 \
  --memory=2g \
  --cpus=4 \
  ...
```

## Summary

Performance tuning is about finding the right balance for your specific use case. Start with the defaults and adjust based on:
- Your library size
- Available system resources
- Expected usage patterns
- Storage performance

Monitor the application using logs and health checks, and adjust settings incrementally to find the optimal configuration for your setup.

For additional help, see:
- [README.md](../README.md) - General usage documentation
- [DEBUG_LOGGING_GUIDE.md](../DEBUG_LOGGING_GUIDE.md) - Debugging guide
- [CONTRIBUTING.md](../CONTRIBUTING.md) - Development guide
