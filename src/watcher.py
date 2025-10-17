
import time
import sys
import signal
from watchdog.observers import Observer
from watchdog.events import FileSystemEventHandler
import subprocess
import os
import logging
from logging.handlers import RotatingFileHandler
from config import get_watcher_enabled, get_log_max_bytes
from markers import is_file_processed, is_file_web_modified, clear_file_web_modified

WATCHED_DIR = os.environ.get('WATCHED_DIR')
CONFIG_DIR = '/Config'
LOG_DIR = os.path.join(CONFIG_DIR, 'Log')
PROCESS_SCRIPT = os.environ.get('PROCESS_SCRIPT', 'process_file.py')
CACHE_UPDATE_MARKER = '.cache_update'
CACHE_CHANGES_FILE = '.cache_changes'

# Ensure log directory exists
os.makedirs(LOG_DIR, exist_ok=True)

# Set up logging to file and stdout with rotation
# Initialize basic logging first to avoid issues with get_log_max_bytes() logging errors
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s [WATCHER] %(levelname)s %(message)s',
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
log_handler.setFormatter(logging.Formatter('%(asctime)s [WATCHER] %(levelname)s %(message)s'))

# Add the file handler to the root logger
logging.getLogger().addHandler(log_handler)


# Debounce settings
DEBOUNCE_SECONDS = 30

def record_cache_change(change_type, old_path=None, new_path=None):
    """Record a file change for incremental cache updates"""
    # Ensure config directory exists
    os.makedirs(CONFIG_DIR, exist_ok=True)
    
    changes_file = os.path.join(CONFIG_DIR, CACHE_CHANGES_FILE)
    
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
    # Ensure config directory exists
    os.makedirs(CONFIG_DIR, exist_ok=True)
    
    marker_path = os.path.join(CONFIG_DIR, CACHE_UPDATE_MARKER)
    try:
        with open(marker_path, 'w') as f:
            f.write(str(time.time()))
    except Exception as e:
        logging.error(f"Error updating watcher timestamp: {e}")

def is_web_modified(filepath):
    """Check if a file was recently modified by the web interface"""
    if is_file_web_modified(filepath):
        # Clear the marker and return True
        clear_file_web_modified(filepath)
        logging.info(f"Skipping {filepath} - modified by web interface")
        return True
    return False

class ChangeHandler(FileSystemEventHandler):
    def on_moved(self, event):
        # Only process if destination is .cbr or .cbz and debounce allows
        if not event.is_directory and self._should_process(event.dest_path) and self._should_process(event.src_path):
            if not get_watcher_enabled():
                logging.debug(f"Watcher disabled, skipping: {event.dest_path}")
                return
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
            if not get_watcher_enabled():
                logging.debug(f"Watcher disabled, skipping: {event.src_path}")
                return
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
            if not get_watcher_enabled():
                logging.debug(f"Watcher disabled, skipping: {event.src_path}")
                return
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
            
            # Skip cache update if file was deleted via web interface
            if self._allowed_extension(event.src_path):
                if is_web_modified(event.src_path):
                    logging.info(f"Skipping cache update for {event.src_path} - deleted by web interface")
                    clear_file_web_modified(event.src_path)
                    return
                
                # Record the deletion for incremental cache update
                record_cache_change('remove', old_path=event.src_path)
                update_watcher_timestamp()

if __name__ == "__main__":
    if not WATCHED_DIR:
        logging.error("WATCHED_DIR environment variable is not set. Exiting.")
        sys.exit(1)
    
    event_handler = ChangeHandler()
    observer = Observer()
    observer.schedule(event_handler, WATCHED_DIR, recursive=True)
    observer.start()
    logging.info(f"Watching directory: {WATCHED_DIR}")
    
    # Set up signal handlers for graceful shutdown
    def signal_handler(signum, frame):
        signal_name = signal.Signals(signum).name
        logging.info(f"Received {signal_name} signal, shutting down watcher...")
        observer.stop()
    
    signal.signal(signal.SIGINT, signal_handler)
    signal.signal(signal.SIGTERM, signal_handler)
    
    # Wait for observer thread to finish (event-driven, no polling)
    # This blocks until observer.stop() is called by signal handler
    try:
        observer.join()
    except KeyboardInterrupt:
        # Fallback for systems where SIGINT handler doesn't work
        logging.info("Keyboard interrupt received, shutting down watcher...")
        observer.stop()
        observer.join()
    
    logging.info("Watcher stopped gracefully")
