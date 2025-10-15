"""
SQLite-based job storage for cross-process job state management.
Enables multiple Gunicorn workers to share job state.
"""
import sqlite3
import json
import logging
import threading
import time
import os
from typing import Optional, List, Dict, Any
from contextlib import contextmanager

# Database configuration
CONFIG_DIR = '/Config'
DB_PATH = os.path.join(CONFIG_DIR, 'jobs.db')

# Thread-local storage for database connections
_thread_local = threading.local()


def _ensure_cache_dir():
    """Ensure config directory exists"""
    os.makedirs(CONFIG_DIR, exist_ok=True)


def _init_db_schema(conn):
    """Initialize database schema on a connection"""
    cursor = conn.cursor()
    
    # Jobs table
    cursor.execute('''
        CREATE TABLE IF NOT EXISTS jobs (
            job_id TEXT PRIMARY KEY,
            status TEXT NOT NULL,
            total_items INTEGER NOT NULL,
            processed_items INTEGER DEFAULT 0,
            error TEXT,
            created_at REAL NOT NULL,
            started_at REAL,
            completed_at REAL
        )
    ''')
    
    # Job results table
    cursor.execute('''
        CREATE TABLE IF NOT EXISTS job_results (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            job_id TEXT NOT NULL,
            item TEXT NOT NULL,
            success INTEGER NOT NULL,
            error TEXT,
            details TEXT,
            FOREIGN KEY (job_id) REFERENCES jobs(job_id) ON DELETE CASCADE
        )
    ''')
    
    # Create index for faster queries
    cursor.execute('''
        CREATE INDEX IF NOT EXISTS idx_job_results_job_id 
        ON job_results(job_id)
    ''')
    
    cursor.execute('''
        CREATE INDEX IF NOT EXISTS idx_jobs_status 
        ON jobs(status)
    ''')
    
    cursor.execute('''
        CREATE INDEX IF NOT EXISTS idx_jobs_completed_at 
        ON jobs(completed_at)
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
        _ensure_cache_dir()
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


def init_db():
    """Initialize database schema (called on module import)"""
    _ensure_cache_dir()
    
    # Create a temporary connection to initialize the database
    conn = sqlite3.connect(DB_PATH, timeout=30.0)
    try:
        _init_db_schema(conn)
        logging.info(f"Initialized job database at {DB_PATH}")
    finally:
        conn.close()


def create_job(job_id: str, total_items: int, created_at: float) -> bool:
    """Create a new job in the database"""
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('''
                INSERT INTO jobs (job_id, status, total_items, created_at)
                VALUES (?, ?, ?, ?)
            ''', (job_id, 'queued', total_items, created_at))
            conn.commit()
            return True
    except Exception as e:
        logging.error(f"Error creating job {job_id}: {e}")
        return False


def update_job_status(job_id: str, status: str, started_at: Optional[float] = None, 
                     completed_at: Optional[float] = None, error: Optional[str] = None) -> bool:
    """Update job status and timestamps"""
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            
            updates = ['status = ?']
            params = [status]
            
            if started_at is not None:
                updates.append('started_at = ?')
                params.append(started_at)
            
            if completed_at is not None:
                updates.append('completed_at = ?')
                params.append(completed_at)
            
            if error is not None:
                updates.append('error = ?')
                params.append(error)
            
            params.append(job_id)
            
            cursor.execute(f'''
                UPDATE jobs 
                SET {', '.join(updates)}
                WHERE job_id = ?
            ''', params)
            conn.commit()
            return True
    except Exception as e:
        logging.error(f"Error updating job {job_id}: {e}")
        return False


def add_job_result(job_id: str, item: str, success: bool, error: Optional[str] = None,
                  details: Optional[Dict[str, Any]] = None) -> bool:
    """Add a result for a job item"""
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            
            # Add result
            cursor.execute('''
                INSERT INTO job_results (job_id, item, success, error, details)
                VALUES (?, ?, ?, ?, ?)
            ''', (job_id, item, 1 if success else 0, error, 
                  json.dumps(details) if details else None))
            
            # Update processed_items count
            cursor.execute('''
                UPDATE jobs 
                SET processed_items = (
                    SELECT COUNT(*) FROM job_results WHERE job_id = ?
                )
                WHERE job_id = ?
            ''', (job_id, job_id))
            
            conn.commit()
            return True
    except Exception as e:
        logging.error(f"Error adding result for job {job_id}: {e}")
        return False


def get_job(job_id: str) -> Optional[Dict[str, Any]]:
    """Get job details with all results"""
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            
            # Get job
            cursor.execute('SELECT * FROM jobs WHERE job_id = ?', (job_id,))
            job_row = cursor.fetchone()
            
            if not job_row:
                return None
            
            # Get results
            cursor.execute('''
                SELECT item, success, error, details 
                FROM job_results 
                WHERE job_id = ?
                ORDER BY id
            ''', (job_id,))
            results = cursor.fetchall()
            
            # Build response
            job = {
                'job_id': job_row['job_id'],
                'status': job_row['status'],
                'total_items': job_row['total_items'],
                'processed_items': job_row['processed_items'],
                'progress': job_row['processed_items'] / job_row['total_items'] if job_row['total_items'] > 0 else 0,
                'results': [
                    {
                        'item': r['item'],
                        'success': bool(r['success']),
                        'error': r['error'],
                        'details': json.loads(r['details']) if r['details'] else {}
                    }
                    for r in results
                ],
                'error': job_row['error'],
                'created_at': job_row['created_at'],
                'started_at': job_row['started_at'],
                'completed_at': job_row['completed_at']
            }
            
            return job
    except Exception as e:
        logging.error(f"Error getting job {job_id}: {e}")
        return None


def list_jobs() -> List[Dict[str, Any]]:
    """List all jobs (summary only, no results)"""
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('''
                SELECT job_id, status, total_items, processed_items, 
                       created_at, started_at, completed_at
                FROM jobs
                ORDER BY created_at DESC
            ''')
            rows = cursor.fetchall()
            
            return [
                {
                    'job_id': row['job_id'],
                    'status': row['status'],
                    'total_items': row['total_items'],
                    'processed_items': row['processed_items'],
                    'created_at': row['created_at'],
                    'started_at': row['started_at'],
                    'completed_at': row['completed_at']
                }
                for row in rows
            ]
    except Exception as e:
        logging.error(f"Error listing jobs: {e}")
        return []


def delete_job(job_id: str) -> bool:
    """Delete a job and its results"""
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('DELETE FROM jobs WHERE job_id = ?', (job_id,))
            deleted = cursor.rowcount > 0
            conn.commit()
            return deleted
    except Exception as e:
        logging.error(f"Error deleting job {job_id}: {e}")
        return False


def cleanup_old_jobs(cutoff_time: float) -> int:
    """Delete old completed jobs"""
    try:
        with get_db_connection() as conn:
            cursor = conn.cursor()
            cursor.execute('''
                DELETE FROM jobs 
                WHERE status IN ('completed', 'failed', 'cancelled')
                AND completed_at IS NOT NULL
                AND completed_at < ?
            ''', (cutoff_time,))
            deleted = cursor.rowcount
            conn.commit()
            return deleted
    except Exception as e:
        logging.error(f"Error cleaning up old jobs: {e}")
        return 0


# Initialize database on module import
init_db()
