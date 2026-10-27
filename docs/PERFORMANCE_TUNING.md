# Performance Tuning Guide

This guide provides recommendations for optimizing ComicMaintainer's performance based on your specific use case and system resources.

## Table of Contents
- [Settings](#settings)
- [Database Performance](#database-performance)
- [File Processing Performance](#file-processing-performance)
- [Web Interface Performance](#web-interface-performance)
- [System Resource Optimization](#system-resource-optimization)
- [Monitoring Performance](#monitoring-performance)

## Settings

The Python version's `MAX_WORKERS`, `GUNICORN_WORKERS`, `DB_CACHE_SIZE_MB` and `LOG_MAX_BYTES` environment variables are not read by the .NET version. Use the settings below instead.

### Core Performance Settings

#### `MaxWorkers`
Maximum number of files processed at the same time.

**Default:** `4`

**Recommendations:**
- **CPU-bound systems:** 2-4 workers
- **Systems with fast storage (SSD/NVMe):** 4-8 workers
- **Systems with slow storage (HDD/Network):** 2-4 workers
- **Low memory systems:** 2-3 workers

It is not on the Settings page: set the `AppSettings__MaxWorkers` environment variable. The value is read at startup.

**Example:**
```bash
docker run -e AppSettings__MaxWorkers=6 ...
```

#### Web server workers
The .NET version serves the web interface from a single process, so there is no web worker count to tune.

#### Database cache
The SQLite page cache is fixed at 64MB per connection and cannot be changed.

### Log Settings

#### Maximum log file size
Size a log file reaches before it is rotated. Set it under **Settings → Log Rotation**; it is applied when the service restarts.

**Default:** 10MB

**Recommendations:**
- **Production systems:** 5-10MB
- **Debug/troubleshooting:** 10-20MB
- **Storage-constrained systems:** 1-5MB

## Database Performance

### SQLite Optimizations

The application automatically configures SQLite with optimal settings:

1. **WAL Mode:** Enabled for better concurrent access
2. **Synchronous Mode:** NORMAL (balanced performance/safety with WAL)
3. **Memory-Mapped I/O:** 256MB for faster reads
4. **Temp Store:** In-memory for temporary tables
5. **Cache Size:** 64MB page cache per connection (fixed)

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
   docker run -e AppSettings__MaxWorkers=8 ...
   ```

2. **Use async job processing:**
   - Use the web interface "Process All Files" button
   - Jobs are persistent and survive page refreshes
   - Real-time progress updates via SSE

### Incremental Library Scans

The **Library Scan** scheduled job (`library-scan`) loads the tracked file
list once per run and reuses it for the additions, deletions and
modified-file passes, so a scan costs one directory walk plus one in-memory
lookup per file. Its options (JSON, editable via
`PUT /api/scheduled-jobs/library-scan`) are:

| Option | Default | Effect |
|--------|---------|--------|
| `scanForNew` | `true` | Add and fully process files present on disk but not tracked. |
| `reconcileDeletions` | `true` | Drop tracked rows whose files no longer exist. |
| `retagStale` | `true` | Re-normalize files whose series-metadata stamp is older than the cache record. |
| `reprocessModified` | `false` | Re-normalize tracked files whose on-disk size or last-write time differs from the stored values (±2 s tolerance for coarse filesystem timestamps). |

`reprocessModified` is useful when other tools replace or edit archives in
place. Stored size/mtime are refreshed before and after the re-normalize (and
after every in-place metadata write by ComicMaintainer), so the app's own
writes never re-trigger the pass. Leave it off for very large libraries on
slow network storage if the extra `stat` per file is noticeable.

## Web Interface Performance

### Frontend Optimizations

The web interface includes several performance optimizations:

1. **Pagination:** 100 files per page (configurable)
2. **Search Debouncing:** 300ms delay reduces API calls by 87%
3. **Server-Side Processing:** Filtering, sorting, and searching on backend
4. **Real-time Updates:** Server-Sent Events (SSE) instead of polling

### Cover Thumbnails

File-list covers (`/api/comicreader/cover`) are served from an on-disk cache
of downscaled thumbnails rather than full-size page 1 images, which typically
shrinks each cover from several hundred KB to a few tens of KB. Settings
(set via environment, e.g. `AppSettings__FileCoverThumbnailMaxWidth=360`):

| Setting | Default | Description |
|---------|---------|-------------|
| `FileCoverThumbnailMaxWidth` | `480` | Max thumbnail width in px. Images are never upscaled. `0` serves the original page bytes without re-encoding. |
| `FileCoverThumbnailFormat` | `webp` | `webp` or `jpeg`. |
| `FileCoverThumbnailQuality` | `80` | Encoder quality (1–100). |
| `FileCoverCacheEnabled` | `true` | Persist thumbnails under `FileCoverCacheDirectory` (default `{ConfigDirectory}/file-covers`). |
| `FileCoverCacheMaxMegabytes` | `512` | Soft cap; least-recently-used entries are pruned. |

The thumbnail variant (format + width + quality) is recorded with each cache
entry and included in the cover `ETag`, so changing any of these settings
regenerates entries on next request and browsers revalidate (304 responses
are still returned for unchanged covers). If a page cannot be decoded, the
original bytes are served instead.

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
- Database cache: up to 64MB per database connection

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
- **SSD/NVMe:** Optimal performance, use higher `MaxWorkers` (6-8)
- **HDD:** Lower `MaxWorkers` (2-4) to avoid disk thrashing
- **Network storage:** Lower `MaxWorkers` (2-3), increase stability checks if needed

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

ComicMaintainer publishes OpenTelemetry metrics. Enable Prometheus scraping
with `TELEMETRY__PROMETHEUSENABLED=true` (optionally protected by
`TELEMETRY__METRICSTOKEN`) or push via OTLP with `OTEL_EXPORTER_OTLP_ENDPOINT`.
Useful series (Prometheus names):

| Metric | What it tells you |
| --- | --- |
| `comicmaintainer_files_processed_total{operation,outcome}` | Processing throughput and failure rate |
| `comicmaintainer_processing_job_duration_seconds{operation,status}` | Batch job duration — compare against `MaxWorkers` changes |
| `comicmaintainer_scheduled_job_runs_total{job,outcome}` / `comicmaintainer_scheduled_job_duration_seconds` | Scheduled job health (library scan, audits, backups) |
| `comicmaintainer_metadata_provider_requests_total{provider,outcome}` | External metadata lookups |
| `comicmaintainer_metadata_provider_rate_limit_waits_total{provider,kind}` | Time lost to provider throttling (`kind=client` local limiter, `kind=server` HTTP 429) — lower `MANGADEX_REQUESTS_PER_SECOND` / `ANILIST_REQUESTS_PER_MINUTE` if `server` waits climb |
| `comicmaintainer_email_queue_depth` / `comicmaintainer_email_sends_total{outcome}` | Ereader email backlog |
| `http_server_request_duration_seconds` | API latency (ASP.NET Core instrumentation) |
| `process_runtime_dotnet_gc_*`, `dotnet_*` | GC / memory pressure (runtime instrumentation) |

Exact names can vary slightly with the exporter version (unit suffixes); check
`/metrics` for the list.

Other things to watch:

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
2. Reduce `MaxWorkers` if CPU/disk is saturated
3. Check if files are actually being modified or skipped (normalized)
4. Enable debug logging to identify bottlenecks

### Slow Web Interface

**Symptoms:** Web UI is sluggish or unresponsive

**Solutions:**
1. Reduce pagination size (if customized)
2. Check browser console for JavaScript errors
3. Clear browser cache

### High Memory Usage

**Symptoms:** Container uses excessive RAM

**Solutions:**
1. Reduce `MaxWorkers`
2. Set Docker memory limits
3. Vacuum database to reclaim space

### High CPU Usage

**Symptoms:** CPU constantly at 100%

**Solutions:**
1. Reduce `MaxWorkers`
2. Check for stuck jobs (cancel and retry)
3. Ensure watcher is not processing same files repeatedly
4. Check if files are already normalized (should skip processing)

## Best Practices

### For Small Libraries (<1000 files)
```bash
docker run \
  -e AppSettings__MaxWorkers=2 \
  ...
```

### For Medium Libraries (1000-5000 files)
```bash
docker run \
  -e AppSettings__MaxWorkers=4 \
  ...
```

### For Large Libraries (>5000 files)
```bash
docker run \
  -e AppSettings__MaxWorkers=6 \
  ...
```

### For Production Systems
```bash
docker run \
  -e AppSettings__MaxWorkers=4 \
  --memory=2g \
  --cpus=4 \
  ...
```

Set the maximum log file size (for example 10MB) under **Settings → Log Rotation**.

## Summary

Performance tuning is about finding the right balance for your specific use case. Start with the defaults and adjust based on:
- Your library size
- Available system resources
- Expected usage patterns
- Storage performance

Monitor the application using logs and health checks, and adjust settings incrementally to find the optimal configuration for your setup.

For additional help, see:
- [README.md](../README.md) - General usage documentation
- [DEBUG_LOGGING_GUIDE.md](DEBUG_LOGGING_GUIDE.md) - Debugging guide
- [CONTRIBUTING.md](../CONTRIBUTING.md) - Development guide
