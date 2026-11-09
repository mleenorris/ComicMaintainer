# Performance Tuning Guide

This guide covers the settings that affect ComicMaintainer's performance, and the limits on what it will read. Start with the defaults and change one thing at a time.

## Contents

- [Processing Files](#processing-files)
- [Database](#database)
- [Web Interface](#web-interface)
- [Comic Pages and Covers](#comic-pages-and-covers)
- [Archive Limits](#archive-limits)
- [Resources](#resources)
- [Monitoring](#monitoring)
- [Troubleshooting](#troubleshooting)

## Processing Files

### `MaxWorkers`

The number of archives processed at the same time. **Default: 4.**

This is one shared limit for the watcher, API metadata edits and every running batch job, so several jobs running together share these slots rather than each getting their own. One archive is never processed by two operations at once.

Set it with the `AppSettings__MaxWorkers` environment variable. It is read at startup.

```bash
docker run -e AppSettings__MaxWorkers=6 ...
```

| Storage | Suggested |
|---|---|
| SSD or NVMe | 4-8 |
| Hard disk | 2-4 |
| Network storage | 2-3 |

Lower it on a machine that is short of memory or CPU.

### File Stability Delay

The watcher waits until a new file has stopped changing before it processes it. **Default: 30 seconds** (`AppSettings__WatcherFileStabilityDelaySeconds`). A slow network copy may need longer. A shorter delay makes new files appear sooner.

### Large Batches

Use the batch actions in the web interface. Batch jobs keep running if you close the page, and report progress over real-time updates.

Files that are already correct are left alone, so running a job twice costs little.

### Incremental Library Scans

The **Library Scan** scheduled job (`library-scan`) loads the tracked file list once per run and reuses it for the additions, deletions and modified-file passes, so a scan costs one directory walk plus one in-memory lookup per file. Its options (JSON, editable with `PUT /api/scheduled-jobs/library-scan`) are:

| Option | Default | Effect |
|---|---|---|
| `scanForNew` | `true` | Add and fully process files present on disk but not tracked. |
| `reconcileDeletions` | `true` | Drop tracked rows whose files no longer exist. |
| `retagStale` | `true` | Re-normalize files whose series-metadata stamp is older than the cache record. |
| `reprocessModified` | `false` | Re-normalize tracked files whose on-disk size or last-write time differs from the stored values (±2 s tolerance for coarse filesystem timestamps). |

`reprocessModified` is useful when other tools replace or edit archives in place. Stored size and modification time are refreshed after every metadata write the app makes and, once the re-normalize job completes, for each file it normalized successfully, so the app's own writes never re-trigger the pass. A changed file whose re-normalize failed or could not be queued keeps its old values and is retried by the next scan. Leave it off for very large libraries on slow network storage if the extra `stat` per file is noticeable.

### Metadata Providers

Every enabled provider is asked at the same time for each lookup. The two AniList providers share a limit of 28 requests a minute, so large Refresh All or Match Unmatched jobs run at about 14 series a minute while AniList is enabled. Disable AniList for faster bulk runs, or raise `ANILIST_REQUESTS_PER_MINUTE` only within AniList's published limit.

## Database

SQLite is configured at each connection:

- WAL mode, so writers do not block readers
- `synchronous=NORMAL`, which is safe with WAL and much faster than `FULL`
- Temporary tables in memory
- A 64 MB page cache per connection (fixed)
- 256 MB of memory-mapped I/O

`PRAGMA optimize` runs after each scheduled database cleanup, so the query planner's statistics stay current without any action from you.

### Reclaiming Disk Space

SQLite does not shrink its file when rows are deleted. A database backup is written with `VACUUM INTO`, so a backup is already compacted. To compact the live database after removing a large part of the library:

1. Stop the container.
2. Copy `comicmaintainer.db` somewhere safe.
3. On the host, run `sqlite3 /path/to/config/comicmaintainer.db "VACUUM;"`
4. Start the container.

Keep the database on local block storage. SQLite over NFS or SMB risks corruption.

## Web Interface

- The library loads series 60 at a time as you scroll.
- Filtering, sorting and search run on the server.
- Real-time updates use Server-Sent Events rather than polling.
- Responses are compressed with Brotli or gzip.
- Read and unread views are cached per user, so marking comics read affects only that user.

## Comic Pages and Covers

### Pages

The reader keeps a 16 MiB in-memory cache of each archive's sorted page list, so turning a page or counting pages does not list the archive again. There is no server-side cache of page images: browsers and the service worker already keep them.

Pages are sent with `Cache-Control: private, max-age=3600`, a strong `ETag` and `Last-Modified`, taken from the archive's size and modification time. A revalidation answers 304 after a `stat`, without opening the archive.

### Solid RAR Archives

The pages of a solid RAR can only be decompressed from the start of the archive, so page turns in a large solid CBR are slower than in a CBZ. If that matters, convert such files to CBZ.

### Cover Thumbnails

Library covers (`/api/comicreader/cover`) are served from an on-disk cache of downscaled thumbnails rather than full-size first pages. Set these with environment variables, for example `AppSettings__FileCoverThumbnailMaxWidth=360`:

| Setting | Default | Description |
|---|---|---|
| `FileCoverThumbnailMaxWidth` | `480` | Maximum thumbnail width in pixels. Images are never enlarged. `0` serves the original page without re-encoding. |
| `FileCoverThumbnailFormat` | `webp` | `webp` or `jpeg`. |
| `FileCoverThumbnailQuality` | `80` | Encoder quality, 1 to 100. |
| `FileCoverCacheEnabled` | `true` | Keep thumbnails on disk under `FileCoverCacheDirectory` (default `{ConfigDirectory}/file-covers`). |
| `FileCoverCacheMaxMegabytes` | `512` | Soft cap. The least recently used entries are removed; a thumbnail's last-used time is refreshed at most once a day. |

Covers are sent with `Cache-Control: private, max-age=3600` and a strong `ETag` that includes the source file's size and modification time and the thumbnail variant, so changing any of these settings regenerates entries on the next request. A revalidation answers 304 without reading the thumbnail or the archive. If a page cannot be decoded, the original image is served.

## Archive Limits

These limits stop a crafted or broken archive from using up the server's memory or disk. They are far above what real comics need.

### Reading

| Limit | Value |
|---|---|
| One page, uncompressed | 100 MiB |
| `ComicInfo.xml`, uncompressed | 8 MiB |
| Entries in an archive | 20,000 |
| Compression ratio, for an entry over 16 MiB | 100:1 |
| RAR 5 decompression dictionary | 256 MiB |
| RAR 5 archive comment | 1 MiB |

An archive over a limit gets `422` with a reason naming the entry and the limit. It is logged as a warning, `Refused to read <file>: <reason>`, once per file version.

The ratio check can also refuse a genuine, nearly blank uncompressed page, such as a BMP over 16 MiB. The error says so; repack that page as JPEG, PNG or WebP.

### Rewriting

These apply when tagging, converting to CBZ or embedding a cover:

| Limit | Value |
|---|---|
| One entry, uncompressed | 1 GiB |
| Compression ratio, for an entry over 16 MiB | 100:1 |
| Total written | 20 times the archive's size (all volumes of a multi-volume set), at least 2 GiB, at most 64 GiB |

A refused archive is left untouched, and the job result names the reason.

### Formats

Archives are recognised from their content, whatever the extension: ZIP (Store, Deflate, Deflate64) and RAR 1.5 to 5.x. Encrypted archives, 7z, tar and gzip are not supported, and a following part with no RAR signature is not joined to the file. Repack such files as CBZ.

## Resources

ComicMaintainer runs as one process, so there is no web-worker count to tune. Each SQLite connection can use up to 64 MB for its page cache.

Set a memory limit for the container so a large batch cannot affect the rest of the host:

```yaml
services:
  comicmaintainer:
    # ... other settings ...
    deploy:
      resources:
        limits:
          cpus: '4'
          memory: 2G
```

Use `docker stats comicmaintainer` to see what it uses in practice.

## Monitoring

### Logs

```bash
docker logs --tail 100 comicmaintainer
docker logs -f comicmaintainer
```

Log files are in the config folder: `app.log`, `debug.log`, `watcher.log`, `metadata.log` and `auth.log`. Set the size at which they rotate under **Settings > Log Rotation**. The change applies when the app restarts. The default is 10 MB.

### Health

```bash
# The app is up: answers "Healthy" with 200
curl http://localhost:5000/health

# The database is reachable too
curl http://localhost:5000/health/ready
```

### Metrics

ComicMaintainer publishes OpenTelemetry metrics. Enable Prometheus scraping with `TELEMETRY__PROMETHEUSENABLED=true` (optionally protected by `TELEMETRY__METRICSTOKEN`), or push them with `OTEL_EXPORTER_OTLP_ENDPOINT`. Useful series, with their Prometheus names:

| Metric | What it tells you |
|---|---|
| `comicmaintainer_files_processed_total{operation,outcome}` | Processing throughput and failure rate |
| `comicmaintainer_processing_job_duration_seconds{operation,status}` | Batch job duration, to compare against `MaxWorkers` changes |
| `comicmaintainer_scheduled_job_runs_total{job,outcome}` / `comicmaintainer_scheduled_job_duration_seconds` | Scheduled job health (library scan, audits, backups) |
| `comicmaintainer_metadata_provider_requests_total{provider,outcome}` | External metadata lookups |
| `comicmaintainer_metadata_provider_rate_limit_waits_total{provider,kind}` | Time lost to provider throttling (`kind=client` is the local limiter, `kind=server` is HTTP 429). Lower `MANGADEX_REQUESTS_PER_SECOND` or `ANILIST_REQUESTS_PER_MINUTE` if `server` waits climb |
| `comicmaintainer_email_queue_depth` / `comicmaintainer_email_sends_total{outcome}` | E-reader email backlog |
| `http_server_request_duration_seconds` | API latency |

Exact names can vary slightly with the exporter version (unit suffixes). `/metrics` shows the current list.

## Troubleshooting

| Symptom | Things to try |
|---|---|
| Files take a long time to process | Check whether the storage is slow; lower `MaxWorkers` if the CPU or disk is saturated |
| The web interface is sluggish | Check the browser console for errors; clear the browser's cache |
| High memory use | Lower `MaxWorkers`; set a container memory limit |
| High CPU use | Lower `MaxWorkers`; check for a stuck job and cancel it |
| Page turns are slow in one comic | It may be a large solid CBR; convert it to CBZ |
| A comic will not open | Check the [Archive Limits](#archive-limits); the log says which one was hit |

## See Also

- [README](../README.md)
- [Reverse Proxy Guide](REVERSE_PROXY.md)
- [Docker Deployment Guide](DOCKER_DEPLOYMENT_DOTNET.md)
