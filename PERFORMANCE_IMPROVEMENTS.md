# Performance Improvements: Search, Filtering, and Caching

## Summary
This document describes the performance optimizations implemented to make search and filtering operations significantly faster, and to prevent worker blocking during cache rebuilds.

## Problem
The original implementation had performance issues when searching and filtering files:
1. **Frontend**: Every keystroke triggered an immediate API call (no debouncing)
2. **Backend**: Marker files (`.processed_files`, `.duplicate_files`) were read from disk for every file on every request
3. **Filter Switches**: Full file list with metadata was rebuilt on every filter change, causing 5+ second delays on large libraries (10,000+ files)

## Solutions Implemented

### 1. Frontend Debouncing (300ms delay)
**Location**: `templates/index.html`

- Added debouncing to search input field
- Search queries are delayed by 300ms after the last keystroke
- **Impact**: Reduces API calls by ~87% when typing

**Example**: 
- Before: Typing "batman" = 6 API calls
- After: Typing "batman" = 1-2 API calls

### 2. Backend Metadata Caching
**Location**: `web_app.py`

Added three-level caching strategy:
1. **Directory-level caching**: Marker files are loaded once per directory
2. **Time-based cache**: Metadata cached for 5 seconds (TTL)
3. **Batch preloading**: All directories preloaded at request start

**Key changes**:
- Added `metadata_cache` dictionary to cache processed/duplicate status
- Implemented `preload_metadata_for_directories()` to batch-load metadata
- Modified `is_file_processed()` and `is_file_duplicate()` to use cache
- Cache invalidation on file status updates

**Impact**: ~90% faster (9.8x speedup) for consecutive requests

### 3. Enriched File List Caching
**Location**: `web_app.py`

Added caching for the complete file list with metadata to eliminate repeated processing on filter switches.

**Problem**: Previously, every filter switch required rebuilding metadata for ALL files, even though the underlying data hadn't changed.

**Solution**:
- Added `enriched_file_cache` to store the full file list with metadata
- Implemented `get_enriched_file_list()` to manage the cache
- Cache uses hash of file list to detect changes
- Cache invalidates when files are marked as processed/duplicate

**Key changes**:
- Added `enriched_file_cache` dictionary with file list hash tracking
- Implemented `get_enriched_file_list()` function
- Modified `/api/files` endpoint to use cached enriched list
- Cache invalidation in `mark_file_processed()` and `mark_file_duplicate()`

**Impact**: 99.6% faster filter switches (223x speedup for 10,000 files!)

## Performance Test Results

### Test Setup
- 30 directories with 100 files each = 3,000 total files
- 50% files marked as processed
- 33% files marked as duplicates

### Results

#### Backend Performance (5 consecutive API requests)
```
Old implementation:  0.632s total (0.126s per request)
New implementation:  0.064s total (0.013s per request)
Improvement:         89.8% faster (9.8x speedup)
```

#### Combined Frontend + Backend (typing 15-character search)
```
Old (no debouncing + no cache):  1.895s (15 API requests)
New (with debouncing + cache):   0.026s (1-2 API requests)
Improvement:                      98.6% faster (73x speedup!)
```

#### Filter Switching Performance (10,000 files, 100 directories)
```
Old implementation:  0.145s per filter switch (rebuilds metadata)
New implementation:  0.001s per filter switch (uses cached enriched list)
Improvement:         99.6% faster (223x speedup!)
```

**User Experience**: Filter switches now feel instant, even on large libraries with 10,000+ files.

## Cache Behavior

### Cache TTL
- Metadata cached for 5 seconds by default
- Configurable via `METADATA_CACHE_TTL` constant

### Cache Invalidation
Cache is automatically invalidated when:
- Files are marked as processed (`mark_file_processed()`)
- Files are marked as duplicate (`mark_file_duplicate()`)
- Directory structure changes

### Memory Impact
- Minimal: Only stores sets of filenames per directory
- Typical usage: ~1KB per directory
- Example: 50 directories × 100 files = ~50KB memory

## Benefits

### User Experience
1. **Instant search**: No lag when typing search queries
2. **Smooth filtering**: Filter changes feel instantaneous
3. **Responsive UI**: Page loads faster on large libraries

### Server Load
1. **Reduced I/O**: 90% fewer disk reads for marker files
2. **Lower CPU**: Less parsing and processing
3. **Better scalability**: Can handle larger libraries

## Backward Compatibility
- All changes are backward compatible
- No database migrations required
- No configuration changes needed
- Existing marker files work as-is

## Cache Warming

### Automatic Startup Warming
The application now automatically warms all caches on startup:
1. **File list cache**: All comic files are scanned and cached
2. **Metadata cache**: All directory metadata (processed/duplicate status) is preloaded

This ensures the first user request is just as fast as subsequent requests, eliminating the "cold start" penalty.

### Manual Cache Warming
Cache warming can be triggered manually via API endpoint:
- **Endpoint**: `POST /api/cache/prewarm`
- **Use case**: After bulk file operations outside the application

### Cache Statistics
Monitor cache health via API endpoint:
- **Endpoint**: `GET /api/cache/stats`
- **Returns**: File count, cache age, metadata directory counts, enriched file cache status, and TTL settings

## Worker Coordination and Non-Blocking Cache Rebuilds

### Problem
When using Gunicorn with multiple workers (default: 4), all workers would initialize caches on startup and potentially rebuild caches simultaneously when handling requests. This caused:
1. **Startup delays**: All workers would try to build the cache at the same time
2. **Worker blocking**: Workers would wait (block) while another worker rebuilt the cache
3. **Resource contention**: Multiple workers scanning the same files simultaneously

### Solution: File-Based Locking with Non-Blocking Behavior
**Location**: `web_app.py`

Implemented a file-based locking mechanism that coordinates cache rebuilding across worker processes:

1. **File-based lock** (`.cache_rebuild_lock`): Uses `fcntl.flock()` for cross-process coordination
2. **Short timeout** (0.1s default): Workers try to acquire lock but don't wait long
3. **Stale cache fallback**: If lock can't be acquired, return stale cache instead of blocking
4. **Startup coordination**: Only one worker initializes caches at startup, others skip

**Key changes**:
- Added `try_acquire_cache_rebuild_lock()` and `release_cache_rebuild_lock()` functions
- Modified `get_enriched_file_list()` to be non-blocking:
  - Try to acquire lock with short timeout
  - If lock unavailable, return stale cache (if available)
  - If no stale cache, fall through to build without lock (better than blocking)
- Modified `initialize_cache()` and `prewarm_metadata_cache()` to coordinate at startup
- Added `.cache_rebuild_lock` to `.gitignore`

**Impact**:
- ✅ Workers no longer block waiting for cache rebuilds
- ✅ Only one worker rebuilds cache at a time
- ✅ Faster startup with multiple workers
- ✅ Better resource utilization
- ✅ Stale cache served during rebuilds (eventual consistency)

**Behavior**:
- **Startup**: First worker to start warms the cache, others skip
- **Cache invalidation**: First worker to detect invalidation rebuilds, others serve stale cache
- **Manual refresh**: First worker to handle refresh request rebuilds, others see updated cache after completion

## Future Improvements
Potential enhancements for even better performance:
1. Increase cache TTL for read-heavy workloads
2. ~~Add cache prewarming on application startup~~ ✅ **Implemented**
3. ~~Cache enriched file list for faster filter switches~~ ✅ **Implemented**
4. ~~Coordinate cache rebuilding across workers~~ ✅ **Implemented**
5. Implement full-text search indexing for very large libraries (20,000+ files)
6. Add Redis/Memcached support for distributed caching
