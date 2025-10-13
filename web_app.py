import os
import sys
import logging
import json
from flask import Flask, render_template, jsonify, request, send_from_directory
from comicapi.comicarchive import ComicArchive
import glob
import threading
import time

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

# Global lock file to mark files modified by web interface
web_modified_files = set()
lock = threading.Lock()

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

def get_comic_files():
    """Get all comic files in the watched directory"""
    if not WATCHED_DIR:
        return []
    
    files = []
    for ext in ['*.cbz', '*.cbr', '*.CBZ', '*.CBR']:
        files.extend(glob.glob(os.path.join(WATCHED_DIR, '**', ext), recursive=True))
    
    return sorted(files)

def get_file_tags(filepath):
    """Get tags from a comic file"""
    try:
        ca = ComicArchive(filepath)
        tags = ca.read_tags('cr')
        
        # Convert tags to dictionary
        tag_dict = {
            'title': tags.title or '',
            'series': tags.series or '',
            'issue': tags.issue or '',
            'volume': tags.volume or '',
            'publisher': tags.publisher or '',
            'year': tags.year or '',
            'month': tags.month or '',
            'writer': tags.writer or '',
            'penciller': tags.penciller or '',
            'inker': tags.inker or '',
            'colorist': tags.colorist or '',
            'letterer': tags.letterer or '',
            'cover_artist': tags.cover_artist or '',
            'editor': tags.editor or '',
            'summary': tags.summary or '',
            'notes': tags.notes or '',
            'genre': tags.genre or '',
            'tags': tags.tags or '',
            'web': tags.web or '',
            'page_count': tags.page_count or 0,
        }
        return tag_dict
    except Exception as e:
        logging.error(f"Error reading tags from {filepath}: {e}")
        return None

def update_file_tags(filepath, tag_updates):
    """Update tags in a comic file"""
    try:
        ca = ComicArchive(filepath)
        tags = ca.read_tags('cr')
        
        # Update tags
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
    """API endpoint to list all comic files"""
    files = get_comic_files()
    result = []
    for f in files:
        rel_path = os.path.relpath(f, WATCHED_DIR) if WATCHED_DIR else f
        result.append({
            'path': f,
            'name': os.path.basename(f),
            'relative_path': rel_path,
            'size': os.path.getsize(f),
            'modified': os.path.getmtime(f)
        })
    return jsonify(result)

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
            
            # Process the file
            process_file(filepath, fixtitle=True, fixseries=True, fixfilename=True)
            results.append({
                'file': os.path.basename(filepath),
                'success': True
            })
            logging.info(f"Processed file via web interface: {filepath}")
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
        
        # Process the file
        process_file(full_path, fixtitle=True, fixseries=True, fixfilename=True)
        logging.info(f"Processed file via web interface: {full_path}")
        return jsonify({'success': True})
    except Exception as e:
        logging.error(f"Error processing file {full_path}: {e}")
        return jsonify({'error': str(e)}), 500

if __name__ == '__main__':
    if not WATCHED_DIR:
        logging.error("WATCHED_DIR environment variable is not set. Exiting.")
        sys.exit(1)
    
    port = int(os.environ.get('WEB_PORT', 5000))
    app.run(host='0.0.0.0', port=port, debug=False)
