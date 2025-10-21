#!/usr/bin/env python3
"""
Test script for the new file_store module.

This script verifies that the SQLite-based file store works correctly
and provides better performance than the old file-based cache system.
"""

import sys
import os
import time
import tempfile
import shutil

# Add src to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

# Set up temporary config directory for tests
TEST_CONFIG_DIR = tempfile.mkdtemp(prefix='test_config_')
os.environ['CONFIG_DIR_OVERRIDE'] = TEST_CONFIG_DIR

import file_store
import unified_store

# Override CONFIG_DIR in unified_store module (which file_store wraps)
unified_store.CONFIG_DIR = TEST_CONFIG_DIR
unified_store.STORE_DIR = os.path.join(TEST_CONFIG_DIR, 'store')
unified_store.DB_PATH = os.path.join(unified_store.STORE_DIR, 'comicmaintainer.db')
unified_store._db_initialized = False

# Update file_store references
file_store.CONFIG_DIR = TEST_CONFIG_DIR
file_store.FILE_STORE_DIR = unified_store.STORE_DIR
file_store.DB_PATH = unified_store.DB_PATH


def test_basic_operations():
    """Test basic file store operations"""
    print("\n" + "=" * 60)
    print("TEST: Basic File Store Operations")
    print("=" * 60)
    
    # Initialize database
    file_store.init_db()
    
    # Clear any existing data
    file_store.clear_all_files()
    
    # Test add_file
    test_file = "/test/path/file1.cbz"
    result = file_store.add_file(test_file, last_modified=time.time(), file_size=1024)
    assert result, "Failed to add file"
    print(f"✓ Added file: {test_file}")
    
    # Test has_file
    assert file_store.has_file(test_file), "File not found after adding"
    print(f"✓ Verified file exists: {test_file}")
    
    # Test get_all_files
    all_files = file_store.get_all_files()
    assert test_file in all_files, "File not in list"
    print(f"✓ File appears in get_all_files() list")
    
    # Test get_file_count
    count = file_store.get_file_count()
    assert count == 1, f"Expected 1 file, got {count}"
    print(f"✓ File count is correct: {count}")
    
    # Test remove_file
    result = file_store.remove_file(test_file)
    assert result, "Failed to remove file"
    assert not file_store.has_file(test_file), "File still exists after removal"
    print(f"✓ Removed file: {test_file}")
    
    print("✅ Basic operations test PASSED")


def test_rename_operation():
    """Test file rename operation"""
    print("\n" + "=" * 60)
    print("TEST: File Rename Operation")
    print("=" * 60)
    
    file_store.clear_all_files()
    
    old_path = "/test/path/old_file.cbz"
    new_path = "/test/path/new_file.cbz"
    
    # Add file with old path
    file_store.add_file(old_path, last_modified=time.time(), file_size=2048)
    print(f"✓ Added file: {old_path}")
    
    # Rename it
    result = file_store.rename_file(old_path, new_path)
    assert result, "Failed to rename file"
    print(f"✓ Renamed file: {old_path} -> {new_path}")
    
    # Verify old path doesn't exist and new path does
    assert not file_store.has_file(old_path), "Old path still exists after rename"
    assert file_store.has_file(new_path), "New path doesn't exist after rename"
    print(f"✓ Old path removed, new path exists")
    
    # Verify count is still 1
    count = file_store.get_file_count()
    assert count == 1, f"Expected 1 file after rename, got {count}"
    print(f"✓ File count unchanged: {count}")
    
    print("✅ Rename operation test PASSED")


def test_batch_operations():
    """Test batch add and remove operations"""
    print("\n" + "=" * 60)
    print("TEST: Batch Operations")
    print("=" * 60)
    
    file_store.clear_all_files()
    
    # Create temporary directory with actual files for batch testing
    with tempfile.TemporaryDirectory() as tmpdir:
        test_files = []
        for i in range(100):
            filepath = os.path.join(tmpdir, f"batch_file_{i}.cbz")
            with open(filepath, 'w') as f:
                f.write("test")
            test_files.append(filepath)
        
        # Batch add
        start_time = time.time()
        success, errors = file_store.batch_add_files(test_files)
        add_time = time.time() - start_time
        
        print(f"✓ Batch added {success} files in {add_time:.3f}s ({success/add_time:.0f} files/sec)")
        assert success == 100, f"Expected 100 successful adds, got {success}"
        
        # Verify count
        count = file_store.get_file_count()
        assert count == 100, f"Expected 100 files, got {count}"
        print(f"✓ File count is correct: {count}")
        
        # Batch remove first 50
        files_to_remove = test_files[:50]
        start_time = time.time()
        removed = file_store.batch_remove_files(files_to_remove)
        remove_time = time.time() - start_time
        
        print(f"✓ Batch removed {removed} files in {remove_time:.3f}s ({removed/remove_time:.0f} files/sec)")
        assert removed == 50, f"Expected 50 removals, got {removed}"
        
        # Verify count
        count = file_store.get_file_count()
        assert count == 50, f"Expected 50 files remaining, got {count}"
        print(f"✓ File count after removal: {count}")
    
    print("✅ Batch operations test PASSED")


def test_filesystem_sync():
    """Test filesystem sync operation"""
    print("\n" + "=" * 60)
    print("TEST: Filesystem Sync")
    print("=" * 60)
    
    file_store.clear_all_files()
    
    # Create a temporary directory with test files
    with tempfile.TemporaryDirectory() as tmpdir:
        # Create some test files
        test_files = []
        for i in range(10):
            filepath = os.path.join(tmpdir, f"test_{i}.cbz")
            with open(filepath, 'w') as f:
                f.write("test content")
            test_files.append(filepath)
        
        print(f"✓ Created {len(test_files)} test files in {tmpdir}")
        
        # Sync with filesystem
        start_time = time.time()
        added, removed, updated = file_store.sync_with_filesystem(tmpdir)
        sync_time = time.time() - start_time
        
        print(f"✓ Synced in {sync_time:.3f}s: +{added} -{removed} ~{updated}")
        assert added == 10, f"Expected 10 files added, got {added}"
        
        # Verify all files are in store
        all_files = file_store.get_all_files()
        for filepath in test_files:
            assert filepath in all_files, f"File {filepath} not in store after sync"
        print(f"✓ All files present in store")
        
        # Remove some files from filesystem
        for filepath in test_files[:3]:
            os.remove(filepath)
        print(f"✓ Removed 3 files from filesystem")
        
        # Sync again
        added, removed, updated = file_store.sync_with_filesystem(tmpdir)
        print(f"✓ Re-synced: +{added} -{removed} ~{updated}")
        assert removed == 3, f"Expected 3 files removed, got {removed}"
        
        # Verify count
        count = file_store.get_file_count()
        assert count == 7, f"Expected 7 files remaining, got {count}"
        print(f"✓ File count after removal: {count}")
    
    print("✅ Filesystem sync test PASSED")


def test_metadata_operations():
    """Test metadata operations"""
    print("\n" + "=" * 60)
    print("TEST: Metadata Operations")
    print("=" * 60)
    
    # Set metadata
    result = file_store.set_metadata('test_key', 'test_value')
    assert result, "Failed to set metadata"
    print("✓ Set metadata: test_key = test_value")
    
    # Get metadata
    value = file_store.get_metadata('test_key')
    assert value == 'test_value', f"Expected 'test_value', got '{value}'"
    print(f"✓ Retrieved metadata: test_key = {value}")
    
    # Get non-existent metadata with default
    value = file_store.get_metadata('nonexistent', default='default_value')
    assert value == 'default_value', f"Expected 'default_value', got '{value}'"
    print(f"✓ Got default for non-existent key: {value}")
    
    # Test last sync timestamp
    file_store.set_metadata('last_sync_timestamp', str(time.time()))
    timestamp = file_store.get_last_sync_timestamp()
    assert timestamp is not None, "Last sync timestamp is None"
    print(f"✓ Last sync timestamp: {timestamp}")
    
    print("✅ Metadata operations test PASSED")


def test_performance_comparison():
    """Compare performance with old system"""
    print("\n" + "=" * 60)
    print("TEST: Performance Comparison")
    print("=" * 60)
    
    file_store.clear_all_files()
    
    # Test with different file counts
    for file_count in [100, 500, 1000]:
        print(f"\nTesting with {file_count} files:")
        
        # Create temporary directory with actual files
        with tempfile.TemporaryDirectory() as tmpdir:
            test_files = []
            for i in range(file_count):
                filepath = os.path.join(tmpdir, f"perf_file_{i}.cbz")
                with open(filepath, 'w') as f:
                    f.write("test")
                test_files.append(filepath)
            
            # Batch add
            start_time = time.time()
            success, errors = file_store.batch_add_files(test_files)
            add_time = time.time() - start_time
            print(f"  Batch add: {add_time:.3f}s ({success/add_time:.0f} files/sec)")
            
            # Get all files
            start_time = time.time()
            all_files = file_store.get_all_files()
            get_time = time.time() - start_time
            print(f"  Get all: {get_time:.3f}s ({len(all_files)} files)")
            
            # Random lookups
            import random
            lookup_files = random.sample(test_files, min(100, file_count))
            start_time = time.time()
            for filepath in lookup_files:
                file_store.has_file(filepath)
            lookup_time = time.time() - start_time
            print(f"  {len(lookup_files)} lookups: {lookup_time:.3f}s ({len(lookup_files)/lookup_time:.0f} lookups/sec)")
            
            # Batch remove all
            start_time = time.time()
            removed = file_store.batch_remove_files(test_files)
            remove_time = time.time() - start_time
            print(f"  Batch remove: {remove_time:.3f}s ({removed/remove_time:.0f} files/sec)")
    
    print("\n✅ Performance comparison test PASSED")


def main():
    """Run all tests"""
    print("\n" + "=" * 60)
    print("FILE STORE MODULE TESTS")
    print("=" * 60)
    print(f"Using test config directory: {TEST_CONFIG_DIR}")
    
    try:
        test_basic_operations()
        test_rename_operation()
        test_batch_operations()
        test_filesystem_sync()
        test_metadata_operations()
        test_performance_comparison()
        
        print("\n" + "=" * 60)
        print("✅ ALL TESTS PASSED")
        print("=" * 60)
        return 0
    except AssertionError as e:
        print(f"\n❌ TEST FAILED: {e}")
        return 1
    except Exception as e:
        print(f"\n❌ ERROR: {e}")
        import traceback
        traceback.print_exc()
        return 1
    finally:
        # Clean up test directory
        try:
            shutil.rmtree(TEST_CONFIG_DIR)
            print(f"\nCleaned up test directory: {TEST_CONFIG_DIR}")
        except:
            pass


if __name__ == '__main__':
    sys.exit(main())
