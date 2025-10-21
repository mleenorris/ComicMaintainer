# Filter Cache Flow Diagram

> **⚠️ NOTE (2025-10-21):** This document is partially outdated. The `file_list_cache` layer has been removed as SQLite is extremely fast (<3ms for 5000 files). The database itself acts as the cache with OS-level caching. Only the `enriched_file_cache` and `filtered_results_cache` layers remain, which cache expensive operations (marker enrichment and filter/search/sort), not database reads.

## Request Flow with Cache

```
┌─────────────────────────────────────────────────────────────┐
│                    User Changes Filter                       │
│              (e.g., "All" → "Unmarked Only")                 │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────┐
│                  Frontend: loadFiles()                       │
│              GET /api/files?filter=unmarked                  │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────┐
│               Backend: list_files()                          │
│  1. Get comic files (from file list cache)                  │
│  2. Get enriched file list (from enriched cache)            │
│  3. Compute file list hash                                  │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────┐
│         Call: get_filtered_sorted_files()                    │
│   cache_key = (filter, search, sort, hash)                  │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
                    ┌────┴────┐
                    │  Cache  │
                    │  Lookup │
                    └────┬────┘
                         │
          ┌──────────────┴──────────────┐
          │                             │
    ┌─────▼─────┐              ┌───────▼────────┐
    │ Cache Hit │              │  Cache Miss    │
    │ (~1ms)    │              │  (~300-500ms)  │
    └─────┬─────┘              └───────┬────────┘
          │                             │
          │                             ▼
          │                    ┌────────────────┐
          │                    │ 1. Filter files│
          │                    │ 2. Search files│
          │                    │ 3. Sort files  │
          │                    └───────┬────────┘
          │                            │
          │                            ▼
          │                    ┌────────────────┐
          │                    │  Store in      │
          │                    │  Cache         │
          │                    └───────┬────────┘
          │                            │
          └────────────┬───────────────┘
                       │
                       ▼
          ┌────────────────────────┐
          │   Return Filtered      │
          │   Results              │
          └────────────┬───────────┘
                       │
                       ▼
          ┌────────────────────────┐
          │   Paginate Results     │
          │   Return to Frontend   │
          └────────────────────────┘
```

## Cache Invalidation Flow

```
┌─────────────────────────────────────────────────────────────┐
│             File Processing / Modification                   │
│  • Process file                                              │
│  • Mark as processed/duplicate                               │
│  • Rename file                                               │
│  • Watcher processes file                                    │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────┐
│        Trigger Cache Invalidation                            │
│                                                              │
│  1. mark_file_processed_wrapper()                            │
│  2. mark_file_duplicate_wrapper()                            │
│  3. handle_file_rename_in_cache()                            │
│  4. clear_file_cache()                                       │
│  5. get_enriched_file_list() [watcher update]               │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────┐
│           Clear filtered_results_cache                       │
│               filtered_results_cache.clear()                 │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────┐
│            Next Filter Request                               │
│     Will rebuild cache (cache miss)                          │
└─────────────────────────────────────────────────────────────┘
```

## Cache Size Management

```
┌─────────────────────────────────────────────────────────────┐
│             Cache Entry Addition                             │
│   Current Size: 20/20 (MAX_FILTERED_CACHE_SIZE)             │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────┐
│              LRU Eviction Strategy                           │
│                                                              │
│  1. Find entry with oldest timestamp                         │
│  2. Remove oldest entry from cache                           │
│  3. Add new entry to cache                                   │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────┐
│           Cache Size: 20/20 (maintained)                     │
└─────────────────────────────────────────────────────────────┘
```

## Cache Key Structure

```
cache_key = (filter_mode, search_query, sort_mode, file_list_hash)
            ─────┬─────  ─────┬─────  ────┬────  ──────┬──────
                 │            │           │             │
                 │            │           │             └─ Detects file changes
                 │            │           │
                 │            │           └─ Sort: name/date/size
                 │            │
                 │            └─ Search: "" or "batman" etc.
                 │
                 └─ Filter: all/marked/unmarked/duplicates

Examples:
- ('all', '', 'name', 123456789)           → All files, no search, sort by name
- ('unmarked', '', 'name', 123456789)      → Unmarked files, sort by name
- ('all', 'batman', 'name', 123456789)     → Search "batman", sort by name
- ('marked', '', 'date', 123456789)        → Marked files, sort by date
```

## Performance Comparison

```
┌────────────────────────────────────────────────────────────┐
│             Without Cache (Original)                        │
├────────────────────────────────────────────────────────────┤
│  Filter Switch 1: ████████████████████ 400ms               │
│  Filter Switch 2: ████████████████████ 400ms               │
│  Filter Switch 3: ████████████████████ 400ms               │
│  Filter Switch 4: ████████████████████ 400ms               │
│  Total Time: 1600ms                                         │
└────────────────────────────────────────────────────────────┘

┌────────────────────────────────────────────────────────────┐
│              With Cache (Optimized)                         │
├────────────────────────────────────────────────────────────┤
│  Filter Switch 1: ████████████████████ 400ms (cache miss)  │
│  Filter Switch 2: █ 2ms (cache hit)                        │
│  Filter Switch 3: █ 2ms (cache hit)                        │
│  Filter Switch 4: █ 2ms (cache hit)                        │
│  Total Time: 406ms                                          │
│  Improvement: ~75% faster                                   │
└────────────────────────────────────────────────────────────┘

┌────────────────────────────────────────────────────────────┐
│        Repeated Filter Switches (Typical Usage)             │
├────────────────────────────────────────────────────────────┤
│  Without Cache: 400ms per switch                            │
│  With Cache:    2ms per switch (200x faster!)               │
└────────────────────────────────────────────────────────────┘
```

## Thread Safety

```
┌───────────────┐     ┌───────────────┐     ┌───────────────┐
│   Worker 1    │     │   Worker 2    │     │   Worker 3    │
└───────┬───────┘     └───────┬───────┘     └───────┬───────┘
        │                     │                     │
        │  Request Filter A   │  Request Filter B   │  Request Filter C
        ▼                     ▼                     ▼
┌────────────────────────────────────────────────────────────┐
│              filtered_results_cache_lock                    │
│  (ensures thread-safe access to cache)                      │
└────────────┬──────────────┬──────────────┬─────────────────┘
             │              │              │
             ▼              ▼              ▼
      ┌──────────┐    ┌──────────┐   ┌──────────┐
      │ Cache A  │    │ Cache B  │   │ Cache C  │
      └──────────┘    └──────────┘   └──────────┘
```
