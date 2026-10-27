# ComicMaintainer API Documentation

This document provides detailed information about the ComicMaintainer REST API endpoints.

## Table of Contents

- [Overview](#overview)
- [Base URL](#base-url)
- [Authentication](#authentication)
- [Health Check](#health-check)
- [Diagnostics](#diagnostics)
- [File Management](#file-management)
- [Job Management](#job-management)
- [Settings](#settings)
- [Suwayomi Downloads](#suwayomi-downloads)
- [Events](#events)
- [Database Backups](#database-backups)
- [Request Correlation](#request-correlation)
- [Error Responses](#error-responses)

## Overview

The ComicMaintainer API is a RESTful API that provides programmatic access to the comic file management system. The API returns JSON responses and uses standard HTTP response codes.

## Base URL

```
http://localhost:5000
```

Replace `localhost:5000` with your actual host and port.

## Authentication

Currently, the API does not require authentication. Consider adding authentication if exposing the API to the internet.

## Health Check

### GET /health
### GET /api/health

Health check endpoint for container orchestration.

**Response (200 OK - Healthy):**
```json
{
  "status": "healthy",
  "version": "1.0.0",
  "checks": {
    "watched_dir": "ok",
    "database": "ok",
    "watcher": "running"
  },
  "file_count": 1234
}
```

**Response (503 Service Unavailable - Unhealthy):**
```json
{
  "status": "unhealthy",
  "version": "1.0.0",
  "checks": {
    "watched_dir": "error: Directory not found",
    "database": "ok",
    "watcher": "not_running"
  }
}
```

## Diagnostics

### GET /api/diagnostics

A single snapshot of the running instance, intended for troubleshooting and bug reports.
Unlike `/health`, which is a liveness probe for container orchestration, this returns the
full operational picture in one call.

**Authorization:** administrator (`CanAdminister`). Non-administrators receive `403`.

**Response (200 OK):**
```json
{
  "generatedAtUtc": "2026-09-29T16:46:45.126Z",
  "correlationId": "0HNOU94L3SUEB:00000001",
  "application": {
    "version": "2.0.310.0",
    "environment": "Production",
    "startedAtUtc": "2026-09-29T16:45:20.625Z",
    "uptimeSeconds": 84
  },
  "runtime": {
    "framework": ".NET 10.0.12",
    "operatingSystem": "Ubuntu 24.04.5 LTS",
    "architecture": "X64",
    "processorCount": 4,
    "workingSetBytes": 221270016,
    "managedHeapBytes": 36993696,
    "threadCount": 24
  },
  "health": {
    "status": "Healthy",
    "totalDurationMs": 16,
    "entries": [
      { "name": "database", "status": "Healthy", "description": "Database is reachable.", "durationMs": 8 }
    ]
  },
  "watcher": {
    "running": true,
    "renameEnabled": true,
    "normalizeEnabled": true,
    "fileStabilityDelaySeconds": 30
  },
  "library": { "total": 1234, "processed": 1200, "unprocessed": 34, "duplicates": 2 },
  "jobs": { "total": 12, "running": 0, "interrupted": 1, "failed": 0, "lastStartedUtc": "2026-09-29T15:02:11.000Z" },
  "storage": [
    { "name": "Watched", "path": "/watched", "exists": true, "writable": true, "freeBytes": 89380139008, "totalBytes": 154894188544 }
  ],
  "logs": {
    "directory": "/Config",
    "files": [
      { "name": "debug20260929.log", "sizeBytes": 126002, "lastWriteUtc": "2026-09-29T16:46:45.154Z" }
    ]
  }
}
```

Each section is gathered independently. A section that cannot be read is replaced by
`{ "error": "..." }` rather than failing the whole request, so a broken probe (for example an
unmounted storage path) never hides the rest of the snapshot.

## File Management

### GET /api/files

List all comic files in the watched directory.

**Query Parameters:**
- `page` (optional) - Page number (default: 1)
- `per_page` (optional) - Results per page (default: 100, max: 100)
- `search` (optional) - Search term for filtering files
- `filter` (optional) - Status filter: `all`, `marked`, `unmarked`, `duplicates`

**Response:**
```json
{
  "files": [
    {
      "path": "/comics/Batman/Batman #001.cbz",
      "size": 12345678,
      "modified": 1234567890.0,
      "is_processed": true,
      "is_duplicate": false
    }
  ],
  "pagination": {
    "page": 1,
    "per_page": 100,
    "total_pages": 5,
    "total_files": 450
  }
}
```

### GET /api/files/{encodedFilePath}/tags

Get the metadata stored in a file's `ComicInfo.xml`.

`{encodedFilePath}` is the file's full path, encoded as UTF-8 and then as base64url
(`-` and `_` in place of `+` and `/`; the trailing `=` padding is optional). The file
must be inside the watched directory. For example, `/comics/Batman/Batman #001.cbz`
is encoded as `L2NvbWljcy9CYXRtYW4vQmF0bWFuICMwMDEuY2J6`:

```bash
printf '%s' '/comics/Batman/Batman #001.cbz' | base64 | tr -d '\n=' | tr '+/' '-_'
```

```python
import base64
path = '/comics/Batman/Batman #001.cbz'
encoded = base64.urlsafe_b64encode(path.encode()).decode().rstrip('=')
```

**Response:**
```json
{
  "series": "Batman",
  "title": "The Dark Knight",
  "issue": "1",
  "volume": "2016",
  "publisher": "DC Comics",
  "year": 2023,
  "summary": "Gotham's protector returns.",
  "authors": ["Tom King"],
  "tags": ["Superhero"],
  "isUserEdited": false,
  "userLockedFieldsMask": 0
}
```

`authors` holds the `<Writer>` entries and `tags` the `<Tag>` entries. Fields the file
does not set are `null` (or `[]`). A file without a `ComicInfo.xml` reports only `series`,
taken from its folder name, and `issue`, taken from its file name. `isUserEdited` and
`userLockedFieldsMask` are not stored in the file: they are always `false` and `0` here,
and the endpoints below ignore them.

`400` when the path cannot be decoded (`Invalid file path`) or is outside the watched
directory (`File path is outside the allowed directory`). `404` when the file does not
exist, is not a comic archive, or cannot be read.

### PUT /api/files/{encodedFilePath}/tags

Replace the metadata of one file. Requires library write permission. `{encodedFilePath}`
is encoded as for `GET`, and the body takes the fields of the `GET` response.

**Request Body:**
```json
{
  "series": "Batman",
  "issue": "1",
  "title": "The Dark Knight",
  "publisher": "DC Comics",
  "year": 2023,
  "authors": ["Tom King"]
}
```

Returns `200` with an empty body. The metadata replaces the file's `ComicInfo.xml`
rather than being merged into it: every field the body leaves out is removed, as is
every text or number field set to `null`. `authors` and `tags` cannot be `null` (that
is rejected with a `400` validation error); send `[]` or leave them out to clear them.
Elements the API does not model, such as `<Genre>` or `<Penciller>`, are always
dropped, even when you send back exactly what `GET` returned.

`400` for the same path errors as `GET`, and `400` (`Failed to update tags`) when the
file does not exist or is not a comic archive.

### POST /api/files/tags

Write the same metadata to one or more files. Requires library write permission.

`files` lists plain paths (not base64url-encoded); empty entries are ignored.
`metadata` takes the fields of the `GET` response and replaces each file's
`ComicInfo.xml` as `PUT` does. Every field it leaves out, including `issue`, is removed
from every file, and leaving out `metadata` altogether clears all their tags. Use `PUT`
for per-file fields such as `issue`.

**Request Body:**
```json
{
  "files": ["/comics/Batman/Batman #001.cbz"],
  "metadata": {
    "series": "Batman",
    "issue": "1",
    "title": "The Dark Knight",
    "publisher": "DC Comics",
    "year": 2023
  }
}
```

Returns `200` with an empty body. A file that cannot be written, for example because
it does not exist, is skipped without being reported, so check the result with `GET`.

A request whose `files` is missing or an empty list is rejected with `400`
(`No files provided`). A request naming any file outside the watched directory is
rejected with `400` (`One or more file paths are outside the allowed directory`)
before any file is updated.

### POST /api/files/cleanup-stale

Remove stale database entries for files that no longer exist on disk. This is useful when files have been moved or deleted outside the application, leaving orphaned records in the database.

**Response:**
```json
{
  "success": true,
  "removedCount": 15,
  "message": "Removed 15 stale database entries"
}
```

**Error Response (499 - Cancelled):**
```json
{
  "error": "Cleanup operation was cancelled"
}
```

**Error Response (500 - Internal Server Error):**
```json
{
  "error": "Failed to cleanup stale entries"
}
```

**Note:** This endpoint scans all files in the database and removes entries where the file no longer exists on the filesystem. The operation also removes entries from the in-memory cache to ensure consistency.

## Job Management

### POST /api/jobs/process-all

Start asynchronous processing of all files.

**Response:**
```json
{
  "job_id": "job-abc123",
  "total_items": 450
}
```

When there is no file to work on, no job is created and the response is
`{ "job_id": "00000000-0000-0000-0000-000000000000", "total_items": 0 }`. The
other library-wide starters (`rename-all`, `normalize-all`, `process-unmarked`,
`rename-unmarked`, `normalize-unmarked`, `remove-metadata-all`) answer the same
way; the web UI then reports that there is nothing to do instead of tracking a job.

### POST /api/jobs/process-selected

Start asynchronous processing of selected files.

**Request Body:**
```json
{
  "files": [
    "/comics/Batman/Batman #001.cbz",
    "/comics/Superman/Superman #001.cbz"
  ]
}
```

**Response:**
```json
{
  "job_id": "job-xyz789",
  "total_items": 2
}
```

### GET /api/jobs/{job_id}

Get status of a specific job.

**Response:**
```json
{
  "id": "job-abc123",
  "status": "processing",
  "created_at": 1234567890.0,
  "started_at": 1234567891.0,
  "progress": {
    "processed": 45,
    "total": 450,
    "success": 44,
    "errors": 1,
    "percentage": 10
  },
  "results": [
    {
      "file": "/comics/Batman/Batman #001.cbz",
      "status": "success",
      "new_path": "/comics/Batman - Chapter 0001.cbz"
    }
  ]
}
```

### GET /api/jobs

List all jobs.

**Response:**
```json
{
  "jobs": [
    {
      "id": "job-abc123",
      "status": "completed",
      "created_at": 1234567890.0,
      "progress": {
        "processed": 450,
        "total": 450,
        "percentage": 100
      }
    }
  ]
}
```

### DELETE /api/jobs/{job_id}

Delete a job from history.

**Response:**
```json
{
  "success": true
}
```

### POST /api/jobs/{job_id}/cancel

Cancel a running job.

**Response:**
```json
{
  "success": true,
  "job_id": "job-abc123"
}
```

## Settings

Changing a setting requires the `Admin` role (`CanAdminister` policy). Each
`PUT /api/settings/...` endpoint below also accepts `POST`, a legacy alias with the
same body and response.

### GET /api/settings/filename-format

Get the current filename format template and the built-in default (the
format "Reset Filename Format" on the settings page restores).

**Response:**
```json
{
  "format": "{series} - Chapter {issue}",
  "default": "{series} - Chapter {issue}"
}
```

### PUT /api/settings/filename-format

Update the filename format template.

**Request Body:**
```json
{
  "format": "{series} v{volume} #{issue_no_pad}"
}
```

**Response:**
```json
{
  "message": "Filename format updated successfully"
}
```

A blank format is not saved; the response is `500` with
`{"error": "Failed to update filename format"}`.

### GET /api/settings/issue-number-padding

Get the minimum number of digits `{issue}` is padded to in generated filenames
(default 4, so issue 1 becomes `0001`). `{issue_no_pad}` is never padded.

**Response:**
```json
{
  "padding": 4
}
```

### PUT /api/settings/issue-number-padding

Set issue number padding.

**Request Body:**
```json
{
  "padding": 6
}
```

**Response:**
```json
{
  "message": "Issue number padding updated successfully"
}
```

A negative padding is not saved; the response is `500` with
`{"error": "Failed to update issue number padding"}`.

### GET /api/settings/watcher-enable-rename
### GET /api/settings/watcher-enable-normalize

Whether processing renames files, or normalizes their metadata.

**Response:**
```json
{
  "enabled": true
}
```

### PUT /api/settings/watcher-enable-rename
### PUT /api/settings/watcher-enable-normalize

Turn renaming or normalizing on or off. Despite the names, these settings apply to every
processing run, including files processed on request and processing jobs, not only to
the watcher: with a step off, processing skips it but still marks the file as renamed or
normalized. With both off, the watcher still tracks files but processes none of them
automatically.

**Request Body:**
```json
{
  "enabled": false
}
```

**Response:**
```json
{
  "message": "Watcher enable rename updated successfully"
}
```

(`Watcher enable normalize updated successfully` for the normalize setting.)

### GET /api/watcher

Get the file watcher state. `GET /api/watcher/status` is a legacy alias.

**Response:**
```json
{
  "running": true,
  "enabled": true
}
```

`enabled` always equals `running`. The watcher runs whenever the watched directory
exists and cannot be stopped through the API; use the `watcher-enable-rename` and
`watcher-enable-normalize` settings above to control what it does.

`PUT /api/watcher` and `POST /api/watcher/enable` are deprecated no-ops kept for old
clients and scheduled for removal in v3.0. They change nothing: `PUT` returns the
current state as `enabled` (equal to `running`) with a `message` saying the watcher is
controlled by the rename and normalize settings, and `POST` returns `200` with an
empty body.

## Suwayomi Downloads

Queue downloads for missing issues on a connected [Suwayomi](https://github.com/Suwayomi/Suwayomi-Server) server. All endpoints require authentication; `POST` requires library write permission.

### GET /api/suwayomi/status

Probe the configured server.

**Response:**
```json
{
  "enabled": true,
  "configured": true,
  "connected": true,
  "serverName": "Suwayomi-Server",
  "version": "1.1.0",
  "error": null
}
```

### GET /api/suwayomi/series

Search the Suwayomi library by title.

**Query Parameters:**
- `query` (string, optional) - Title to match. Omit to list the first entries.
- `limit` (int, optional) - Maximum results, 1-100 (default 20).

**Response:**
```json
{
  "series": [
    { "id": 4, "title": "Solo Leveling", "inLibrary": true, "thumbnailUrl": "/api/v1/manga/4/thumbnail" }
  ]
}
```

### GET /api/suwayomi/series/{id}/chapters

List the chapters Suwayomi knows about for one of its series.

**Response:**
```json
{
  "chapters": [
    { "id": 41, "name": "Chapter 1", "chapterNumber": 1, "isDownloaded": true }
  ]
}
```

### POST /api/suwayomi/download-missing

Queue downloads for the issue numbers a series is missing locally.

**Request Body:**
```json
{
  "seriesTitle": "Solo Leveling",
  "alternateTitles": ["Ore dake Level Up na Ken", "Na Honjaman Level Up"],
  "suwayomiSeriesId": 4,
  "issueNumbers": [2, 3]
}
```

`suwayomiSeriesId` is optional; when omitted the series is resolved by matching `seriesTitle` against the Suwayomi library.

`alternateTitles` is optional too: the series' other titles (the web UI sends its canonical title and aliases), for when Suwayomi lists the series under a different name. Without `suwayomiSeriesId` the series is resolved by, in order, an exact match on `seriesTitle`, an exact match on each of `alternateTitles` in the order given, and a prefix/substring match on `seriesTitle`. Matching ignores punctuation and case, and `alternateTitles` are never matched partially. Both title fields are ignored when `suwayomiSeriesId` is given.

**Response:**
```json
{
  "series": { "id": 4, "title": "Solo Leveling", "inLibrary": true },
  "queuedChapters": [
    { "id": 42, "name": "Chapter 2", "chapterNumber": 2, "isDownloaded": false }
  ],
  "unmatchedIssueNumbers": [3],
  "alreadyDownloadedIssueNumbers": []
}
```

Returns `400` with `{ "error": "..." }` when the integration is disabled or misconfigured, the server is unreachable, or no matching series is in the Suwayomi library.

## Events

### GET /api/events

Server-Sent Events (SSE) endpoint for real-time updates.

**Event Types:**

1. **file_processed**
   ```json
   {
     "type": "file_processed",
     "data": {
       "file": "/comics/Batman/Batman #001.cbz",
       "success": true
     }
   }
   ```

2. **job_updated**
   ```json
   {
     "type": "job_updated",
     "data": {
       "job_id": "job-abc123",
       "status": "processing",
       "progress": {
         "processed": 45,
         "total": 450,
         "percentage": 10
       }
     }
   }
   ```

3. **watcher_status**
   ```json
   {
     "type": "watcher_status",
     "data": {
       "enabled": true
     }
   }
   ```

### GET /api/events/stats

Get event broadcasting statistics.

**Response:**
```json
{
  "active_clients": 3,
  "total_events_broadcast": 1234
}
```

## Database Backups

Online backups of the SQLite database (taken with `VACUUM INTO`, so they are
consistent while the app is running). Backups are written to
`{CONFIG_DIR}/backups` (override with `AppSettings__DatabaseBackupDirectory`)
and named `comicmaintainer-yyyyMMdd-HHmmss.db` (UTC; safety backups taken before
a restore end in `-prerestore`).

**Every endpoint — including the reads — requires the `Admin` role
(`CanAdminister` policy).** A backup is a full copy of the database and
contains password hashes and stored credentials (SMTP, ComicVine, Suwayomi).
The `{name}` parameter must be a generated backup name; anything else
(including path traversal attempts) is rejected with `400`.

### GET /api/backups

List backups, newest first.

```json
{
  "backups": [
    { "name": "comicmaintainer-20261020-040000.db", "size_bytes": 1048576, "created_utc": "2026-10-20T04:00:00Z" }
  ],
  "restore_pending": false
}
```

### POST /api/backups

Create a backup now. Returns the new backup (`name`, `size_bytes`, `created_utc`).
Returns `409` if the database file does not exist.

### GET /api/backups/{name}/download

Download a backup (`application/vnd.sqlite3`). `404` if it does not exist.

### DELETE /api/backups/{name}

Delete a backup. `404` if it does not exist.

### POST /api/backups/{name}/restore

Stage a restore from an existing backup. The file is validated (SQLite header,
`PRAGMA integrity_check`, ComicMaintainer schema), a safety backup of the
current database is taken, and the file is staged as
`comicmaintainer.db.restore-pending`. **The restore is applied on the next
restart**, before migrations run.

```json
{
  "source": "comicmaintainer-20261020-040000.db",
  "safety_backup": { "name": "comicmaintainer-20261021-090000-prerestore.db", "size_bytes": 1050000, "created_utc": "2026-10-21T09:00:00Z" },
  "restart_required": true,
  "message": "Restore staged. Restart ComicMaintainer to apply it."
}
```

`400` when the backup fails validation, `404` when it does not exist.

### POST /api/backups/upload-restore

Upload a database file as `multipart/form-data` (field `file`, max 2 GiB) and
stage it as a restore. Same validation, safety backup and response as above.

```bash
curl -X POST -H "Authorization: Bearer $TOKEN" \
  -F "file=@comicmaintainer-20261020-040000.db" \
  http://localhost:5000/api/backups/upload-restore
```

### DELETE /api/backups/restore-pending

Discard a staged restore before restarting. `404` when none is pending.

A `database-backup` scheduled job (enabled by default, every 1440 minutes)
creates a backup and prunes old ones down to `DatabaseBackupRetention`
(default 7). Manage it like any other job under **Scheduled Jobs**.

## Version

### GET /api/version

Get application version.

**Response:**
```json
{
  "version": "1.0.0"
}
```

## Request Correlation

Every request is assigned a correlation ID and it is returned in the **`X-Correlation-Id`**
response header:

```bash
curl -i http://localhost:5000/api/version | grep -i x-correlation-id
# X-Correlation-Id: 0HNOU94L3SUDV:00000001
```

If the request already carries an `X-Correlation-Id` header, that value is reused so a trace can
span a reverse proxy or a calling service. Supplied values are sanitised (only `A-Z a-z 0-9 - _ :`
survive, so header injection is not possible) and truncated to 64 characters.

The same id is written to every log line the request produces (`[{CorrelationId}]` in the debug
log; non-request lines show `-`) and is returned in the `correlationId` field of error responses,
so a failure reported by a user can be matched to the exact server log lines:

```bash
grep '0HNOU94L3SUDV:00000001' /Config/*.log
```

## Error Responses

The API uses standard HTTP response codes:

- **200 OK** - Request succeeded
- **400 Bad Request** - Invalid request parameters
- **401 Unauthorized** - Authentication required or the token has expired
- **403 Forbidden** - Authenticated, but the account lacks the required capability
- **404 Not Found** - Resource not found
- **500 Internal Server Error** - Server error
- **503 Service Unavailable** - Service is unhealthy

**Error Response Format:**

Most endpoints return a simple object:
```json
{
  "error": "Description of the error"
}
```

An unhandled server error returns an [RFC 7807](https://datatracker.ietf.org/doc/html/rfc7807)
problem document (`application/problem+json`) carrying the correlation id:
```json
{
  "type": "https://datatracker.ietf.org/doc/html/rfc9110#section-15.6.1",
  "title": "An unexpected error occurred.",
  "status": 500,
  "correlationId": "0HNOU94L3SUDV:00000001"
}
```

The exception message and stack trace are only included when the server runs in the Development
environment; in production the correlation id is the link to the full detail in the log.

## Rate Limiting

Currently, there is no rate limiting implemented. Consider adding rate limiting if the API is exposed to the internet.

## CORS

CORS is not currently configured. If you need to access the API from a different origin, consider adding CORS headers.

## Examples

### Using curl

```bash
# Health check
curl http://localhost:5000/health

# List files
curl http://localhost:5000/api/files?page=1&per_page=100

# Start processing all files
curl -X POST http://localhost:5000/api/jobs/process-all

# Get job status
curl http://localhost:5000/api/jobs/job-abc123

# Update filename format
curl -X PUT http://localhost:5000/api/settings/filename-format \
  -H "Content-Type: application/json" \
  -d '{"format": "{series} - Chapter {issue}"}'
```

### Using Python

```python
import requests

# Health check
response = requests.get('http://localhost:5000/health')
print(response.json())

# List files
response = requests.get('http://localhost:5000/api/files', params={'page': 1})
files = response.json()

# Start processing
response = requests.post('http://localhost:5000/api/jobs/process-all')
job = response.json()
print(f"Job ID: {job['job_id']}")
```

### Using JavaScript

```javascript
// Health check
fetch('http://localhost:5000/health')
  .then(response => response.json())
  .then(data => console.log(data));

// List files
fetch('http://localhost:5000/api/files?page=1')
  .then(response => response.json())
  .then(data => console.log(data.files));

// Start processing
fetch('http://localhost:5000/api/jobs/process-all', {
  method: 'POST'
})
  .then(response => response.json())
  .then(data => console.log('Job ID:', data.job_id));
```

## Need Help?

For questions or issues, please:
- Check the [README](../README.md)
- Review the [DEBUG_LOGGING_GUIDE](DEBUG_LOGGING_GUIDE.md)
- Open an issue on GitHub
