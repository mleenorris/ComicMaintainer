import os
import sys
import logging
import json
from flask import Flask, render_template, jsonify, request, send_from_directory
from comicapi.comicarchive import ComicArchive
import glob
import threading
import time
from config import get_filename_format, set_filename_format, DEFAULT_FILENAME_FORMAT

# Set up logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s %(levelname)s %(message)s',
    handlers=[
        logging.FileHandler("ComicMaintainer.log"),
        logging.StreamHandler(sys.stdout)
    ]
)

app = Flask(__name__)

WATCHED_DIR = os.environ.get('WATCHED_DIR')
WEB_MODIFIED_MARKER = '.web_modified'
PROCESSED_MARKER = '.processed_files'
DUPLICATE_MARKER = '.duplicate_files'

# Global lock file to mark files modified by web interface
web_modified_files = set()
lock = threading.Lock()

# Cache for file list to improve performance on large libraries
file_list_cache = {
    'files': None,
    'timestamp': 0,
    'cache_duration': 30  # Cache for 30 seconds
}
cache_lock = threading.Lock()

def mark_web_modified(filepath):
    """Mark a file as modified by the web interface"""
    with lock:
        web_modified_files.add(os.path.abspath(filepath))
        # Write marker file
        marker_path = os.path.join(os.path.dirname(filepath), WEB_MODIFIED_MARKER)
        with open(marker_path, 'a') as f:
            f.write(f"{os.path.basename(filepath)}\n")

def is_web_modified(filepath):
    """Check if a file was modified by the web interface"""
    with lock:
        return os.path.abspath(filepath) in web_modified_files

def clear_web_modified(filepath):
    """Clear the web modified marker for a file"""
    with lock:
        abs_path = os.path.abspath(filepath)
        if abs_path in web_modified_files:
            web_modified_files.discard(abs_path)

def mark_file_processed(filepath, original_filepath=None):
    """Mark a file as processed, optionally cleaning up old filename if renamed"""
    marker_path = os.path.join(os.path.dirname(filepath), PROCESSED_MARKER)
    try:
        filename = os.path.basename(filepath)
        # Read existing processed files
        processed_files = set()
        if os.path.exists(marker_path):
            with open(marker_path, 'r') as f:
                processed_files = set(f.read().splitlines())
        
        # If file was renamed, remove the old filename from the marker
        if original_filepath and original_filepath != filepath:
            original_filename = os.path.basename(original_filepath)
            if original_filename in processed_files:
                processed_files.discard(original_filename)
                logging.info(f"Removed old filename '{original_filename}' from processed marker after rename")
        
        # Add current file
        processed_files.add(filename)
        
        # Write back
        with open(marker_path, 'w') as f:
            f.write('\n'.join(sorted(processed_files)))
        logging.info(f"Marked {filepath} as processed")
    except Exception as e:
        logging.error(f"Error marking file as processed: {e}")

def is_file_processed(filepath):
    """Check if a file has been processed"""
    marker_path = os.path.join(os.path.dirname(filepath), PROCESSED_MARKER)
    if not os.path.exists(marker_path):
        return False
    
    try:
        with open(marker_path, 'r') as f:
            processed_files = set(f.read().splitlines())
            filename = os.path.basename(filepath)
            return filename in processed_files
    except Exception as e:
        logging.error(f"Error checking if file is processed: {e}")
        return False

def mark_file_duplicate(filepath):
    """Mark a file as a duplicate"""
    marker_path = os.path.join(os.path.dirname(filepath), DUPLICATE_MARKER)
    try:
        filename = os.path.basename(filepath)
        # Read existing duplicate files
        duplicate_files = set()
        if os.path.exists(marker_path):
            with open(marker_path, 'r') as f:
                duplicate_files = set(f.read().splitlines())
        
        # Add current file
        duplicate_files.add(filename)
        
        # Write back
        with open(marker_path, 'w') as f:
            f.write('\n'.join(sorted(duplicate_files)))
        logging.info(f"Marked {filepath} as duplicate")
    except Exception as e:
        logging.error(f"Error marking file as duplicate: {e}")

def is_file_duplicate(filepath):
    """Check if a file is marked as a duplicate"""
    marker_path = os.path.join(os.path.dirname(filepath), DUPLICATE_MARKER)
    if not os.path.exists(marker_path):
        return False
    
    try:
        with open(marker_path, 'r') as f:
            duplicate_files = set(f.read().splitlines())
            filename = os.path.basename(filepath)
            return filename in duplicate_files
    except Exception as e:
        logging.error(f"Error checking if file is duplicate: {e}")
        return False

def cleanup_web_markers():
    """Periodically clean up old web modified markers"""
    while True:
        time.sleep(300)  # Run every 5 minutes
        with lock:
            # Keep only the last 100 files
            if len(web_modified_files) > 100:
                to_remove = list(web_modified_files)[:len(web_modified_files) - 100]
                for f in to_remove:
                    web_modified_files.discard(f)

# Start cleanup thread
cleanup_thread = threading.Thread(target=cleanup_web_markers, daemon=True)
cleanup_thread.start()

def get_comic_files(use_cache=True):
    """Get all comic files in the watched directory with optional caching"""
    if not WATCHED_DIR:
        return []
    
    # Check cache if enabled
    if use_cache:
        with cache_lock:
            current_time = time.time()
            if (file_list_cache['files'] is not None and 
                current_time - file_list_cache['timestamp'] < file_list_cache['cache_duration']):
                return file_list_cache['files']
    
    # Build file list
    files = []
    for ext in ['*.cbz', '*.cbr', '*.CBZ', '*.CBR']:
        files.extend(glob.glob(os.path.join(WATCHED_DIR, '**', ext), recursive=True))
    
    sorted_files = sorted(files)
    
    # Update cache
    if use_cache:
        with cache_lock:
            file_list_cache['files'] = sorted_files
            file_list_cache['timestamp'] = time.time()
    
    return sorted_files

def clear_file_cache():
    """Clear the file list cache"""
    with cache_lock:
        file_list_cache['files'] = None
        file_list_cache['timestamp'] = 0

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
        mark_web_modified(filepath)
        
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

@app.route('/api/files')
def list_files():
    """API endpoint to list all comic files with pagination"""
    # Get pagination parameters
    page = request.args.get('page', 1, type=int)
    per_page = request.args.get('per_page', 100, type=int)
    refresh = request.args.get('refresh', 'false').lower() == 'true'
    
    # Limit per_page to reasonable values
    per_page = min(max(per_page, 10), 500)
    
    # Get files with optional cache refresh
    files = get_comic_files(use_cache=not refresh)
    total_files = len(files)
    
    # Calculate pagination
    total_pages = (total_files + per_page - 1) // per_page if total_files > 0 else 1
    page = max(1, min(page, total_pages))
    
    start_idx = (page - 1) * per_page
    end_idx = start_idx + per_page
    
    paginated_files = files[start_idx:end_idx]
    
    result = []
    for f in paginated_files:
        rel_path = os.path.relpath(f, WATCHED_DIR) if WATCHED_DIR else f
        result.append({
            'path': f,
            'name': os.path.basename(f),
            'relative_path': rel_path,
            'size': os.path.getsize(f),
            'modified': os.path.getmtime(f),
            'processed': is_file_processed(f),
            'duplicate': is_file_duplicate(f)
        })
    
    return jsonify({
        'files': result,
        'page': page,
        'per_page': per_page,
        'total_files': total_files,
        'total_pages': total_pages
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
    """API endpoint to update tags for multiple files"""
    data = request.json
    files = data.get('files', [])
    tag_updates = data.get('tags', {})
    
    if not files or not tag_updates:
        return jsonify({'error': 'Files and tags are required'}), 400
    
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

@app.route('/api/process-all', methods=['POST'])
def process_all_files():
    """API endpoint to process all files in the watched directory"""
    from process_file import process_file
    
    files = get_comic_files()
    results = []
    
    for filepath in files:
        try:
            # Mark as web modified to prevent watcher from processing
            mark_web_modified(filepath)
            
            # Process the file and get the final filepath (may be renamed)
            final_filepath = process_file(filepath, fixtitle=True, fixseries=True, fixfilename=True)
            
            # Mark as processed using the final filepath, cleanup old filename if renamed
            mark_file_processed(final_filepath, original_filepath=filepath)
            
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

@app.route('/api/process-file/<path:filepath>', methods=['POST'])
def process_single_file(filepath):
    """API endpoint to process a single file"""
    from process_file import process_file
    
    full_path = os.path.join(WATCHED_DIR, filepath) if WATCHED_DIR else filepath
    
    if not os.path.exists(full_path):
        return jsonify({'error': 'File not found'}), 404
    
    try:
        # Mark as web modified to prevent watcher from processing
        mark_web_modified(full_path)
        
        # Process the file and get the final filepath (may be renamed)
        final_filepath = process_file(full_path, fixtitle=True, fixseries=True, fixfilename=True)
        
        # Mark as processed using the final filepath, cleanup old filename if renamed
        mark_file_processed(final_filepath, original_filepath=full_path)
        
        logging.info(f"Processed file via web interface: {full_path} -> {final_filepath}")
        return jsonify({'success': True})
    except Exception as e:
        logging.error(f"Error processing file {full_path}: {e}")
        return jsonify({'error': str(e)}), 500

@app.route('/api/process-selected', methods=['POST'])
def process_selected_files():
    """API endpoint to process selected files"""
    from process_file import process_file
    
    data = request.json
    file_list = data.get('files', [])
    
    if not file_list:
        return jsonify({'error': 'No files specified'}), 400
    
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
            # Mark as web modified to prevent watcher from processing
            mark_web_modified(full_path)
            
            # Process the file and get the final filepath (may be renamed)
            final_filepath = process_file(full_path, fixtitle=True, fixseries=True, fixfilename=True)
            
            # Mark as processed using the final filepath, cleanup old filename if renamed
            mark_file_processed(final_filepath, original_filepath=full_path)
            
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
    """API endpoint to process only unmarked files"""
    from process_file import process_file
    
    files = get_comic_files(use_cache=False)
    unmarked_files = []
    
    # Filter to only unmarked files
    for filepath in files:
        if not is_file_processed(filepath):
            unmarked_files.append(filepath)
    
    results = []
    
    for filepath in unmarked_files:
        try:
            # Mark as web modified to prevent watcher from processing
            mark_web_modified(filepath)
            
            # Process the file and get the final filepath (may be renamed)
            final_filepath = process_file(filepath, fixtitle=True, fixseries=True, fixfilename=True)
            
            # Mark as processed using the final filepath, cleanup old filename if renamed
            mark_file_processed(final_filepath, original_filepath=filepath)
            
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

if __name__ == '__main__':
    if not WATCHED_DIR:
        logging.error("WATCHED_DIR environment variable is not set. Exiting.")
        sys.exit(1)
    
    port = int(os.environ.get('WEB_PORT', 5000))
    app.run(host='0.0.0.0', port=port, debug=False)
