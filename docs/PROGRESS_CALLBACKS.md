# Progress Callbacks via Server-Sent Events (SSE)

## Overview

ComicMaintainer now provides real-time progress updates for batch processing jobs through Server-Sent Events (SSE), eliminating the need for frequent polling.

## How It Works

### Backend (Python)

The `job_manager.py` module broadcasts progress updates via SSE after each item is processed:

1. **Job Start**: When a job starts, status changes from `queued` to `processing` and is broadcast
2. **Item Progress**: After each item completes, progress is broadcast with:
   - Current count of processed items
   - Total items
   - Success count
   - Error count
   - Percentage complete
3. **Job Completion**: Final status (`completed`, `failed`, or `cancelled`) is broadcast

**Key Functions:**
- `_broadcast_job_progress()` - Broadcasts progress updates via SSE
- Updates are sent after **every item** completes, not just every 10 items

### Frontend (JavaScript)

The web interface subscribes to SSE events and updates the UI in real-time:

1. **SSE Connection**: `EventSource` connects to `/api/events/stream`
2. **Event Handler**: `handleJobUpdatedEvent()` processes job updates
3. **UI Updates**: Progress bar and details update immediately
4. **Fallback Polling**: Reduced to 5-second intervals (from 500ms) as a backup

## Performance Benefits

### Before (Polling-Based)
- Poll every 500ms (2 requests per second)
- High network overhead
- Delayed updates (up to 500ms lag)
- Increased server load

### After (SSE-Based)
- Real-time push notifications
- **90% reduction in network requests**
- Instant updates (no polling lag)
- Lower server load
- Fallback polling at 5-second intervals

## Example Event Flow

```javascript
// 1. Job starts
{
  "type": "job_updated",
  "data": {
    "job_id": "abc-123",
    "status": "processing",
    "progress": {
      "processed": 0,
      "total": 100,
      "success": 0,
      "errors": 0,
      "percentage": 0
    }
  }
}

// 2. After each item
{
  "type": "job_updated",
  "data": {
    "job_id": "abc-123",
    "status": "processing",
    "progress": {
      "processed": 1,
      "total": 100,
      "success": 1,
      "errors": 0,
      "percentage": 1.0
    }
  }
}

// ... continues for each item ...

// 3. Job completes
{
  "type": "job_updated",
  "data": {
    "job_id": "abc-123",
    "status": "completed",
    "progress": {
      "processed": 100,
      "total": 100,
      "success": 98,
      "errors": 2,
      "percentage": 100.0
    }
  }
}
```

## Testing

Run the test suite to verify progress callbacks work correctly:

```bash
python3 test_progress_callbacks.py
```

Tests verify:
- ✅ Job updates are broadcast via SSE
- ✅ Multiple clients can subscribe
- ✅ Progress includes detailed information
- ✅ Real-time notifications eliminate polling

## Architecture

```
┌─────────────────┐         ┌──────────────────┐         ┌────────────────┐
│   Job Manager   │────────▶│ Event Broadcaster│────────▶│  Web Clients   │
│                 │ Callback│                  │   SSE   │                │
│ _process_job()  │         │ broadcast_job_   │         │ EventSource    │
│                 │         │ updated()        │         │                │
└─────────────────┘         └──────────────────┘         └────────────────┘
        │                            │                            │
        │ After each item            │ Push to all               │
        │ completes                  │ subscribers               │ Update UI
        ▼                            ▼                            ▼
```

## Benefits Summary

- ✅ **Real-time updates**: No polling lag
- ✅ **Better UX**: Instant feedback to users
- ✅ **Reduced load**: 90% fewer HTTP requests
- ✅ **Scalable**: Handles multiple concurrent jobs
- ✅ **Reliable**: Fallback polling for connection issues

## Related Files

- `src/job_manager.py` - Broadcasts progress updates
- `src/event_broadcaster.py` - SSE broadcasting infrastructure
- `templates/index.html` - Frontend SSE event handling
- `test_progress_callbacks.py` - Test suite
