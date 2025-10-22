#!/usr/bin/env python3
"""
Test script to verify async file sync functionality
"""
import os
import sys
import time
import tempfile
import shutil
import threading
import logging

# Setup logging
logging.basicConfig(level=logging.INFO, format='%(asctime)s [TEST] %(message)s')

# Add src directory to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

# Set environment variables before importing
test_dir = tempfile.mkdtemp()
config_dir = tempfile.mkdtemp()
os.environ['WATCHED_DIR'] = test_dir
os.environ['CONFIG_DIR'] = config_dir

# Override the unified_store CONFIG_DIR
import unified_store
unified_store.CONFIG_DIR = config_dir
unified_store.STORE_DIR = os.path.join(config_dir, 'store')
unified_store.DB_PATH = os.path.join(unified_store.STORE_DIR, 'comicmaintainer.db')

# Mock the sync status from web_app since we can't import it
_sync_status = {
    'in_progress': False,
    'completed': False,
    'error': None,
    'added': 0,
    'removed': 0,
    'updated': 0,
    'start_time': None,
    'end_time': None
}
_sync_status_lock = threading.Lock()

def _update_sync_status(**kwargs):
    """Thread-safe update of sync status"""
    with _sync_status_lock:
        _sync_status.update(kwargs)

def _get_sync_status():
    """Thread-safe read of sync status"""
    with _sync_status_lock:
        return _sync_status.copy()

def _async_sync_filesystem(watched_dir):
    """Background task to sync file store with filesystem"""
    try:
        _update_sync_status(in_progress=True, start_time=time.time())
        logging.info("Starting asynchronous file store sync...")
        
        added, removed, updated = unified_store.sync_with_filesystem(watched_dir)
        
        _update_sync_status(
            in_progress=False,
            completed=True,
            added=added,
            removed=removed,
            updated=updated,
            end_time=time.time()
        )
        logging.info(f"File store sync complete: +{added} new files, -{removed} deleted files, ~{updated} updated files")
    except Exception as e:
        _update_sync_status(
            in_progress=False,
            completed=True,
            error=str(e),
            end_time=time.time()
        )
        logging.error(f"Error during async file store sync: {e}")

def init_app_mock(watched_dir):
    """Mock init_app function"""
    # Initialize database
    unified_store.init_db()
    
    # Check if sync is needed
    last_sync = unified_store.get_last_sync_timestamp()
    current_time = time.time()
    
    # Sync if never synced or last sync was more than 5 minutes ago
    if last_sync is None or (current_time - last_sync) > 300:
        # Start async sync in background thread
        logging.info("Starting file store sync in background...")
        sync_thread = threading.Thread(target=_async_sync_filesystem, args=(watched_dir,), daemon=True)
        sync_thread.start()
        return sync_thread
    else:
        # Mark as completed since no sync needed
        _update_sync_status(completed=True)
        logging.info(f"File store was recently synced ({int(current_time - last_sync)}s ago), skipping sync")
        return None

def create_test_files(directory, count=10):
    """Create test comic files"""
    files = []
    for i in range(count):
        filepath = os.path.join(directory, f'test_comic_{i:03d}.cbz')
        with open(filepath, 'w') as f:
            f.write(f'Test content {i}')
        files.append(filepath)
    return files

def test_async_sync():
    """Test that async sync works correctly"""
    print("=" * 60)
    print("Testing Async File Sync")
    print("=" * 60)
    
    # Create test directory and files
    print(f"\nTest directory: {test_dir}")
    print(f"Config directory: {config_dir}")
    
    print("\nCreating 100 test files...")
    test_files = create_test_files(test_dir, 100)
    print(f"Created {len(test_files)} test files")
    
    # Clear any previous sync timestamp to force a sync
    try:
        unified_store.set_metadata('last_sync_timestamp', '0')
    except:
        pass
    
    # Initialize the app (should start async sync)
    print("\nInitializing app (should start async sync)...")
    start_time = time.time()
    sync_thread = init_app_mock(test_dir)
    init_duration = time.time() - start_time
    print(f"init_app() completed in {init_duration:.3f} seconds")
    
    # Check if init was fast (should be < 1 second if async)
    if init_duration < 1.0:
        print("✓ Init was fast (< 1 second) - async sync is working!")
    else:
        print("✗ Init was slow (>= 1 second) - sync might be blocking")
    
    # Check sync status
    print("\nChecking sync status...")
    status = _get_sync_status()
    print(f"Sync in progress: {status['in_progress']}")
    print(f"Sync completed: {status['completed']}")
    
    # Wait for sync to complete
    if status['in_progress']:
        print("\nWaiting for sync to complete...")
        max_wait = 30  # Maximum 30 seconds
        wait_start = time.time()
        while time.time() - wait_start < max_wait:
            time.sleep(0.5)
            status = _get_sync_status()
            if status['completed']:
                break
        
        if status['completed']:
            sync_duration = status['end_time'] - status['start_time']
            print(f"✓ Sync completed in {sync_duration:.3f} seconds")
            print(f"  Added: {status['added']}")
            print(f"  Removed: {status['removed']}")
            print(f"  Updated: {status['updated']}")
            if status['error']:
                print(f"  Error: {status['error']}")
        else:
            print("✗ Sync did not complete within 30 seconds")
            return False
    elif status['completed']:
        print("✓ Sync already completed")
        if status.get('start_time'):
            sync_duration = status['end_time'] - status['start_time']
            print(f"  Duration: {sync_duration:.3f} seconds")
        print(f"  Added: {status['added']}")
        print(f"  Removed: {status['removed']}")
        print(f"  Updated: {status['updated']}")
    
    # Verify files were synced
    print("\nVerifying file store...")
    file_count = unified_store.get_file_count()
    print(f"Files in store: {file_count}")
    
    if file_count == len(test_files):
        print(f"✓ All {len(test_files)} files synced correctly")
    else:
        print(f"✗ Expected {len(test_files)} files, got {file_count}")
        return False
    
    # Test that second init is fast (skip sync)
    print("\nTesting second init (should skip sync)...")
    start_time = time.time()
    sync_thread2 = init_app_mock(test_dir)
    init_duration = time.time() - start_time
    print(f"Second init_app() completed in {init_duration:.3f} seconds")
    
    if init_duration < 0.5:
        print("✓ Second init was very fast - skip logic working!")
    else:
        print("! Second init was slower than expected")
    
    print("\n" + "=" * 60)
    print("Test completed successfully!")
    print("=" * 60)
    
    return True

if __name__ == '__main__':
    try:
        success = test_async_sync()
        sys.exit(0 if success else 1)
    finally:
        # Cleanup
        try:
            shutil.rmtree(test_dir)
            shutil.rmtree(config_dir)
        except:
            pass
