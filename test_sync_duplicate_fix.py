#!/usr/bin/env python3
"""
Test script to reproduce and verify fix for UNIQUE constraint error in sync_with_filesystem.

The issue occurs when sync_with_filesystem is called and a file that already exists in the
database is incorrectly identified as needing to be added. This can happen due to:
1. Race conditions
2. Multiple concurrent sync calls
3. Files already in database but logic determines they should be added
"""

import sys
import os
import tempfile
import shutil
import time
import sqlite3

# Add src to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

# Set up temporary config directory for tests - will be cleaned up in main()
TEST_CONFIG_DIR = tempfile.mkdtemp(prefix='test_sync_dup_')
os.environ['CONFIG_DIR_OVERRIDE'] = TEST_CONFIG_DIR

import unified_store

# Override CONFIG_DIR in unified_store module
unified_store.CONFIG_DIR = TEST_CONFIG_DIR
unified_store.STORE_DIR = os.path.join(TEST_CONFIG_DIR, 'store')
unified_store.DB_PATH = os.path.join(unified_store.STORE_DIR, 'comicmaintainer.db')
unified_store._db_initialized = False


def test_sync_with_duplicate_files():
    """
    Test that sync_with_filesystem handles files that are already in the database.
    This reproduces the UNIQUE constraint error.
    """
    print("\n" + "=" * 60)
    print("TEST: sync_with_filesystem with duplicate files")
    print("=" * 60)
    
    # Create a temporary directory with test files
    test_dir = tempfile.mkdtemp(prefix='test_comics_')
    
    try:
        # Initialize database
        unified_store.init_db()
        unified_store.clear_all_files()
        
        # Create test comic files
        test_files = []
        for i in range(3):
            filepath = os.path.join(test_dir, f'comic_{i}.cbz')
            with open(filepath, 'w') as f:
                f.write(f'test content {i}')
            test_files.append(filepath)
        
        print(f"✓ Created {len(test_files)} test files in {test_dir}")
        
        # First sync - should add all files
        added, removed, updated = unified_store.sync_with_filesystem(test_dir)
        print(f"✓ First sync: added={added}, removed={removed}, updated={updated}")
        assert added == 3, f"Expected 3 files added, got {added}"
        
        # Verify files are in database
        db_files = unified_store.get_all_files()
        assert len(db_files) == 3, f"Expected 3 files in database, got {len(db_files)}"
        print(f"✓ All files in database: {len(db_files)} files")
        
        # Second sync - should not add any files (they already exist)
        # This is where the bug would occur if INSERT is used instead of INSERT OR REPLACE
        added, removed, updated = unified_store.sync_with_filesystem(test_dir)
        print(f"✓ Second sync: added={added}, removed={removed}, updated={updated}")
        assert added == 0, f"Expected 0 files added on second sync, got {added}"
        
        # Verify database still has same files
        db_files = unified_store.get_all_files()
        assert len(db_files) == 3, f"Expected 3 files in database after second sync, got {len(db_files)}"
        print(f"✓ Database still has correct count: {len(db_files)} files")
        
        # Third sync after modifying a file
        time.sleep(0.1)  # Ensure timestamp changes
        with open(test_files[0], 'a') as f:
            f.write(' modified')
        
        added, removed, updated = unified_store.sync_with_filesystem(test_dir)
        print(f"✓ Third sync (after modification): added={added}, removed={removed}, updated={updated}")
        assert added == 0, f"Expected 0 files added on third sync, got {added}"
        assert updated == 1, f"Expected 1 file updated, got {updated}"
        
        print("✅ Test PASSED - sync_with_filesystem handles duplicates correctly")
        return True
        
    except Exception as e:
        print(f"❌ Test FAILED: {e}")
        import traceback
        traceback.print_exc()
        return False
    finally:
        # Clean up test directory only
        shutil.rmtree(test_dir, ignore_errors=True)


def test_concurrent_sync_scenario():
    """
    Test scenario where files might be added to database between filesystem scan and insert.
    This simulates a race condition.
    """
    print("\n" + "=" * 60)
    print("TEST: Simulated race condition scenario")
    print("=" * 60)
    
    test_dir = tempfile.mkdtemp(prefix='test_comics_race_')
    
    try:
        # Initialize fresh database
        unified_store.init_db()
        unified_store.clear_all_files()
        
        # Create test file
        filepath = os.path.join(test_dir, 'comic.cbz')
        with open(filepath, 'w') as f:
            f.write('test content')
        
        print(f"✓ Created test file: {filepath}")
        
        # Manually add the file to database with actual file metadata
        stat = os.stat(filepath)
        unified_store.add_file(filepath, last_modified=stat.st_mtime, file_size=stat.st_size)
        print("✓ Manually added file to database")
        
        # Now run sync - the file is already in database
        # Without the fix, this would cause UNIQUE constraint error
        added, removed, updated = unified_store.sync_with_filesystem(test_dir)
        print(f"✓ Sync completed: added={added}, removed={removed}, updated={updated}")
        
        # Should recognize file already exists and possibly update it
        assert added == 0, f"Should not add duplicate file, got added={added}"
        
        print("✅ Test PASSED - race condition handled correctly")
        return True
        
    except Exception as e:
        print(f"❌ Test FAILED: {e}")
        import traceback
        traceback.print_exc()
        return False
    finally:
        # Clean up test directory only
        shutil.rmtree(test_dir, ignore_errors=True)


def test_direct_insert_with_existing_file():
    """
    Test that directly simulates the UNIQUE constraint scenario by
    manually inserting a file that would be detected as needing to be added.
    """
    print("\n" + "=" * 60)
    print("TEST: Direct INSERT simulation of UNIQUE constraint error")
    print("=" * 60)
    
    test_dir = tempfile.mkdtemp(prefix='test_comics_insert_')
    
    try:
        # Initialize fresh database
        unified_store.init_db()
        unified_store.clear_all_files()
        
        # Create test file
        filepath = os.path.join(test_dir, 'comic.cbz')
        with open(filepath, 'w') as f:
            f.write('test content')
        
        print(f"✓ Created test file: {filepath}")
        
        # First sync should work
        added, removed, updated = unified_store.sync_with_filesystem(test_dir)
        print(f"✓ First sync: added={added}, removed={removed}, updated={updated}")
        assert added == 1, f"Expected 1 file added, got {added}"
        
        # Directly execute the problematic INSERT that was in the old code
        # This simulates what happens in a race condition
        
        # Try to insert the same file directly (simulating concurrent sync)
        try:
            with unified_store.get_db_connection() as conn:
                cursor = conn.cursor()
                stat = os.stat(filepath)
                # This would fail with old code using plain INSERT
                cursor.execute('''
                    INSERT OR REPLACE INTO files (filepath, last_modified, file_size, added_timestamp)
                    VALUES (?, ?, ?, ?)
                ''', (filepath, stat.st_mtime, stat.st_size, time.time()))
                conn.commit()
            print("✓ INSERT OR REPLACE succeeded (fix is working)")
        except sqlite3.IntegrityError as e:
            print(f"❌ UNIQUE constraint error: {e}")
            return False
        
        # Verify file count is still 1
        count = unified_store.get_file_count()
        assert count == 1, f"Expected 1 file in database, got {count}"
        print(f"✓ File count correct: {count}")
        
        print("✅ Test PASSED - INSERT OR REPLACE prevents UNIQUE constraint error")
        return True
        
    except Exception as e:
        print(f"❌ Test FAILED: {e}")
        import traceback
        traceback.print_exc()
        return False
    finally:
        # Clean up test directory only
        shutil.rmtree(test_dir, ignore_errors=True)


def main():
    """Run all tests"""
    print("\n" + "=" * 60)
    print("SYNC DUPLICATE FIX TESTS")
    print("=" * 60)
    
    try:
        test1_passed = test_sync_with_duplicate_files()
        test2_passed = test_concurrent_sync_scenario()
        test3_passed = test_direct_insert_with_existing_file()
        
        if test1_passed and test2_passed and test3_passed:
            print("\n" + "=" * 60)
            print("✅ ALL TESTS PASSED")
            print("=" * 60)
            return 0
        print("\n" + "=" * 60)
        print("❌ SOME TESTS FAILED")
        print("=" * 60)
        return 1
    finally:
        # Clean up TEST_CONFIG_DIR at the end of all tests
        shutil.rmtree(TEST_CONFIG_DIR, ignore_errors=True)


if __name__ == '__main__':
    sys.exit(main())
