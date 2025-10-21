#!/usr/bin/env python3
"""
Test to verify that removing file_list_cache doesn't break functionality
"""
import sys
import os
import tempfile
import shutil
import time

# Add src to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

def test_get_comic_files_without_cache():
    """Test that direct database access works efficiently without in-memory cache"""
    import unified_store
    
    # Create a temporary config directory
    temp_config = tempfile.mkdtemp()
    temp_watched = tempfile.mkdtemp()
    original_config = unified_store.CONFIG_DIR
    original_store = unified_store.STORE_DIR
    original_db = unified_store.DB_PATH
    
    try:
        # Override config paths
        unified_store.CONFIG_DIR = temp_config
        unified_store.STORE_DIR = os.path.join(temp_config, 'store')
        unified_store.DB_PATH = os.path.join(unified_store.STORE_DIR, 'comicmaintainer.db')
        
        # Create the store directory
        os.makedirs(unified_store.STORE_DIR, exist_ok=True)
        
        # Clear thread-local connection to force new connection
        if hasattr(unified_store._thread_local, 'connection') and unified_store._thread_local.connection:
            try:
                unified_store._thread_local.connection.close()
            except:
                pass
            unified_store._thread_local.connection = None
        
        # Initialize database
        unified_store.init_db()
        
        # Add some test files
        test_files = [
            f"{temp_watched}/folder1/comic1.cbz",
            f"{temp_watched}/folder1/comic2.cbz",
            f"{temp_watched}/folder2/comic3.cbr",
        ]
        
        for filepath in test_files:
            unified_store.add_file(filepath, time.time(), 1024*1024)
        
        # Test 1: Database returns correct files
        print("Test 1: Database returns correct files")
        files = unified_store.get_all_files()
        assert len(files) == 3, f"Expected 3 files, got {len(files)}"
        assert all(f in files for f in test_files), "Not all test files returned"
        print("  ✓ PASSED: Database returns correct files")
        
        # Test 2: Multiple calls return same results (consistency)
        print("\nTest 2: Multiple calls return consistent results")
        files1 = unified_store.get_all_files()
        files2 = unified_store.get_all_files()
        files3 = unified_store.get_all_files()
        assert files1 == files2 == files3, "Multiple calls should return same results"
        print("  ✓ PASSED: Multiple calls return consistent results")
        
        # Test 3: Adding a file is immediately visible (no cache staleness)
        print("\nTest 3: Adding a file is immediately visible")
        new_file = f"{temp_watched}/folder3/comic4.cbz"
        unified_store.add_file(new_file, time.time(), 1024*1024)
        files = unified_store.get_all_files()
        assert len(files) == 4, f"Expected 4 files after adding, got {len(files)}"
        assert new_file in files, "New file not found in results"
        print("  ✓ PASSED: New file immediately visible (no cache staleness)")
        
        # Test 4: Removing a file is immediately visible (no cache invalidation needed)
        print("\nTest 4: Removing a file is immediately visible")
        unified_store.remove_file(new_file)
        files = unified_store.get_all_files()
        assert len(files) == 3, f"Expected 3 files after removing, got {len(files)}"
        assert new_file not in files, "Removed file still in results"
        print("  ✓ PASSED: Removed file immediately disappears (no cache invalidation needed)")
        
        # Test 5: Performance is still good (database is fast)
        print("\nTest 5: Performance test (should be fast without cache)")
        # Add more files for performance test
        for i in range(100):
            filepath = f"{temp_watched}/bulk/comic{i:03d}.cbz"
            unified_store.add_file(filepath, time.time(), 1024*1024)
        
        # Measure time for 10 consecutive reads
        times = []
        for _ in range(10):
            start = time.time()
            files = unified_store.get_all_files()
            elapsed = (time.time() - start) * 1000  # Convert to ms
            times.append(elapsed)
        
        avg_time = sum(times) / len(times)
        max_time = max(times)
        
        print(f"  Average read time: {avg_time:.2f} ms")
        print(f"  Maximum read time: {max_time:.2f} ms")
        
        # Assert that performance is acceptable (< 50ms on average)
        assert avg_time < 50, f"Average read time too slow: {avg_time:.2f} ms"
        assert max_time < 100, f"Maximum read time too slow: {max_time:.2f} ms"
        print("  ✓ PASSED: Performance is acceptable without cache")
        
        print("\n" + "="*70)
        print("ALL TESTS PASSED!")
        print("="*70)
        print("\nConclusion:")
        print("  - Removing file_list_cache doesn't break functionality")
        print("  - Database reads are fast enough without in-memory cache")
        print("  - Changes are immediately visible without cache invalidation")
        print("  - Performance is excellent (<50ms average for 103 files)")
        
    finally:
        # Restore original paths
        unified_store.CONFIG_DIR = original_config
        unified_store.STORE_DIR = original_store
        unified_store.DB_PATH = original_db
        
        # Clean up temp directories
        if os.path.exists(temp_config):
            shutil.rmtree(temp_config)
        if os.path.exists(temp_watched):
            shutil.rmtree(temp_watched)

if __name__ == '__main__':
    test_get_comic_files_without_cache()
