
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
DEBOUNCE_SECONDS = 2

class ChangeHandler(FileSystemEventHandler):
    def __init__(self):
        super().__init__()
        self.last_processed = {}  # {filepath: timestamp}

    def _should_process(self, path):
        now = time.time()
        last = self.last_processed.get(path, 0)
        if now - last > DEBOUNCE_SECONDS:
            self.last_processed[path] = now
            return True
        return False

    def on_modified(self, event):
        if not event.is_directory and self._should_process(event.src_path):
            logging.info(f"File modified: {event.src_path}")
            subprocess.run([sys.executable, PROCESS_SCRIPT, event.src_path])
    def on_created(self, event):
        if not event.is_directory and self._should_process(event.src_path):
            logging.info(f"File created: {event.src_path}")
            subprocess.run([sys.executable, PROCESS_SCRIPT, event.src_path])
    def on_deleted(self, event):
        if not event.is_directory:
            logging.info(f"File deleted: {event.src_path}")

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
