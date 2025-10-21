
import time
import sys
from watchdog.observers import Observer
from watchdog.events import FileSystemEventHandler
import subprocess
import os
import logging
from logging.handlers import RotatingFileHandler
from config import get_watcher_enabled, get_log_max_bytes
from markers import is_file_processed, is_file_web_modified, clear_file_web_modified
from error_handler import (
    setup_debug_logging, log_debug, log_error_with_context,
    log_function_entry, log_function_exit
)
import file_store

WATCHED_DIR = os.environ.get('WATCHED_DIR')
CONFIG_DIR = '/Config'
LOG_DIR = os.path.join(CONFIG_DIR, 'Log')
PROCESS_SCRIPT = os.environ.get('PROCESS_SCRIPT', 'process_file.py')
CACHE_UPDATE_MARKER = '.cache_update'

# Initialize file store on startup
file_store.init_db()

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

# Setup debug logging if DEBUG_MODE is enabled
setup_debug_logging()
log_debug("Watcher module initialized", watched_dir=WATCHED_DIR, process_script=PROCESS_SCRIPT)


# Debounce settings
DEBOUNCE_SECONDS = 30

def record_file_change(change_type, old_path=None, new_path=None):
    """Record a file change directly in the file store"""
    log_function_entry("record_file_change", change_type=change_type, old_path=old_path, new_path=new_path)
    
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
        
        log_function_exit("record_file_change", result="success")
    except Exception as e:
        log_error_with_context(
            e,
            context=f"Recording file change: {change_type}",
            additional_info={"old_path": old_path, "new_path": new_path}
        )
        logging.error(f"Error recording file change: {e}")

def update_watcher_timestamp():
    """Update the watcher cache invalidation timestamp"""
    log_function_entry("update_watcher_timestamp")
    
    # Ensure config directory exists
    os.makedirs(CONFIG_DIR, exist_ok=True)
    
    marker_path = os.path.join(CONFIG_DIR, CACHE_UPDATE_MARKER)
    try:
        timestamp = str(time.time())
        log_debug("Updating watcher timestamp", marker_path=marker_path, timestamp=timestamp)
        
        with open(marker_path, 'w') as f:
            f.write(timestamp)
        
        log_function_exit("update_watcher_timestamp", result="success")
    except Exception as e:
        log_error_with_context(
            e,
            context="Updating watcher timestamp",
            additional_info={"marker_path": marker_path}
        )
        logging.error(f"Error updating watcher timestamp: {e}")

def is_web_modified(filepath):
    """Check if a file was recently modified by the web interface"""
    log_debug("Checking if file is web modified", filepath=filepath)
    
    if is_file_web_modified(filepath):
        # Clear the marker and return True
        clear_file_web_modified(filepath)
        logging.info(f"Skipping {filepath} - modified by web interface")
        log_debug("File was web modified, skipping", filepath=filepath)
        return True
    
    log_debug("File was not web modified", filepath=filepath)
    return False

class ChangeHandler(FileSystemEventHandler):
    def on_moved(self, event):
        # Only process if destination is .cbr or .cbz and debounce allows
        log_debug("File move event detected", src=event.src_path, dest=event.dest_path, is_dir=event.is_directory)
        
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
                log_debug("Processing moved file", src=event.src_path, dest=event.dest_path, script=PROCESS_SCRIPT)
                
                try:
                    result = subprocess.run([sys.executable, PROCESS_SCRIPT, event.dest_path])
                    log_debug("File processing completed", dest=event.dest_path, returncode=result.returncode)
                except Exception as e:
                    log_error_with_context(
                        e,
                        context=f"Processing moved file: {event.dest_path}",
                        additional_info={"src_path": event.src_path, "dest_path": event.dest_path}
                    )
                
                # Note: process_file.py now marks files as processed itself
                self.last_processed[event.dest_path] = time.time()
            else:
                logging.info(f"Moved file not stable yet: {event.dest_path}")
                log_debug("File not stable or wrong extension", dest=event.dest_path)
    def _is_file_stable(self, path, wait_time=2, checks=3):
        """Return True if file size is unchanged for wait_time*checks seconds."""
        log_debug("Checking file stability", path=path, wait_time=wait_time, checks=checks)
        
        try:
            prev_size = None
            for check_num in range(checks):
                if not os.path.exists(path):
                    log_debug("File does not exist during stability check", path=path)
                    return False
                
                size = os.path.getsize(path)
                log_debug("File size check", path=path, check=check_num, size=size, prev_size=prev_size)
                
                if prev_size is not None and size != prev_size:
                    log_debug("File size changed, continuing checks", path=path, old_size=prev_size, new_size=size)
                    prev_size = size
                    time.sleep(wait_time)
                    continue
                
                prev_size = size
                time.sleep(wait_time)
            
            log_debug("File is stable", path=path, final_size=prev_size)
            return True
        except Exception as e:
            log_error_with_context(
                e,
                context=f"Checking file stability: {path}",
                additional_info={"path": path, "wait_time": wait_time, "checks": checks}
            )
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
        log_debug("File modified event detected", path=event.src_path, is_dir=event.is_directory)
        
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
                log_debug("Processing modified file", path=event.src_path, script=PROCESS_SCRIPT)
                
                try:
                    result = subprocess.run([sys.executable, PROCESS_SCRIPT, event.src_path])
                    log_debug("File processing completed", path=event.src_path, returncode=result.returncode)
                except Exception as e:
                    log_error_with_context(
                        e,
                        context=f"Processing modified file: {event.src_path}",
                        additional_info={"path": event.src_path}
                    )
                
                # Note: process_file.py now marks files as processed itself
                self.last_processed[event.src_path] = time.time()
            else:
                logging.info(f"File not stable yet: {event.src_path}")
                log_debug("Modified file not stable", path=event.src_path)
    def on_created(self, event):
        log_debug("File created event detected", path=event.src_path, is_dir=event.is_directory)
        
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
                log_debug("Processing created file", path=event.src_path, script=PROCESS_SCRIPT)
                
                try:
                    result = subprocess.run([sys.executable, PROCESS_SCRIPT, event.src_path])
                    log_debug("File processing completed", path=event.src_path, returncode=result.returncode)
                except Exception as e:
                    log_error_with_context(
                        e,
                        context=f"Processing created file: {event.src_path}",
                        additional_info={"path": event.src_path}
                    )
                
                # Note: process_file.py now marks files as processed itself
                # process_file.py will record cache changes (add or rename as appropriate)
                self.last_processed[event.src_path] = time.time()
            else:
                logging.info(f"File not stable yet: {event.src_path}")
                log_debug("Created file not stable", path=event.src_path)
    def on_deleted(self, event):
        log_debug("File deleted event detected", path=event.src_path, is_dir=event.is_directory)
        
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
                
                # Record the deletion in file store
                log_debug("Recording file change for deletion", path=event.src_path)
                record_file_change('remove', old_path=event.src_path)
                update_watcher_timestamp()

if __name__ == "__main__":
    log_debug("Watcher starting", watched_dir=WATCHED_DIR)
    
    if not WATCHED_DIR:
        logging.error("WATCHED_DIR environment variable is not set. Exiting.")
        log_error_with_context(
            ValueError("WATCHED_DIR not set"),
            context="Starting watcher service",
            additional_info={"env_vars": dict(os.environ)}
        )
        sys.exit(1)
    
    # Perform initial filesystem sync to populate/update file store
    logging.info("Performing initial filesystem sync...")
    log_debug("Starting filesystem sync")
    added, removed, updated = file_store.sync_with_filesystem(WATCHED_DIR)
    logging.info(f"Initial sync complete: +{added} new files, -{removed} deleted files, ~{updated} updated files")
    log_debug("Filesystem sync complete", added=added, removed=removed, updated=updated)
    
    # Update watcher timestamp after initial sync
    update_watcher_timestamp()
    
    event_handler = ChangeHandler()
    observer = Observer()
    
    log_debug("Scheduling observer", path=WATCHED_DIR, recursive=True)
    observer.schedule(event_handler, WATCHED_DIR, recursive=True)
    observer.start()
    
    logging.info(f"Watching directory: {WATCHED_DIR}")
    log_debug("Watcher observer started successfully", watched_dir=WATCHED_DIR)
    
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        logging.info("Keyboard interrupt received, stopping watcher")
        log_debug("Stopping observer due to keyboard interrupt")
        observer.stop()
    except Exception as e:
        log_error_with_context(
            e,
            context="Running watcher main loop",
            additional_info={"watched_dir": WATCHED_DIR}
        )
        observer.stop()
        raise
    
    observer.join()
    log_debug("Watcher shutdown complete")
