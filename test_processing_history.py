#!/usr/bin/env python
"""Test processing history functionality"""
import sys
import os
import tempfile
import time

# Create temp directory first
temp_dir = tempfile.mkdtemp()
print(f"Using temp directory: {temp_dir}")

# Add src to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

# Monkey-patch the CONFIG_DIR before importing unified_store
import unified_store
unified_store.CONFIG_DIR = temp_dir
unified_store.STORE_DIR = os.path.join(temp_dir, 'store')
unified_store.DB_PATH = os.path.join(unified_store.STORE_DIR, 'comicmaintainer.db')

def test_processing_history():
    """Test processing history database operations"""
    print("Testing processing history functionality...")
    
    # Initialize database
    unified_store.init_db()
    print("✓ Database initialized")
    
    # Test adding a history entry
    result = unified_store.add_processing_history(
        filepath='/test/file.cbz',
        operation_type='process',
        before_filename='old_file.cbz',
        after_filename='new_file.cbz',
        before_title='Chapter 1',
        after_title='Chapter 0001',
        before_series='Test Series',
        after_series='Test Series',
        before_issue='1',
        after_issue='0001'
    )
    assert result == True, "Failed to add processing history"
    print("✓ Added processing history entry")
    
    # Test getting history count
    count = unified_store.get_processing_history_count()
    assert count == 1, f"Expected 1 history entry, got {count}"
    print(f"✓ History count: {count}")
    
    # Test retrieving history
    history = unified_store.get_processing_history(limit=10, offset=0)
    assert len(history) == 1, f"Expected 1 history entry, got {len(history)}"
    print(f"✓ Retrieved history: {len(history)} entries")
    
    # Validate history entry
    entry = history[0]
    assert entry['filepath'] == '/test/file.cbz', "Filepath mismatch"
    assert entry['before_filename'] == 'old_file.cbz', "Before filename mismatch"
    assert entry['after_filename'] == 'new_file.cbz', "After filename mismatch"
    assert entry['before_title'] == 'Chapter 1', "Before title mismatch"
    assert entry['after_title'] == 'Chapter 0001', "After title mismatch"
    assert entry['operation_type'] == 'process', "Operation type mismatch"
    print("✓ History entry data validated")
    
    # Test adding another entry
    unified_store.add_processing_history(
        filepath='/test/file2.cbz',
        operation_type='rename',
        before_filename='file2.cbz',
        after_filename='renamed_file2.cbz'
    )
    
    count = unified_store.get_processing_history_count()
    assert count == 2, f"Expected 2 history entries, got {count}"
    print(f"✓ Added second entry, total count: {count}")
    
    # Test pagination
    history_page1 = unified_store.get_processing_history(limit=1, offset=0)
    assert len(history_page1) == 1, "Page 1 should have 1 entry"
    print("✓ Pagination works (page 1)")
    
    history_page2 = unified_store.get_processing_history(limit=1, offset=1)
    assert len(history_page2) == 1, "Page 2 should have 1 entry"
    print("✓ Pagination works (page 2)")
    
    # Verify entries are ordered by timestamp (newest first)
    assert history_page1[0]['timestamp'] > history_page2[0]['timestamp'], \
        "History should be ordered by timestamp descending"
    print("✓ History ordered correctly (newest first)")
    
    # Test clearing history
    cleared = unified_store.clear_processing_history()
    assert cleared == 2, f"Expected to clear 2 entries, cleared {cleared}"
    print(f"✓ Cleared {cleared} history entries")
    
    count = unified_store.get_processing_history_count()
    assert count == 0, f"Expected 0 history entries after clear, got {count}"
    print("✓ History cleared successfully")
    
    print("\n✅ All tests passed!")
    
    # Cleanup
    import shutil
    shutil.rmtree(temp_dir)
    print("✓ Cleanup completed")

if __name__ == '__main__':
    try:
        test_processing_history()
        sys.exit(0)
    except Exception as e:
        print(f"\n❌ Test failed: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)
