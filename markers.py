"""
Centralized marker management for tracking file processing status.
Markers are now stored server-side in SQLite database in /Config/markers instead of JSON files.

Key Features:
- Thread-safe operations with SQLite database
- ACID compliance prevents data corruption
- WAL mode enables concurrent reads and writes
- More efficient storage and retrieval
- Better scalability for large file collections

Migration from JSON:
If JSON marker files exist, they will be automatically imported on first use.
Original JSON files are preserved as backups.
"""

import os
import json
import logging
import threading
from typing import Set, Optional
from marker_store import add_marker, remove_marker, has_marker, get_markers, cleanup_markers

# Marker storage configuration (for legacy JSON migration)
CONFIG_DIR = '/Config'
MARKERS_DIR = os.path.join(CONFIG_DIR, 'markers')
MARKER_UPDATE_TIMESTAMP = '.marker_update'

# Legacy marker file names (for migration)
PROCESSED_MARKER_FILE = 'processed_files.json'
DUPLICATE_MARKER_FILE = 'duplicate_files.json'
WEB_MODIFIED_MARKER_FILE = 'web_modified_files.json'

# Marker type constants
MARKER_TYPE_PROCESSED = 'processed'
MARKER_TYPE_DUPLICATE = 'duplicate'
MARKER_TYPE_WEB_MODIFIED = 'web_modified'

# Thread locks for concurrent access (used during migration)
_processed_lock = threading.Lock()
_duplicate_lock = threading.Lock()
_web_modified_lock = threading.Lock()
_migration_lock = threading.Lock()
_migrated = set()  # Track which marker types have been migrated


def _migrate_json_markers(marker_file: str, marker_type: str):
    """
    Migrate markers from legacy JSON file to SQLite database.
    This is called once per marker type on first access.
    """
    with _migration_lock:
        # Check if already migrated
        if marker_type in _migrated:
            return
        
        marker_path = os.path.join(MARKERS_DIR, marker_file)
        if not os.path.exists(marker_path):
            _migrated.add(marker_type)
            return
        
        try:
            with open(marker_path, 'r') as f:
                data = json.load(f)
                files = set(data.get('files', []))
            
            # Import into SQLite
            import_count = 0
            for filepath in files:
                if add_marker(filepath, marker_type):
                    import_count += 1
            
            logging.info(f"Migrated {import_count} markers of type '{marker_type}' from JSON to SQLite")
            
            # Rename JSON file as backup
            import time
            backup_path = f"{marker_path}.migrated.{int(time.time())}"
            os.rename(marker_path, backup_path)
            logging.info(f"Backed up JSON marker file to {backup_path}")
            
            _migrated.add(marker_type)
            
        except json.JSONDecodeError as e:
            logging.error(f"Error migrating marker file {marker_path}: {e}")
            
            # Try to recover data by reading the file and extracting valid entries
            try:
                with open(marker_path, 'r') as f:
                    content = f.read()
                
                # Attempt to extract file paths from the corrupted JSON
                import re
                file_paths = set()
                path_pattern = r'"(/[^"]+)"'
                matches = re.findall(path_pattern, content)
                if matches:
                    file_paths = set(matches)
                    
                    # Import recovered paths
                    import_count = 0
                    for filepath in file_paths:
                        if add_marker(filepath, marker_type):
                            import_count += 1
                    
                    logging.info(f"Recovered and migrated {import_count} markers from corrupted JSON")
                
                # Rename corrupted file as backup
                import time
                backup_path = f"{marker_path}.corrupt.{int(time.time())}"
                os.rename(marker_path, backup_path)
                logging.warning(f"Backed up corrupted marker file to {backup_path}")
                
            except Exception as recovery_error:
                logging.error(f"Failed to recover data from corrupted file: {recovery_error}")
            
            _migrated.add(marker_type)
            
        except Exception as e:
            logging.error(f"Unexpected error migrating marker file {marker_path}: {e}")
            _migrated.add(marker_type)


def _update_marker_timestamp():
    """Update the marker invalidation timestamp to trigger cache refresh"""
    # Ensure config directory exists
    os.makedirs(CONFIG_DIR, exist_ok=True)
    
    marker_path = os.path.join(CONFIG_DIR, MARKER_UPDATE_TIMESTAMP)
    try:
        import time
        with open(marker_path, 'w') as f:
            f.write(str(time.time()))
    except Exception as e:
        logging.error(f"Error updating marker timestamp: {e}")


# Processed files marker functions
def is_file_processed(filepath: str) -> bool:
    """Check if a file has been processed"""
    _migrate_json_markers(PROCESSED_MARKER_FILE, MARKER_TYPE_PROCESSED)
    abs_path = os.path.abspath(filepath)
    return has_marker(abs_path, MARKER_TYPE_PROCESSED)


def mark_file_processed(filepath: str, original_filepath: Optional[str] = None):
    """Mark a file as processed, optionally cleaning up old filename if renamed"""
    _migrate_json_markers(PROCESSED_MARKER_FILE, MARKER_TYPE_PROCESSED)
    abs_path = os.path.abspath(filepath)
    
    # If file was renamed, remove the old path
    if original_filepath and original_filepath != filepath:
        old_abs_path = os.path.abspath(original_filepath)
        if has_marker(old_abs_path, MARKER_TYPE_PROCESSED):
            remove_marker(old_abs_path, MARKER_TYPE_PROCESSED)
            logging.info(f"Removed old path '{original_filepath}' from processed marker after rename")
    
    # Add current file
    add_marker(abs_path, MARKER_TYPE_PROCESSED)
    logging.info(f"Marked {filepath} as processed")
    
    # Invalidate enriched cache by updating marker timestamp
    _update_marker_timestamp()


def unmark_file_processed(filepath: str):
    """Remove a file from the processed marker (e.g., when deleted)"""
    _migrate_json_markers(PROCESSED_MARKER_FILE, MARKER_TYPE_PROCESSED)
    abs_path = os.path.abspath(filepath)
    remove_marker(abs_path, MARKER_TYPE_PROCESSED)
    
    # Invalidate enriched cache by updating marker timestamp
    _update_marker_timestamp()


# Duplicate files marker functions
def is_file_duplicate(filepath: str) -> bool:
    """Check if a file is marked as a duplicate"""
    _migrate_json_markers(DUPLICATE_MARKER_FILE, MARKER_TYPE_DUPLICATE)
    abs_path = os.path.abspath(filepath)
    return has_marker(abs_path, MARKER_TYPE_DUPLICATE)


def mark_file_duplicate(filepath: str):
    """Mark a file as a duplicate"""
    _migrate_json_markers(DUPLICATE_MARKER_FILE, MARKER_TYPE_DUPLICATE)
    abs_path = os.path.abspath(filepath)
    add_marker(abs_path, MARKER_TYPE_DUPLICATE)
    logging.info(f"Marked {filepath} as duplicate")
    
    # Invalidate enriched cache by updating marker timestamp
    _update_marker_timestamp()


def unmark_file_duplicate(filepath: str):
    """Remove a file from the duplicate marker"""
    _migrate_json_markers(DUPLICATE_MARKER_FILE, MARKER_TYPE_DUPLICATE)
    abs_path = os.path.abspath(filepath)
    remove_marker(abs_path, MARKER_TYPE_DUPLICATE)
    
    # Invalidate enriched cache by updating marker timestamp
    _update_marker_timestamp()


# Web modified files marker functions
def is_file_web_modified(filepath: str) -> bool:
    """Check if a file was recently modified by the web interface"""
    _migrate_json_markers(WEB_MODIFIED_MARKER_FILE, MARKER_TYPE_WEB_MODIFIED)
    abs_path = os.path.abspath(filepath)
    return has_marker(abs_path, MARKER_TYPE_WEB_MODIFIED)


def mark_file_web_modified(filepath: str):
    """Mark a file as modified by the web interface"""
    _migrate_json_markers(WEB_MODIFIED_MARKER_FILE, MARKER_TYPE_WEB_MODIFIED)
    abs_path = os.path.abspath(filepath)
    add_marker(abs_path, MARKER_TYPE_WEB_MODIFIED)


def clear_file_web_modified(filepath: str):
    """Clear the web modified marker for a file (consumed by watcher)"""
    _migrate_json_markers(WEB_MODIFIED_MARKER_FILE, MARKER_TYPE_WEB_MODIFIED)
    abs_path = os.path.abspath(filepath)
    if has_marker(abs_path, MARKER_TYPE_WEB_MODIFIED):
        remove_marker(abs_path, MARKER_TYPE_WEB_MODIFIED)
        logging.info(f"Cleared web modified marker for {filepath}")
        return True
    return False


def cleanup_web_modified_markers(max_files: int = 100):
    """Clean up old web modified markers, keeping only the most recent ones"""
    _migrate_json_markers(WEB_MODIFIED_MARKER_FILE, MARKER_TYPE_WEB_MODIFIED)
    total_markers = len(get_markers(MARKER_TYPE_WEB_MODIFIED))
    if total_markers > max_files:
        deleted = cleanup_markers(MARKER_TYPE_WEB_MODIFIED, max_files)
        logging.info(f"Cleaned up web modified markers, removed {deleted} old markers, keeping {max_files}")
