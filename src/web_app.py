import os
import sys
import logging
from logging.handlers import RotatingFileHandler
import json
import fcntl
from flask import Flask, render_template, jsonify, request, send_from_directory
from comicapi.comicarchive import ComicArchive
import glob
import threading
import time
from config import get_filename_format, set_filename_format, DEFAULT_FILENAME_FORMAT, get_watcher_enabled, set_watcher_enabled, get_log_max_bytes, set_log_max_bytes, get_max_workers, get_issue_number_padding, set_issue_number_padding, DEFAULT_ISSUE_NUMBER_PADDING
from version import __version__
from markers import (
    is_file_processed, mark_file_processed, unmark_file_processed,
    is_file_duplicate, mark_file_duplicate, unmark_file_duplicate,
    is_file_web_modified, mark_file_web_modified, clear_file_web_modified,
    cleanup_web_modified_markers, get_all_marker_data
)
from job_manager import get_job_manager, JobResult
from preferences_store import (
    get_preference, set_preference, get_all_preferences,
    get_active_job, set_active_job, clear_active_job
)

CONFIG_DIR = '/Config'
LOG_DIR = os.path.join(CONFIG_DIR, 'Log')

# Ensure log directory exists
os.makedirs(LOG_DIR, exist_ok=True)

# Set up logging with rotation
# Initialize basic logging first to avoid issues with get_log_max_bytes() logging errors
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s [WEBPAGE] %(levelname)s %(message)s',
    handlers=[
        logging.StreamHandler(sys.stdout)
    ]
)

# Explicitly set root logger level to INFO (in case it was already configured by imports)
logging.getLogger().setLevel(logging.INFO)

# Now safely get log max bytes (which may log warnings)
log_max_bytes = get_log_max_bytes()
log_handler = RotatingFileHandler(
    os.path.join(LOG_DIR, "ComicMaintainer.log"),
    maxBytes=log_max_bytes,
    backupCount=3
)
log_handler.setLevel(logging.INFO)
log_handler.setFormatter(logging.Formatter('%(asctime)s [WEBPAGE] %(levelname)s %(message)s'))

# Add the file handler to the root logger
logging.getLogger().addHandler(log_handler)

app = Flask(__name__)

WATCHED_DIR = os.environ.get('WATCHED_DIR')
CACHE_UPDATE_MARKER = '.cache_update'
CACHE_CHANGES_FILE = '.cache_changes'
CACHE_REBUILD_LOCK = '.cache_rebuild_lock'

# Cache for file list to improve performance on large libraries
file_list_cache = {
    'files': None,
    'timestamp': 0,
    'watcher_update_time': 0  # Track last watcher update time
}
cache_lock = threading.Lock()

# Cache for enriched file list (files with metadata) to speed up filtering
enriched_file_cache = {
    'files': None,  # List of file dicts with metadata
    'timestamp': 0,
    'file_list_hash': None,  # Hash of raw file list to detect changes
    'rebuild_in_progress': False,  # Track if async rebuild is running
    'rebuild_thread': None,  # Reference to rebuild thread
    'watcher_update_time': 0  # Track last watcher update time for invalidation
}
enriched_file_cache_lock = threading.Lock()

# Cache for filtered and sorted results to speed up filter switching
filtered_results_cache = {
    # Key: (filter_mode, search_query, sort_mode, file_list_hash)
    # Value: {'filtered_files': [...], 'timestamp': ...}
}
filtered_results_cache_lock = threading.Lock()
MAX_FILTERED_CACHE_SIZE = 20  # Keep up to 20 different filter combinations

def try_acquire_cache_rebuild_lock(timeout=0.1):
    """Try to acquire a file-based lock for cache rebuilding across processes
    
    Args:
        timeout: Maximum time to wait for lock in seconds (default: 0.1)
        
    Returns:
        File handle if lock acquired, None if lock could not be acquired
    """
    # Ensure config directory exists
    os.makedirs(CONFIG_DIR, exist_ok=True)
    
    lock_file_path = os.path.join(CONFIG_DIR, CACHE_REBUILD_LOCK)
    
    try:
        # Open lock file (create if doesn't exist)
        lock_fd = open(lock_file_path, 'w')
        
        # Try to acquire lock with timeout
        start_time = time.time()
        while time.time() - start_time < timeout:
            try:
                # Try non-blocking lock
                fcntl.flock(lock_fd.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                # Lock acquired successfully
                logging.debug("Cache rebuild lock acquired")
                return lock_fd
            except IOError:
                # Lock is held by another process, wait a bit
                time.sleep(0.01)
        
        # Timeout - could not acquire lock
        lock_fd.close()
        logging.debug("Cache rebuild lock timeout - another worker is rebuilding")
        return None
    except Exception as e:
        logging.error(f"Error acquiring cache rebuild lock: {e}")
        return None

def release_cache_rebuild_lock(lock_fd):
    """Release the cache rebuild lock
    
    Args:
        lock_fd: File handle returned by try_acquire_cache_rebuild_lock
    """
    if lock_fd:
        try:
            fcntl.flock(lock_fd.fileno(), fcntl.LOCK_UN)
            lock_fd.close()
            logging.debug("Cache rebuild lock released")
        except Exception as e:
            logging.error(f"Error releasing cache rebuild lock: {e}")

# Wrapper functions for marker operations with cache invalidation
def mark_file_processed_wrapper(filepath, original_filepath=None):
    """Mark a file as processed and invalidate relevant caches"""
    mark_file_processed(filepath, original_filepath=original_filepath)
    
    # Invalidate enriched file cache
    with enriched_file_cache_lock:
        enriched_file_cache['files'] = None
        enriched_file_cache['file_list_hash'] = None
        enriched_file_cache['watcher_update_time'] = 0  # Reset to force rebuild
    
    # Invalidate filtered results cache (since processed status changed)
    with filtered_results_cache_lock:
        filtered_results_cache.clear()

def mark_file_duplicate_wrapper(filepath):
    """Mark a file as duplicate and invalidate relevant caches"""
    mark_file_duplicate(filepath)
    
    # Invalidate enriched file cache
    with enriched_file_cache_lock:
        enriched_file_cache['files'] = None
        enriched_file_cache['file_list_hash'] = None
        enriched_file_cache['watcher_update_time'] = 0  # Reset to force rebuild
    
    # Invalidate filtered results cache (since duplicate status changed)
    with filtered_results_cache_lock:
        filtered_results_cache.clear()

def cleanup_web_markers_thread():
    """Periodically clean up old web modified markers"""
    while True:
        time.sleep(300)  # Run every 5 minutes
        cleanup_web_modified_markers(max_files=100)

# Start cleanup thread
cleanup_thread = threading.Thread(target=cleanup_web_markers_thread, daemon=True)
cleanup_thread.start()

def get_watcher_update_time():
    """Get the last time the watcher updated files"""
    # Ensure config directory exists
    os.makedirs(CONFIG_DIR, exist_ok=True)
    
    marker_path = os.path.join(CONFIG_DIR, CACHE_UPDATE_MARKER)
    if os.path.exists(marker_path):
        try:
            with open(marker_path, 'r') as f:
                return float(f.read().strip())
        except:
            return 0
    return 0

def update_watcher_timestamp():
    """Update the watcher cache invalidation timestamp"""
    # Ensure config directory exists
    os.makedirs(CONFIG_DIR, exist_ok=True)
    
    marker_path = os.path.join(CONFIG_DIR, CACHE_UPDATE_MARKER)
    try:
        with open(marker_path, 'w') as f:
            f.write(str(time.time()))
    except Exception as e:
        logging.error(f"Error updating watcher timestamp: {e}")

def record_cache_change(change_type, old_path=None, new_path=None):
    """Record a file change for incremental cache updates
    
    Args:
        change_type: 'add', 'remove', or 'rename'
        old_path: Original file path (for 'remove' and 'rename')
        new_path: New file path (for 'add' and 'rename')
    """
    # Ensure config directory exists
    os.makedirs(CONFIG_DIR, exist_ok=True)
    
    changes_file = os.path.join(CONFIG_DIR, CACHE_CHANGES_FILE)
    
    try:
        change_entry = {
            'type': change_type,
            'old_path': old_path,
            'new_path': new_path,
            'timestamp': time.time()
        }
        
        with cache_lock:
            # Append the change to the file
            with open(changes_file, 'a') as f:
                f.write(json.dumps(change_entry) + '\n')
    except Exception as e:
        logging.error(f"Error recording cache change: {e}")

def apply_cache_changes():
    """Apply pending cache changes incrementally instead of invalidating entire cache
    
    Returns:
        True if changes were applied, False if cache needs full rebuild
    """
    if file_list_cache['files'] is None:
        return False
    
    changes_file = os.path.join(CONFIG_DIR, CACHE_CHANGES_FILE)
    
    if not os.path.exists(changes_file):
        return True  # No changes to apply
    
    try:
        with cache_lock:
            # Read all pending changes
            with open(changes_file, 'r') as f:
                lines = f.readlines()
            
            if not lines:
                return True
            
            # Get current cache
            cached_files = file_list_cache['files']
            if cached_files is None:
                return False
            
            # Convert to set for faster operations
            cached_set = set(cached_files)
            
            # Apply each change
            for line in lines:
                try:
                    change = json.loads(line.strip())
                    change_type = change.get('type')
                    old_path = change.get('old_path')
                    new_path = change.get('new_path')
                    
                    if change_type == 'add' and new_path:
                        # Add new file if it exists and not already in cache
                        if os.path.exists(new_path) and new_path not in cached_set:
                            cached_set.add(new_path)
                            logging.info(f"Cache: Added {new_path}")
                    
                    elif change_type == 'remove' and old_path:
                        # Remove file from cache
                        if old_path in cached_set:
                            cached_set.discard(old_path)
                            logging.info(f"Cache: Removed {old_path}")
                    
                    elif change_type == 'rename' and old_path and new_path:
                        # Remove old path and add new path
                        if old_path in cached_set:
                            cached_set.discard(old_path)
                        if os.path.exists(new_path):
                            cached_set.add(new_path)
                            logging.info(f"Cache: Renamed {old_path} -> {new_path}")
                
                except json.JSONDecodeError as e:
                    logging.warning(f"Invalid cache change entry: {line.strip()}")
                except Exception as e:
                    logging.error(f"Error applying cache change: {e}")
            
            # Update cache with modified list (sorted)
            file_list_cache['files'] = sorted(list(cached_set))
            file_list_cache['timestamp'] = time.time()
            
            # Clear the changes file after applying
            try:
                os.remove(changes_file)
            except:
                pass
            
            logging.info(f"Applied {len(lines)} cache changes incrementally")
            return True
    
    except Exception as e:
        logging.error(f"Error applying cache changes: {e}")
        return False

def get_comic_files(use_cache=True):
    """Get all comic files in the watched directory with optional caching"""
    if not WATCHED_DIR:
        return []
    
    # Check cache if enabled
    if use_cache:
        with cache_lock:
            watcher_update_time = get_watcher_update_time()
            
            # Check if watcher has changes since cache was created
            if watcher_update_time > file_list_cache['watcher_update_time']:
                # Try to apply incremental changes first
                if file_list_cache['files'] is not None:
                    if apply_cache_changes():
                        # Successfully applied changes incrementally
                        file_list_cache['watcher_update_time'] = watcher_update_time
                        return file_list_cache['files']
                    else:
                        # Failed to apply changes, need full rebuild
                        logging.info("Cache: Incremental update failed, rebuilding")
                
                # If no cache or incremental update failed, will rebuild below
                file_list_cache['files'] = None
                file_list_cache['watcher_update_time'] = watcher_update_time
            
            # Return cached files if valid (no time-based expiration)
            if file_list_cache['files'] is not None:
                return file_list_cache['files']
    
    # Build file list
    logging.info("Cache: Building full file list")
    files = []
    for ext in ['*.cbz', '*.cbr', '*.CBZ', '*.CBR']:
        files.extend(glob.glob(os.path.join(WATCHED_DIR, '**', ext), recursive=True))
    
    sorted_files = sorted(files)
    
    # Update cache
    if use_cache:
        with cache_lock:
            file_list_cache['files'] = sorted_files
            file_list_cache['timestamp'] = time.time()
            file_list_cache['watcher_update_time'] = get_watcher_update_time()
    
    return sorted_files

def clear_file_cache():
    """Clear the file list cache"""
    with cache_lock:
        file_list_cache['files'] = None
        file_list_cache['timestamp'] = 0
    
    # Also clear filtered results cache
    with filtered_results_cache_lock:
        filtered_results_cache.clear()

def handle_file_rename_in_cache(original_path, final_path):
    """Handle file rename in cache - record change if file was actually renamed"""
    if original_path != final_path:
        record_cache_change('rename', old_path=original_path, new_path=final_path)
        update_watcher_timestamp()
        
        # Clear filtered results cache since file list changed
        with filtered_results_cache_lock:
            filtered_results_cache.clear()

def get_credits_by_role(credits_list, role_synonyms):
    """Extract credits for a specific role from credits list"""
    if not credits_list:
        return ''
    
    role_synonyms_lower = [r.lower() for r in role_synonyms]
    matching_credits = [
        credit.person for credit in credits_list 
        if credit.role.lower() in role_synonyms_lower
    ]
    return ', '.join(matching_credits) if matching_credits else ''

def get_file_tags(filepath):
    """Get tags from a comic file"""
    try:
        ca = ComicArchive(filepath)
        tags = ca.read_tags('cr')
        
        # Handle both old and new API structures
        # New API uses credits list, old API has direct attributes
        if hasattr(tags, 'credits'):
            # New API (develop branch)
            writer = get_credits_by_role(tags.credits, tags.writer_synonyms)
            penciller = get_credits_by_role(tags.credits, tags.penciller_synonyms)
            inker = get_credits_by_role(tags.credits, tags.inker_synonyms)
            colorist = get_credits_by_role(tags.credits, tags.colorist_synonyms)
            letterer = get_credits_by_role(tags.credits, tags.letterer_synonyms)
            cover_artist = get_credits_by_role(tags.credits, tags.cover_synonyms)
            editor = get_credits_by_role(tags.credits, tags.editor_synonyms)
            
            # New API uses 'description' instead of 'summary'
            summary = tags.description or ''
            
            # New API uses sets for tags and genres
            tags_str = ', '.join(sorted(tags.tags)) if tags.tags else ''
            genre = ', '.join(sorted(tags.genres)) if tags.genres else ''
            
            # New API uses web_links list
            web = ', '.join(str(link) for link in tags.web_links) if tags.web_links else ''
        else:
            # Old API (master branch) - direct attributes
            writer = tags.writer or ''
            penciller = tags.penciller or ''
            inker = tags.inker or ''
            colorist = tags.colorist or ''
            letterer = tags.letterer or ''
            cover_artist = tags.cover_artist or ''
            editor = tags.editor or ''
            summary = tags.summary or ''
            tags_str = tags.tags or ''
            genre = tags.genre or ''
            web = tags.web or ''
        
        # Convert tags to dictionary
        tag_dict = {
            'title': tags.title or '',
            'series': tags.series or '',
            'issue': tags.issue or '',
            'volume': str(tags.volume) if tags.volume else '',
            'publisher': tags.publisher or '',
            'year': str(tags.year) if tags.year else '',
            'month': str(tags.month) if tags.month else '',
            'writer': writer,
            'penciller': penciller,
            'inker': inker,
            'colorist': colorist,
            'letterer': letterer,
            'cover_artist': cover_artist,
            'editor': editor,
            'summary': summary,
            'notes': tags.notes or '',
            'genre': genre,
            'tags': tags_str,
            'web': web,
            'page_count': tags.page_count or 0,
        }
        return tag_dict
    except Exception as e:
        logging.error(f"Error reading tags from {filepath}: {e}")
        return None

def update_credits_by_role(credits_list, role_synonyms, value_str):
    """Update credits for a specific role in credits list"""
    if not value_str or not value_str.strip():
        # Remove all credits with this role
        role_synonyms_lower = [r.lower() for r in role_synonyms]
        return [c for c in credits_list if c.role.lower() not in role_synonyms_lower]
    
    # Parse comma-separated names
    names = [name.strip() for name in value_str.split(',') if name.strip()]
    
    # Remove existing credits with this role
    role_synonyms_lower = [r.lower() for r in role_synonyms]
    filtered_credits = [c for c in credits_list if c.role.lower() not in role_synonyms_lower]
    
    # Add new credits with primary role name
    from comicapi.genericmetadata import Credit
    primary_role = role_synonyms[0]
    for i, name in enumerate(names):
        filtered_credits.append(Credit(person=name, role=primary_role, primary=(i == 0)))
    
    return filtered_credits

def update_file_tags(filepath, tag_updates):
    """Update tags in a comic file"""
    try:
        ca = ComicArchive(filepath)
        tags = ca.read_tags('cr')
        
        # Handle both old and new API structures
        if hasattr(tags, 'credits'):
            # New API (develop branch)
            for key, value in tag_updates.items():
                if key == 'writer':
                    tags.credits = update_credits_by_role(tags.credits, tags.writer_synonyms, value)
                elif key == 'penciller':
                    tags.credits = update_credits_by_role(tags.credits, tags.penciller_synonyms, value)
                elif key == 'inker':
                    tags.credits = update_credits_by_role(tags.credits, tags.inker_synonyms, value)
                elif key == 'colorist':
                    tags.credits = update_credits_by_role(tags.credits, tags.colorist_synonyms, value)
                elif key == 'letterer':
                    tags.credits = update_credits_by_role(tags.credits, tags.letterer_synonyms, value)
                elif key == 'cover_artist':
                    tags.credits = update_credits_by_role(tags.credits, tags.cover_synonyms, value)
                elif key == 'editor':
                    tags.credits = update_credits_by_role(tags.credits, tags.editor_synonyms, value)
                elif key == 'summary':
                    # New API uses 'description' instead of 'summary'
                    tags.description = value
                elif key == 'genre':
                    # New API uses set for genres
                    if value and value.strip():
                        tags.genres = set(g.strip() for g in value.split(',') if g.strip())
                    else:
                        tags.genres = set()
                elif key == 'tags':
                    # New API uses set for tags
                    if value and value.strip():
                        tags.tags = set(t.strip() for t in value.split(',') if t.strip())
                    else:
                        tags.tags = set()
                elif key == 'web':
                    # New API uses web_links list
                    from comicapi._url import parse_url
                    if value and value.strip():
                        tags.web_links = [parse_url(url.strip()) for url in value.split(',') if url.strip()]
                    else:
                        tags.web_links = []
                elif key in ('volume', 'year', 'month'):
                    # Convert string to int for these fields
                    if value and value.strip():
                        try:
                            setattr(tags, key, int(value))
                        except ValueError:
                            logging.warning(f"Invalid integer value for {key}: {value}")
                    else:
                        setattr(tags, key, None)
                elif hasattr(tags, key):
                    setattr(tags, key, value)
        else:
            # Old API (master branch) - direct attributes
            for key, value in tag_updates.items():
                if hasattr(tags, key):
                    setattr(tags, key, value)
        
        # Mark as web modified before writing
        mark_file_web_modified(filepath)
        
        # Write tags
        ca.write_tags(tags, 'cr')
        logging.info(f"Updated tags for {filepath}")
        
        return True
    except Exception as e:
        logging.error(f"Error updating tags for {filepath}: {e}")
        return False

@app.route('/')
def index():
    """Serve the main page"""
    return render_template('index.html')

def preload_metadata_for_directories(files):
    """No longer needed - markers are now centralized in /Config"""
    # This function is kept for backward compatibility but does nothing
    # since markers are now stored centrally, not per-directory
    pass

def rebuild_enriched_cache_async(files, file_list_hash):
    """Background thread function to rebuild enriched cache asynchronously
    
    Args:
        files: List of file paths
        file_list_hash: Hash of the file list to detect if it changed
    """
    lock_fd = None
    try:
        # Try to acquire file-based lock for cache rebuild
        lock_fd = try_acquire_cache_rebuild_lock(timeout=0.5)
        
        if lock_fd is None:
            logging.info("Async cache rebuild: Another worker is already rebuilding, aborting")
            with enriched_file_cache_lock:
                enriched_file_cache['rebuild_in_progress'] = False
                enriched_file_cache['rebuild_thread'] = None
            return
        
        # Check if cache was already rebuilt by another thread
        with enriched_file_cache_lock:
            if (enriched_file_cache['files'] is not None and 
                enriched_file_cache['file_list_hash'] == file_list_hash):
                logging.info("Async cache rebuild: Cache already updated, aborting")
                enriched_file_cache['rebuild_in_progress'] = False
                enriched_file_cache['rebuild_thread'] = None
                return
        
        logging.info("Async cache rebuild: Starting background rebuild")
        
        # Preload metadata for all directories (batch operation)
        preload_metadata_for_directories(files)
        
        # Get all marker data in one batch query (much faster than individual queries)
        marker_data = get_all_marker_data()
        processed_files = marker_data.get('processed', set())
        duplicate_files = marker_data.get('duplicate', set())
        
        # Build file list with metadata
        all_files = []
        for f in files:
            abs_path = os.path.abspath(f)
            rel_path = os.path.relpath(f, WATCHED_DIR) if WATCHED_DIR else f
            all_files.append({
                'path': f,
                'name': os.path.basename(f),
                'relative_path': rel_path,
                'size': os.path.getsize(f),
                'modified': os.path.getmtime(f),
                'processed': abs_path in processed_files,
                'duplicate': abs_path in duplicate_files
            })
        
        # Update cache
        with enriched_file_cache_lock:
            enriched_file_cache['files'] = all_files
            enriched_file_cache['timestamp'] = time.time()
            enriched_file_cache['file_list_hash'] = file_list_hash
            enriched_file_cache['watcher_update_time'] = get_watcher_update_time()
            enriched_file_cache['rebuild_in_progress'] = False
            enriched_file_cache['rebuild_thread'] = None
        
        logging.info(f"Async cache rebuild: Complete ({len(all_files)} files)")
    except Exception as e:
        logging.error(f"Async cache rebuild: Error - {e}")
        with enriched_file_cache_lock:
            enriched_file_cache['rebuild_in_progress'] = False
            enriched_file_cache['rebuild_thread'] = None
    finally:
        # Always release the lock if we acquired it
        if lock_fd is not None:
            release_cache_rebuild_lock(lock_fd)

def get_enriched_file_list(files, force_rebuild=False):
    """Get file list enriched with metadata, using cache when possible
    
    Args:
        files: List of file paths
        force_rebuild: Force rebuilding the cache even if valid
        
    Returns:
        List of file dictionaries with metadata
    """
    # Create a simple hash of the file list to detect changes
    file_list_hash = hash(tuple(files))
    
    # Check if watcher has updated files since cache was built
    watcher_update_time = get_watcher_update_time()
    
    # Check if cache is valid (without holding lock)
    with enriched_file_cache_lock:
        # Invalidate cache if watcher has processed files since cache was built
        if (enriched_file_cache['files'] is not None and 
            watcher_update_time > enriched_file_cache['watcher_update_time']):
            logging.info(f"Invalidating enriched cache: watcher has processed files (watcher time: {watcher_update_time}, cache time: {enriched_file_cache['watcher_update_time']})")
            enriched_file_cache['files'] = None
            enriched_file_cache['file_list_hash'] = None
            
            # Also clear filtered results cache since enriched data changed
            with filtered_results_cache_lock:
                filtered_results_cache.clear()
        
        if (not force_rebuild and 
            enriched_file_cache['files'] is not None and 
            enriched_file_cache['file_list_hash'] == file_list_hash):
            
            logging.debug("Using enriched file cache")
            return enriched_file_cache['files']
        
        # Check if we have stale cache that can be returned
        stale_cache = enriched_file_cache['files']
        rebuild_in_progress = enriched_file_cache['rebuild_in_progress']
    
    # If rebuild is already in progress, return stale cache if available
    if rebuild_in_progress:
        if stale_cache is not None:
            logging.info("Async cache rebuild in progress, returning stale cache")
            return stale_cache
        else:
            logging.info("Async cache rebuild in progress, but no stale cache available")
            # Fall through to trigger rebuild if no stale cache
    
    # If force_rebuild or no rebuild in progress, trigger async rebuild
    with enriched_file_cache_lock:
        # Double-check if cache was updated while we were checking
        if (not force_rebuild and 
            enriched_file_cache['files'] is not None and 
            enriched_file_cache['file_list_hash'] == file_list_hash):
            logging.debug("Cache was updated by another thread")
            return enriched_file_cache['files']
        
        # Check if we should start async rebuild
        if not enriched_file_cache['rebuild_in_progress']:
            logging.info("Triggering async cache rebuild")
            enriched_file_cache['rebuild_in_progress'] = True
            
            # Start background rebuild thread
            rebuild_thread = threading.Thread(
                target=rebuild_enriched_cache_async,
                args=(files, file_list_hash),
                daemon=True
            )
            enriched_file_cache['rebuild_thread'] = rebuild_thread
            rebuild_thread.start()
        
        # Return stale cache if available, otherwise return empty list
        if stale_cache is not None:
            logging.info("Returning stale cache while async rebuild runs")
            return stale_cache
        else:
            # No stale cache - need to build synchronously for first request
            logging.info("No stale cache available, building synchronously for first request")
    
    # First-time cache build or another worker is building
    # Return empty list immediately to avoid blocking the worker
    # The async rebuild will populate the cache for subsequent requests
    logging.info("No cache available, async rebuild in progress - returning empty list for now")
    logging.info("Cache will be available on next request after rebuild completes")
    return []

def get_filtered_sorted_files(all_files, filter_mode, search_query, sort_mode, file_list_hash):
    """Get filtered and sorted files with caching
    
    Args:
        all_files: List of all enriched files
        filter_mode: Filter mode ('all', 'marked', 'unmarked', 'duplicates')
        search_query: Search query string
        sort_mode: Sort mode ('name', 'date', 'size')
        file_list_hash: Hash of the file list to detect changes
        
    Returns:
        List of filtered and sorted files
    """
    # Create cache key
    cache_key = (filter_mode, search_query, sort_mode, file_list_hash)
    
    # Check cache
    with filtered_results_cache_lock:
        if cache_key in filtered_results_cache:
            logging.debug(f"Using filtered results cache for filter={filter_mode}, search='{search_query}', sort={sort_mode}")
            return filtered_results_cache[cache_key]['filtered_files']
    
    # Cache miss - compute filtered and sorted results
    logging.info(f"Computing filtered results for filter={filter_mode}, search='{search_query}', sort={sort_mode}")
    
    # Apply filters
    filtered_files = all_files
    
    # Apply processing status filter
    if filter_mode == 'marked':
        filtered_files = [f for f in filtered_files if f['processed']]
    elif filter_mode == 'unmarked':
        filtered_files = [f for f in filtered_files if not f['processed']]
    elif filter_mode == 'duplicates':
        filtered_files = [f for f in filtered_files if f['duplicate']]
    
    # Apply search filter
    if search_query:
        query_lower = search_query.lower()
        filtered_files = [
            f for f in filtered_files
            if query_lower in f['name'].lower() or query_lower in f['relative_path'].lower()
        ]
    
    # Apply sorting
    if sort_mode == 'date':
        filtered_files = sorted(filtered_files, key=lambda f: f['modified'], reverse=True)
    elif sort_mode == 'size':
        filtered_files = sorted(filtered_files, key=lambda f: f['size'], reverse=True)
    else:  # Default to 'name'
        filtered_files = sorted(filtered_files, key=lambda f: f['name'].lower())
    
    # Store in cache (with LRU eviction if needed)
    with filtered_results_cache_lock:
        # Evict oldest cache entries if cache is full
        if len(filtered_results_cache) >= MAX_FILTERED_CACHE_SIZE:
            # Remove entries with oldest timestamp
            oldest_key = min(filtered_results_cache.keys(), 
                           key=lambda k: filtered_results_cache[k]['timestamp'])
            del filtered_results_cache[oldest_key]
            logging.debug(f"Evicted oldest filtered results cache entry")
        
        # Add new entry
        filtered_results_cache[cache_key] = {
            'filtered_files': filtered_files,
            'timestamp': time.time()
        }
    
    return filtered_files


@app.route('/api/files')
def list_files():
    """API endpoint to list all comic files with pagination"""
    # Get pagination parameters
    page = request.args.get('page', 1, type=int)
    per_page = request.args.get('per_page', 100, type=int)
    refresh = request.args.get('refresh', 'false').lower() == 'true'
    
    # Get filter parameters
    search_query = request.args.get('search', '', type=str).strip()
    filter_mode = request.args.get('filter', 'all', type=str)  # 'all', 'marked', 'unmarked', 'duplicates'
    sort_mode = request.args.get('sort', 'name', type=str)  # 'name', 'date', 'size'
    
    # Get files with optional cache refresh
    files = get_comic_files(use_cache=not refresh)
    
    # Get enriched file list with metadata (cached)
    all_files = get_enriched_file_list(files, force_rebuild=refresh)
    
    # Check if cache rebuild is in progress
    with enriched_file_cache_lock:
        cache_rebuilding = enriched_file_cache['rebuild_in_progress']
    
    # If cache is empty and rebuild is in progress, return minimal response
    # This prevents worker timeout while cache is being built
    if not all_files and cache_rebuilding:
        logging.debug("Cache is rebuilding, returning empty response")
        return jsonify({
            'files': [],
            'page': 1,
            'per_page': per_page,
            'total_files': 0,
            'total_pages': 1,
            'unmarked_count': 0,
            'cache_rebuilding': True
        })
    
    # Calculate unmarked count from all files (before filtering)
    unmarked_count = sum(1 for f in all_files if not f['processed'])
    
    # Create a hash of the file list to detect changes
    file_list_hash = hash(tuple(f['path'] for f in all_files))
    
    # Get filtered and sorted files (with caching)
    filtered_files = get_filtered_sorted_files(all_files, filter_mode, search_query, sort_mode, file_list_hash)
    
    total_filtered = len(filtered_files)
    
    # Handle "all files" request (per_page = -1 or 0)
    if per_page <= 0:
        # Return all files in a single page
        paginated_files = filtered_files
        total_pages = 1
        page = 1
    else:
        # Limit per_page to reasonable values
        per_page = min(max(per_page, 10), 500)
        
        # Calculate pagination
        total_pages = (total_filtered + per_page - 1) // per_page if total_filtered > 0 else 1
        page = max(1, min(page, total_pages))
        
        start_idx = (page - 1) * per_page
        end_idx = start_idx + per_page
        
        paginated_files = filtered_files[start_idx:end_idx]
    
    return jsonify({
        'files': paginated_files,
        'page': page,
        'per_page': per_page,
        'total_files': total_filtered,
        'total_pages': total_pages,
        'unmarked_count': unmarked_count,
        'cache_rebuilding': cache_rebuilding
    })

@app.route('/api/file/<path:filepath>/tags')
def get_tags(filepath):
    """API endpoint to get tags for a specific file"""
    full_path = os.path.join(WATCHED_DIR, filepath) if WATCHED_DIR else filepath
    
    if not os.path.exists(full_path):
        return jsonify({'error': 'File not found'}), 404
    
    tags = get_file_tags(full_path)
    if tags is None:
        return jsonify({'error': 'Failed to read tags'}), 500
    
    return jsonify(tags)

@app.route('/api/file/<path:filepath>/tags', methods=['POST'])
def update_tags(filepath):
    """API endpoint to update tags for a specific file"""
    full_path = os.path.join(WATCHED_DIR, filepath) if WATCHED_DIR else filepath
    
    if not os.path.exists(full_path):
        return jsonify({'error': 'File not found'}), 404
    
    tag_updates = request.json
    if not tag_updates:
        return jsonify({'error': 'No tag updates provided'}), 400
    
    success = update_file_tags(full_path, tag_updates)
    if success:
        return jsonify({'success': True})
    else:
        return jsonify({'error': 'Failed to update tags'}), 500

@app.route('/api/files/tags', methods=['POST'])
def batch_update_tags():
    """API endpoint to update tags for multiple files with streaming progress"""
    data = request.json
    files = data.get('files', [])
    tag_updates = data.get('tags', {})
    stream = request.args.get('stream', 'false').lower() == 'true'
    
    if not files or not tag_updates:
        return jsonify({'error': 'Files and tags are required'}), 400
    
    if not stream:
        # Non-streaming mode (backward compatible)
        results = []
        for filepath in files:
            full_path = os.path.join(WATCHED_DIR, filepath) if WATCHED_DIR else filepath
            if os.path.exists(full_path):
                success = update_file_tags(full_path, tag_updates)
                results.append({
                    'file': filepath,
                    'success': success
                })
            else:
                results.append({
                    'file': filepath,
                    'success': False,
                    'error': 'File not found'
                })
        
        return jsonify({'results': results})
    
    # Streaming mode - send progress updates
    def generate():
        import json
        results = []
        for i, filepath in enumerate(files):
            full_path = os.path.join(WATCHED_DIR, filepath) if WATCHED_DIR else filepath
            result = {'file': filepath}
            
            if os.path.exists(full_path):
                success = update_file_tags(full_path, tag_updates)
                result['success'] = success
                if not success:
                    result['error'] = 'Failed to update tags'
            else:
                result['success'] = False
                result['error'] = 'File not found'
            
            results.append(result)
            
            # Send progress update
            progress = {
                'current': i + 1,
                'total': len(files),
                'file': os.path.basename(filepath),
                'success': result['success'],
                'error': result.get('error')
            }
            yield f"data: {json.dumps(progress)}\n\n"
        
        # Send final results
        yield f"data: {json.dumps({'done': True, 'results': results})}\n\n"
    
    return app.response_class(generate(), mimetype='text/event-stream')


@app.route('/api/process-all', methods=['POST'])
def process_all_files():
    """API endpoint to process all files in the watched directory with streaming progress"""
    from process_file import process_file
    
    stream = request.args.get('stream', 'false').lower() == 'true'
    files = get_comic_files()
    
    if not stream:
        # Non-streaming mode (backward compatible)
        results = []
        for filepath in files:
            try:
                mark_file_web_modified(filepath)
                final_filepath = process_file(filepath, fixtitle=True, fixseries=True, fixfilename=True)
                mark_file_processed_wrapper(final_filepath, original_filepath=filepath)
                handle_file_rename_in_cache(filepath, final_filepath)
                results.append({
                    'file': os.path.basename(final_filepath),
                    'success': True
                })
                logging.info(f"Processed file via web interface: {filepath} -> {final_filepath}")
            except Exception as e:
                results.append({
                    'file': os.path.basename(filepath),
                    'success': False,
                    'error': str(e)
                })
                logging.error(f"Error processing file {filepath}: {e}")
        
        return jsonify({'results': results})
    
    # Streaming mode - send progress updates
    def generate():
        import json
        results = []
        for i, filepath in enumerate(files):
            result = {'file': os.path.basename(filepath)}
            
            try:
                mark_file_web_modified(filepath)
                final_filepath = process_file(filepath, fixtitle=True, fixseries=True, fixfilename=True)
                mark_file_processed_wrapper(final_filepath, original_filepath=filepath)
                handle_file_rename_in_cache(filepath, final_filepath)
                result['success'] = True
                logging.info(f"Processed file via web interface: {filepath} -> {final_filepath}")
            except Exception as e:
                result['success'] = False
                result['error'] = str(e)
                logging.error(f"Error processing file {filepath}: {e}")
            
            results.append(result)
            
            # Send progress update
            progress = {
                'current': i + 1,
                'total': len(files),
                'file': result['file'],
                'success': result['success'],
                'error': result.get('error')
            }
            yield f"data: {json.dumps(progress)}\n\n"
        
        # Send final results
        yield f"data: {json.dumps({'done': True, 'results': results})}\n\n"
    
    return app.response_class(generate(), mimetype='text/event-stream')


@app.route('/api/rename-all', methods=['POST'])
def rename_all_files():
    """API endpoint to rename all files in the watched directory with streaming progress"""
    from process_file import process_file
    
    stream = request.args.get('stream', 'false').lower() == 'true'
    files = get_comic_files()
    
    if not stream:
        # Non-streaming mode (backward compatible)
        results = []
        for filepath in files:
            try:
                mark_file_web_modified(filepath)
                final_filepath = process_file(filepath, fixtitle=False, fixseries=False, fixfilename=True)
                mark_file_processed_wrapper(final_filepath)
                handle_file_rename_in_cache(filepath, final_filepath)
                results.append({
                    'file': os.path.basename(final_filepath),
                    'success': True
                })
                logging.info(f"Renamed file via web interface: {filepath} -> {final_filepath}")
            except Exception as e:
                results.append({
                    'file': os.path.basename(filepath),
                    'success': False,
                    'error': str(e)
                })
                logging.error(f"Error renaming file {filepath}: {e}")
        
        return jsonify({'results': results})
    
    # Streaming mode - send progress updates
    def generate():
        import json
        results = []
        for i, filepath in enumerate(files):
            result = {'file': os.path.basename(filepath)}
            
            try:
                mark_file_web_modified(filepath)
                final_filepath = process_file(filepath, fixtitle=False, fixseries=False, fixfilename=True)
                mark_file_processed_wrapper(final_filepath)
                handle_file_rename_in_cache(filepath, final_filepath)
                result['success'] = True
                logging.info(f"Renamed file via web interface: {filepath} -> {final_filepath}")
            except Exception as e:
                result['success'] = False
                result['error'] = str(e)
                logging.error(f"Error renaming file {filepath}: {e}")
            
            results.append(result)
            
            # Send progress update
            progress = {
                'current': i + 1,
                'total': len(files),
                'file': result['file'],
                'success': result['success'],
                'error': result.get('error')
            }
            yield f"data: {json.dumps(progress)}\n\n"
        
        # Send final results
        yield f"data: {json.dumps({'done': True, 'results': results})}\n\n"
    
    return app.response_class(generate(), mimetype='text/event-stream')


@app.route('/api/normalize-all', methods=['POST'])
def normalize_all_files():
    """API endpoint to normalize metadata for all files in the watched directory with streaming progress"""
    from process_file import process_file
    
    stream = request.args.get('stream', 'false').lower() == 'true'
    files = get_comic_files()
    
    if not stream:
        # Non-streaming mode (backward compatible)
        results = []
        for filepath in files:
            try:
                mark_file_web_modified(filepath)
                final_filepath = process_file(filepath, fixtitle=True, fixseries=True, fixfilename=False)
                mark_file_processed_wrapper(final_filepath)
                results.append({
                    'file': os.path.basename(final_filepath),
                    'success': True
                })
                logging.info(f"Normalized metadata for file via web interface: {filepath}")
            except Exception as e:
                results.append({
                    'file': os.path.basename(filepath),
                    'success': False,
                    'error': str(e)
                })
                logging.error(f"Error normalizing metadata for file {filepath}: {e}")
        
        return jsonify({'results': results})
    
    # Streaming mode - send progress updates
    def generate():
        import json
        results = []
        for i, filepath in enumerate(files):
            result = {'file': os.path.basename(filepath)}
            
            try:
                mark_file_web_modified(filepath)
                final_filepath = process_file(filepath, fixtitle=True, fixseries=True, fixfilename=False)
                mark_file_processed_wrapper(final_filepath)
                result['success'] = True
                logging.info(f"Normalized metadata for file via web interface: {filepath}")
            except Exception as e:
                result['success'] = False
                result['error'] = str(e)
                logging.error(f"Error normalizing metadata for file {filepath}: {e}")
            
            results.append(result)
            
            # Send progress update
            progress = {
                'current': i + 1,
                'total': len(files),
                'file': result['file'],
                'success': result['success'],
                'error': result.get('error')
            }
            yield f"data: {json.dumps(progress)}\n\n"
        
        # Send final results
        yield f"data: {json.dumps({'done': True, 'results': results})}\n\n"
    
    return app.response_class(generate(), mimetype='text/event-stream')


@app.route('/api/process-file/<path:filepath>', methods=['POST'])
def process_single_file(filepath):
    """API endpoint to process a single file"""
    from process_file import process_file
    
    full_path = os.path.join(WATCHED_DIR, filepath) if WATCHED_DIR else filepath
    
    if not os.path.exists(full_path):
        return jsonify({'error': 'File not found'}), 404
    
    try:
        # Mark as web modified to prevent watcher from processing
        mark_file_web_modified(full_path)
        
        # Process the file and get the final filepath (may be renamed)
        final_filepath = process_file(full_path, fixtitle=True, fixseries=True, fixfilename=True)
        
        # Mark as processed using the final filepath, cleanup old filename if renamed
        mark_file_processed_wrapper(final_filepath, original_filepath=full_path)
        
        # Update cache incrementally if file was renamed
        handle_file_rename_in_cache(full_path, final_filepath)
        
        logging.info(f"Processed file via web interface: {full_path} -> {final_filepath}")
        return jsonify({'success': True})
    except Exception as e:
        logging.error(f"Error processing file {full_path}: {e}")
        return jsonify({'error': str(e)}), 500

@app.route('/api/rename-file/<path:filepath>', methods=['POST'])
def rename_single_file(filepath):
    """API endpoint to rename a single file based on metadata"""
    from process_file import process_file
    
    full_path = os.path.join(WATCHED_DIR, filepath) if WATCHED_DIR else filepath
    
    if not os.path.exists(full_path):
        return jsonify({'error': 'File not found'}), 404
    
    try:
        # Mark as web modified to prevent watcher from processing
        mark_file_web_modified(full_path)
        
        # Only rename the file
        final_filepath = process_file(full_path, fixtitle=False, fixseries=False, fixfilename=True)
        
        # Mark as processed using the final filepath
        mark_file_processed_wrapper(final_filepath)
        
        # Update cache incrementally if file was renamed
        handle_file_rename_in_cache(full_path, final_filepath)
        
        logging.info(f"Renamed file via web interface: {full_path} -> {final_filepath}")
        return jsonify({'success': True})
    except Exception as e:
        logging.error(f"Error renaming file {full_path}: {e}")
        return jsonify({'error': str(e)}), 500

@app.route('/api/normalize-file/<path:filepath>', methods=['POST'])
def normalize_single_file(filepath):
    """API endpoint to normalize metadata for a single file"""
    from process_file import process_file
    
    full_path = os.path.join(WATCHED_DIR, filepath) if WATCHED_DIR else filepath
    
    if not os.path.exists(full_path):
        return jsonify({'error': 'File not found'}), 404
    
    try:
        # Mark as web modified to prevent watcher from processing
        mark_file_web_modified(full_path)
        
        # Only normalize metadata
        final_filepath = process_file(full_path, fixtitle=True, fixseries=True, fixfilename=False)
        
        # Mark as processed using the final filepath
        mark_file_processed_wrapper(final_filepath)
        
        logging.info(f"Normalized metadata for file via web interface: {full_path}")
        return jsonify({'success': True})
    except Exception as e:
        logging.error(f"Error normalizing metadata for file {full_path}: {e}")
        return jsonify({'error': str(e)}), 500

@app.route('/api/process-selected', methods=['POST'])
def process_selected_files():
    """API endpoint to process selected files with streaming progress"""
    from process_file import process_file
    
    data = request.json
    file_list = data.get('files', [])
    stream = request.args.get('stream', 'false').lower() == 'true'
    
    if not file_list:
        return jsonify({'error': 'No files specified'}), 400
    
    if not stream:
        # Non-streaming mode (backward compatible)
        results = []
        for filepath in file_list:
            full_path = os.path.join(WATCHED_DIR, filepath) if WATCHED_DIR else filepath
            
            if not os.path.exists(full_path):
                results.append({
                    'file': os.path.basename(filepath),
                    'success': False,
                    'error': 'File not found'
                })
                continue
            
            try:
                mark_file_web_modified(full_path)
                final_filepath = process_file(full_path, fixtitle=True, fixseries=True, fixfilename=True)
                mark_file_processed_wrapper(final_filepath, original_filepath=full_path)
                handle_file_rename_in_cache(full_path, final_filepath)
                results.append({
                    'file': os.path.basename(final_filepath),
                    'success': True
                })
                logging.info(f"Processed file via web interface: {full_path} -> {final_filepath}")
            except Exception as e:
                results.append({
                    'file': os.path.basename(filepath),
                    'success': False,
                    'error': str(e)
                })
                logging.error(f"Error processing file {full_path}: {e}")
        
        return jsonify({'results': results})
    
    # Streaming mode - send progress updates
    def generate():
        import json
        results = []
        for i, filepath in enumerate(file_list):
            full_path = os.path.join(WATCHED_DIR, filepath) if WATCHED_DIR else filepath
            result = {'file': os.path.basename(filepath)}
            
            if not os.path.exists(full_path):
                result['success'] = False
                result['error'] = 'File not found'
            else:
                try:
                    mark_file_web_modified(full_path)
                    final_filepath = process_file(full_path, fixtitle=True, fixseries=True, fixfilename=True)
                    mark_file_processed_wrapper(final_filepath, original_filepath=full_path)
                    handle_file_rename_in_cache(full_path, final_filepath)
                    result['success'] = True
                    logging.info(f"Processed file via web interface: {full_path} -> {final_filepath}")
                except Exception as e:
                    result['success'] = False
                    result['error'] = str(e)
                    logging.error(f"Error processing file {full_path}: {e}")
            
            results.append(result)
            
            # Send progress update
            progress = {
                'current': i + 1,
                'total': len(file_list),
                'file': result['file'],
                'success': result['success'],
                'error': result.get('error')
            }
            yield f"data: {json.dumps(progress)}\n\n"
        
        # Send final results
        yield f"data: {json.dumps({'done': True, 'results': results})}\n\n"
    
    return app.response_class(generate(), mimetype='text/event-stream')


@app.route('/api/rename-selected', methods=['POST'])
def rename_selected_files():
    """API endpoint to rename selected files with streaming progress"""
    from process_file import process_file
    
    data = request.json
    file_list = data.get('files', [])
    stream = request.args.get('stream', 'false').lower() == 'true'
    
    if not file_list:
        return jsonify({'error': 'No files specified'}), 400
    
    if not stream:
        # Non-streaming mode (backward compatible)
        results = []
        for filepath in file_list:
            full_path = os.path.join(WATCHED_DIR, filepath) if WATCHED_DIR else filepath
            
            if not os.path.exists(full_path):
                results.append({
                    'file': os.path.basename(filepath),
                    'success': False,
                    'error': 'File not found'
                })
                continue
            
            try:
                mark_file_web_modified(full_path)
                final_filepath = process_file(full_path, fixtitle=False, fixseries=False, fixfilename=True)
                mark_file_processed_wrapper(final_filepath)
                handle_file_rename_in_cache(full_path, final_filepath)
                results.append({
                    'file': os.path.basename(final_filepath),
                    'success': True
                })
                logging.info(f"Renamed file via web interface: {full_path} -> {final_filepath}")
            except Exception as e:
                results.append({
                    'file': os.path.basename(filepath),
                    'success': False,
                    'error': str(e)
                })
                logging.error(f"Error renaming file {full_path}: {e}")
        
        return jsonify({'results': results})
    
    # Streaming mode - send progress updates
    def generate():
        import json
        results = []
        for i, filepath in enumerate(file_list):
            full_path = os.path.join(WATCHED_DIR, filepath) if WATCHED_DIR else filepath
            result = {'file': os.path.basename(filepath)}
            
            if not os.path.exists(full_path):
                result['success'] = False
                result['error'] = 'File not found'
            else:
                try:
                    mark_file_web_modified(full_path)
                    final_filepath = process_file(full_path, fixtitle=False, fixseries=False, fixfilename=True)
                    mark_file_processed_wrapper(final_filepath)
                    handle_file_rename_in_cache(full_path, final_filepath)
                    result['success'] = True
                    logging.info(f"Renamed file via web interface: {full_path} -> {final_filepath}")
                except Exception as e:
                    result['success'] = False
                    result['error'] = str(e)
                    logging.error(f"Error renaming file {full_path}: {e}")
            
            results.append(result)
            
            # Send progress update
            progress = {
                'current': i + 1,
                'total': len(file_list),
                'file': result['file'],
                'success': result['success'],
                'error': result.get('error')
            }
            yield f"data: {json.dumps(progress)}\n\n"
        
        # Send final results
        yield f"data: {json.dumps({'done': True, 'results': results})}\n\n"
    
    return app.response_class(generate(), mimetype='text/event-stream')


@app.route('/api/normalize-selected', methods=['POST'])
def normalize_selected_files():
    """API endpoint to normalize metadata for selected files with streaming progress"""
    from process_file import process_file
    
    data = request.json
    file_list = data.get('files', [])
    stream = request.args.get('stream', 'false').lower() == 'true'
    
    if not file_list:
        return jsonify({'error': 'No files specified'}), 400
    
    if not stream:
        # Non-streaming mode (backward compatible)
        results = []
        for filepath in file_list:
            full_path = os.path.join(WATCHED_DIR, filepath) if WATCHED_DIR else filepath
            
            if not os.path.exists(full_path):
                results.append({
                    'file': os.path.basename(filepath),
                    'success': False,
                    'error': 'File not found'
                })
                continue
            
            try:
                mark_file_web_modified(full_path)
                final_filepath = process_file(full_path, fixtitle=True, fixseries=True, fixfilename=False)
                mark_file_processed_wrapper(final_filepath)
                results.append({
                    'file': os.path.basename(final_filepath),
                    'success': True
                })
                logging.info(f"Normalized metadata for file via web interface: {full_path}")
            except Exception as e:
                results.append({
                    'file': os.path.basename(filepath),
                    'success': False,
                    'error': str(e)
                })
                logging.error(f"Error normalizing metadata for file {full_path}: {e}")
        
        return jsonify({'results': results})
    
    # Streaming mode - send progress updates
    def generate():
        import json
        results = []
        for i, filepath in enumerate(file_list):
            full_path = os.path.join(WATCHED_DIR, filepath) if WATCHED_DIR else filepath
            result = {'file': os.path.basename(filepath)}
            
            if not os.path.exists(full_path):
                result['success'] = False
                result['error'] = 'File not found'
            else:
                try:
                    mark_file_web_modified(full_path)
                    final_filepath = process_file(full_path, fixtitle=True, fixseries=True, fixfilename=False)
                    mark_file_processed_wrapper(final_filepath)
                    result['success'] = True
                    logging.info(f"Normalized metadata for file via web interface: {full_path}")
                except Exception as e:
                    result['success'] = False
                    result['error'] = str(e)
                    logging.error(f"Error normalizing metadata for file {full_path}: {e}")
            
            results.append(result)
            
            # Send progress update
            progress = {
                'current': i + 1,
                'total': len(file_list),
                'file': result['file'],
                'success': result['success'],
                'error': result.get('error')
            }
            yield f"data: {json.dumps(progress)}\n\n"
        
        # Send final results
        yield f"data: {json.dumps({'done': True, 'results': results})}\n\n"
    
    return app.response_class(generate(), mimetype='text/event-stream')


@app.route('/api/jobs/process-all', methods=['POST'])
def async_process_all_files():
    """API endpoint to start async processing of all files"""
    from process_file import process_file
    
    logging.info("[API] Request to process all files (async)")
    files = get_comic_files()
    
    if not files:
        logging.warning("[API] No files found to process")
        return jsonify({'error': 'No files to process'}), 400
    
    logging.info(f"[API] Found {len(files)} files to process")
    
    # Create job
    job_manager = get_job_manager(max_workers=get_max_workers())
    job_id = job_manager.create_job(files)
    
    # Set active job on server IMMEDIATELY when job is created
    # This ensures the job is tracked even if the page refreshes before polling starts
    set_active_job(job_id, 'Processing Files...')
    logging.info(f"[API] Set active job {job_id} on server")
    
    # Define processing function
    def process_item(filepath):
        try:
            mark_file_web_modified(filepath)
            final_filepath = process_file(filepath, fixtitle=True, fixseries=True, fixfilename=True)
            mark_file_processed_wrapper(final_filepath, original_filepath=filepath)
            handle_file_rename_in_cache(filepath, final_filepath)
            logging.info(f"[BATCH] Processed file: {filepath} -> {final_filepath}")
            return JobResult(
                item=os.path.basename(filepath),
                success=True,
                details={'original': filepath, 'final': final_filepath}
            )
        except Exception as e:
            logging.error(f"[BATCH] Error processing file {filepath}: {e}")
            return JobResult(
                item=os.path.basename(filepath),
                success=False,
                error=str(e)
            )
    
    # Start job
    job_manager.start_job(job_id, process_item, files)
    
    logging.info(f"[API] Created and started job {job_id} for {len(files)} files")
    return jsonify({
        'job_id': job_id,
        'total_items': len(files)
    })


@app.route('/api/jobs/process-selected', methods=['POST'])
def async_process_selected_files():
    """API endpoint to start async processing of selected files"""
    from process_file import process_file
    
    data = request.json
    file_list = data.get('files', [])
    
    logging.info(f"[API] Request to process {len(file_list)} selected files (async)")
    
    if not file_list:
        logging.warning("[API] No files specified in request")
        return jsonify({'error': 'No files specified'}), 400
    
    # Build full paths
    full_paths = []
    for filepath in file_list:
        full_path = os.path.join(WATCHED_DIR, filepath) if WATCHED_DIR else filepath
        if os.path.exists(full_path):
            full_paths.append(full_path)
    
    if not full_paths:
        logging.warning(f"[API] None of the {len(file_list)} specified files exist")
        return jsonify({'error': 'No valid files to process'}), 400
    
    logging.info(f"[API] Found {len(full_paths)} valid files out of {len(file_list)} requested")
    
    # Create job
    job_manager = get_job_manager(max_workers=get_max_workers())
    job_id = job_manager.create_job(full_paths)
    
    # Set active job on server IMMEDIATELY when job is created
    # This ensures the job is tracked even if the page refreshes before polling starts
    set_active_job(job_id, 'Processing Selected Files...')
    logging.info(f"[API] Set active job {job_id} on server")
    
    # Define processing function
    def process_item(filepath):
        try:
            mark_file_web_modified(filepath)
            final_filepath = process_file(filepath, fixtitle=True, fixseries=True, fixfilename=True)
            mark_file_processed_wrapper(final_filepath, original_filepath=filepath)
            handle_file_rename_in_cache(filepath, final_filepath)
            logging.info(f"[BATCH] Processed file: {filepath} -> {final_filepath}")
            return JobResult(
                item=os.path.basename(filepath),
                success=True,
                details={'original': filepath, 'final': final_filepath}
            )
        except Exception as e:
            logging.error(f"[BATCH] Error processing file {filepath}: {e}")
            return JobResult(
                item=os.path.basename(filepath),
                success=False,
                error=str(e)
            )
    
    # Start job
    job_manager.start_job(job_id, process_item, full_paths)
    
    logging.info(f"[API] Created and started job {job_id} for {len(full_paths)} files")
    return jsonify({
        'job_id': job_id,
        'total_items': len(full_paths)
    })


@app.route('/api/jobs/<job_id>', methods=['GET'])
def get_job_status(job_id):
    """API endpoint to get job status"""
    logging.debug(f"[API] Status request for job {job_id}")
    job_manager = get_job_manager(max_workers=get_max_workers())
    status = job_manager.get_job_status(job_id)
    
    if status is None:
        logging.warning(f"[API] Job {job_id} not found")
        return jsonify({'error': 'Job not found'}), 404
    
    return jsonify(status)


@app.route('/api/jobs', methods=['GET'])
def list_jobs():
    """API endpoint to list all jobs"""
    job_manager = get_job_manager(max_workers=get_max_workers())
    jobs = job_manager.list_jobs()
    return jsonify({'jobs': jobs})


@app.route('/api/jobs/<job_id>', methods=['DELETE'])
def delete_job(job_id):
    """API endpoint to delete a job"""
    job_manager = get_job_manager(max_workers=get_max_workers())
    
    if job_manager.delete_job(job_id):
        return jsonify({'success': True})
    else:
        return jsonify({'error': 'Job not found'}), 404


@app.route('/api/jobs/<job_id>/cancel', methods=['POST'])
def cancel_job(job_id):
    """API endpoint to cancel a job"""
    logging.info(f"[API] Request to cancel job {job_id}")
    job_manager = get_job_manager(max_workers=get_max_workers())
    
    if job_manager.cancel_job(job_id):
        logging.info(f"[API] Job {job_id} cancelled successfully")
        return jsonify({'success': True})
    else:
        logging.warning(f"[API] Cannot cancel job {job_id} - not found or already completed")
        return jsonify({'error': 'Job not found or already completed'}), 400


@app.route('/api/jobs/process-unmarked', methods=['POST'])
def async_process_unmarked_files():
    """API endpoint to start async processing of unmarked files"""
    from process_file import process_file
    
    logging.info("[API] Request to process unmarked files (async)")
    
    # Get all files and filter to unmarked only
    files = get_comic_files(use_cache=False)
    unmarked_files = []
    
    for filepath in files:
        if not is_file_processed(filepath):
            unmarked_files.append(filepath)
    
    if not unmarked_files:
        logging.warning("[API] No unmarked files found to process")
        return jsonify({'error': 'No unmarked files to process'}), 400
    
    logging.info(f"[API] Found {len(unmarked_files)} unmarked files to process")
    
    # Create job
    job_manager = get_job_manager(max_workers=get_max_workers())
    job_id = job_manager.create_job(unmarked_files)
    
    # Set active job on server IMMEDIATELY when job is created
    # This ensures the job is tracked even if the page refreshes before polling starts
    set_active_job(job_id, 'Processing Unmarked Files...')
    logging.info(f"[API] Set active job {job_id} on server")
    
    # Define processing function
    def process_item(filepath):
        try:
            mark_file_web_modified(filepath)
            final_filepath = process_file(filepath, fixtitle=True, fixseries=True, fixfilename=True)
            mark_file_processed_wrapper(final_filepath, original_filepath=filepath)
            handle_file_rename_in_cache(filepath, final_filepath)
            logging.info(f"[BATCH] Processed unmarked file: {filepath} -> {final_filepath}")
            return JobResult(
                item=os.path.basename(filepath),
                success=True,
                details={'original': filepath, 'final': final_filepath}
            )
        except Exception as e:
            logging.error(f"[BATCH] Error processing unmarked file {filepath}: {e}")
            return JobResult(
                item=os.path.basename(filepath),
                success=False,
                error=str(e)
            )
    
    # Start job
    job_manager.start_job(job_id, process_item, unmarked_files)
    
    logging.info(f"[API] Created and started job {job_id} for {len(unmarked_files)} unmarked files")
    return jsonify({
        'job_id': job_id,
        'total_items': len(unmarked_files)
    })


@app.route('/api/jobs/rename-unmarked', methods=['POST'])
def async_rename_unmarked_files():
    """API endpoint to start async renaming of unmarked files"""
    from process_file import process_file
    
    logging.info("[API] Request to rename unmarked files (async)")
    
    # Get all files and filter to unmarked only
    files = get_comic_files(use_cache=False)
    unmarked_files = []
    
    for filepath in files:
        if not is_file_processed(filepath):
            unmarked_files.append(filepath)
    
    if not unmarked_files:
        logging.warning("[API] No unmarked files found to rename")
        return jsonify({'error': 'No unmarked files to rename'}), 400
    
    logging.info(f"[API] Found {len(unmarked_files)} unmarked files to rename")
    
    # Create job
    job_manager = get_job_manager(max_workers=get_max_workers())
    job_id = job_manager.create_job(unmarked_files)
    
    # Set active job on server IMMEDIATELY when job is created
    set_active_job(job_id, 'Renaming Unmarked Files...')
    logging.info(f"[API] Set active job {job_id} on server")
    
    # Define processing function
    def process_item(filepath):
        try:
            mark_file_web_modified(filepath)
            final_filepath = process_file(filepath, fixtitle=False, fixseries=False, fixfilename=True)
            mark_file_processed_wrapper(final_filepath)
            handle_file_rename_in_cache(filepath, final_filepath)
            logging.info(f"[BATCH] Renamed unmarked file: {filepath} -> {final_filepath}")
            return JobResult(
                item=os.path.basename(filepath),
                success=True,
                details={'original': filepath, 'final': final_filepath}
            )
        except Exception as e:
            logging.error(f"[BATCH] Error renaming unmarked file {filepath}: {e}")
            return JobResult(
                item=os.path.basename(filepath),
                success=False,
                error=str(e)
            )
    
    # Start job
    job_manager.start_job(job_id, process_item, unmarked_files)
    
    logging.info(f"[API] Created and started job {job_id} for {len(unmarked_files)} unmarked files")
    return jsonify({
        'job_id': job_id,
        'total_items': len(unmarked_files)
    })


@app.route('/api/jobs/normalize-unmarked', methods=['POST'])
def async_normalize_unmarked_files():
    """API endpoint to start async normalizing of unmarked files"""
    from process_file import process_file
    
    logging.info("[API] Request to normalize unmarked files (async)")
    
    # Get all files and filter to unmarked only
    files = get_comic_files(use_cache=False)
    unmarked_files = []
    
    for filepath in files:
        if not is_file_processed(filepath):
            unmarked_files.append(filepath)
    
    if not unmarked_files:
        logging.warning("[API] No unmarked files found to normalize")
        return jsonify({'error': 'No unmarked files to normalize'}), 400
    
    logging.info(f"[API] Found {len(unmarked_files)} unmarked files to normalize")
    
    # Create job
    job_manager = get_job_manager(max_workers=get_max_workers())
    job_id = job_manager.create_job(unmarked_files)
    
    # Set active job on server IMMEDIATELY when job is created
    set_active_job(job_id, 'Normalizing Unmarked Files...')
    logging.info(f"[API] Set active job {job_id} on server")
    
    # Define processing function
    def process_item(filepath):
        try:
            mark_file_web_modified(filepath)
            final_filepath = process_file(filepath, fixtitle=True, fixseries=True, fixfilename=False)
            mark_file_processed_wrapper(final_filepath)
            logging.info(f"[BATCH] Normalized unmarked file: {filepath}")
            return JobResult(
                item=os.path.basename(filepath),
                success=True,
                details={'original': filepath, 'final': final_filepath}
            )
        except Exception as e:
            logging.error(f"[BATCH] Error normalizing unmarked file {filepath}: {e}")
            return JobResult(
                item=os.path.basename(filepath),
                success=False,
                error=str(e)
            )
    
    # Start job
    job_manager.start_job(job_id, process_item, unmarked_files)
    
    logging.info(f"[API] Created and started job {job_id} for {len(unmarked_files)} unmarked files")
    return jsonify({
        'job_id': job_id,
        'total_items': len(unmarked_files)
    })


@app.route('/api/settings/filename-format', methods=['GET'])
def get_filename_format_api():
    """API endpoint to get the filename format setting"""
    return jsonify({
        'format': get_filename_format(),
        'default': DEFAULT_FILENAME_FORMAT
    })

@app.route('/api/settings/filename-format', methods=['POST'])
def set_filename_format_api():
    """API endpoint to set the filename format setting"""
    data = request.json
    format_string = data.get('format')
    
    if not format_string:
        return jsonify({'error': 'Format string is required'}), 400
    
    success = set_filename_format(format_string)
    
    if success:
        logging.info(f"Filename format updated to: {format_string}")
        return jsonify({'success': True, 'format': format_string})
    else:
        return jsonify({'error': 'Failed to save filename format'}), 500

@app.route('/api/settings/watcher-enabled', methods=['GET'])
def get_watcher_enabled_api():
    """API endpoint to get the watcher enabled setting"""
    return jsonify({
        'enabled': get_watcher_enabled()
    })

@app.route('/api/settings/watcher-enabled', methods=['POST'])
def set_watcher_enabled_api():
    """API endpoint to set the watcher enabled setting"""
    data = request.json
    enabled = data.get('enabled')
    
    if enabled is None:
        return jsonify({'error': 'Enabled value is required'}), 400
    
    success = set_watcher_enabled(enabled)
    
    if success:
        status = "enabled" if enabled else "disabled"
        logging.info(f"Watcher {status}")
        return jsonify({'success': True, 'enabled': enabled})
    else:
        return jsonify({'error': 'Failed to save watcher enabled setting'}), 500

@app.route('/api/watcher/status', methods=['GET'])
def get_watcher_status_api():
    """API endpoint to get the watcher process status"""
    import subprocess
    
    is_running = False
    try:
        # Check if watcher.py process is running
        result = subprocess.run(
            ['pgrep', '-f', 'python.*watcher.py'],
            capture_output=True,
            text=True
        )
        is_running = result.returncode == 0 and len(result.stdout.strip()) > 0
    except Exception as e:
        logging.error(f"Error checking watcher status: {e}")
    
    # Get the enabled setting from config
    enabled_setting = get_watcher_enabled()
    
    return jsonify({
        'running': is_running,
        'enabled': enabled_setting
    })

@app.route('/api/settings/log-max-bytes', methods=['GET'])
def get_log_max_bytes_api():
    """API endpoint to get the log max bytes setting"""
    return jsonify({
        'maxBytes': get_log_max_bytes(),
        'maxMB': get_log_max_bytes() / (1024 * 1024)
    })

@app.route('/api/settings/log-max-bytes', methods=['POST'])
def set_log_max_bytes_api():
    """API endpoint to set the log max bytes setting"""
    data = request.json
    max_mb = data.get('maxMB')
    
    if max_mb is None:
        return jsonify({'error': 'maxMB value is required'}), 400
    
    try:
        max_mb = float(max_mb)
        if max_mb <= 0:
            return jsonify({'error': 'maxMB must be greater than 0'}), 400
        
        max_bytes = int(max_mb * 1024 * 1024)
        success = set_log_max_bytes(max_bytes)
        
        if success:
            logging.info(f"Log max size updated to {max_mb}MB ({max_bytes} bytes)")
            # Note: The actual log handler rotation limit will be applied on next restart
            return jsonify({'success': True, 'maxBytes': max_bytes, 'maxMB': max_mb})
        else:
            return jsonify({'error': 'Failed to save log max bytes setting'}), 500
    except (ValueError, TypeError):
        return jsonify({'error': 'Invalid maxMB value'}), 400

@app.route('/api/settings/issue-number-padding', methods=['GET'])
def get_issue_number_padding_api():
    """API endpoint to get the issue number padding setting"""
    return jsonify({
        'padding': get_issue_number_padding(),
        'default': DEFAULT_ISSUE_NUMBER_PADDING
    })

@app.route('/api/settings/issue-number-padding', methods=['POST'])
def set_issue_number_padding_api():
    """API endpoint to set the issue number padding setting"""
    data = request.json
    padding = data.get('padding')
    
    if padding is None:
        return jsonify({'error': 'Padding value is required'}), 400
    
    try:
        padding = int(padding)
        if padding < 0:
            return jsonify({'error': 'Padding must be 0 or greater'}), 400
        
        success = set_issue_number_padding(padding)
        
        if success:
            logging.info(f"Issue number padding updated to: {padding}")
            return jsonify({'success': True, 'padding': padding})
        else:
            return jsonify({'error': 'Failed to save issue number padding setting'}), 500
    except (ValueError, TypeError):
        return jsonify({'error': 'Invalid padding value'}), 400

@app.route('/api/version', methods=['GET'])
def get_version():
    """API endpoint to get the application version"""
    return jsonify({
        'version': __version__
    })

@app.route('/api/logs', methods=['GET'])
def get_logs():
    """API endpoint to get the log file contents"""
    log_file = os.path.join(LOG_DIR, "ComicMaintainer.log")
    
    if not os.path.exists(log_file):
        return jsonify({'error': 'Log file not found'}), 404
    
    try:
        # Get the number of lines to return (default: last 500 lines)
        lines = request.args.get('lines', default=500, type=int)
        
        with open(log_file, 'r', encoding='utf-8', errors='replace') as f:
            # Read all lines
            all_lines = f.readlines()
            
            # Return the last N lines (or all if lines=0)
            if lines > 0:
                log_content = ''.join(all_lines[-lines:])
                returned_lines = min(lines, len(all_lines))
            else:
                log_content = ''.join(all_lines)
                returned_lines = len(all_lines)
        
        return jsonify({
            'logs': log_content,
            'total_lines': len(all_lines),
            'returned_lines': returned_lines
        })
    except Exception as e:
        logging.error(f"Error reading log file: {e}")
        return jsonify({'error': str(e)}), 500

@app.route('/api/scan-unmarked', methods=['GET'])
def scan_unmarked_files():
    """API endpoint to scan for unmarked files"""
    files = get_comic_files(use_cache=False)
    unmarked_files = []
    marked_files = []
    
    for filepath in files:
        if is_file_processed(filepath):
            marked_files.append(filepath)
        else:
            unmarked_files.append(filepath)
    
    return jsonify({
        'unmarked_count': len(unmarked_files),
        'marked_count': len(marked_files),
        'total_count': len(files)
    })

@app.route('/api/process-unmarked', methods=['POST'])
def process_unmarked_files():
    """API endpoint to process only unmarked files with streaming progress"""
    from process_file import process_file
    
    stream = request.args.get('stream', 'false').lower() == 'true'
    files = get_comic_files(use_cache=False)
    unmarked_files = []
    
    # Filter to only unmarked files
    for filepath in files:
        if not is_file_processed(filepath):
            unmarked_files.append(filepath)
    
    if not stream:
        # Non-streaming mode (backward compatible)
        results = []
        
        for filepath in unmarked_files:
            try:
                # Mark as web modified to prevent watcher from processing
                mark_file_web_modified(filepath)
                
                # Process the file and get the final filepath (may be renamed)
                final_filepath = process_file(filepath, fixtitle=True, fixseries=True, fixfilename=True)
                
                # Mark as processed using the final filepath, cleanup old filename if renamed
                mark_file_processed_wrapper(final_filepath, original_filepath=filepath)
                
                # Update cache incrementally if file was renamed
                handle_file_rename_in_cache(filepath, final_filepath)
                
                results.append({
                    'file': os.path.basename(final_filepath),
                    'success': True
                })
                logging.info(f"Processed unmarked file via web interface: {filepath} -> {final_filepath}")
            except Exception as e:
                results.append({
                    'file': os.path.basename(filepath),
                    'success': False,
                    'error': str(e)
                })
                logging.error(f"Error processing unmarked file {filepath}: {e}")
        
        return jsonify({'results': results})
    
    # Streaming mode - send progress updates
    def generate():
        import json
        results = []
        for i, filepath in enumerate(unmarked_files):
            result = {'file': os.path.basename(filepath)}
            
            try:
                mark_file_web_modified(filepath)
                final_filepath = process_file(filepath, fixtitle=True, fixseries=True, fixfilename=True)
                mark_file_processed_wrapper(final_filepath, original_filepath=filepath)
                handle_file_rename_in_cache(filepath, final_filepath)
                result['success'] = True
                logging.info(f"Processed unmarked file via web interface: {filepath} -> {final_filepath}")
            except Exception as e:
                result['success'] = False
                result['error'] = str(e)
                logging.error(f"Error processing unmarked file {filepath}: {e}")
            
            results.append(result)
            
            # Send progress update
            progress = {
                'current': i + 1,
                'total': len(unmarked_files),
                'file': result['file'],
                'success': result['success'],
                'error': result.get('error')
            }
            yield f"data: {json.dumps(progress)}\n\n"
        
        # Send final results
        yield f"data: {json.dumps({'done': True, 'results': results})}\n\n"
    
    return app.response_class(generate(), mimetype='text/event-stream')

@app.route('/api/rename-unmarked', methods=['POST'])
def rename_unmarked_files():
    """API endpoint to rename only unmarked files with streaming progress"""
    from process_file import process_file
    
    stream = request.args.get('stream', 'false').lower() == 'true'
    files = get_comic_files(use_cache=False)
    unmarked_files = []
    
    # Filter to only unmarked files
    for filepath in files:
        if not is_file_processed(filepath):
            unmarked_files.append(filepath)
    
    if not stream:
        # Non-streaming mode (backward compatible)
        results = []
        
        for filepath in unmarked_files:
            try:
                # Mark as web modified to prevent watcher from processing
                mark_file_web_modified(filepath)
                
                # Only rename the file
                final_filepath = process_file(filepath, fixtitle=False, fixseries=False, fixfilename=True)
                
                # Mark as processed using the final filepath
                mark_file_processed_wrapper(final_filepath)
                
                # Update cache incrementally if file was renamed
                handle_file_rename_in_cache(filepath, final_filepath)
                
                results.append({
                    'file': os.path.basename(final_filepath),
                    'success': True
                })
                logging.info(f"Renamed unmarked file via web interface: {filepath} -> {final_filepath}")
            except Exception as e:
                results.append({
                    'file': os.path.basename(filepath),
                    'success': False,
                    'error': str(e)
                })
                logging.error(f"Error renaming unmarked file {filepath}: {e}")
        
        return jsonify({'results': results})
    
    # Streaming mode - send progress updates
    def generate():
        import json
        results = []
        for i, filepath in enumerate(unmarked_files):
            result = {'file': os.path.basename(filepath)}
            
            try:
                mark_file_web_modified(filepath)
                final_filepath = process_file(filepath, fixtitle=False, fixseries=False, fixfilename=True)
                mark_file_processed_wrapper(final_filepath)
                handle_file_rename_in_cache(filepath, final_filepath)
                result['success'] = True
                logging.info(f"Renamed unmarked file via web interface: {filepath} -> {final_filepath}")
            except Exception as e:
                result['success'] = False
                result['error'] = str(e)
                logging.error(f"Error renaming unmarked file {filepath}: {e}")
            
            results.append(result)
            
            # Send progress update
            progress = {
                'current': i + 1,
                'total': len(unmarked_files),
                'file': result['file'],
                'success': result['success'],
                'error': result.get('error')
            }
            yield f"data: {json.dumps(progress)}\n\n"
        
        # Send final results
        yield f"data: {json.dumps({'done': True, 'results': results})}\n\n"
    
    return app.response_class(generate(), mimetype='text/event-stream')

@app.route('/api/normalize-unmarked', methods=['POST'])
def normalize_unmarked_files():
    """API endpoint to normalize metadata for only unmarked files with streaming progress"""
    from process_file import process_file
    
    stream = request.args.get('stream', 'false').lower() == 'true'
    files = get_comic_files(use_cache=False)
    unmarked_files = []
    
    # Filter to only unmarked files
    for filepath in files:
        if not is_file_processed(filepath):
            unmarked_files.append(filepath)
    
    if not stream:
        # Non-streaming mode (backward compatible)
        results = []
        
        for filepath in unmarked_files:
            try:
                # Mark as web modified to prevent watcher from processing
                mark_file_web_modified(filepath)
                
                # Only normalize metadata
                final_filepath = process_file(filepath, fixtitle=True, fixseries=True, fixfilename=False)
                
                # Mark as processed using the final filepath
                mark_file_processed_wrapper(final_filepath)
                
                results.append({
                    'file': os.path.basename(final_filepath),
                    'success': True
                })
                logging.info(f"Normalized metadata for unmarked file via web interface: {filepath}")
            except Exception as e:
                results.append({
                    'file': os.path.basename(filepath),
                    'success': False,
                    'error': str(e)
                })
                logging.error(f"Error normalizing metadata for unmarked file {filepath}: {e}")
        
        return jsonify({'results': results})
    
    # Streaming mode - send progress updates
    def generate():
        import json
        results = []
        for i, filepath in enumerate(unmarked_files):
            result = {'file': os.path.basename(filepath)}
            
            try:
                mark_file_web_modified(filepath)
                final_filepath = process_file(filepath, fixtitle=True, fixseries=True, fixfilename=False)
                mark_file_processed_wrapper(final_filepath)
                result['success'] = True
                logging.info(f"Normalized metadata for unmarked file via web interface: {filepath}")
            except Exception as e:
                result['success'] = False
                result['error'] = str(e)
                logging.error(f"Error normalizing metadata for unmarked file {filepath}: {e}")
            
            results.append(result)
            
            # Send progress update
            progress = {
                'current': i + 1,
                'total': len(unmarked_files),
                'file': result['file'],
                'success': result['success'],
                'error': result.get('error')
            }
            yield f"data: {json.dumps(progress)}\n\n"
        
        # Send final results
        yield f"data: {json.dumps({'done': True, 'results': results})}\n\n"
    
    return app.response_class(generate(), mimetype='text/event-stream')

@app.route('/api/delete-file/<path:filepath>', methods=['DELETE'])
def delete_single_file(filepath):
    """API endpoint to delete a single file"""
    full_path = os.path.join(WATCHED_DIR, filepath) if WATCHED_DIR else filepath
    
    if not os.path.exists(full_path):
        return jsonify({'error': 'File not found'}), 404
    
    try:
        # Mark as web modified before deletion to prevent watcher from processing
        mark_file_web_modified(full_path)
        
        # Delete the file
        os.remove(full_path)
        
        # Clear processed and duplicate markers (web_modified marker will be consumed by watcher)
        unmark_file_processed(full_path)
        unmark_file_duplicate(full_path)
        
        # Update cache incrementally instead of clearing it
        record_cache_change('remove', old_path=full_path)
        update_watcher_timestamp()
        
        logging.info(f"Deleted file via web interface: {full_path}")
        return jsonify({'success': True})
    except Exception as e:
        logging.error(f"Error deleting file {full_path}: {e}")
        return jsonify({'error': str(e)}), 500

@app.route('/api/preferences', methods=['GET'])
def get_preferences_endpoint():
    """API endpoint to get all user preferences"""
    try:
        preferences = get_all_preferences()
        # Add defaults for common preferences if not set
        if 'theme' not in preferences:
            preferences['theme'] = None  # Will use system preference
        if 'perPage' not in preferences:
            preferences['perPage'] = 100
        return jsonify(preferences)
    except Exception as e:
        logging.error(f"Error getting preferences: {e}")
        return jsonify({'error': str(e)}), 500

@app.route('/api/preferences', methods=['POST'])
def set_preferences_endpoint():
    """API endpoint to set user preferences"""
    try:
        data = request.json
        if not data:
            return jsonify({'error': 'No data provided'}), 400
        
        # Update each preference
        for key, value in data.items():
            set_preference(key, value)
        
        return jsonify({'success': True, 'message': 'Preferences updated'})
    except Exception as e:
        logging.error(f"Error setting preferences: {e}")
        return jsonify({'error': str(e)}), 500

@app.route('/api/active-job', methods=['GET'])
def get_active_job_endpoint():
    """API endpoint to get the currently active job"""
    try:
        active_job = get_active_job()
        if active_job:
            return jsonify(active_job)
        else:
            return jsonify({'job_id': None, 'job_title': None})
    except Exception as e:
        logging.error(f"Error getting active job: {e}")
        return jsonify({'error': str(e)}), 500

@app.route('/api/active-job', methods=['POST'])
def set_active_job_endpoint():
    """API endpoint to set the currently active job"""
    try:
        data = request.json
        if not data or 'job_id' not in data:
            return jsonify({'error': 'job_id is required'}), 400
        
        job_id = data['job_id']
        job_title = data.get('job_title', 'Processing...')
        
        set_active_job(job_id, job_title)
        return jsonify({'success': True, 'message': 'Active job set'})
    except Exception as e:
        logging.error(f"Error setting active job: {e}")
        return jsonify({'error': str(e)}), 500

@app.route('/api/active-job', methods=['DELETE'])
def clear_active_job_endpoint():
    """API endpoint to clear the currently active job"""
    try:
        clear_active_job()
        return jsonify({'success': True, 'message': 'Active job cleared'})
    except Exception as e:
        logging.error(f"Error clearing active job: {e}")
        return jsonify({'error': str(e)}), 500

@app.route('/api/cache/prewarm', methods=['POST'])
def prewarm_cache_endpoint():
    """API endpoint to manually prewarm the metadata cache"""
    try:
        prewarm_metadata_cache()
        return jsonify({
            'success': True,
            'message': 'Metadata cache prewarmed successfully'
        })
    except Exception as e:
        logging.error(f"Error prewarming cache via API: {e}")
        return jsonify({'error': str(e)}), 500

@app.route('/api/cache/stats', methods=['GET'])
def cache_stats_endpoint():
    """API endpoint to get cache statistics"""
    try:
        with cache_lock:
            file_count = len(file_list_cache['files']) if file_list_cache['files'] else 0
            cache_age = time.time() - file_list_cache['timestamp'] if file_list_cache['timestamp'] else 0
        
        with enriched_file_cache_lock:
            enriched_count = len(enriched_file_cache['files']) if enriched_file_cache['files'] else 0
            enriched_age = time.time() - enriched_file_cache['timestamp'] if enriched_file_cache['timestamp'] else 0
            rebuild_in_progress = enriched_file_cache['rebuild_in_progress']
        
        # Get marker counts from centralized storage
        from markers import MARKER_TYPE_PROCESSED, MARKER_TYPE_DUPLICATE, MARKER_TYPE_WEB_MODIFIED
        from marker_store import get_markers
        processed_count = len(get_markers(MARKER_TYPE_PROCESSED))
        duplicate_count = len(get_markers(MARKER_TYPE_DUPLICATE))
        web_modified_count = len(get_markers(MARKER_TYPE_WEB_MODIFIED))
        
        return jsonify({
            'file_list_cache': {
                'file_count': file_count,
                'age_seconds': cache_age,
                'is_populated': file_list_cache['files'] is not None
            },
            'enriched_file_cache': {
                'file_count': enriched_count,
                'age_seconds': enriched_age,
                'is_populated': enriched_file_cache['files'] is not None,
                'rebuild_in_progress': rebuild_in_progress
            },
            'markers': {
                'processed_files': processed_count,
                'duplicate_files': duplicate_count,
                'web_modified_files': web_modified_count,
                'storage_location': '/Config/markers/'
            }
        })
    except Exception as e:
        logging.error(f"Error getting cache stats: {e}")
        return jsonify({'error': str(e)}), 500

def prewarm_metadata_cache():
    """Prewarm metadata cache by ensuring marker files are loaded"""
    # Note: With centralized markers in /Config/markers/, markers are already
    # loaded efficiently when first accessed. This function is kept for 
    # backward compatibility but no longer needs to scan directories.
    # The markers.py module handles loading on first access.
    logging.info("Metadata markers are stored centrally and loaded on-demand")

def initialize_cache():
    """Initialize file list cache and trigger async metadata cache rebuild on startup"""
    if not WATCHED_DIR:
        return
    
    # Try to acquire lock to coordinate cache initialization across workers
    lock_fd = try_acquire_cache_rebuild_lock(timeout=0.1)
    
    if lock_fd is None:
        logging.info("Another worker is already initializing caches, skipping")
        return
    
    try:
        logging.info("Initializing caches on startup...")
        
        # Load the file list cache quickly
        logging.info("Building file list cache...")
        files = get_comic_files(use_cache=True)
        logging.info(f"File list cache initialized with {len(files)} files")
        
        # Prewarm the metadata cache (markers) - this is fast as it's centralized
        prewarm_metadata_cache()
        
        # Trigger async enriched file cache rebuild instead of building synchronously
        # This prevents worker timeouts during startup
        logging.info("Triggering async enriched file cache rebuild...")
        get_enriched_file_list(files, force_rebuild=True)
        logging.info("Async cache rebuild triggered - cache will be available shortly")
        
        logging.info("Cache initialization complete")
    finally:
        if lock_fd is not None:
            release_cache_rebuild_lock(lock_fd)

def init_app():
    """Initialize the application on startup"""
    if not WATCHED_DIR:
        logging.error("WATCHED_DIR environment variable is not set. Exiting.")
        sys.exit(1)
    
    # Initialize cache on startup
    initialize_cache()

if __name__ == '__main__':
    # This block is only for development/testing purposes
    # In production, Gunicorn will import the app directly
    init_app()
    port = int(os.environ.get('WEB_PORT', 5000))
    app.run(host='0.0.0.0', port=port, debug=False)
else:
    # When imported by Gunicorn, initialize the app
    init_app()
