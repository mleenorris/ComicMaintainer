
import time
import sys
from watchdog.observers import Observer
from watchdog.events import FileSystemEventHandler
import subprocess
import os
import logging

WATCHED_DIR = os.environ.get('WATCHED_DIR', '/watched_dir')
PROCESS_SCRIPT = os.environ.get('PROCESS_SCRIPT', 'process_file.py')

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

class ChangeHandler(FileSystemEventHandler):
    def on_moved(self, event):
        # Only process if destination is .cbr or .cbz and debounce allows
        if not event.is_directory and self._should_process(event.dest_path) and self._should_process(event.src_path):
            if self._allowed_extension(event.dest_path) and self._is_file_stable(event.dest_path):
                logging.info(f"File moved/renamed: {event.src_path} -> {event.dest_path}")
                subprocess.run([sys.executable, PROCESS_SCRIPT, event.dest_path])
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
            if self._is_file_stable(event.src_path):
                logging.info(f"File modified: {event.src_path}")
                subprocess.run([sys.executable, PROCESS_SCRIPT, event.src_path])
                self.last_processed[event.src_path] = time.time()
            else:
                logging.info(f"File not stable yet: {event.src_path}")
    def on_created(self, event):
        if not event.is_directory and self._should_process(event.src_path) and self._allowed_extension(event.src_path):
            if self._is_file_stable(event.src_path):
                logging.info(f"File created: {event.src_path}")
                subprocess.run([sys.executable, PROCESS_SCRIPT, event.src_path])
                self.last_processed[event.src_path] = time.time()
            else:
                logging.info(f"File not stable yet: {event.src_path}")
    def on_deleted(self, event):
        if not event.is_directory:
            logging.info(f"File deleted: {event.src_path}")
            if event.src_path in self.last_processed:
                del self.last_processed[event.src_path]

if __name__ == "__main__":
    event_handler = ChangeHandler()
    observer = Observer()
    observer.schedule(event_handler, WATCHED_DIR, recursive=True)
    observer.start()
    logging.info(f"Watching directory: {WATCHED_DIR}")
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        observer.stop()
    observer.join()
