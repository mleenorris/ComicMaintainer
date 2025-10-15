# Cache Invalidation Flow Diagram

## System Architecture

```
┌─────────────────┐         ┌──────────────────┐         ┌─────────────────┐
│                 │         │                  │         │                 │
│  Watched Dir    │────────▶│   Watcher        │────────▶│  process_file   │
│  (.cbz files)   │  detects│   (watcher.py)   │  calls  │  (process_file  │
│                 │  change │                  │         │   .py)          │
└─────────────────┘         └──────────────────┘         └────────┬────────┘
                                                                   │
                                                                   │ marks file
                                                                   ▼
                            ┌──────────────────────────────────────────────┐
                            │  Markers Module (markers.py)                 │
                            │  ┌────────────────────────────────────────┐ │
                            │  │ mark_file_processed()                  │ │
                            │  │  1. Update SQLite database             │ │
                            │  │  2. Call _update_marker_timestamp()    │ │
                            │  └────────────────┬───────────────────────┘ │
                            │                   │                          │
                            │  ┌────────────────▼───────────────────────┐ │
                            │  │ _update_marker_timestamp()             │ │
                            │  │  Write current time to:                │ │
                            │  │  /Config/.marker_update                │ │
                            │  └────────────────────────────────────────┘ │
                            └──────────────────────────────────────────────┘
                                                   │
                                                   │ timestamp updated
                                                   ▼
                            ┌──────────────────────────────────────────────┐
                            │  File System                                 │
                            │  /Config/.marker_update                      │
                            │  Contains: 1760562973.8339124               │
                            └──────────────────┬───────────────────────────┘
                                              │
                                              │ read on next request
                                              ▼
┌─────────────────┐         ┌──────────────────────────────────────────────┐
│                 │  request│  Web App (web_app.py)                        │
│  User Browser   │────────▶│  ┌────────────────────────────────────────┐ │
│                 │         │  │ list_files() API endpoint               │ │
│                 │         │  │  1. Call get_comic_files()              │ │
│                 │         │  │  2. Call get_enriched_file_list()       │ │
│                 │         │  └────────────────┬───────────────────────┘ │
│                 │         │                   │                          │
│                 │         │  ┌────────────────▼───────────────────────┐ │
│                 │         │  │ get_enriched_file_list()                │ │
│                 │         │  │  1. Get marker_update_time from file    │ │
│                 │         │  │  2. Compare with cache timestamp        │ │
│                 │         │  │  3. If newer → invalidate cache         │ │
│                 │         │  │  4. Trigger async rebuild               │ │
│                 │         │  │  5. Return stale cache immediately      │ │
│                 │         │  └────────────────┬───────────────────────┘ │
│                 │         │                   │                          │
│                 │         │  ┌────────────────▼───────────────────────┐ │
│                 │         │  │ rebuild_enriched_cache_async()          │ │
│                 │         │  │  (background thread)                    │ │
│                 │         │  │  1. Read all file markers from DB       │ │
│                 │         │  │  2. Build enriched file list            │ │
│                 │         │  │  3. Store marker_update_time in cache   │ │
│                 │         │  │  4. Set cache_rebuilding = false        │ │
│                 │         │  └────────────────┬───────────────────────┘ │
│                 │         └────────────────────┼──────────────────────────┘
│                 │  response (stale data)       │
│                 │◀─────────────────────────────┘
│                 │  + cache_rebuilding: true
│                 │
│  ┌──────────────▼─────────────┐
│  │ Frontend JavaScript         │
│  │ 1. Detects cache_rebuilding │
│  │ 2. Starts polling (2s)      │
│  │ 3. Waits for rebuild done   │
│  └──────────────┬──────────────┘
│                 │
│                 │ poll every 2s
│                 │
│  ┌──────────────▼──────────────┐
│  │ Poll: cache_rebuilding?     │
│  │ Response: false (done)      │
│  └──────────────┬───────────────┘
│                 │
│                 │ auto-refresh
│                 │
│  ┌──────────────▼──────────────┐
│  │ loadFiles() - Refresh List  │
│  │ Shows updated markers ✓     │
│  └─────────────────────────────┘
│                 │
└─────────────────┘
```

## Timeline Comparison

### Before Fix
```
Time (seconds)    0        5        10       15       60       120      180
                  │────────│────────│────────│────────│────────│────────│
Watcher           ├─Process─────────┤
                            └─Mark─┤
Web Cache                          [────────── Stale ─────────────────────]
User Action                                   └Refresh┤
UI Display                                            [──Still Unprocessed──]
User Action                                                   └Force Refresh─┤
UI Display                                                                  [✓]
                  
Total Time: 180+ seconds until user sees update
```

### After Fix
```
Time (seconds)    0        1        2        3        4        5        6
                  │────────│────────│────────│────────│────────│────────│
Watcher           ├─Process────┤
                          └Mark┤
Marker Update                  ├─Timestamp Updated─┤
User Action                            └Refresh────┤
Cache Check                                   ├Check Timestamp┤
Cache Rebuild                                 ├─Async Rebuild──┤
UI Polling                                    ├──Poll──┤──Poll──┤
UI Display                                                      [✓ Updated]
                  
Total Time: 2-7 seconds until user sees update
```

## Key Components

### 1. Timestamp File
- **Location**: `/Config/.marker_update`
- **Format**: Unix timestamp as float (e.g., `1760562973.8339124`)
- **Updated by**: All marker modification functions
- **Read by**: `get_marker_update_time()` in web_app.py

### 2. Cache Invalidation Check
```python
marker_update_time = get_marker_update_time()
cache_invalid = (marker_update_time > enriched_file_cache['marker_update_time'])
```

### 3. Async Rebuild
- Runs in background thread
- Doesn't block HTTP response
- Updates cache with fresh marker data
- Stores new marker_update_time

### 4. UI Auto-Refresh
- Frontend detects `cache_rebuilding: true`
- Polls every 2 seconds
- Auto-refreshes when rebuild completes
- User sees update without manual action

## Benefits

1. **Cross-Process Communication**: Watcher and web app coordinate via file
2. **Minimal Overhead**: Single file read per request (~0.1ms)
3. **Non-Blocking**: Async rebuild doesn't freeze UI
4. **Automatic**: No user intervention required
5. **Scalable**: Works with multiple Gunicorn workers
