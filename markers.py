"""
Centralized marker management for tracking file processing status.
Markers are now stored server-side in CACHE_DIR instead of in the watched directory.
"""

import os
import json
import logging
import threading
from typing import Set, Optional

# Marker storage configuration
CACHE_DIR = os.environ.get('CACHE_DIR', '/app/cache')
MARKERS_DIR = os.path.join(CACHE_DIR, 'markers')

# Marker file names in the cache directory
PROCESSED_MARKER_FILE = 'processed_files.json'
DUPLICATE_MARKER_FILE = 'duplicate_files.json'
WEB_MODIFIED_MARKER_FILE = 'web_modified_files.json'

# Thread locks for concurrent access
_processed_lock = threading.Lock()
_duplicate_lock = threading.Lock()
_web_modified_lock = threading.Lock()


def _ensure_markers_dir():
    """Ensure the markers directory exists"""
    os.makedirs(MARKERS_DIR, exist_ok=True)


def _load_marker_set(marker_file: str) -> Set[str]:
    """Load a marker set from JSON file"""
    marker_path = os.path.join(MARKERS_DIR, marker_file)
    if not os.path.exists(marker_path):
        return set()
    
    try:
        with open(marker_path, 'r') as f:
            data = json.load(f)
            return set(data.get('files', []))
    except json.JSONDecodeError as e:
        logging.error(f"Error loading marker file {marker_path}: {e}")
        
        # Create backup of corrupted file
        import time
        backup_path = f"{marker_path}.corrupt.{int(time.time())}"
        try:
            import shutil
            shutil.copy2(marker_path, backup_path)
            logging.warning(f"Created backup of corrupted marker file: {backup_path}")
        except Exception as backup_error:
            logging.error(f"Failed to create backup of corrupted file: {backup_error}")
        
        # Try to recover data by reading the file and extracting valid entries
        try:
            with open(marker_path, 'r') as f:
                content = f.read()
            
            # Attempt to extract file paths from the corrupted JSON
            # Look for patterns like "/path/to/file" in the content
            import re
            file_paths = set()
            # Match quoted strings that look like file paths (starting with /)
            path_pattern = r'"(/[^"]+)"'
            matches = re.findall(path_pattern, content)
            if matches:
                file_paths = set(matches)
                logging.info(f"Recovered {len(file_paths)} file paths from corrupted marker file")
            
            # Remove the corrupted file and start fresh
            os.remove(marker_path)
            logging.warning(f"Removed corrupted marker file: {marker_path}")
            
            return file_paths
        except Exception as recovery_error:
            logging.error(f"Failed to recover data from corrupted file: {recovery_error}")
            
            # Remove the corrupted file to start fresh
            try:
                os.remove(marker_path)
                logging.warning(f"Removed corrupted marker file: {marker_path}")
            except Exception as remove_error:
                logging.error(f"Failed to remove corrupted file: {remove_error}")
            
            return set()
    except Exception as e:
        logging.error(f"Unexpected error loading marker file {marker_path}: {e}")
        return set()


def _save_marker_set(marker_file: str, files: Set[str]):
    """Save a marker set to JSON file using atomic write"""
    _ensure_markers_dir()
    marker_path = os.path.join(MARKERS_DIR, marker_file)
    
    try:
        # Convert to absolute paths and save
        data = {
            'files': sorted(list(files))
        }
        
        # Validate JSON before writing
        json_str = json.dumps(data, indent=2)
        
        # Use atomic write: write to temp file, then rename
        temp_path = f"{marker_path}.tmp"
        with open(temp_path, 'w') as f:
            f.write(json_str)
            f.flush()
            os.fsync(f.fileno())  # Ensure data is written to disk
        
        # Atomic rename - this is atomic on POSIX systems
        os.replace(temp_path, marker_path)
        
    except Exception as e:
        logging.error(f"Error saving marker file {marker_path}: {e}")
        # Clean up temp file if it exists
        temp_path = f"{marker_path}.tmp"
        if os.path.exists(temp_path):
            try:
                os.remove(temp_path)
            except:
                pass


# Processed files marker functions
def is_file_processed(filepath: str) -> bool:
    """Check if a file has been processed"""
    abs_path = os.path.abspath(filepath)
    with _processed_lock:
        processed_files = _load_marker_set(PROCESSED_MARKER_FILE)
        return abs_path in processed_files


def mark_file_processed(filepath: str, original_filepath: Optional[str] = None):
    """Mark a file as processed, optionally cleaning up old filename if renamed"""
    abs_path = os.path.abspath(filepath)
    
    with _processed_lock:
        processed_files = _load_marker_set(PROCESSED_MARKER_FILE)
        
        # If file was renamed, remove the old path
        if original_filepath and original_filepath != filepath:
            old_abs_path = os.path.abspath(original_filepath)
            if old_abs_path in processed_files:
                processed_files.discard(old_abs_path)
                logging.info(f"Removed old path '{original_filepath}' from processed marker after rename")
        
        # Add current file
        processed_files.add(abs_path)
        _save_marker_set(PROCESSED_MARKER_FILE, processed_files)
        
        logging.info(f"Marked {filepath} as processed")


def unmark_file_processed(filepath: str):
    """Remove a file from the processed marker (e.g., when deleted)"""
    abs_path = os.path.abspath(filepath)
    
    with _processed_lock:
        processed_files = _load_marker_set(PROCESSED_MARKER_FILE)
        if abs_path in processed_files:
            processed_files.discard(abs_path)
            _save_marker_set(PROCESSED_MARKER_FILE, processed_files)


# Duplicate files marker functions
def is_file_duplicate(filepath: str) -> bool:
    """Check if a file is marked as a duplicate"""
    abs_path = os.path.abspath(filepath)
    with _duplicate_lock:
        duplicate_files = _load_marker_set(DUPLICATE_MARKER_FILE)
        return abs_path in duplicate_files


def mark_file_duplicate(filepath: str):
    """Mark a file as a duplicate"""
    abs_path = os.path.abspath(filepath)
    
    with _duplicate_lock:
        duplicate_files = _load_marker_set(DUPLICATE_MARKER_FILE)
        duplicate_files.add(abs_path)
        _save_marker_set(DUPLICATE_MARKER_FILE, duplicate_files)
        
        logging.info(f"Marked {filepath} as duplicate")


def unmark_file_duplicate(filepath: str):
    """Remove a file from the duplicate marker"""
    abs_path = os.path.abspath(filepath)
    
    with _duplicate_lock:
        duplicate_files = _load_marker_set(DUPLICATE_MARKER_FILE)
        if abs_path in duplicate_files:
            duplicate_files.discard(abs_path)
            _save_marker_set(DUPLICATE_MARKER_FILE, duplicate_files)


# Web modified files marker functions
def is_file_web_modified(filepath: str) -> bool:
    """Check if a file was recently modified by the web interface"""
    abs_path = os.path.abspath(filepath)
    with _web_modified_lock:
        web_modified_files = _load_marker_set(WEB_MODIFIED_MARKER_FILE)
        return abs_path in web_modified_files


def mark_file_web_modified(filepath: str):
    """Mark a file as modified by the web interface"""
    abs_path = os.path.abspath(filepath)
    
    with _web_modified_lock:
        web_modified_files = _load_marker_set(WEB_MODIFIED_MARKER_FILE)
        web_modified_files.add(abs_path)
        _save_marker_set(WEB_MODIFIED_MARKER_FILE, web_modified_files)


def clear_file_web_modified(filepath: str):
    """Clear the web modified marker for a file (consumed by watcher)"""
    abs_path = os.path.abspath(filepath)
    
    with _web_modified_lock:
        web_modified_files = _load_marker_set(WEB_MODIFIED_MARKER_FILE)
        if abs_path in web_modified_files:
            web_modified_files.discard(abs_path)
            _save_marker_set(WEB_MODIFIED_MARKER_FILE, web_modified_files)
            logging.info(f"Cleared web modified marker for {filepath}")
            return True
    return False


def cleanup_web_modified_markers(max_files: int = 100):
    """Clean up old web modified markers, keeping only the most recent ones"""
    with _web_modified_lock:
        web_modified_files = _load_marker_set(WEB_MODIFIED_MARKER_FILE)
        if len(web_modified_files) > max_files:
            # Keep the last max_files entries
            files_list = sorted(list(web_modified_files))
            to_keep = set(files_list[-max_files:])
            _save_marker_set(WEB_MODIFIED_MARKER_FILE, to_keep)
            logging.info(f"Cleaned up web modified markers, keeping {len(to_keep)} of {len(web_modified_files)}")
