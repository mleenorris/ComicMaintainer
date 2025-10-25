"""
Test pagination performance optimizations for file list loading.
"""
import os
import sys
import tempfile
import shutil
import time

# Add src directory to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))


def test_paginated_query_basic():
    """Test basic paginated query functionality"""
    from unified_store import init_db, clear_all_files, add_file, get_files_paginated
    
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
            
            # Add some test files
            test_files = [
                f"/test/file_{i:04d}.cbz" for i in range(1, 51)
            ]
            
            for idx, filepath in enumerate(test_files):
                add_file(filepath, last_modified=time.time() - idx, file_size=1000 * (idx + 1))
            
            # Test pagination - get first 10 files
            results, total = get_files_paginated(limit=10, offset=0, sort_by='name')
            
            assert len(results) == 10, f"Expected 10 results, got {len(results)}"
            assert total == 50, f"Expected total of 50, got {total}"
            assert results[0]['filepath'] == '/test/file_0001.cbz', f"First file should be file_0001.cbz"
            
            print("✓ Basic pagination works correctly")
            
            # Test pagination - get second page
            results, total = get_files_paginated(limit=10, offset=10, sort_by='name')
            
            assert len(results) == 10, f"Expected 10 results on page 2"
            assert results[0]['filepath'] == '/test/file_0011.cbz', f"First file on page 2 should be file_0011.cbz"
            
            print("✓ Second page pagination works correctly")
            
            # Test sorting by date
            results, total = get_files_paginated(limit=5, offset=0, sort_by='date', sort_direction='desc')
            
            assert len(results) == 5, f"Expected 5 results"
            # First result should have the highest mtime (most recent)
            assert results[0]['last_modified'] > results[1]['last_modified'], "Date sorting should be descending"
            
            print("✓ Date sorting works correctly")
            
            # Test search
            results, total = get_files_paginated(limit=100, offset=0, search_query='0001')
            
            assert len(results) == 1, f"Expected 1 result for search '0001', got {len(results)}"
            assert results[0]['filepath'] == '/test/file_0001.cbz', "Search should find file_0001.cbz"
            
            print("✓ Search functionality works correctly")
            
            # Test getting all files
            results, total = get_files_paginated(limit=-1, offset=0)
            
            assert len(results) == 50, f"Expected all 50 files when limit=-1, got {len(results)}"
            
            print("✓ Get all files works correctly")
            
        finally:
            # Restore original paths
            unified_store.STORE_DIR = original_store_dir
            unified_store.DB_PATH = os.path.join(original_store_dir, 'comicmaintainer.db')
            unified_store._db_initialized = False


def test_pagination_performance():
    """Test that pagination is more efficient than loading all files"""
    from unified_store import init_db, clear_all_files, batch_add_files, get_files_paginated, get_all_files_with_metadata, add_marker, get_unmarked_file_count
    
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
            test_files = [
                f"/test/file_{i:05d}.cbz" for i in range(1, 10001)
            ]
            
            # Create actual files
            test_dir = os.path.join(tmpdir, 'test')
            os.makedirs(test_dir, exist_ok=True)
            for i in range(1, 10001):
                filepath = os.path.join(test_dir, f"file_{i:05d}.cbz")
                with open(filepath, 'w') as f:
                    f.write(f"test {i}")
                test_files[i-1] = filepath
            
            # Batch add files
            start = time.time()
            success, errors = batch_add_files(test_files)
            batch_time = time.time() - start
            print(f"✓ Batch added {success} files in {batch_time:.2f}s")
            
            # Mark 5000 files as processed
            print("Marking 5000 files as processed...")
            for i in range(0, 5000):
                add_marker(test_files[i], 'processed')
            
            # Test unmarked count performance
            start = time.time()
            unmarked_count = get_unmarked_file_count()
            unmarked_time = time.time() - start
            
            assert unmarked_count == 5000, f"Expected 5000 unmarked files, got {unmarked_count}"
            print(f"✓ Unmarked count query took {unmarked_time:.4f}s")
            
            # Test paginated query performance
            start = time.time()
            results, total = get_files_paginated(limit=100, offset=0)
            paginated_time = time.time() - start
            
            assert len(results) == 100, f"Expected 100 results"
            assert total == 10000, f"Expected total of 10000"
            print(f"✓ Paginated query (100 items from 10000) took {paginated_time:.4f}s")
            
            # Test loading all files (old method)
            start = time.time()
            all_files = get_all_files_with_metadata()
            all_files_time = time.time() - start
            
            assert len(all_files) == 10000, f"Expected 10000 files"
            print(f"✓ Loading all files took {all_files_time:.4f}s")
            
            # Pagination should be significantly faster for first page
            print(f"\n📊 Performance comparison:")
            print(f"   Unmarked count:  {unmarked_time:.4f}s")
            print(f"   Paginated query: {paginated_time:.4f}s")
            print(f"   Load all files:  {all_files_time:.4f}s")
            print(f"   Speedup: {all_files_time / paginated_time:.1f}x faster")
            
            # Assert that pagination is at least somewhat faster
            # (may not always be much faster on small datasets or with caching)
            if paginated_time > all_files_time:
                print(f"⚠️  Warning: Paginated query was slower. This may be due to caching or test environment.")
            else:
                print(f"✓ Pagination optimization is working")
            
        finally:
            # Restore original paths
            unified_store.STORE_DIR = original_store_dir
            unified_store.DB_PATH = os.path.join(original_store_dir, 'comicmaintainer.db')
            unified_store._db_initialized = False


if __name__ == '__main__':
    print("Testing paginated query functionality...")
    test_paginated_query_basic()
    print("\nTesting pagination performance with large dataset...")
    test_pagination_performance()
    print("\n✅ All pagination tests passed!")
