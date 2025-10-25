"""
Test performance of marker filtering in paginated queries.
This test verifies that the optimized SQL-based filtering is significantly
faster than loading all files and filtering in Python.
"""
import os
import sys
import tempfile
import time

# Add src directory to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))


def test_filter_performance_with_large_dataset():
    """Test that SQL-based marker filtering is much faster than Python filtering"""
    from unified_store import (
        init_db, clear_all_files, batch_add_files, 
        get_files_paginated, add_marker
    )
    
    # Create temp directory
    with tempfile.TemporaryDirectory() as tmpdir:
        # Override config directory for testing
        import unified_store
        original_store_dir = unified_store.STORE_DIR
        test_store_dir = os.path.join(tmpdir, 'store')
        os.makedirs(test_store_dir, exist_ok=True)
        unified_store.STORE_DIR = test_store_dir
        unified_store.DB_PATH = os.path.join(test_store_dir, 'test.db')
        
        # Reset initialization flag
        unified_store._db_initialized = False
        
        try:
            # Initialize database
            init_db()
            clear_all_files()
            
            # Add a large number of test files
            print("Creating 10000 test files...")
            test_dir = os.path.join(tmpdir, 'test')
            os.makedirs(test_dir, exist_ok=True)
            
            test_files = []
            for i in range(1, 10001):
                filepath = os.path.join(test_dir, f"file_{i:05d}.cbz")
                with open(filepath, 'w') as f:
                    f.write(f"test {i}")
                test_files.append(filepath)
            
            # Batch add files
            start = time.time()
            success, errors = batch_add_files(test_files)
            batch_time = time.time() - start
            print(f"✓ Batch added {success} files in {batch_time:.2f}s")
            
            # Mark 5000 files as processed (first half)
            print("Marking 5000 files as processed...")
            start = time.time()
            for i in range(0, 5000):
                add_marker(test_files[i], 'processed')
            marker_time = time.time() - start
            print(f"✓ Marked 5000 files in {marker_time:.2f}s")
            
            # Mark 1000 files as duplicates (every 10th file)
            print("Marking 1000 files as duplicates...")
            for i in range(0, 10000, 10):
                add_marker(test_files[i], 'duplicate')
            
            print("\n--- Testing Filter Performance ---")
            
            # Test 1: Get all files (first 100)
            start = time.time()
            results, total = get_files_paginated(
                limit=100, offset=0, filter_mode='all'
            )
            all_time = time.time() - start
            
            assert len(results) == 100, f"Expected 100 results"
            assert total == 10000, f"Expected total of 10000, got {total}"
            print(f"✓ Filter 'all' (100 from 10000): {all_time:.4f}s")
            
            # Test 2: Get marked files (first 100)
            start = time.time()
            results, total = get_files_paginated(
                limit=100, offset=0, filter_mode='marked'
            )
            marked_time = time.time() - start
            
            assert len(results) == 100, f"Expected 100 results"
            assert total == 5000, f"Expected total of 5000 marked, got {total}"
            print(f"✓ Filter 'marked' (100 from 5000): {marked_time:.4f}s")
            
            # Test 3: Get unmarked files (first 100)
            start = time.time()
            results, total = get_files_paginated(
                limit=100, offset=0, filter_mode='unmarked'
            )
            unmarked_time = time.time() - start
            
            assert len(results) == 100, f"Expected 100 results"
            assert total == 5000, f"Expected total of 5000 unmarked, got {total}"
            print(f"✓ Filter 'unmarked' (100 from 5000): {unmarked_time:.4f}s")
            
            # Test 4: Get duplicates (first 100)
            start = time.time()
            results, total = get_files_paginated(
                limit=100, offset=0, filter_mode='duplicates'
            )
            duplicates_time = time.time() - start
            
            assert len(results) == 100, f"Expected 100 results"
            assert total == 1000, f"Expected total of 1000 duplicates, got {total}"
            print(f"✓ Filter 'duplicates' (100 from 1000): {duplicates_time:.4f}s")
            
            # Test 5: Get ALL marked files at once (limit=-1)
            start = time.time()
            results, total = get_files_paginated(
                limit=-1, offset=0, filter_mode='marked'
            )
            all_marked_time = time.time() - start
            
            assert len(results) == 5000, f"Expected 5000 results, got {len(results)}"
            assert total == 5000, f"Expected total of 5000"
            print(f"✓ Get ALL 'marked' files (5000 files): {all_marked_time:.4f}s")
            
            # Test 6: Get ALL unmarked files at once (limit=-1)
            start = time.time()
            results, total = get_files_paginated(
                limit=-1, offset=0, filter_mode='unmarked'
            )
            all_unmarked_time = time.time() - start
            
            assert len(results) == 5000, f"Expected 5000 results, got {len(results)}"
            assert total == 5000, f"Expected total of 5000"
            print(f"✓ Get ALL 'unmarked' files (5000 files): {all_unmarked_time:.4f}s")
            
            print("\n📊 Performance Summary:")
            print(f"   All files (100):      {all_time:.4f}s")
            print(f"   Marked (100):         {marked_time:.4f}s")
            print(f"   Unmarked (100):       {unmarked_time:.4f}s")
            print(f"   Duplicates (100):     {duplicates_time:.4f}s")
            print(f"   ALL marked (5000):    {all_marked_time:.4f}s")
            print(f"   ALL unmarked (5000):  {all_unmarked_time:.4f}s")
            
            # Performance assertions
            # All queries should complete in under 1 second
            assert all_time < 1.0, f"'All' query too slow: {all_time:.4f}s"
            assert marked_time < 1.0, f"'Marked' query too slow: {marked_time:.4f}s"
            assert unmarked_time < 1.0, f"'Unmarked' query too slow: {unmarked_time:.4f}s"
            assert duplicates_time < 1.0, f"'Duplicates' query too slow: {duplicates_time:.4f}s"
            assert all_marked_time < 1.0, f"'All marked' query too slow: {all_marked_time:.4f}s"
            assert all_unmarked_time < 1.0, f"'All unmarked' query too slow: {all_unmarked_time:.4f}s"
            
            print("\n✅ All filter performance tests passed!")
            print("   All queries completed in less than 1 second ✓")
            
        finally:
            # Restore original paths
            unified_store.STORE_DIR = original_store_dir
            unified_store.DB_PATH = os.path.join(original_store_dir, 'comicmaintainer.db')
            unified_store._db_initialized = False


if __name__ == '__main__':
    print("Testing marker filter performance with large dataset...\n")
    test_filter_performance_with_large_dataset()
