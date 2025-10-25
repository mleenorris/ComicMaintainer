"""
Integration test to simulate real web app scenario with marker filtering.
This test verifies the complete flow from database queries with different filters.
"""
import os
import sys
import tempfile
import time

# Add src directory to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))


def test_marker_filter_integration():
    """Test the complete flow of marker filtering with get_files_paginated"""
    from unified_store import (
        init_db, clear_all_files, batch_add_files, add_marker,
        get_files_paginated, get_all_markers_by_type
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
        
        # Set up test watched directory
        test_watched_dir = os.path.join(tmpdir, 'watched')
        os.makedirs(test_watched_dir, exist_ok=True)
        
        try:
            # Initialize database
            init_db()
            clear_all_files()
            
            # Create test files
            print("Creating 1000 test files...")
            test_files = []
            for i in range(1, 1001):
                filepath = os.path.join(test_watched_dir, f"comic_{i:04d}.cbz")
                with open(filepath, 'w') as f:
                    f.write(f"test comic {i}")
                test_files.append(filepath)
            
            # Batch add files
            success, errors = batch_add_files(test_files)
            print(f"✓ Added {success} files to database")
            
            # Mark some files
            # Mark 400 as processed
            for i in range(0, 400):
                add_marker(test_files[i], 'processed')
            
            # Mark 100 as duplicates (every 10th file)
            for i in range(0, 1000, 10):
                add_marker(test_files[i], 'duplicate')
            
            print("✓ Marked 400 as processed, 100 as duplicates")
            
            # Get marker data for verification
            marker_data = get_all_markers_by_type(['processed', 'duplicate'])
            processed_files = marker_data.get('processed', set())
            duplicate_files = marker_data.get('duplicate', set())
            
            print("\n--- Testing Query Integration ---")
            
            # Test 1: Get first page of all files
            start = time.time()
            results, total = get_files_paginated(
                limit=100, offset=0, filter_mode='all'
            )
            elapsed = time.time() - start
            
            assert len(results) == 100
            assert total == 1000
            print(f"✓ Query all files (page 1): {elapsed:.4f}s - {len(results)} files")
            
            # Test 2: Get marked files
            start = time.time()
            results, total = get_files_paginated(
                limit=100, offset=0, filter_mode='marked'
            )
            elapsed = time.time() - start
            
            assert len(results) == 100
            assert total == 400
            # Verify all returned files are marked as processed
            for file_data in results:
                filepath = file_data['filepath']
                assert filepath in processed_files, f"File {filepath} should be marked as processed"
            print(f"✓ Query marked files (page 1): {elapsed:.4f}s - {len(results)} files")
            
            # Test 3: Get unmarked files
            start = time.time()
            results, total = get_files_paginated(
                limit=100, offset=0, filter_mode='unmarked'
            )
            elapsed = time.time() - start
            
            assert len(results) == 100
            assert total == 600
            # Verify all returned files are NOT marked as processed
            for file_data in results:
                filepath = file_data['filepath']
                assert filepath not in processed_files, f"File {filepath} should NOT be marked as processed"
            print(f"✓ Query unmarked files (page 1): {elapsed:.4f}s - {len(results)} files")
            
            # Test 4: Get duplicate files
            start = time.time()
            results, total = get_files_paginated(
                limit=50, offset=0, filter_mode='duplicates'
            )
            elapsed = time.time() - start
            
            assert len(results) == 50
            assert total == 100
            # Verify all returned files are marked as duplicates
            for file_data in results:
                filepath = file_data['filepath']
                assert filepath in duplicate_files, f"File {filepath} should be marked as duplicate"
            print(f"✓ Query duplicate files (page 1): {elapsed:.4f}s - {len(results)} files")
            
            # Test 5: Get ALL files with limit=-1 (the problematic case from the issue)
            start = time.time()
            results, total = get_files_paginated(
                limit=-1, offset=0, filter_mode='all'
            )
            elapsed = time.time() - start
            
            assert len(results) == 1000
            assert total == 1000
            print(f"✓ Query ALL files (limit=-1): {elapsed:.4f}s - {len(results)} files")
            assert elapsed < 1.0, f"Query too slow: {elapsed:.4f}s"
            
            # Test 6: Get ALL marked files with limit=-1
            start = time.time()
            results, total = get_files_paginated(
                limit=-1, offset=0, filter_mode='marked'
            )
            elapsed = time.time() - start
            
            assert len(results) == 400
            assert total == 400
            print(f"✓ Query ALL marked files (limit=-1): {elapsed:.4f}s - {len(results)} files")
            assert elapsed < 1.0, f"Query too slow: {elapsed:.4f}s"
            
            # Test 7: Get ALL unmarked files with limit=-1
            start = time.time()
            results, total = get_files_paginated(
                limit=-1, offset=0, filter_mode='unmarked'
            )
            elapsed = time.time() - start
            
            assert len(results) == 600
            assert total == 600
            print(f"✓ Query ALL unmarked files (limit=-1): {elapsed:.4f}s - {len(results)} files")
            assert elapsed < 1.0, f"Query too slow: {elapsed:.4f}s"
            
            # Test 8: Search functionality
            start = time.time()
            results, total = get_files_paginated(
                limit=100, offset=0, search_query='comic_0001'
            )
            elapsed = time.time() - start
            
            assert len(results) == 1
            assert 'comic_0001.cbz' in results[0]['filepath']
            print(f"✓ Query with search='comic_0001': {elapsed:.4f}s - {len(results)} files")
            
            # Test 9: Search with filter
            start = time.time()
            results, total = get_files_paginated(
                limit=100, offset=0, search_query='comic_00', filter_mode='marked'
            )
            elapsed = time.time() - start
            
            # Files comic_0001 through comic_0099 that are marked (first 99 are marked)
            assert len(results) == 99
            print(f"✓ Query with search='comic_00' + filter=marked: {elapsed:.4f}s - {len(results)} files")
            
            # Test 10: Sorting by date
            start = time.time()
            results, total = get_files_paginated(
                limit=100, offset=0, filter_mode='marked', sort_by='date', sort_direction='desc'
            )
            elapsed = time.time() - start
            
            assert len(results) == 100
            # Verify sorting
            assert results[0]['last_modified'] >= results[-1]['last_modified']
            print(f"✓ Query marked files sorted by date desc: {elapsed:.4f}s")
            
            print("\n📊 Performance Summary:")
            print("   All queries completed successfully!")
            print("   ✅ All filter modes work correctly")
            print("   ✅ Pagination works for all filter modes")
            print("   ✅ Search works with and without filters")
            print("   ✅ Sorting works with filters")
            print("   ✅ All 'ALL files' queries (limit=-1) completed in under 1 second")
            
        finally:
            # Restore original paths
            unified_store.STORE_DIR = original_store_dir
            unified_store.DB_PATH = os.path.join(original_store_dir, 'comicmaintainer.db')
            unified_store._db_initialized = False


if __name__ == '__main__':
    print("Testing marker filter integration...\n")
    test_marker_filter_integration()
    print("\n✅ All integration tests passed!")
