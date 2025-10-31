"""
Unified SQLite-based storage for both file tracking and marker management.
Combines the functionality of file_store and marker_store into a single database.
"""
import sqlite3
import logging
import threading
import os
import time
from typing import List, Optional, Set, Tuple, Dict
from contextlib import contextmanager
from config import get_db_cache_size_mb

# Database configuration
CONFIG_DIR = '/Config'
STORE_DIR = os.path.join(CONFIG_DIR, 'store')
DB_PATH = os.path.join(STORE_DIR, 'comicmaintainer.db')

# Thread-local storage for database connections
_thread_local = threading.local()


def _ensure_store_dir():
    """Ensure store directory exists"""
    os.makedirs(STORE_DIR, exist_ok=True)


def _init_db_schema(conn):
    """Initialize database schema on a connection"""
    cursor = conn.cursor()
    
    # Files table - stores all comic files with metadata
    cursor.execute('''
        CREATE TABLE IF NOT EXISTS files (
            filepath TEXT PRIMARY KEY NOT NULL,
            last_modified REAL NOT NULL,
            file_size INTEGER,
            added_timestamp REAL NOT NULL
        )
    ''')
    
    # Markers table - stores all file markers with their type
    cursor.execute('''
        CREATE TABLE IF NOT EXISTS markers (
            filepath TEXT NOT NULL,
            marker_type TEXT NOT NULL,
            PRIMARY KEY (filepath, marker_type)
        )
    ''')
    
    # Metadata table for tracking state and configuration
    cursor.execute('''
        CREATE TABLE IF NOT EXISTS metadata (
            key TEXT PRIMARY KEY NOT NULL,
            value TEXT NOT NULL
        )
    ''')
    
    # Processing history table - tracks before/after changes for each file processing
    cursor.execute('''
        CREATE TABLE IF NOT EXISTS processing_history (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            filepath TEXT NOT NULL,
            timestamp REAL NOT NULL,
            before_filename TEXT,
            after_filename TEXT,
            before_title TEXT,
            after_title TEXT,
            before_series TEXT,
            after_series TEXT,
            before_issue TEXT,
            after_issue TEXT,
            before_publisher TEXT,
            after_publisher TEXT,
            before_year TEXT,
            after_year TEXT,
            before_volume TEXT,
            after_volume TEXT,
            operation_type TEXT NOT NULL
        )
    ''')
    
    # Create indexes for faster queries
    cursor.execute('''
        CREATE INDEX IF NOT EXISTS idx_files_last_modified 
        ON files(last_modified)
    ''')
    
    cursor.execute('''
        CREATE INDEX IF NOT EXISTS idx_files_added_timestamp 
        ON files(added_timestamp)
    ''')
    
    cursor.execute('''
        CREATE INDEX IF NOT EXISTS idx_markers_filepath 
        ON markers(filepath)
    ''')
    
    cursor.execute('''
        CREATE INDEX IF NOT EXISTS idx_markers_type 
        ON markers(marker_type)
    ''')
    
    cursor.execute('''
        CREATE INDEX IF NOT EXISTS idx_processing_history_filepath 
        ON processing_history(filepath)
    ''')
    
    cursor.execute('''
        CREATE INDEX IF NOT EXISTS idx_processing_history_timestamp 
        ON processing_history(timestamp DESC)
    ''')
    
    conn.commit()


@contextmanager
def get_db_connection():
    """
    Get a thread-local database connection.
    Uses context manager for automatic cleanup.
    Ensures database is initialized on first access.
    """
    # Ensure database is initialized
    init_db()
    
    # Check if connection exists in thread-local storage
    if not hasattr(_thread_local, 'connection') or _thread_local.connection is None:
        _thread_local.connection = sqlite3.connect(DB_PATH, timeout=30.0)
        _thread_local.connection.row_factory = sqlite3.Row
        # Enable WAL mode for better concurrent access
        _thread_local.connection.execute('PRAGMA journal_mode=WAL')
        # Performance optimizations
        _thread_local.connection.execute('PRAGMA synchronous=NORMAL')  # Faster than FULL, safe with WAL
        cache_size_mb = get_db_cache_size_mb()
        cache_size_kb = cache_size_mb * 1024
        _thread_local.connection.execute(f'PRAGMA cache_size=-{cache_size_kb}')  # Negative value = KB
        _thread_local.connection.execute('PRAGMA temp_store=MEMORY')  # Use memory for temp tables
        _thread_local.connection.execute('PRAGMA mmap_size=268435456')  # 256MB memory-mapped I/O
        # Ensure database is initialized in this process/thread
        _init_db_schema(_thread_local.connection)
    
    try:
        yield _thread_local.connection
    except Exception:
        _thread_local.connection.rollback()
        raise


_db_initialized = False
_db_init_lock = threading.Lock()


def init_db():
    """Initialize database schema (called lazily on first access)"""
    global _db_initialized
    
    if _db_initialized:
        return
    
    with _db_init_lock:
        if _db_initialized:
            return
        
        _ensure_store_dir()
        
        # Create a temporary connection to initialize the database
        conn = sqlite3.connect(DB_PATH, timeout=30.0)
        try:
            _init_db_schema(conn)
            logging.info(f"Initialized unified database at {DB_PATH}")
        finally:
            conn.close()
        
        _db_initialized = True


# ==============================================================================
# FILE STORE FUNCTIONS
# ==============================================================================

def add_file(filepath: str, last_modified: float = None, file_size: int = None) -> bool:
    """
    Add a file to the file store.
    
    Args:
        filepath: Full path to the file
        last_modified: Last modification timestamp (defaults to current file mtime)
        file_size: File size in bytes (defaults to current file size)
    
    Returns:
        True if successful, False otherwise
    """
    try:
        # Get file metadata if not provided
        if last_modified is None or file_size is None:
            try:
                stat = os.stat(filepath)
                if last_modified is None:
                    last_modified = stat.st_mtime
                if file_size is None:
                    file_size = stat.st_size
            except OSError:
                # File doesn't exist or can't be read, use defaults
                last_modified = last_modified or time.time()
                file_size = file_size or 0
        
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('''
                INSERT OR REPLACE INTO files (filepath, last_modified, file_size, added_timestamp)
                VALUES (?, ?, ?, ?)
            ''', (filepath, last_modified, file_size, time.time()))
            conn.commit()
            logging.debug(f"Added file to store: {filepath}")
            return True
    except Exception as e:
        logging.error(f"Error adding file {filepath} to store: {e}")
        return False


def remove_file(filepath: str) -> bool:
    """
    Remove a file from the file store.
    
    Args:
        filepath: Full path to the file
    
    Returns:
        True if file was removed, False if it didn't exist or error occurred
    """
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('''
                DELETE FROM files 
                WHERE filepath = ?
            ''', (filepath,))
            deleted = cursor.rowcount > 0
            conn.commit()
            if deleted:
                logging.debug(f"Removed file from store: {filepath}")
            return deleted
    except Exception as e:
        logging.error(f"Error removing file {filepath} from store: {e}")
        return False


def rename_file(old_path: str, new_path: str) -> bool:
    """
    Rename a file in the file store.
    
    Args:
        old_path: Original file path
        new_path: New file path
    
    Returns:
        True if successful, False otherwise
    """
    try:
        # Get file metadata if available
        last_modified = None
        file_size = None
        try:
            stat = os.stat(new_path)
            last_modified = stat.st_mtime
            file_size = stat.st_size
        except OSError:
            pass
        
        with get_db_connection() as conn:
            cursor = conn.cursor()
            
            # Check if old path exists
            cursor.execute('SELECT * FROM files WHERE filepath = ?', (old_path,))
            old_file = cursor.fetchone()
            
            if old_file:
                # Remove old path
                cursor.execute('DELETE FROM files WHERE filepath = ?', (old_path,))
                
                # Add new path with updated metadata
                cursor.execute('''
                    INSERT OR REPLACE INTO files (filepath, last_modified, file_size, added_timestamp)
                    VALUES (?, ?, ?, ?)
                ''', (new_path, 
                      last_modified or old_file['last_modified'], 
                      file_size or old_file['file_size'],
                      old_file['added_timestamp']))
                
                conn.commit()
                logging.debug(f"Renamed file in store: {old_path} -> {new_path}")
                return True
            else:
                # Old path doesn't exist, just add new path
                return add_file(new_path, last_modified, file_size)
    except Exception as e:
        logging.error(f"Error renaming file {old_path} -> {new_path} in store: {e}")
        return False


def has_file(filepath: str) -> bool:
    """
    Check if a file exists in the file store.
    
    Args:
        filepath: Full path to the file
    
    Returns:
        True if file exists in store, False otherwise
    """
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('''
                SELECT 1 FROM files 
                WHERE filepath = ?
            ''', (filepath,))
            return cursor.fetchone() is not None
    except Exception as e:
        logging.error(f"Error checking file {filepath} in store: {e}")
        return False


def get_all_files() -> List[str]:
    """
    Get all files from the file store.
    
    Returns:
        List of file paths sorted alphabetically
    """
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('''
                SELECT filepath FROM files 
                ORDER BY filepath
            ''')
            return [row['filepath'] for row in cursor.fetchall()]
    except Exception as e:
        logging.error(f"Error getting all files from store: {e}")
        return []


def get_all_files_with_metadata() -> List[Dict]:
    """
    Get all files from the file store with their metadata.
    
    Returns:
        List of dictionaries containing filepath, last_modified, and file_size
    """
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('''
                SELECT filepath, last_modified, file_size, added_timestamp
                FROM files 
                ORDER BY filepath
            ''')
            results = []
            for row in cursor.fetchall():
                results.append({
                    'filepath': row['filepath'],
                    'last_modified': row['last_modified'],
                    'file_size': row['file_size'],
                    'added_timestamp': row['added_timestamp']
                })
            return results
    except Exception as e:
        logging.error(f"Error getting all files with metadata from store: {e}")
        return []


def get_files_paginated(
    limit: int = 100,
    offset: int = 0,
    sort_by: str = 'name',
    sort_direction: str = 'asc',
    search_query: str = None,
    filter_mode: str = 'all'
) -> Tuple[List[Dict], int]:
    """
    Get files from the file store with pagination, sorting, search, and marker filtering.
    This is much more efficient than loading all files and filtering in Python.
    
    Args:
        limit: Maximum number of files to return (use -1 for all files)
        offset: Number of files to skip
        sort_by: Sort field ('name', 'date', 'size')
        sort_direction: Sort direction ('asc', 'desc')
        search_query: Optional search query to filter by filename
        filter_mode: Filter by marker status ('all', 'marked', 'unmarked', 'duplicates')
    
    Returns:
        Tuple of (list of file dictionaries, total count matching criteria)
    """
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            
            # Build WHERE clauses
            where_clauses = []
            params = []
            
            # Add search filter
            if search_query:
                where_clauses.append("f.filepath LIKE ?")
                params.append(f"%{search_query}%")
            
            # Build base query depending on filter mode
            if filter_mode == 'marked':
                # Files with 'processed' marker
                from_clause = """
                    files f
                    INNER JOIN markers m ON f.filepath = m.filepath AND m.marker_type = 'processed'
                """
            elif filter_mode == 'unmarked':
                # Files without 'processed' marker
                from_clause = """
                    files f
                    LEFT JOIN markers m ON f.filepath = m.filepath AND m.marker_type = 'processed'
                """
                where_clauses.append("m.filepath IS NULL")
            elif filter_mode == 'duplicates':
                # Files with 'duplicate' marker
                from_clause = """
                    files f
                    INNER JOIN markers m ON f.filepath = m.filepath AND m.marker_type = 'duplicate'
                """
            else:
                # All files
                from_clause = "files f"
            
            # Combine WHERE clauses
            where_clause = ""
            if where_clauses:
                where_clause = "WHERE " + " AND ".join(where_clauses)
            
            # Get total count matching search criteria
            count_query = f"SELECT COUNT(*) as count FROM {from_clause} {where_clause}"
            cursor.execute(count_query, params)
            total_count = cursor.fetchone()['count']
            
            # Build ORDER BY clause
            if sort_by == 'date':
                order_by = 'f.last_modified'
            elif sort_by == 'size':
                order_by = 'f.file_size'
            else:  # Default to name
                order_by = 'f.filepath'
            
            # Add direction
            direction = 'DESC' if sort_direction == 'desc' else 'ASC'
            
            # Build LIMIT clause
            limit_clause = ""
            if limit > 0:
                limit_clause = f"LIMIT {limit} OFFSET {offset}"
                
            # Execute query
            query = f'''
                SELECT f.filepath, f.last_modified, f.file_size, f.added_timestamp
                FROM {from_clause}
                {where_clause}
                ORDER BY {order_by} {direction}
                {limit_clause}
            '''
            cursor.execute(query, params)
            
            results = []
            for row in cursor.fetchall():
                results.append({
                    'filepath': row['filepath'],
                    'last_modified': row['last_modified'],
                    'file_size': row['file_size'],
                    'added_timestamp': row['added_timestamp']
                })
            
            return results, total_count
    except Exception as e:
        logging.error(f"Error getting paginated files from store: {e}")
        return [], 0


def get_file_count() -> int:
    """
    Get the total number of files in the store.
    
    Returns:
        Number of files
    """
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('SELECT COUNT(*) as count FROM files')
            return cursor.fetchone()['count']
    except Exception as e:
        logging.error(f"Error getting file count from store: {e}")
        return 0


def clear_all_files() -> int:
    """
    Clear all files from the file store.
    
    Returns:
        Number of files removed
    """
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('DELETE FROM files')
            deleted = cursor.rowcount
            conn.commit()
            logging.info(f"Cleared {deleted} files from store")
            return deleted
    except Exception as e:
        logging.error(f"Error clearing files from store: {e}")
        return 0


def batch_add_files(filepaths: List[str]) -> Tuple[int, int]:
    """
    Add multiple files to the store in a single transaction.
    Much faster than calling add_file() multiple times.
    
    Args:
        filepaths: List of file paths to add
    
    Returns:
        Tuple of (successful_count, error_count)
    """
    if not filepaths:
        return (0, 0)
    
    success_count = 0
    error_count = 0
    
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            
            for filepath in filepaths:
                try:
                    # Get file metadata
                    stat = os.stat(filepath)
                    last_modified = stat.st_mtime
                    file_size = stat.st_size
                    
                    cursor.execute('''
                        INSERT OR REPLACE INTO files (filepath, last_modified, file_size, added_timestamp)
                        VALUES (?, ?, ?, ?)
                    ''', (filepath, last_modified, file_size, time.time()))
                    success_count += 1
                except OSError:
                    # File doesn't exist, skip it
                    error_count += 1
                except Exception as e:
                    logging.warning(f"Error adding file {filepath}: {e}")
                    error_count += 1
            
            conn.commit()
            logging.info(f"Batch added {success_count} files to store ({error_count} errors)")
    except Exception as e:
        logging.error(f"Error in batch add files: {e}")
        error_count += len(filepaths) - success_count
    
    return (success_count, error_count)


def batch_remove_files(filepaths: List[str]) -> int:
    """
    Remove multiple files from the store in a single transaction.
    
    Args:
        filepaths: List of file paths to remove
    
    Returns:
        Number of files removed
    """
    if not filepaths:
        return 0
    
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            
            # Use IN clause for efficient deletion
            placeholders = ','.join('?' * len(filepaths))
            cursor.execute(f'''
                DELETE FROM files 
                WHERE filepath IN ({placeholders})
            ''', filepaths)
            
            deleted = cursor.rowcount
            conn.commit()
            logging.info(f"Batch removed {deleted} files from store")
            return deleted
    except Exception as e:
        logging.error(f"Error in batch remove files: {e}")
        return 0


def sync_with_filesystem(watched_dir: str, extensions: List[str] = None) -> Tuple[int, int, int]:
    """
    Synchronize the file store with the actual filesystem.
    Adds new files, removes deleted files, updates modified files.
    
    Args:
        watched_dir: Directory to scan
        extensions: List of file extensions to track (e.g., ['.cbz', '.cbr'])
                   If None, defaults to ['.cbz', '.cbr', '.CBZ', '.CBR']
    
    Returns:
        Tuple of (added_count, removed_count, updated_count)
    """
    if extensions is None:
        extensions = ['.cbz', '.cbr', '.CBZ', '.CBR']
    
    import glob
    
    try:
        # Get all files from filesystem
        fs_files = set()
        for ext in extensions:
            pattern = f'*{ext}'
            files = glob.glob(os.path.join(watched_dir, '**', pattern), recursive=True)
            fs_files.update(files)
        
        # Get all files from database
        db_files = set(get_all_files())
        
        # Calculate differences
        files_to_add = fs_files - db_files
        files_to_remove = db_files - fs_files
        files_to_check = fs_files & db_files  # Files in both
        
        added_count = 0
        removed_count = 0
        updated_count = 0
        
        with get_db_connection() as conn:
            cursor = conn.cursor()
            
            # Add new files
            if files_to_add:
                for filepath in files_to_add:
                    try:
                        stat = os.stat(filepath)
                        cursor.execute('''
                            INSERT OR REPLACE INTO files (filepath, last_modified, file_size, added_timestamp)
                            VALUES (?, ?, ?, ?)
                        ''', (filepath, stat.st_mtime, stat.st_size, time.time()))
                        added_count += 1
                    except OSError:
                        pass
            
            # Remove deleted files
            if files_to_remove:
                placeholders = ','.join('?' * len(files_to_remove))
                cursor.execute(f'''
                    DELETE FROM files 
                    WHERE filepath IN ({placeholders})
                ''', list(files_to_remove))
                removed_count = cursor.rowcount
            
            # Check for updated files (modified timestamp changed)
            if files_to_check:
                for filepath in files_to_check:
                    try:
                        stat = os.stat(filepath)
                        cursor.execute('''
                            SELECT last_modified, file_size FROM files 
                            WHERE filepath = ?
                        ''', (filepath,))
                        row = cursor.fetchone()
                        if row and (abs(row['last_modified'] - stat.st_mtime) > 0.01 or 
                                   row['file_size'] != stat.st_size):
                            cursor.execute('''
                                UPDATE files 
                                SET last_modified = ?, file_size = ?
                                WHERE filepath = ?
                            ''', (stat.st_mtime, stat.st_size, filepath))
                            updated_count += 1
                    except OSError:
                        pass
            
            conn.commit()
        
        logging.info(f"Synced file store: +{added_count} -{removed_count} ~{updated_count}")
        
        # Update last sync timestamp
        set_metadata('last_sync_timestamp', str(time.time()))
        
        return (added_count, removed_count, updated_count)
    except Exception as e:
        logging.error(f"Error syncing file store with filesystem: {e}")
        return (0, 0, 0)


# ==============================================================================
# MARKER STORE FUNCTIONS
# ==============================================================================

def add_marker(filepath: str, marker_type: str) -> bool:
    """Add a marker for a file"""
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('''
                INSERT OR IGNORE INTO markers (filepath, marker_type)
                VALUES (?, ?)
            ''', (filepath, marker_type))
            conn.commit()
            return True
    except Exception as e:
        logging.error(f"Error adding marker {marker_type} for {filepath}: {e}")
        return False


def remove_marker(filepath: str, marker_type: str) -> bool:
    """Remove a marker for a file"""
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('''
                DELETE FROM markers 
                WHERE filepath = ? AND marker_type = ?
            ''', (filepath, marker_type))
            deleted = cursor.rowcount > 0
            conn.commit()
            return deleted
    except Exception as e:
        logging.error(f"Error removing marker {marker_type} for {filepath}: {e}")
        return False


def has_marker(filepath: str, marker_type: str) -> bool:
    """Check if a file has a specific marker"""
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('''
                SELECT 1 FROM markers 
                WHERE filepath = ? AND marker_type = ?
            ''', (filepath, marker_type))
            return cursor.fetchone() is not None
    except Exception as e:
        logging.error(f"Error checking marker {marker_type} for {filepath}: {e}")
        return False


def get_markers(marker_type: str) -> Set[str]:
    """Get all files with a specific marker type"""
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('''
                SELECT filepath FROM markers 
                WHERE marker_type = ?
            ''', (marker_type,))
            return {row['filepath'] for row in cursor.fetchall()}
    except Exception as e:
        logging.error(f"Error getting markers of type {marker_type}: {e}")
        return set()


def get_all_markers_by_type(marker_types: list) -> dict:
    """
    Get all markers for multiple marker types in a single query.
    Returns a dict mapping marker_type to set of filepaths.
    
    This is much faster than calling get_markers() multiple times.
    
    Args:
        marker_types: List of marker type strings (e.g., ['processed', 'duplicate'])
    
    Returns:
        Dict mapping marker_type to set of filepaths
        Example: {'processed': {'/path/file1.cbz', ...}, 'duplicate': {'/path/file2.cbz', ...}}
    """
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            
            # Build query with IN clause for multiple types
            placeholders = ','.join('?' * len(marker_types))
            cursor.execute(f'''
                SELECT marker_type, filepath FROM markers 
                WHERE marker_type IN ({placeholders})
            ''', marker_types)
            
            # Build result dictionary
            result = {marker_type: set() for marker_type in marker_types}
            for row in cursor.fetchall():
                marker_type = row['marker_type']
                filepath = row['filepath']
                if marker_type in result:
                    result[marker_type].add(filepath)
            
            return result
    except Exception as e:
        logging.error(f"Error getting markers for types {marker_types}: {e}")
        return {marker_type: set() for marker_type in marker_types}


def get_unmarked_file_count() -> int:
    """
    Get the count of files that are not marked as 'processed'.
    This is much more efficient than loading all files and checking markers.
    
    Returns:
        Number of unmarked files
    """
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            
            # Count files that don't have a 'processed' marker
            cursor.execute('''
                SELECT COUNT(*) as count
                FROM files
                WHERE filepath NOT IN (
                    SELECT filepath FROM markers WHERE marker_type = 'processed'
                )
            ''')
            return cursor.fetchone()['count']
    except Exception as e:
        logging.error(f"Error getting unmarked file count: {e}")
        return 0


def cleanup_markers(marker_type: str, max_files: int) -> int:
    """Clean up old markers, keeping only the most recent ones"""
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            
            # Get count
            cursor.execute('''
                SELECT COUNT(*) as count FROM markers 
                WHERE marker_type = ?
            ''', (marker_type,))
            count = cursor.fetchone()['count']
            
            if count <= max_files:
                return 0
            
            # Delete oldest entries (keep last max_files based on filepath sort)
            cursor.execute('''
                DELETE FROM markers 
                WHERE marker_type = ? 
                AND filepath NOT IN (
                    SELECT filepath FROM markers 
                    WHERE marker_type = ?
                    ORDER BY filepath DESC
                    LIMIT ?
                )
            ''', (marker_type, marker_type, max_files))
            
            deleted = cursor.rowcount
            conn.commit()
            return deleted
    except Exception as e:
        logging.error(f"Error cleaning up markers of type {marker_type}: {e}")
        return 0


def batch_add_markers(filepaths: List[str], marker_type: str) -> int:
    """
    Add markers for multiple files in a single transaction.
    Much faster than calling add_marker() multiple times.
    
    Args:
        filepaths: List of file paths
        marker_type: Type of marker to add
        
    Returns:
        Number of markers added
    """
    if not filepaths:
        return 0
    
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            # Use executemany for batch insert
            cursor.executemany('''
                INSERT OR IGNORE INTO markers (filepath, marker_type)
                VALUES (?, ?)
            ''', [(filepath, marker_type) for filepath in filepaths])
            added = cursor.rowcount
            conn.commit()
            return added
    except Exception as e:
        logging.error(f"Error batch adding markers of type {marker_type}: {e}")
        return 0


def batch_remove_markers(filepaths: List[str], marker_type: str) -> int:
    """
    Remove markers for multiple files in a single transaction.
    Much faster than calling remove_marker() multiple times.
    
    Args:
        filepaths: List of file paths
        marker_type: Type of marker to remove
        
    Returns:
        Number of markers removed
    """
    if not filepaths:
        return 0
    
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            # Use executemany for batch delete
            cursor.executemany('''
                DELETE FROM markers 
                WHERE filepath = ? AND marker_type = ?
            ''', [(filepath, marker_type) for filepath in filepaths])
            removed = cursor.rowcount
            conn.commit()
            return removed
    except Exception as e:
        logging.error(f"Error batch removing markers of type {marker_type}: {e}")
        return 0


# ==============================================================================
# METADATA FUNCTIONS
# ==============================================================================

def set_metadata(key: str, value: str) -> bool:
    """Set a metadata value"""
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('''
                INSERT OR REPLACE INTO metadata (key, value)
                VALUES (?, ?)
            ''', (key, value))
            conn.commit()
            return True
    except Exception as e:
        logging.error(f"Error setting metadata {key}: {e}")
        return False


def get_metadata(key: str, default: str = None) -> Optional[str]:
    """Get a metadata value"""
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('''
                SELECT value FROM metadata 
                WHERE key = ?
            ''', (key,))
            row = cursor.fetchone()
            return row['value'] if row else default
    except Exception as e:
        logging.error(f"Error getting metadata {key}: {e}")
        return default


def get_last_sync_timestamp() -> Optional[float]:
    """Get the timestamp of the last filesystem sync"""
    value = get_metadata('last_sync_timestamp')
    if value:
        try:
            return float(value)
        except ValueError:
            return None
    return None


# ==============================================================================
# MIGRATION FUNCTIONS
# ==============================================================================

def migrate_from_old_databases():
    """
    Migrate data from the old separate databases (marker_store and file_store)
    to the new unified database.
    """
    marker_db_path = os.path.join(CONFIG_DIR, 'markers', 'markers.db')
    file_db_path = os.path.join(CONFIG_DIR, 'file_store', 'files.db')
    
    migrated_markers = False
    migrated_files = False
    
    # Migrate markers
    if os.path.exists(marker_db_path):
        try:
            logging.info("Migrating data from old marker database...")
            old_conn = sqlite3.connect(marker_db_path, timeout=30.0)
            old_conn.row_factory = sqlite3.Row
            old_cursor = old_conn.cursor()
            
            # Get all markers
            old_cursor.execute('SELECT filepath, marker_type FROM markers')
            markers_data = old_cursor.fetchall()
            old_conn.close()
            
            # Insert into new database
            with get_db_connection() as conn:
                cursor = conn.cursor()
                for row in markers_data:
                    cursor.execute('''
                        INSERT OR IGNORE INTO markers (filepath, marker_type)
                        VALUES (?, ?)
                    ''', (row['filepath'], row['marker_type']))
                conn.commit()
            
            logging.info(f"Migrated {len(markers_data)} markers from old database")
            
            # Rename old database as backup
            backup_path = f"{marker_db_path}.migrated.{int(time.time())}"
            os.rename(marker_db_path, backup_path)
            logging.info(f"Backed up old marker database to {backup_path}")
            migrated_markers = True
            
        except Exception as e:
            logging.error(f"Error migrating marker database: {e}")
    
    # Migrate files
    if os.path.exists(file_db_path):
        try:
            logging.info("Migrating data from old file store database...")
            old_conn = sqlite3.connect(file_db_path, timeout=30.0)
            old_conn.row_factory = sqlite3.Row
            old_cursor = old_conn.cursor()
            
            # Get all files
            old_cursor.execute('SELECT filepath, last_modified, file_size, added_timestamp FROM files')
            files_data = old_cursor.fetchall()
            
            # Get metadata
            old_cursor.execute('SELECT key, value FROM metadata')
            metadata_data = old_cursor.fetchall()
            
            old_conn.close()
            
            # Insert into new database
            with get_db_connection() as conn:
                cursor = conn.cursor()
                
                # Migrate files
                for row in files_data:
                    cursor.execute('''
                        INSERT OR REPLACE INTO files (filepath, last_modified, file_size, added_timestamp)
                        VALUES (?, ?, ?, ?)
                    ''', (row['filepath'], row['last_modified'], row['file_size'], row['added_timestamp']))
                
                # Migrate metadata
                for row in metadata_data:
                    cursor.execute('''
                        INSERT OR REPLACE INTO metadata (key, value)
                        VALUES (?, ?)
                    ''', (row['key'], row['value']))
                
                conn.commit()
            
            logging.info(f"Migrated {len(files_data)} files and {len(metadata_data)} metadata entries from old database")
            
            # Rename old database as backup
            backup_path = f"{file_db_path}.migrated.{int(time.time())}"
            os.rename(file_db_path, backup_path)
            logging.info(f"Backed up old file store database to {backup_path}")
            migrated_files = True
            
        except Exception as e:
            logging.error(f"Error migrating file store database: {e}")
    
    if migrated_markers or migrated_files:
        logging.info("Database migration completed successfully")
        set_metadata('migration_completed', str(time.time()))
    
    return migrated_markers, migrated_files


# ============================================================================
# Processing History Management
# ============================================================================

def add_processing_history(
    filepath: str,
    operation_type: str,
    before_filename: str = None,
    after_filename: str = None,
    before_title: str = None,
    after_title: str = None,
    before_series: str = None,
    after_series: str = None,
    before_issue: str = None,
    after_issue: str = None,
    before_publisher: str = None,
    after_publisher: str = None,
    before_year: str = None,
    after_year: str = None,
    before_volume: str = None,
    after_volume: str = None
) -> bool:
    """
    Add a processing history entry.
    
    Args:
        filepath: Path to the file being processed
        operation_type: Type of operation (e.g., 'process', 'rename', 'normalize')
        before_*/after_*: Before and after values for various fields
    
    Returns:
        True if successful, False otherwise
    """
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('''
                INSERT INTO processing_history (
                    filepath, timestamp, before_filename, after_filename,
                    before_title, after_title, before_series, after_series,
                    before_issue, after_issue, before_publisher, after_publisher,
                    before_year, after_year, before_volume, after_volume,
                    operation_type
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            ''', (
                filepath, time.time(), before_filename, after_filename,
                before_title, after_title, before_series, after_series,
                before_issue, after_issue, before_publisher, after_publisher,
                before_year, after_year, before_volume, after_volume,
                operation_type
            ))
            conn.commit()
            logging.debug(f"Added processing history entry for {filepath}")
            return True
    except Exception as e:
        logging.error(f"Error adding processing history: {e}")
        return False


def get_processing_history(limit: int = 100, offset: int = 0) -> List[Dict]:
    """
    Get processing history entries, ordered by timestamp (newest first).
    
    Args:
        limit: Maximum number of entries to return
        offset: Number of entries to skip (for pagination)
    
    Returns:
        List of history entries as dictionaries
    """
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('''
                SELECT 
                    id, filepath, timestamp, before_filename, after_filename,
                    before_title, after_title, before_series, after_series,
                    before_issue, after_issue, before_publisher, after_publisher,
                    before_year, after_year, before_volume, after_volume,
                    operation_type
                FROM processing_history
                ORDER BY timestamp DESC
                LIMIT ? OFFSET ?
            ''', (limit, offset))
            
            rows = cursor.fetchall()
            history = []
            for row in rows:
                history.append({
                    'id': row[0],
                    'filepath': row[1],
                    'timestamp': row[2],
                    'before_filename': row[3],
                    'after_filename': row[4],
                    'before_title': row[5],
                    'after_title': row[6],
                    'before_series': row[7],
                    'after_series': row[8],
                    'before_issue': row[9],
                    'after_issue': row[10],
                    'before_publisher': row[11],
                    'after_publisher': row[12],
                    'before_year': row[13],
                    'after_year': row[14],
                    'before_volume': row[15],
                    'after_volume': row[16],
                    'operation_type': row[17]
                })
            return history
    except Exception as e:
        logging.error(f"Error getting processing history: {e}")
        return []


def get_processing_history_count() -> int:
    """
    Get total count of processing history entries.
    
    Returns:
        Number of history entries
    """
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('SELECT COUNT(*) FROM processing_history')
            return cursor.fetchone()[0]
    except Exception as e:
        logging.error(f"Error getting processing history count: {e}")
        return 0


def clear_processing_history() -> int:
    """
    Clear all processing history entries.
    
    Returns:
        Number of entries deleted
    """
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('DELETE FROM processing_history')
            deleted = cursor.rowcount
            conn.commit()
            logging.info(f"Cleared {deleted} processing history entries")
            return deleted
    except Exception as e:
        logging.error(f"Error clearing processing history: {e}")
        return 0
