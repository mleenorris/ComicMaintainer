
import time
import sys
from watchdog.observers import Observer
from watchdog.events import FileSystemEventHandler
import subprocess
import os
import logging

WATCHED_DIR = os.environ.get('WATCHED_DIR')
PROCESS_SCRIPT = os.environ.get('PROCESS_SCRIPT', 'process_file.py')
WEB_MODIFIED_MARKER = '.web_modified'
PROCESSED_MARKER = '.processed_files'
CACHE_UPDATE_MARKER = '.cache_update'
CACHE_CHANGES_FILE = '.cache_changes'

# Set up logging to file and stdout
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s %(levelname)s %(message)s',
    handlers=[
        logging.FileHandler("ComicMaintainer.log"),
        logging.StreamHandler(sys.stdout)
    ]
)


# Debounce settings
DEBOUNCE_SECONDS = 30

def record_cache_change(change_type, old_path=None, new_path=None):
    """Record a file change for incremental cache updates"""
    if not WATCHED_DIR:
        return
    
    changes_file = os.path.join(WATCHED_DIR, CACHE_CHANGES_FILE)
    
    try:
        import json
        
        change_entry = {
            'type': change_type,
            'old_path': old_path,
            'new_path': new_path,
            'timestamp': time.time()
        }
        
        with open(changes_file, 'a') as f:
            f.write(json.dumps(change_entry) + '\n')
        
        logging.info(f"Recorded cache change: {change_type} {old_path or ''} -> {new_path or ''}")
    except Exception as e:
        logging.error(f"Error recording cache change: {e}")

def update_watcher_timestamp():
    """Update the watcher cache invalidation timestamp"""
    if not WATCHED_DIR:
        return
    
    marker_path = os.path.join(WATCHED_DIR, CACHE_UPDATE_MARKER)
    try:
        with open(marker_path, 'w') as f:
            f.write(str(time.time()))
    except Exception as e:
        logging.error(f"Error updating watcher timestamp: {e}")

def is_web_modified(filepath):
    """Check if a file was recently modified by the web interface"""
    marker_path = os.path.join(os.path.dirname(filepath), WEB_MODIFIED_MARKER)
    if not os.path.exists(marker_path):
        return False
    
    try:
        with open(marker_path, 'r') as f:
            modified_files = f.read().splitlines()
            filename = os.path.basename(filepath)
            if filename in modified_files:
                # Remove the file from the marker
                modified_files = [f for f in modified_files if f != filename]
                with open(marker_path, 'w') as wf:
                    wf.write('\n'.join(modified_files))
                logging.info(f"Skipping {filepath} - modified by web interface")
                return True
    except Exception as e:
        logging.error(f"Error checking web modified marker: {e}")
    
    return False

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

class ChangeHandler(FileSystemEventHandler):
    def on_moved(self, event):
        # Only process if destination is .cbr or .cbz and debounce allows
        if not event.is_directory and self._should_process(event.dest_path) and self._should_process(event.src_path):
            if is_web_modified(event.dest_path):
                self.last_processed[event.dest_path] = time.time()
                return
            if is_file_processed(event.dest_path):
                logging.info(f"Skipping {event.dest_path} - already processed")
                self.last_processed[event.dest_path] = time.time()
                return
            if self._allowed_extension(event.dest_path) and self._is_file_stable(event.dest_path):
                logging.info(f"File moved/renamed: {event.src_path} -> {event.dest_path}")
                result = subprocess.run([sys.executable, PROCESS_SCRIPT, event.dest_path])
                # Note: process_file.py now marks files as processed itself
                self.last_processed[event.dest_path] = time.time()
            else:
                logging.info(f"Moved file not stable yet: {event.dest_path}")
    def _is_file_stable(self, path, wait_time=2, checks=3):
        """Return True if file size is unchanged for wait_time*checks seconds."""
        try:
            prev_size = None
            for _ in range(checks):
                if not os.path.exists(path):
                    return False
                size = os.path.getsize(path)
                if prev_size is not None and size != prev_size:
                    prev_size = size
                    time.sleep(wait_time)
                    continue
                prev_size = size
                time.sleep(wait_time)
            return True
        except Exception as e:
            logging.info(f"Error checking file stability for {path}: {e}")
            return False
    def __init__(self):
        super().__init__()
        self.last_processed = {}  # {filepath: timestamp}
    def _allowed_extension(self, path):
        if not (path.lower().endswith('.cbr') or path.lower().endswith('.cbz')):
            return False
        else:
            return True
    def _should_process(self, path):
        now = time.time()
        last = self.last_processed.get(path, 0)
        if now - last > DEBOUNCE_SECONDS:
            return True
        return False

    def on_modified(self, event):
        if not event.is_directory and self._should_process(event.src_path) and self._allowed_extension(event.src_path):
            if is_web_modified(event.src_path):
                self.last_processed[event.src_path] = time.time()
                return
            if is_file_processed(event.src_path):
                logging.info(f"Skipping {event.src_path} - already processed")
                self.last_processed[event.src_path] = time.time()
                return
            if self._is_file_stable(event.src_path):
                logging.info(f"File modified: {event.src_path}")
                result = subprocess.run([sys.executable, PROCESS_SCRIPT, event.src_path])
                # Note: process_file.py now marks files as processed itself
                self.last_processed[event.src_path] = time.time()
            else:
                logging.info(f"File not stable yet: {event.src_path}")
    def on_created(self, event):
        if not event.is_directory and self._should_process(event.src_path) and self._allowed_extension(event.src_path):
            if is_web_modified(event.src_path):
                self.last_processed[event.src_path] = time.time()
                return
            if is_file_processed(event.src_path):
                logging.info(f"Skipping {event.src_path} - already processed")
                self.last_processed[event.src_path] = time.time()
                return
            if self._is_file_stable(event.src_path):
                logging.info(f"File created: {event.src_path}")
                result = subprocess.run([sys.executable, PROCESS_SCRIPT, event.src_path])
                # Note: process_file.py now marks files as processed itself
                # process_file.py will record cache changes (add or rename as appropriate)
                self.last_processed[event.src_path] = time.time()
            else:
                logging.info(f"File not stable yet: {event.src_path}")
    def on_deleted(self, event):
        if not event.is_directory:
            logging.info(f"File deleted: {event.src_path}")
            if event.src_path in self.last_processed:
                del self.last_processed[event.src_path]
            
            # Record the deletion for incremental cache update
            if self._allowed_extension(event.src_path):
                record_cache_change('remove', old_path=event.src_path)
                update_watcher_timestamp()

if __name__ == "__main__":
    event_handler = ChangeHandler()
    observer = Observer()
    if WATCHED_DIR:
        observer.schedule(event_handler, WATCHED_DIR, recursive=True)
        observer.start()
    else:
        logging.error("WATCHED_DIR environment variable is not set. Exiting.")
        sys.exit(1)
    logging.info(f"Watching directory: {WATCHED_DIR}")
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        observer.stop()
    observer.join()
