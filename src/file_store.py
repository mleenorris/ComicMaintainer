"""
SQLite-based file list storage for tracking comic files.
Provides efficient file list management with atomic operations for adding, removing, and renaming files.
"""
import sqlite3
import logging
import threading
import os
import time
from typing import List, Optional, Set, Tuple
from contextlib import contextmanager

# Database configuration
CONFIG_DIR = '/Config'
FILE_STORE_DIR = os.path.join(CONFIG_DIR, 'file_store')
DB_PATH = os.path.join(FILE_STORE_DIR, 'files.db')

# Thread-local storage for database connections
_thread_local = threading.local()


def _ensure_file_store_dir():
    """Ensure file_store directory exists"""
    os.makedirs(FILE_STORE_DIR, exist_ok=True)


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
    
    # Create indexes for faster queries
    cursor.execute('''
        CREATE INDEX IF NOT EXISTS idx_files_last_modified 
        ON files(last_modified)
    ''')
    
    cursor.execute('''
        CREATE INDEX IF NOT EXISTS idx_files_added_timestamp 
        ON files(added_timestamp)
    ''')
    
    # Metadata table for tracking last full scan
    cursor.execute('''
        CREATE TABLE IF NOT EXISTS metadata (
            key TEXT PRIMARY KEY NOT NULL,
            value TEXT NOT NULL
        )
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
        
        _ensure_file_store_dir()
        
        # Create a temporary connection to initialize the database
        conn = sqlite3.connect(DB_PATH, timeout=30.0)
        try:
            _init_db_schema(conn)
            logging.info(f"Initialized file store database at {DB_PATH}")
        finally:
            conn.close()
        
        _db_initialized = True


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
                            INSERT INTO files (filepath, last_modified, file_size, added_timestamp)
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
