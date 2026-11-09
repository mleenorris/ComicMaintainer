# Unified Database Migration Guide

## Overview

The ComicMaintainer service has been updated to use a single unified database instead of separate databases for file storage and markers. This change provides several benefits while maintaining full backward compatibility.

## What Changed

### Before: Separate Databases
- **File Store Database**: `/Config/file_store/files.db`
  - Stored the list of comic files
  - Tracked file metadata (size, last modified)
- **Marker Database**: `/Config/markers/markers.db`
  - Stored processing status (processed, duplicate, web-modified)

### After: Unified Database
- **Unified Database**: `/Config/store/comicmaintainer.db`
  - Contains all file data, markers, and metadata in one database
  - Simpler backup and maintenance
  - Better performance through shared connection pooling
  - Atomic operations across both files and markers

## Benefits

### 1. Simplified Management
- **Single database file** instead of two separate databases
- **Easier backups**: Only one database file to back up
- **Simpler maintenance**: One database to manage and optimize

### 2. Improved Performance
- **Shared connection pool**: Both file and marker operations use the same database connections
- **Better caching**: SQLite caches for both types of data
- **Atomic transactions**: Operations that involve both files and markers can now be atomic

### 3. Better Reliability
- **Consistent state**: Files and their markers are always in sync
- **WAL mode**: Write-Ahead Logging enabled for better concurrent access
- **ACID compliance**: All operations are atomic, consistent, isolated, and durable

## Migration Process

### Automatic Migration

The migration happens automatically when you upgrade to the new version:

1. **First startup after upgrade**:
   - The service detects the old databases at their original locations
   - All data is copied to the new unified database
   - Old databases are renamed as backups (`.migrated.TIMESTAMP`)
   - Service continues running normally

2. **Migration is transparent**:
   - No downtime required
   - No manual steps needed
   - All existing data is preserved
   - Takes less than a second for typical libraries (1000+ files)

3. **Verification**:
   - Check logs for migration messages
   - Verify the new database exists at `/Config/store/comicmaintainer.db`
   - Confirm old databases have been renamed with `.migrated.TIMESTAMP` suffix

### Manual Verification

If you want to verify the migration was successful:

```bash
# Check if the new unified database exists
ls -lh /Config/store/comicmaintainer.db

# Check if old databases were backed up
ls -lh /Config/markers/markers.db.migrated.*
ls -lh /Config/file_store/files.db.migrated.*

# Check the database structure
sqlite3 /Config/store/comicmaintainer.db ".schema"
```

Expected tables in the unified database:
- `files` - Comic file list with metadata
- `markers` - Processing status markers
- `metadata` - Configuration and state data

## Database Schema

### Files Table
```sql
CREATE TABLE files (
    filepath TEXT PRIMARY KEY NOT NULL,
    last_modified REAL NOT NULL,
    file_size INTEGER,
    added_timestamp REAL NOT NULL
);
```

### Markers Table
```sql
CREATE TABLE markers (
    filepath TEXT NOT NULL,
    marker_type TEXT NOT NULL,
    PRIMARY KEY (filepath, marker_type)
);
```

### Metadata Table
```sql
CREATE TABLE metadata (
    key TEXT PRIMARY KEY NOT NULL,
    value TEXT NOT NULL
);
```

## Rollback (Not Recommended)

If you need to rollback to the old version:

1. **Stop the service**
2. **Restore the old databases**:
   ```bash
   # Remove the unified database
   rm /Config/store/comicmaintainer.db*
   
   # Restore the old databases from backups
   mv /Config/markers/markers.db.migrated.* /Config/markers/markers.db
   mv /Config/file_store/files.db.migrated.* /Config/file_store/files.db
   ```
3. **Start the old version of the service**

**Note**: Any changes made after the migration will be lost when rolling back.

## Backward Compatibility

The migration maintains 100% backward compatibility:

- **No API changes**: All function calls remain the same
- **Same module names**: `file_store` and `marker_store` modules still work
- **Transparent wrapping**: Old modules now wrap the unified store
- **No code changes needed**: Existing scripts continue to work

### For Developers

If you're importing these modules in your own scripts:

```python
# Old imports still work:
import file_store
import marker_store

# Both now use the unified database internally
file_store.add_file('/path/to/file.cbz')
marker_store.add_marker('/path/to/file.cbz', 'processed')

# Or use the new unified_store directly:
import unified_store
unified_store.add_file('/path/to/file.cbz')
unified_store.add_marker('/path/to/file.cbz', 'processed')
```

## Troubleshooting

### Migration Not Detected

If the migration doesn't run automatically:

1. Check logs for migration messages
2. Verify old database paths:
   - `/Config/markers/markers.db`
   - `/Config/file_store/files.db`
3. Check permissions on `/Config` directory

### Migration Failed

If migration fails:

1. Check logs for error messages
2. Verify disk space is available
3. Check file permissions
4. Old databases are preserved - you can retry or rollback

### Performance Issues

If you experience performance issues:

1. **Check database size**: `ls -lh /Config/store/comicmaintainer.db`
2. **Optimize database**: Run `VACUUM` if needed
3. **Check WAL files**: WAL files (`.db-wal`) are temporary and normal
4. **Monitor connections**: The service uses thread-local connections

### Database Corruption

If the database becomes corrupted:

1. **Stop the service**
2. **Check integrity**:
   ```bash
   sqlite3 /Config/store/comicmaintainer.db "PRAGMA integrity_check;"
   ```
3. **Restore from backup** if needed
4. **Re-run migration** from old databases if backups exist

## Best Practices

### Backups

- **Backup the `/Config` directory** regularly
- The unified database is located at `/Config/store/comicmaintainer.db`
- WAL files (`.db-wal`, `.db-shm`) are temporary - backup optional
- Old database backups are kept for safety

### Monitoring

- Check logs for migration messages on first startup
- Monitor database size over time
- Watch for any error messages related to database operations

### Maintenance

- The database is self-maintaining
- No manual optimization needed
- WAL mode automatically handles concurrent access
- Indexes are maintained automatically

## Support

If you encounter issues:

1. Check logs at `/Config/Log/ComicMaintainer.log`
2. Look for migration-related error messages
3. Verify file permissions on `/Config` directory
4. Create an issue on GitHub with logs and error messages

## Technical Details

### Database Location
- **Unified Database**: `/Config/store/comicmaintainer.db`
- **WAL Files**: `/Config/store/comicmaintainer.db-wal` (temporary)
- **SHM Files**: `/Config/store/comicmaintainer.db-shm` (temporary)

### Connection Management
- Thread-local connections for thread safety
- Connection pooling for better performance
- WAL mode for concurrent access
- 30-second timeout for database locks

### Performance Characteristics
- **File operations**: Same performance as before
- **Marker operations**: Same performance as before
- **Combined operations**: Improved performance (atomic)
- **Database size**: Similar to sum of old databases

## Conclusion

The unified database migration is a transparent upgrade that provides better performance and maintainability while maintaining full backward compatibility. The migration happens automatically, and the service continues to work exactly as before with no user intervention required.
