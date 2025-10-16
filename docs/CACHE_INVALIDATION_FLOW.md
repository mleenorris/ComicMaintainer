# Cache Invalidation Flow Diagram

## System Components

```
┌─────────────────┐         ┌──────────────────┐         ┌─────────────────┐
│  Watcher        │         │  SQLite DB       │         │  Web UI         │
│  Service        │         │  (markers.db)    │         │  (Flask)        │
└─────────────────┘         └──────────────────┘         └─────────────────┘
```

## Before Fix: The Problem

```
┌─────────────────┐         ┌──────────────────┐         ┌─────────────────┐
│  Watcher        │         │  SQLite DB       │         │  Web UI         │
└────────┬────────┘         └────────┬─────────┘         └────────┬────────┘
         │                           │                            │
         │ 1. Detect file change     │                            │
         │                           │                            │
         │ 2. Process file           │                            │
         ├──────────────────────────>│                            │
         │    Mark as processed      │                            │
         │                           │                            │
         │ 3. Update timestamp       │                            │
         │    (.cache_update)        │                            │
         │                           │                            │
         │                           │    4. User requests page   │
         │                           │                            │
         │                           │    5. Check cache          │
         │                           │    enriched_file_cache     │
         │                           │    ✓ Valid (WRONG!)       │
         │                           │                            │
         │                           │    6. Return stale data    │
         │                           │<───────────────────────────│
         │                           │    Shows: ⚠️ unprocessed   │
         │                           │    (Actually: ✅ processed)│
         │                           │                            │
         │                           │    😞 User confused        │
         │                           │    (file looks unprocessed)│
         │                           │                            │
         │                           │    ⏰ Minutes pass...      │
         │                           │                            │
         │                           │    Eventually cache expires│
         │                           │    or manual refresh       │
         │                           │                            │

Problem: enriched_file_cache never checks watcher timestamp!
```

## After Fix: The Solution

```
┌─────────────────┐         ┌──────────────────┐         ┌─────────────────┐
│  Watcher        │         │  SQLite DB       │         │  Web UI         │
└────────┬────────┘         └────────┬─────────┘         └────────┬────────┘
         │                           │                            │
         │ 1. Detect file change     │                            │
         │                           │                            │
         │ 2. Process file           │                            │
         ├──────────────────────────>│                            │
         │    Mark as processed      │                            │
         │                           │                            │
         │ 3. Update timestamp       │                            │
         │    (.cache_update)        │                            │
         │    time = 150             │                            │
         │                           │                            │
         │                           │    4. User requests page   │
         │                           │                            │
         │                           │    5. Read watcher time    │
         │                           │<───────────────────────────│
         │                           │    time = 150              │
         │                           │                            │
         │                           │    6. Check cache          │
         │                           │    enriched_file_cache     │
         │                           │    watcher_time: 100       │
         │                           │                            │
         │                           │    7. Compare: 150 > 100   │
         │                           │    ❌ Cache is STALE!      │
         │                           │                            │
         │                           │    8. Invalidate cache     │
         │                           │                            │
         │                           │    9. Query SQLite         │
         │                           │<───────────────────────────│
         │                           │──────────────────────────>│
         │                           │    Fresh marker data       │
         │                           │                            │
         │                           │    10. Rebuild cache       │
         │                           │    watcher_time: 150       │
         │                           │                            │
         │                           │    11. Return fresh data   │
         │                           │<───────────────────────────│
         │                           │    Shows: ✅ processed     │
         │                           │                            │
         │                           │    😊 User happy           │
         │                           │    (sees correct status!)  │

Solution: enriched_file_cache checks watcher timestamp and invalidates when stale!
```

## Code Flow Detail

### Invalidation Check (Added to `get_enriched_file_list`)

```python
def get_enriched_file_list(files, force_rebuild=False):
    # STEP 1: Get current watcher timestamp
    watcher_update_time = get_watcher_update_time()  # Read from .cache_update file
    
    with enriched_file_cache_lock:
        # STEP 2: Check if cache is stale
        if (enriched_file_cache['files'] is not None and 
            watcher_update_time > enriched_file_cache['watcher_update_time']):
            
            # STEP 3: Invalidate stale cache
            logging.info("Invalidating enriched cache: watcher has processed files")
            enriched_file_cache['files'] = None
            enriched_file_cache['file_list_hash'] = None
        
        # STEP 4: Check if can use cache
        if (not force_rebuild and 
            enriched_file_cache['files'] is not None and 
            enriched_file_cache['file_list_hash'] == file_list_hash):
            
            return enriched_file_cache['files']  # Use cached data
        
        # STEP 5: Need to rebuild cache...
```

### Cache Rebuild (Both Async and Sync)

```python
# After building fresh file list with SQLite data...

with enriched_file_cache_lock:
    enriched_file_cache['files'] = all_files
    enriched_file_cache['timestamp'] = time.time()
    enriched_file_cache['file_list_hash'] = file_list_hash
    
    # NEW: Record watcher timestamp when cache was built
    enriched_file_cache['watcher_update_time'] = get_watcher_update_time()
    
    enriched_file_cache['rebuild_in_progress'] = False
    enriched_file_cache['rebuild_thread'] = None
```

## Timeline Comparison

### Before Fix - User Experience

```
T+0s:   Watcher processes file → SQLite updated
T+0s:   Watcher updates timestamp file
T+5s:   User loads page → sees ⚠️ unprocessed (WRONG)
T+10s:  User refreshes → still sees ⚠️ unprocessed (WRONG)
T+30s:  User refreshes → still sees ⚠️ unprocessed (WRONG)
T+120s: Cache expires or manual rebuild → sees ✅ processed (FINALLY!)

Delay: ~2 minutes of confusion 😞
```

### After Fix - User Experience

```
T+0s:   Watcher processes file → SQLite updated
T+0s:   Watcher updates timestamp file
T+1s:   User loads page → cache invalidated → fresh query → sees ✅ processed (CORRECT!)
T+5s:   User refreshes → cache valid → sees ✅ processed (CORRECT!)
T+10s:  User refreshes → cache valid → sees ✅ processed (CORRECT!)

Delay: <1 second 😊
```

## Key Changes Summary

| Component                  | Before Fix          | After Fix                |
|---------------------------|---------------------|--------------------------|
| Cache Structure           | No watcher tracking | `watcher_update_time: 0` |
| Cache Check               | Hash only           | Hash + watcher timestamp |
| Invalidation Trigger      | Manual only         | Automatic on watcher update |
| SQLite Visibility         | Delayed             | Immediate                |
| User Experience           | Confusing delays    | Instant updates          |

## Performance Impact

```
Per-Request Cost:
  Before: 0μs (no check)
  After:  ~10μs (file read for timestamp)
  
Cache Rebuild Frequency:
  Before: Time-based or manual
  After:  Event-driven (only when watcher processes files)
  
User-Perceived Latency:
  Before: 30-120 seconds
  After:  <1 second
```

## Conclusion

The fix adds a lightweight timestamp check (~10μs) that provides massive UX improvement by eliminating minutes-long delays. The cache is now event-driven rather than time-based, ensuring users always see current file status.
