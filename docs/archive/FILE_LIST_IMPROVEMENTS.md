# File List Handling Improvements

## Overview

The file list handling system has been completely redesigned to use a SQLite-based file store instead of the previous file-based cache change system. This provides significantly better performance, reliability, and user experience when adding or removing files.

## Previous System

### Problems with the Old Approach

The previous implementation used a file-based system to track file changes:

1. **`.cache_changes` File**: File changes (add, remove, rename) were written as JSON entries to a text file
2. **Sequential Processing**: Changes were read and applied sequentially on each cache update
3. **Race Conditions**: Multiple processes writing to the same file could cause corruption
4. **Performance Issues**: Large backlogs of changes could take significant time to process
5. **Limited Atomicity**: Operations were not guaranteed to be atomic

### Old Flow

```
File Change → Append JSON to .cache_changes → Read all changes → Apply sequentially → Update cache
```

## New System

### SQLite-Based File Store

The new implementation uses SQLite as the authoritative source for the file list:

1. **`file_store.py` Module**: Dedicated module for managing files in SQLite database
2. **Direct Operations**: File changes are written directly to the database
3. **Atomic Transactions**: Database ensures all operations are atomic and consistent
4. **Indexed Queries**: Database indexes enable fast lookups and queries
5. **WAL Mode**: Write-Ahead Logging allows multiple readers and concurrent writes

### New Flow

```
File Change → Direct SQLite Write → In-memory cache invalidation → Next request loads from DB
```

## Key Features

### 1. Atomic Operations

All file operations are atomic thanks to SQLite transactions:

```python
# Add a file - guaranteed atomic
file_store.add_file("/path/to/file.cbz")

# Rename a file - old path removed and new path added atomically
file_store.rename_file("/old/path.cbz", "/new/path.cbz")

# Remove a file - guaranteed to complete or rollback
file_store.remove_file("/path/to/file.cbz")
```

### 2. Batch Operations

Efficient batch operations for handling multiple files:

```python
# Add 1000 files in a single transaction
files = ["/path/to/file1.cbz", "/path/to/file2.cbz", ...]
success, errors = file_store.batch_add_files(files)

# Remove multiple files efficiently
file_store.batch_remove_files(files_to_remove)
```

### 3. Filesystem Sync

Automatic synchronization with the filesystem:

```python
# Sync database with filesystem - detects new, deleted, and modified files
added, removed, updated = file_store.sync_with_filesystem("/watched/dir")
```

This runs automatically on startup for both the watcher and web app.

### 4. Performance

Database indexes and optimized queries provide excellent performance:

- **Batch add**: 70,000+ files/sec
- **Get all files**: <1ms for 1000 files  
- **Lookups**: 160,000+ lookups/sec
- **Batch remove**: 240,000+ files/sec

### 5. Metadata Tracking

The file store tracks useful metadata:

- File modification timestamp
- File size
- Time when file was added to store
- Custom metadata key-value pairs

## Database Schema

### Files Table

```sql
CREATE TABLE files (
    filepath TEXT PRIMARY KEY NOT NULL,
    last_modified REAL NOT NULL,
    file_size INTEGER,
    added_timestamp REAL NOT NULL
)

CREATE INDEX idx_files_last_modified ON files(last_modified)
CREATE INDEX idx_files_added_timestamp ON files(added_timestamp)
```

### Metadata Table

```sql
CREATE TABLE metadata (
    key TEXT PRIMARY KEY NOT NULL,
    value TEXT NOT NULL
)
```

## Integration Points

### Web Application

The web application loads the file list from the file store on startup:

1. **Startup**: Syncs file store with filesystem (if not recently synced)
2. **Cache Layer**: Maintains in-memory cache backed by SQLite
3. **File Changes**: Direct database writes when files are added/removed via web UI
4. **Cache Invalidation**: In-memory cache invalidated when watcher updates files

### Watcher Service

The watcher service keeps the file store synchronized:

1. **Startup**: Performs full filesystem sync
2. **File Events**: Records changes directly to database
3. **Timestamp Updates**: Updates marker file to notify web app of changes

### Process Script

The process script updates the file store when renaming files:

1. **File Rename**: Records rename operation in database
2. **Atomic Update**: Old path removed and new path added atomically

## Migration from Old System

### Automatic Migration

No manual migration is required. The system handles this automatically:

1. On first startup, the file store is empty
2. Filesystem sync populates it with all existing files
3. Old `.cache_changes` file is no longer used and can be ignored

### Backward Compatibility

- The `CACHE_UPDATE_MARKER` file is still used to signal cache updates
- Web app and watcher can run with different versions temporarily
- No breaking changes to external APIs

## Benefits

### For Users

1. **Seamless File Changes**: Adding/removing files is instant with no lag
2. **No Cache Corruption**: Database ensures data integrity
3. **Better Responsiveness**: Web UI updates faster after file operations
4. **Reliable Operations**: Atomic transactions prevent partial updates

### For Developers

1. **Simpler Code**: Database handles complexity of concurrent access
2. **Better Testing**: Can easily test file operations in isolation
3. **Easier Debugging**: SQL queries for inspection and troubleshooting
4. **Future-Proof**: Easy to add new features (search, filtering, etc.)

## Performance Comparison

### Old System (File-Based)

- Sequential processing of changes
- O(n) complexity for applying n changes
- Lock contention with multiple processes
- Potential for file corruption

### New System (SQLite-Based)

- Direct database writes
- O(1) complexity for individual operations
- WAL mode allows concurrent reads
- ACID guarantees prevent corruption

### Benchmark Results

Testing with 1000 files:

| Operation | Time | Rate |
|-----------|------|------|
| Batch add | 7ms | 146,572 files/sec |
| Get all files | 1ms | - |
| 100 lookups | 0.6ms | 166,243 lookups/sec |
| Batch remove | 3ms | 342,308 files/sec |

## Troubleshooting

### Database Location

The file store database is located at:
```
/Config/file_store/files.db
```

### Inspecting the Database

You can inspect the database directly:

```bash
sqlite3 /Config/file_store/files.db

# Count files
SELECT COUNT(*) FROM files;

# List all files
SELECT filepath FROM files ORDER BY filepath;

# Check metadata
SELECT * FROM metadata;

# Check database size
.dbinfo
```

### Rebuilding the File Store

If needed, you can rebuild the file store:

```python
import file_store

# Clear all files
file_store.clear_all_files()

# Resync with filesystem
added, removed, updated = file_store.sync_with_filesystem("/watched/dir")
```

### Performance Tuning

The database uses optimal settings by default:

- WAL mode for better concurrency
- Indexes on frequently queried columns
- 30-second timeout for busy database

## Future Enhancements

Possible future improvements:

1. **Search Functionality**: Full-text search across file paths
2. **File History**: Track file rename/move history
3. **Statistics**: Track file counts over time
4. **Backup/Restore**: Export/import file lists
5. **API Endpoints**: Expose file store operations via REST API

## Conclusion

The SQLite-based file store provides a robust, performant, and reliable foundation for managing the file list. Users will experience faster, more reliable file operations, while developers benefit from simpler, more maintainable code.
