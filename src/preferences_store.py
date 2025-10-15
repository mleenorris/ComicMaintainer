"""
SQLite-based preferences storage for user preferences and active job tracking.
Replaces client-side localStorage with server-side persistence.
"""
import sqlite3
import json
import logging
import threading
import os
from typing import Optional, Dict, Any
from contextlib import contextmanager

# Database configuration
CONFIG_DIR = '/Config'
DB_PATH = os.path.join(CONFIG_DIR, 'preferences.db')

# Thread-local storage for database connections
_thread_local = threading.local()

logger = logging.getLogger(__name__)


def _ensure_config_dir():
    """Ensure config directory exists"""
    os.makedirs(CONFIG_DIR, exist_ok=True)


def _init_db_schema(conn):
    """Initialize database schema on a connection"""
    cursor = conn.cursor()
    
    # Preferences table - stores user preferences
    cursor.execute('''
        CREATE TABLE IF NOT EXISTS preferences (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at REAL NOT NULL
        )
    ''')
    
    # Active job table - tracks currently running batch job
    cursor.execute('''
        CREATE TABLE IF NOT EXISTS active_job (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            job_id TEXT,
            job_title TEXT,
            updated_at REAL NOT NULL
        )
    ''')
    
    # Insert default row for active job tracking
    cursor.execute('''
        INSERT OR IGNORE INTO active_job (id, job_id, job_title, updated_at)
        VALUES (1, NULL, NULL, 0)
    ''')
    
    conn.commit()


@contextmanager
def get_db_connection():
    """
    Get a thread-local database connection.
    Uses context manager for automatic cleanup.
    Ensures database is initialized on first access.
    """
    # Check if connection exists in thread-local storage
    if not hasattr(_thread_local, 'connection') or _thread_local.connection is None:
        _ensure_config_dir()
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


def get_preference(key: str, default: Any = None) -> Any:
    """
    Get a preference value by key.
    
    Args:
        key: Preference key
        default: Default value if key not found
        
    Returns:
        Preference value or default
    """
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('SELECT value FROM preferences WHERE key = ?', (key,))
            row = cursor.fetchone()
            
            if row:
                # Try to parse as JSON, fall back to raw string
                try:
                    return json.loads(row['value'])
                except (json.JSONDecodeError, TypeError):
                    return row['value']
            
            return default
    except Exception as e:
        logger.error(f"Error getting preference {key}: {e}")
        return default


def set_preference(key: str, value: Any):
    """
    Set a preference value.
    
    Args:
        key: Preference key
        value: Preference value (will be JSON-encoded)
    """
    try:
        import time
        with get_db_connection() as conn:
            cursor = conn.cursor()
            
            # JSON-encode the value
            json_value = json.dumps(value) if not isinstance(value, str) else value
            
            cursor.execute('''
                INSERT OR REPLACE INTO preferences (key, value, updated_at)
                VALUES (?, ?, ?)
            ''', (key, json_value, time.time()))
            
            conn.commit()
            logger.info(f"Set preference {key} = {value}")
    except Exception as e:
        logger.error(f"Error setting preference {key}: {e}")
        raise


def get_all_preferences() -> Dict[str, Any]:
    """
    Get all preferences as a dictionary.
    
    Returns:
        Dictionary of all preferences
    """
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('SELECT key, value FROM preferences')
            rows = cursor.fetchall()
            
            preferences = {}
            for row in rows:
                key = row['key']
                # Try to parse as JSON, fall back to raw string
                try:
                    preferences[key] = json.loads(row['value'])
                except (json.JSONDecodeError, TypeError):
                    preferences[key] = row['value']
            
            return preferences
    except Exception as e:
        logger.error(f"Error getting all preferences: {e}")
        return {}


def get_active_job() -> Optional[Dict[str, str]]:
    """
    Get the currently active job.
    
    Returns:
        Dictionary with job_id and job_title, or None if no active job
    """
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('SELECT job_id, job_title FROM active_job WHERE id = 1')
            row = cursor.fetchone()
            
            if row and row['job_id']:
                return {
                    'job_id': row['job_id'],
                    'job_title': row['job_title']
                }
            
            return None
    except Exception as e:
        logger.error(f"Error getting active job: {e}")
        return None


def set_active_job(job_id: str, job_title: str):
    """
    Set the currently active job.
    
    Args:
        job_id: Job ID
        job_title: Job title/description
    """
    try:
        import time
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('''
                UPDATE active_job 
                SET job_id = ?, job_title = ?, updated_at = ?
                WHERE id = 1
            ''', (job_id, job_title, time.time()))
            
            conn.commit()
            logger.info(f"Set active job: {job_id} - {job_title}")
    except Exception as e:
        logger.error(f"Error setting active job: {e}")
        raise


def clear_active_job():
    """Clear the currently active job."""
    try:
        import time
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('''
                UPDATE active_job 
                SET job_id = NULL, job_title = NULL, updated_at = ?
                WHERE id = 1
            ''', (time.time(),))
            
            conn.commit()
            logger.info("Cleared active job")
    except Exception as e:
        logger.error(f"Error clearing active job: {e}")
        raise
