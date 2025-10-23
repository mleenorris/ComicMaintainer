# ComicMaintainer API Documentation

This document provides detailed information about the ComicMaintainer REST API endpoints.

## Table of Contents

- [Overview](#overview)
- [Base URL](#base-url)
- [Authentication](#authentication)
- [Health Check](#health-check)
- [File Management](#file-management)
- [Job Management](#job-management)
- [Settings](#settings)
- [Events](#events)
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

## Error Responses

The API uses standard HTTP response codes:

- **200 OK** - Request succeeded
- **400 Bad Request** - Invalid request parameters
- **404 Not Found** - Resource not found
- **500 Internal Server Error** - Server error
- **503 Service Unavailable** - Service is unhealthy

**Error Response Format:**
```json
{
  "error": "Description of the error"
}
```

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
- Review the [DEBUG_LOGGING_GUIDE](../DEBUG_LOGGING_GUIDE.md)
- Open an issue on GitHub
