import os
import sys
import logging
from logging.handlers import RotatingFileHandler
import json
import fcntl
from flask import Flask, render_template, jsonify, request, send_from_directory
from werkzeug.middleware.proxy_fix import ProxyFix
from comicapi.comicarchive import ComicArchive
import glob
import threading
import time
from config import (
    get_filename_format, set_filename_format, DEFAULT_FILENAME_FORMAT,
    get_watcher_enabled, set_watcher_enabled,
    get_log_max_bytes, set_log_max_bytes,
    get_max_workers,
    get_issue_number_padding, set_issue_number_padding, DEFAULT_ISSUE_NUMBER_PADDING,
    get_github_token, set_github_token, DEFAULT_GITHUB_TOKEN,
    get_github_repository, set_github_repository, DEFAULT_GITHUB_REPOSITORY,
    get_github_issue_assignee, set_github_issue_assignee, DEFAULT_GITHUB_ISSUE_ASSIGNEE
)
from version import __version__
from markers import (
    is_file_processed, mark_file_processed, unmark_file_processed,
    is_file_duplicate, mark_file_duplicate, unmark_file_duplicate,
    is_file_web_modified, mark_file_web_modified, clear_file_web_modified,
    cleanup_web_modified_markers, get_all_marker_data
)
import file_store
from job_manager import get_job_manager, JobResult
from preferences_store import (
    get_preference, set_preference, get_all_preferences,
    get_active_job, set_active_job, clear_active_job
)
from event_broadcaster import (
    get_broadcaster, event_stream_generator,
    broadcast_watcher_status,
    broadcast_file_processed, broadcast_job_updated
)
from watchdog.observers import Observer
from watchdog.events import FileSystemEventHandler

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

# Get the parent directory (project root) for templates and static files
# web_app.py is in src/ during development, but in /app/ when deployed in Docker
script_dir = os.path.dirname(os.path.abspath(__file__))
# Check if we're in the src/ directory or deployed directly in /app/
if os.path.basename(script_dir) == 'src':
    # Development mode: web_app.py is in src/, templates and static are in parent directory
    project_root = os.path.dirname(script_dir)
else:
    # Deployed mode: web_app.py is in /app/, templates and static are in /app/templates and /app/static
    project_root = script_dir

template_folder = os.path.join(project_root, 'templates')
static_folder = os.path.join(project_root, 'static')

app = Flask(__name__, template_folder=template_folder, static_folder=static_folder)

# Configure reverse proxy support
# ProxyFix middleware handles X-Forwarded-* headers from reverse proxies
# This ensures the application generates correct URLs when behind nginx, Traefik, Apache, etc.
app.wsgi_app = ProxyFix(
    app.wsgi_app,
    x_for=1,      # Trust X-Forwarded-For
    x_proto=1,    # Trust X-Forwarded-Proto (http/https)
    x_host=1,     # Trust X-Forwarded-Host
    x_prefix=1    # Trust X-Forwarded-Prefix (for subdirectory deployments)
)

# Optional: Support for serving from a subdirectory (e.g., /comics/)
# Set BASE_PATH environment variable to the path prefix (must start with /)
BASE_PATH = os.environ.get('BASE_PATH', '').rstrip('/')
if BASE_PATH and not BASE_PATH.startswith('/'):
    logging.warning(f"BASE_PATH must start with '/'. Ignoring invalid value: {BASE_PATH}")
    BASE_PATH = ''
if BASE_PATH:
    logging.info(f"Application will be served from base path: {BASE_PATH}")
    app.config['APPLICATION_ROOT'] = BASE_PATH

# Configure Flask for better performance
app.config['JSON_SORT_KEYS'] = False  # Don't sort JSON keys (faster)
app.config['JSONIFY_PRETTYPRINT_REGULAR'] = False  # Disable pretty-print (smaller responses)

WATCHED_DIR = os.environ.get('WATCHED_DIR')

# Initialize file store on startup
file_store.init_db()



# Wrapper functions for marker operations
def mark_file_processed_wrapper(filepath, original_filepath=None):
    """Mark a file as processed"""
    mark_file_processed(filepath, original_filepath=original_filepath)
    
    # Broadcast event to connected clients
    broadcast_file_processed(filepath, success=True)

def mark_file_duplicate_wrapper(filepath):
    """Mark a file as duplicate"""
    mark_file_duplicate(filepath)
    
    # Broadcast event to connected clients
    broadcast_file_processed(filepath, success=True)

def cleanup_web_markers_scheduled():
    """Clean up old web modified markers and reschedule"""
    try:
        cleanup_web_modified_markers(max_files=100)
    except Exception as e:
        logging.error(f"Error cleaning up web markers: {e}")
    finally:
        # Reschedule for next run (5 minutes)
        cleanup_timer = threading.Timer(300.0, cleanup_web_markers_scheduled)
        cleanup_timer.daemon = True
        cleanup_timer.start()

# Start cleanup timer (event-based, not polling)
cleanup_timer = threading.Timer(300.0, cleanup_web_markers_scheduled)
cleanup_timer.daemon = True
cleanup_timer.start()
logging.info("Web markers cleanup scheduled (every 5 minutes)")


@app.after_request
def add_performance_headers(response):
    """Add performance-related headers to responses"""
    # Add cache control for API responses
    if request.path.startswith('/api/'):
        # API responses should not be cached by default (dynamic data)
        response.headers['Cache-Control'] = 'no-cache, no-store, must-revalidate'
        response.headers['Pragma'] = 'no-cache'
        response.headers['Expires'] = '0'
    
    # Add Vary header for better caching
    if 'Vary' not in response.headers:
        response.headers['Vary'] = 'Accept-Encoding'
    
    # Add security headers when behind HTTPS proxy
    # Check if request came through HTTPS (via X-Forwarded-Proto header)
    if request.headers.get('X-Forwarded-Proto') == 'https' or request.scheme == 'https':
        # HSTS: Tell browsers to always use HTTPS for this domain
        # max-age=31536000 = 1 year
        response.headers['Strict-Transport-Security'] = 'max-age=31536000; includeSubDomains'
        
        # Upgrade insecure requests: Tell browser to upgrade HTTP requests to HTTPS
        response.headers['Content-Security-Policy'] = "upgrade-insecure-requests"
    
    return response


def record_file_change(change_type, old_path=None, new_path=None):
    """Record a file change directly in the file store
    
    Args:
        change_type: 'add', 'remove', or 'rename'
        old_path: Original file path (for 'remove' and 'rename')
        new_path: New file path (for 'add' and 'rename')
    """
    try:
        if change_type == 'add' and new_path:
            file_store.add_file(new_path)
            logging.info(f"Added file to store: {new_path}")
        elif change_type == 'remove' and old_path:
            file_store.remove_file(old_path)
            logging.info(f"Removed file from store: {old_path}")
        elif change_type == 'rename' and old_path and new_path:
            file_store.rename_file(old_path, new_path)
            logging.info(f"Renamed file in store: {old_path} -> {new_path}")
        
    except Exception as e:
        logging.error(f"Error recording file change: {e}")

def load_files_from_store():
    """Load file list from the file store database
    
    Returns:
        List of file paths sorted alphabetically
    """
    try:
        files = file_store.get_all_files()
        logging.debug(f"Loaded {len(files)} files from store")
        return files
    except Exception as e:
        logging.error(f"Error loading files from store: {e}")
        return []

def load_files_with_metadata_from_store():
    """Load file list with metadata from the file store database
    
    Returns:
        Dictionary mapping file paths to their metadata
    """
    try:
        files_with_metadata = file_store.get_all_files_with_metadata()
        logging.debug(f"Loaded {len(files_with_metadata)} files with metadata from store")
        # Convert to dict for fast lookup
        return {item['filepath']: item for item in files_with_metadata}
    except Exception as e:
        logging.error(f"Error loading files with metadata from store: {e}")
        return {}

def get_comic_files():
    """Get all comic files directly from the file store database
    
    SQLite provides extremely fast reads (<3ms for 5000 files) with OS-level caching.
    """
    if not WATCHED_DIR:
        return []
    
    return load_files_from_store()

def handle_file_rename_in_store(original_path, final_path):
    """Handle file rename in file store - record change if file was actually renamed"""
    if original_path != final_path:
        record_file_change('rename', old_path=original_path, new_path=final_path)

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
    # Get base_path, converting Flask's default '/' to empty string for root deployment
    base_path = app.config.get('APPLICATION_ROOT', '')
    if base_path == '/':
        base_path = ''
    response = render_template('index.html', base_path=base_path)
    # Add cache control for static HTML (short cache for dynamic content)
    return response

@app.route('/manifest.json')
def serve_manifest():
    """Serve the web app manifest for PWA installation with dynamic paths"""
    # Get base_path, converting Flask's default '/' to empty string for root deployment
    base_path = app.config.get('APPLICATION_ROOT', '')
    if base_path == '/':
        base_path = ''
    
    manifest = {
        "name": "Comic Maintainer",
        "short_name": "ComicMaintainer",
        "description": "Manage and process your comic archive files with ComicTagger",
        "start_url": f"{base_path}/",
        "display": "standalone",
        "background_color": "#2c3e50",
        "theme_color": "#2c3e50",
        "orientation": "any",
        "icons": [
            {
                "src": f"{base_path}/static/icons/icon-192x192.png",
                "sizes": "192x192",
                "type": "image/png",
                "purpose": "any maskable"
            },
            {
                "src": f"{base_path}/static/icons/icon-512x512.png",
                "sizes": "512x512",
                "type": "image/png",
                "purpose": "any maskable"
            }
        ],
        "categories": ["utilities", "productivity"],
        "screenshots": []
    }
    
    return jsonify(manifest)

@app.route('/sw.js')
def serve_service_worker():
    """Serve the service worker with BASE_PATH injected"""
    # Get base_path, converting Flask's default '/' to empty string for root deployment
    base_path = app.config.get('APPLICATION_ROOT', '')
    if base_path == '/':
        base_path = ''
    
    # Read the service worker template
    sw_path = os.path.join(static_folder, 'sw.js')
    with open(sw_path, 'r') as f:
        sw_content = f.read()
    
    # Inject BASE_PATH at the beginning
    sw_with_base = f"""// Service Worker for Comic Maintainer PWA with reverse proxy support
// BASE_PATH is injected dynamically based on deployment configuration
const BASE_PATH = '{base_path}';

{sw_content}"""
    
    # Replace hardcoded paths with BASE_PATH-aware versions
    sw_with_base = sw_with_base.replace("'/'", "BASE_PATH + '/'")
    sw_with_base = sw_with_base.replace("'/manifest.json'", "BASE_PATH + '/manifest.json'")
    sw_with_base = sw_with_base.replace("'/static/", "BASE_PATH + '/static/")
    sw_with_base = sw_with_base.replace("url.pathname.startsWith('/api/')", "url.pathname.startsWith(BASE_PATH + '/api/')")
    sw_with_base = sw_with_base.replace("url.pathname.startsWith('/static/')", "url.pathname.startsWith(BASE_PATH + '/static/')")
    sw_with_base = sw_with_base.replace("url.pathname === '/'", "url.pathname === BASE_PATH + '/'")
    
    return app.response_class(
        response=sw_with_base,
        status=200,
        mimetype='application/javascript'
    )

@app.route('/static/<path:filename>')
def serve_static(filename):
    """Serve static files (icons, etc.) for PWA"""
    return send_from_directory(static_folder, filename)

def preload_metadata_for_directories(files):
    """No longer needed - markers are now centralized in /Config"""
    # This function is kept for backward compatibility but does nothing
    # since markers are now stored centrally, not per-directory
    pass

def get_enriched_file_list(files):
    """Get file list enriched with metadata
    
    Args:
        files: List of file paths
        
    Returns:
        List of file dictionaries with metadata
    """
    # Preload metadata for all directories (batch operation)
    preload_metadata_for_directories(files)
    
    # Get all marker data in one batch query
    marker_data = get_all_marker_data()
    processed_files = marker_data.get('processed', set())
    duplicate_files = marker_data.get('duplicate', set())
    
    # Get all file metadata from database in a single query
    file_metadata = load_files_with_metadata_from_store()
    
    # Build file list with metadata
    all_files = []
    for f in files:
        abs_path = os.path.abspath(f)
        rel_path = os.path.relpath(f, WATCHED_DIR) if WATCHED_DIR else f
        
        # Get metadata from database or fall back to os.path
        metadata = file_metadata.get(f)
        if metadata:
            file_size = metadata['file_size'] or 0
            file_mtime = metadata['last_modified']
        else:
            # Fallback to os.path if not in database
            try:
                file_size = os.path.getsize(f)
                file_mtime = os.path.getmtime(f)
            except OSError:
                file_size = 0
                file_mtime = 0
        
        all_files.append({
            'path': f,
            'name': os.path.basename(f),
            'relative_path': rel_path,
            'size': file_size,
            'modified': file_mtime,
            'processed': abs_path in processed_files,
            'duplicate': abs_path in duplicate_files
        })
    
    return all_files

def get_filtered_sorted_files(all_files, filter_mode, search_query, sort_mode, sort_direction):
    """Get filtered and sorted files
    
    Args:
        all_files: List of all enriched files
        filter_mode: Filter mode ('all', 'marked', 'unmarked', 'duplicates')
        search_query: Search query string
        sort_mode: Sort mode ('name', 'date', 'size')
        sort_direction: Sort direction ('asc', 'desc')
        
    Returns:
        List of filtered and sorted files
    """
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
    reverse = (sort_direction == 'desc')
    if sort_mode == 'date':
        filtered_files = sorted(filtered_files, key=lambda f: f['modified'], reverse=reverse)
    elif sort_mode == 'size':
        filtered_files = sorted(filtered_files, key=lambda f: f['size'], reverse=reverse)
    else:  # Default to 'name'
        filtered_files = sorted(filtered_files, key=lambda f: f['name'].lower(), reverse=reverse)
    
    return filtered_files


@app.route('/api/files')
def list_files():
    """API endpoint to list all comic files with pagination"""
    # Get pagination parameters
    page = request.args.get('page', 1, type=int)
    per_page = request.args.get('per_page', 100, type=int)
    
    # Get filter parameters
    search_query = request.args.get('search', '', type=str).strip()
    filter_mode = request.args.get('filter', 'all', type=str)  # 'all', 'marked', 'unmarked', 'duplicates'
    sort_mode = request.args.get('sort', 'name', type=str)  # 'name', 'date', 'size'
    sort_direction = request.args.get('direction', 'asc', type=str)  # 'asc', 'desc'
    
    # Get files from database
    files = get_comic_files()
    
    # Get enriched file list with metadata
    all_files = get_enriched_file_list(files)
    
    # Calculate unmarked count from all files (before filtering)
    unmarked_count = sum(1 for f in all_files if not f['processed'])
    
    # Get filtered and sorted files
    filtered_files = get_filtered_sorted_files(all_files, filter_mode, search_query, sort_mode, sort_direction)
    
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
        'unmarked_count': unmarked_count
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
                handle_file_rename_in_store(filepath, final_filepath)
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
                handle_file_rename_in_store(filepath, final_filepath)
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
                mark_file_processed_wrapper(final_filepath, original_filepath=filepath)
                handle_file_rename_in_store(filepath, final_filepath)
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
                mark_file_processed_wrapper(final_filepath, original_filepath=filepath)
                handle_file_rename_in_store(filepath, final_filepath)
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
                mark_file_processed_wrapper(final_filepath, original_filepath=filepath)
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
                mark_file_processed_wrapper(final_filepath, original_filepath=filepath)
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
        
        handle_file_rename_in_store(full_path, final_filepath)
        
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
        
        # Mark as processed using the final filepath, cleanup old filename if renamed
        mark_file_processed_wrapper(final_filepath, original_filepath=full_path)
        
        handle_file_rename_in_store(full_path, final_filepath)
        
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
        
        # Mark as processed using the final filepath, cleanup old filename if renamed
        mark_file_processed_wrapper(final_filepath, original_filepath=full_path)
        
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
                handle_file_rename_in_store(full_path, final_filepath)
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
                    handle_file_rename_in_store(full_path, final_filepath)
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
                mark_file_processed_wrapper(final_filepath, original_filepath=full_path)
                handle_file_rename_in_store(full_path, final_filepath)
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
                    mark_file_processed_wrapper(final_filepath, original_filepath=full_path)
                    handle_file_rename_in_store(full_path, final_filepath)
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
                mark_file_processed_wrapper(final_filepath, original_filepath=full_path)
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
                    mark_file_processed_wrapper(final_filepath, original_filepath=full_path)
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
            handle_file_rename_in_store(filepath, final_filepath)
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
            handle_file_rename_in_store(filepath, final_filepath)
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
    import uuid
    
    # Validate job_id format (should be a UUID)
    try:
        uuid.UUID(job_id)
    except ValueError:
        # Invalid job_id format - likely stale data or incorrect usage
        logging.debug(f"[API] Invalid job_id format: {job_id} (expected UUID)")
        return jsonify({'error': 'Job not found'}), 404
    
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
    import uuid
    
    # Validate job_id format (should be a UUID)
    try:
        uuid.UUID(job_id)
    except ValueError:
        # Invalid job_id format - likely stale data or incorrect usage
        logging.debug(f"[API] Invalid job_id format for delete: {job_id} (expected UUID)")
        return jsonify({'error': 'Job not found'}), 404
    
    job_manager = get_job_manager(max_workers=get_max_workers())
    
    if job_manager.delete_job(job_id):
        return jsonify({'success': True})
    else:
        return jsonify({'error': 'Job not found'}), 404


@app.route('/api/jobs/<job_id>/cancel', methods=['POST'])
def cancel_job(job_id):
    """API endpoint to cancel a job"""
    import uuid
    
    # Validate job_id format (should be a UUID)
    try:
        uuid.UUID(job_id)
    except ValueError:
        # Invalid job_id format - likely stale data or incorrect usage
        logging.debug(f"[API] Invalid job_id format for cancel: {job_id} (expected UUID)")
        return jsonify({'error': 'Job not found'}), 404
    
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
    files = get_comic_files()
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
            handle_file_rename_in_store(filepath, final_filepath)
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
    files = get_comic_files()
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
            mark_file_processed_wrapper(final_filepath, original_filepath=filepath)
            handle_file_rename_in_store(filepath, final_filepath)
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
    files = get_comic_files()
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
            mark_file_processed_wrapper(final_filepath, original_filepath=filepath)
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

@app.route('/api/settings/github-token', methods=['GET'])
def get_github_token_api():
    """API endpoint to get the GitHub token setting (masked for security)"""
    from config import get_github_token, DEFAULT_GITHUB_TOKEN
    token = get_github_token()
    # Mask the token for security - show only first 4 and last 4 characters
    masked_token = ''
    if token and len(token) > 8:
        masked_token = token[:4] + '...' + token[-4:]
    elif token:
        masked_token = '***'
    
    return jsonify({
        'token': masked_token,
        'has_token': bool(token),
        'default': DEFAULT_GITHUB_TOKEN
    })

@app.route('/api/settings/github-token', methods=['POST'])
def set_github_token_api():
    """API endpoint to set the GitHub token setting"""
    from config import set_github_token
    data = request.json
    token = data.get('token', '').strip()
    
    # Allow empty string to clear the token
    success = set_github_token(token)
    
    if success:
        logging.info("GitHub token updated")
        return jsonify({'success': True, 'has_token': bool(token)})
    else:
        return jsonify({'error': 'Failed to save GitHub token setting'}), 500

@app.route('/api/settings/github-repository', methods=['GET'])
def get_github_repository_api():
    """API endpoint to get the GitHub repository setting"""
    from config import get_github_repository, DEFAULT_GITHUB_REPOSITORY
    return jsonify({
        'repository': get_github_repository(),
        'default': DEFAULT_GITHUB_REPOSITORY
    })

@app.route('/api/settings/github-repository', methods=['POST'])
def set_github_repository_api():
    """API endpoint to set the GitHub repository setting"""
    from config import set_github_repository
    data = request.json
    repository = data.get('repository', '').strip()
    
    if not repository:
        return jsonify({'error': 'Repository value is required'}), 400
    
    # Basic validation for repository format (owner/repo)
    if '/' not in repository or repository.count('/') != 1:
        return jsonify({'error': 'Repository must be in format owner/repo'}), 400
    
    success = set_github_repository(repository)
    
    if success:
        logging.info(f"GitHub repository updated to: {repository}")
        return jsonify({'success': True, 'repository': repository})
    else:
        return jsonify({'error': 'Failed to save GitHub repository setting'}), 500

@app.route('/api/settings/github-issue-assignee', methods=['GET'])
def get_github_issue_assignee_api():
    """API endpoint to get the GitHub issue assignee setting"""
    from config import get_github_issue_assignee, DEFAULT_GITHUB_ISSUE_ASSIGNEE
    return jsonify({
        'assignee': get_github_issue_assignee(),
        'default': DEFAULT_GITHUB_ISSUE_ASSIGNEE
    })

@app.route('/api/settings/github-issue-assignee', methods=['POST'])
def set_github_issue_assignee_api():
    """API endpoint to set the GitHub issue assignee setting"""
    from config import set_github_issue_assignee
    data = request.json
    assignee = data.get('assignee', '').strip()
    
    # Allow empty string for no assignee
    success = set_github_issue_assignee(assignee)
    
    if success:
        logging.info(f"GitHub issue assignee updated to: {assignee}")
        return jsonify({'success': True, 'assignee': assignee})
    else:
        return jsonify({'error': 'Failed to save GitHub issue assignee setting'}), 500

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
    files = get_comic_files()
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
    files = get_comic_files()
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
                
                handle_file_rename_in_store(filepath, final_filepath)
                
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
                handle_file_rename_in_store(filepath, final_filepath)
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
    files = get_comic_files()
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
                
                # Mark as processed using the final filepath, cleanup old filename if renamed
                mark_file_processed_wrapper(final_filepath, original_filepath=filepath)
                
                handle_file_rename_in_store(filepath, final_filepath)
                
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
                mark_file_processed_wrapper(final_filepath, original_filepath=filepath)
                handle_file_rename_in_store(filepath, final_filepath)
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
    files = get_comic_files()
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
                
                # Mark as processed using the final filepath, cleanup old filename if renamed
                mark_file_processed_wrapper(final_filepath, original_filepath=filepath)
                
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
                mark_file_processed_wrapper(final_filepath, original_filepath=filepath)
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
        
        # Update file store
        record_file_change('remove', old_path=full_path)
        
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
    import uuid
    
    try:
        data = request.json
        if not data or 'job_id' not in data:
            return jsonify({'error': 'job_id is required'}), 400
        
        job_id = data['job_id']
        job_title = data.get('job_title', 'Processing...')
        
        # Validate job_id format (should be a UUID)
        try:
            uuid.UUID(job_id)
        except ValueError:
            # Invalid job_id format - reject the request
            logging.warning(f"[API] Attempt to set active job with invalid job_id format: {job_id} (expected UUID)")
            return jsonify({'error': 'Invalid job_id format (must be UUID)'}), 400
        
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


@app.route('/api/processing-history', methods=['GET'])
def get_processing_history_endpoint():
    """API endpoint to get processing history"""
    try:
        from unified_store import get_processing_history, get_processing_history_count
        
        # Get pagination parameters
        limit = int(request.args.get('limit', 100))
        offset = int(request.args.get('offset', 0))
        
        # Validate parameters
        if limit < 1 or limit > 1000:
            limit = 100
        if offset < 0:
            offset = 0
        
        history = get_processing_history(limit=limit, offset=offset)
        total_count = get_processing_history_count()
        
        return jsonify({
            'history': history,
            'total': total_count,
            'limit': limit,
            'offset': offset
        })
    except Exception as e:
        logging.error(f"Error getting processing history: {e}")
        return jsonify({'error': 'Failed to retrieve processing history'}), 500


@app.route('/api/events/stream', methods=['GET'])
def events_stream():
    """
    Server-Sent Events (SSE) endpoint for real-time updates
    
    Clients can subscribe to receive real-time events:
    - watcher_status: Watcher service status changed
    - file_processed: File has been processed
    - job_updated: Batch job status changed
    
    Example JavaScript usage:
        const eventSource = new EventSource('/api/events/stream');
        eventSource.onmessage = (event) => {
            const data = JSON.parse(event.data);
            console.log('Event:', data.type, data.data);
        };
    """
    broadcaster = get_broadcaster()
    client_queue = broadcaster.subscribe()
    
    def cleanup():
        broadcaster.unsubscribe(client_queue)
    
    try:
        response = app.response_class(
            event_stream_generator(client_queue, timeout=300),  # 5 minute timeout
            mimetype='text/event-stream',
            headers={
                'Cache-Control': 'no-cache',
                'X-Accel-Buffering': 'no',  # Disable nginx buffering
                'Connection': 'keep-alive'
            }
        )
        # Register cleanup on response completion
        response.call_on_close(cleanup)
        return response
    except Exception as e:
        logging.error(f"Error in SSE stream: {e}")
        cleanup()
        return jsonify({'error': str(e)}), 500

@app.route('/api/events/stats', methods=['GET'])
def events_stats():
    """Get statistics about the event broadcasting system"""
    broadcaster = get_broadcaster()
    return jsonify({
        'active_clients': broadcaster.get_client_count(),
        'total_events_broadcast': broadcaster.get_event_count()
    })

@app.route('/health', methods=['GET'])
@app.route('/api/health', methods=['GET'])
def health_check():
    """Health check endpoint for container orchestration (Docker, Kubernetes, etc.)
    
    Returns:
        200 OK - Service is healthy and operational
        503 Service Unavailable - Service is unhealthy
    """
    health_status = {
        'status': 'healthy',
        'version': __version__,
        'checks': {}
    }
    
    # Check if watched directory is accessible
    try:
        if WATCHED_DIR and os.path.exists(WATCHED_DIR) and os.path.isdir(WATCHED_DIR):
            health_status['checks']['watched_dir'] = 'ok'
        else:
            health_status['checks']['watched_dir'] = 'error'
            health_status['status'] = 'unhealthy'
    except Exception as e:
        health_status['checks']['watched_dir'] = f'error: {str(e)}'
        health_status['status'] = 'unhealthy'
    
    # Check database connectivity
    try:
        # Simple database check - get file count
        file_count = file_store.get_file_count()
        health_status['checks']['database'] = 'ok'
        health_status['file_count'] = file_count
    except Exception as e:
        health_status['checks']['database'] = f'error: {str(e)}'
        health_status['status'] = 'unhealthy'
    
    # Check watcher process status
    try:
        import subprocess
        result = subprocess.run(
            ['pgrep', '-f', 'python.*watcher.py'],
            capture_output=True,
            text=True,
            timeout=5
        )
        is_running = result.returncode == 0 and len(result.stdout.strip()) > 0
        health_status['checks']['watcher'] = 'running' if is_running else 'not_running'
        # Note: watcher not running is not necessarily unhealthy if it's disabled
    except Exception as e:
        health_status['checks']['watcher'] = f'unknown: {str(e)}'
    
    status_code = 200 if health_status['status'] == 'healthy' else 503
    return jsonify(health_status), status_code

def init_app():
    """Initialize the application on startup"""
    if not WATCHED_DIR:
        logging.error("WATCHED_DIR environment variable is not set. Exiting.")
        sys.exit(1)
    
    # Sync file store with filesystem if not recently synced
    last_sync = file_store.get_last_sync_timestamp()
    current_time = time.time()
    # Sync if never synced or last sync was more than 5 minutes ago
    if last_sync is None or (current_time - last_sync) > 300:
        logging.info("Syncing file store with filesystem...")
        added, removed, updated = file_store.sync_with_filesystem(WATCHED_DIR)
        logging.info(f"File store sync complete: +{added} new files, -{removed} deleted files, ~{updated} updated files")
    else:
        logging.info(f"File store was recently synced ({int(current_time - last_sync)}s ago), skipping sync")
    
    logging.info("Application initialization complete")

if __name__ == '__main__':
    # This block is only for development/testing purposes
    # In production, Gunicorn will import the app directly
    init_app()
    port = int(os.environ.get('WEB_PORT', 5000))
    app.run(host='0.0.0.0', port=port, debug=False)
else:
    # When imported by Gunicorn, initialize the app
    init_app()
