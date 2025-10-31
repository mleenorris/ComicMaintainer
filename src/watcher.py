
import time
import sys
from watchdog.observers import Observer
from watchdog.events import FileSystemEventHandler
import subprocess
import os
import logging
from config import get_watcher_enabled
from markers import is_file_processed, is_file_web_modified, clear_file_web_modified
from error_handler import (
    setup_debug_logging, log_debug, log_error_with_context,
    log_function_entry, log_function_exit
)
import file_store
from logging_setup import setup_logging
from file_operations import record_file_change

WATCHED_DIR = os.environ.get('WATCHED_DIR')
PROCESS_SCRIPT = os.environ.get('PROCESS_SCRIPT', 'process_file.py')

# Initialize file store on startup
file_store.init_db()

# Set up logging for this module
setup_logging('WATCHER', use_rotation=True)

# Setup debug logging if DEBUG_MODE is enabled
setup_debug_logging()
log_debug("Watcher module initialized", watched_dir=WATCHED_DIR, process_script=PROCESS_SCRIPT)


# Debounce settings
DEBOUNCE_SECONDS = 30



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
    def _is_file_stable(self, path, wait_time=2, checks=2):
        """Return True if file size is unchanged for wait_time*checks seconds.
        
        Optimized to use 2 checks instead of 3, reducing wait time from 6s to 4s
        while still ensuring files are stable before processing.
        
        Note: The time.sleep() calls here are NOT polling - they are intentional
        debouncing delays to ensure files have finished copying/writing before
        processing. This is a necessary wait for file stability verification.
        """
        log_debug("Checking file stability", path=path, wait_time=wait_time, checks=checks)
        
        try:
            # First check - get initial file size
            if not os.path.exists(path):
                log_debug("File does not exist during stability check", path=path)
                return False
            
            prev_size = os.path.getsize(path)
            log_debug("File size check", path=path, check=0, size=prev_size, prev_size=None)
            
            # Wait and check again
            for check_num in range(1, checks + 1):
                time.sleep(wait_time)
                
                if not os.path.exists(path):
                    log_debug("File disappeared during stability check", path=path)
                    return False
                
                size = os.path.getsize(path)
                log_debug("File size check", path=path, check=check_num, size=size, prev_size=prev_size)
                
                if size != prev_size:
                    log_debug("File size changed, not stable", path=path, old_size=prev_size, new_size=size)
                    return False
            
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
        self._extension_cache = {}  # Cache for file extension checks
        
    def _allowed_extension(self, path):
        """Check if file has allowed extension (.cbr or .cbz) with caching"""
        # Check cache first
        if path in self._extension_cache:
            return self._extension_cache[path]
        
        # Compute and cache result
        result = path.lower().endswith('.cbr') or path.lower().endswith('.cbz')
        self._extension_cache[path] = result
        
        # Limit cache size to prevent memory growth (keep last 1000 entries)
        if len(self._extension_cache) > 1000:
            # Remove oldest 200 entries (simple FIFO-like cleanup)
            keys_to_remove = list(self._extension_cache.keys())[:200]
            for key in keys_to_remove:
                del self._extension_cache[key]
        
        return result
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
            
            # Skip store update if file was deleted via web interface
            if self._allowed_extension(event.src_path):
                if is_web_modified(event.src_path):
                    logging.info(f"Skipping file store update for {event.src_path} - deleted by web interface")
                    clear_file_web_modified(event.src_path)
                    return
                
                # Record the deletion in file store
                log_debug("Recording file change for deletion", path=event.src_path)
                record_file_change('remove', old_path=event.src_path)

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
    
    event_handler = ChangeHandler()
    observer = Observer()
    
    log_debug("Scheduling observer", path=WATCHED_DIR, recursive=True)
    observer.schedule(event_handler, WATCHED_DIR, recursive=True)
    observer.start()
    
    logging.info(f"Watching directory: {WATCHED_DIR}")
    log_debug("Watcher observer started successfully", watched_dir=WATCHED_DIR)
    
    # Use an Event object instead of sleep polling
    # Event.wait() blocks efficiently without consuming CPU, unlike a while True + sleep(1) loop
    # The watchdog observer runs in its own thread and handles file events asynchronously
    import threading
    shutdown_event = threading.Event()
    
    try:
        # Wait indefinitely for shutdown signal (Ctrl+C or exception)
        # This is event-driven: the thread sleeps until interrupted, using no CPU
        shutdown_event.wait()
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
