"""
SQLite-based marker storage for tracking file processing status.
Replaces JSON file storage with a more efficient and reliable database backend.
"""
import sqlite3
import logging
import threading
import os
from typing import Set, Optional
from contextlib import contextmanager

# Database configuration
CONFIG_DIR = '/Config'
MARKERS_DIR = os.path.join(CONFIG_DIR, 'markers')
DB_PATH = os.path.join(MARKERS_DIR, 'markers.db')

# Thread-local storage for database connections
_thread_local = threading.local()


def _ensure_markers_dir():
    """Ensure markers directory exists"""
    os.makedirs(MARKERS_DIR, exist_ok=True)


def _init_db_schema(conn):
    """Initialize database schema on a connection"""
    cursor = conn.cursor()
    
    # Markers table - stores all file markers with their type
    cursor.execute('''
        CREATE TABLE IF NOT EXISTS markers (
            filepath TEXT NOT NULL,
            marker_type TEXT NOT NULL,
            PRIMARY KEY (filepath, marker_type)
        )
    ''')
    
    # Create indexes for faster queries
    cursor.execute('''
        CREATE INDEX IF NOT EXISTS idx_markers_filepath 
        ON markers(filepath)
    ''')
    
    cursor.execute('''
        CREATE INDEX IF NOT EXISTS idx_markers_type 
        ON markers(marker_type)
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
        
        _ensure_markers_dir()
        
        # Create a temporary connection to initialize the database
        conn = sqlite3.connect(DB_PATH, timeout=30.0)
        try:
            _init_db_schema(conn)
            logging.info(f"Initialized marker database at {DB_PATH}")
        finally:
            conn.close()
        
        _db_initialized = True


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
