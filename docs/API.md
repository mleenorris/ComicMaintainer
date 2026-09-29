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
- [Events](#events)
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
    "directory": "/Config/Log",
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

### GET /api/files/tags

Get metadata tags for a specific file.

**Query Parameters:**
- `file` (required) - File path

**Response:**
```json
{
  "series": "Batman",
  "issue": "1",
  "title": "The Dark Knight",
  "year": "2023",
  "publisher": "DC Comics"
}
```

### POST /api/files/tags

Update metadata tags for a file.

**Request Body:**
```json
{
  "file": "/comics/Batman/Batman #001.cbz",
  "tags": {
    "series": "Batman",
    "issue": "1",
    "title": "The Dark Knight"
  }
}
```

**Response:**
```json
{
  "success": true,
  "file": "/comics/Batman/Batman #001.cbz"
}
```

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

### GET /api/settings/filename-format

Get the current filename format template.

**Response:**
```json
{
  "format": "{series} - Chapter {issue}",
  "default": "{series} - Chapter {issue}"
}
```

### POST /api/settings/filename-format

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
  "success": true,
  "format": "{series} v{volume} #{issue_no_pad}"
}
```

### GET /api/settings/issue-number-padding

Get issue number padding setting.

**Response:**
```json
{
  "padding": 4,
  "default": 4
}
```

### POST /api/settings/issue-number-padding

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
  "success": true,
  "padding": 6
}
```

### GET /api/watcher/enabled

Get watcher enabled state.

**Response:**
```json
{
  "enabled": true
}
```

### POST /api/watcher/enabled

Enable or disable the watcher.

**Request Body:**
```json
{
  "enabled": false
}
```

**Response:**
```json
{
  "success": true,
  "enabled": false
}
```

### GET /api/watcher/status

Get watcher process status.

**Response:**
```json
{
  "running": true,
  "enabled": true
}
```

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
grep '0HNOU94L3SUDV:00000001' /Config/Log/*.log
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
  "type": "https://tools.ietf.org/html/rfc9110#section-15.6.1",
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
curl -X POST http://localhost:5000/api/settings/filename-format \
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
