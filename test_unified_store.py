#!/usr/bin/env python3
"""
Test script for the unified_store module.

This script verifies that the unified database combines both file and marker storage
and provides the same functionality as the separate databases.
"""

import sys
import os
import time
import tempfile
import shutil

# Add src to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

# Set up temporary config directory for tests
TEST_CONFIG_DIR = tempfile.mkdtemp(prefix='test_unified_')
os.environ['CONFIG_DIR_OVERRIDE'] = TEST_CONFIG_DIR

import unified_store

# Override CONFIG_DIR in unified_store module
unified_store.CONFIG_DIR = TEST_CONFIG_DIR
unified_store.STORE_DIR = os.path.join(TEST_CONFIG_DIR, 'store')
unified_store.DB_PATH = os.path.join(unified_store.STORE_DIR, 'comicmaintainer.db')
unified_store._db_initialized = False


def test_unified_database_structure():
    """Test that the unified database has correct structure"""
    print("\n" + "=" * 60)
    print("TEST: Unified Database Structure")
    print("=" * 60)
    
    # Initialize database
    unified_store.init_db()
    
    # Verify database file exists
    assert os.path.exists(unified_store.DB_PATH), "Database file not created"
    print(f"✓ Database created at: {unified_store.DB_PATH}")
    
    # Verify tables exist
    import sqlite3
    conn = sqlite3.connect(unified_store.DB_PATH)
    cursor = conn.cursor()
    
    cursor.execute("SELECT name FROM sqlite_master WHERE type='table'")
    tables = {row[0] for row in cursor.fetchall()}
    
    expected_tables = {'files', 'markers', 'metadata'}
    assert expected_tables.issubset(tables), f"Missing tables. Expected {expected_tables}, got {tables}"
    print(f"✓ All required tables present: {', '.join(expected_tables)}")
    
    # Verify indexes exist
    cursor.execute("SELECT name FROM sqlite_master WHERE type='index'")
    indexes = {row[0] for row in cursor.fetchall()}
    
    expected_indexes = {
        'idx_files_last_modified',
        'idx_files_added_timestamp',
        'idx_markers_filepath',
        'idx_markers_type'
    }
    assert expected_indexes.issubset(indexes), f"Missing indexes. Expected {expected_indexes}, got {indexes}"
    print(f"✓ All required indexes present")
    
    conn.close()
    
    print("✅ Database structure test PASSED")


def test_file_operations():
    """Test file store operations"""
    print("\n" + "=" * 60)
    print("TEST: File Operations in Unified Database")
    print("=" * 60)
    
    # Clear any existing data
    unified_store.clear_all_files()
    
    # Test add_file
    test_file = "/test/unified/file1.cbz"
    result = unified_store.add_file(test_file, last_modified=time.time(), file_size=1024)
    assert result, "Failed to add file"
    print(f"✓ Added file: {test_file}")
    
    # Test has_file
    assert unified_store.has_file(test_file), "File not found after adding"
    print(f"✓ Verified file exists")
    
    # Test get_all_files
    all_files = unified_store.get_all_files()
    assert test_file in all_files, "File not in list"
    print(f"✓ File appears in get_all_files()")
    
    # Test get_file_count
    count = unified_store.get_file_count()
    assert count == 1, f"Expected 1 file, got {count}"
    print(f"✓ File count correct: {count}")
    
    # Test remove_file
    result = unified_store.remove_file(test_file)
    assert result, "Failed to remove file"
    assert not unified_store.has_file(test_file), "File still exists after removal"
    print(f"✓ Removed file successfully")
    
    print("✅ File operations test PASSED")


def test_marker_operations():
    """Test marker operations"""
    print("\n" + "=" * 60)
    print("TEST: Marker Operations in Unified Database")
    print("=" * 60)
    
    test_file = "/test/unified/marker_file.cbz"
    marker_type = "processed"
    
    # Test add_marker
    result = unified_store.add_marker(test_file, marker_type)
    assert result, "Failed to add marker"
    print(f"✓ Added marker: {marker_type} for {test_file}")
    
    # Test has_marker
    assert unified_store.has_marker(test_file, marker_type), "Marker not found after adding"
    print(f"✓ Verified marker exists")
    
    # Test get_markers
    markers = unified_store.get_markers(marker_type)
    assert test_file in markers, "File not in markers set"
    print(f"✓ File appears in get_markers()")
    
    # Test remove_marker
    result = unified_store.remove_marker(test_file, marker_type)
    assert result, "Failed to remove marker"
    assert not unified_store.has_marker(test_file, marker_type), "Marker still exists after removal"
    print(f"✓ Removed marker successfully")
    
    print("✅ Marker operations test PASSED")


def test_combined_operations():
    """Test that files and markers work together in the same database"""
    print("\n" + "=" * 60)
    print("TEST: Combined File and Marker Operations")
    print("=" * 60)
    
    # Clear data
    unified_store.clear_all_files()
    
    # Add files
    test_files = [
        "/test/unified/file1.cbz",
        "/test/unified/file2.cbz",
        "/test/unified/file3.cbz"
    ]
    
    for f in test_files:
        unified_store.add_file(f, last_modified=time.time(), file_size=2048)
    print(f"✓ Added {len(test_files)} files")
    
    # Mark some as processed
    unified_store.add_marker(test_files[0], "processed")
    unified_store.add_marker(test_files[1], "processed")
    print(f"✓ Marked 2 files as processed")
    
    # Mark one as duplicate
    unified_store.add_marker(test_files[2], "duplicate")
    print(f"✓ Marked 1 file as duplicate")
    
    # Verify counts
    file_count = unified_store.get_file_count()
    assert file_count == 3, f"Expected 3 files, got {file_count}"
    print(f"✓ File count: {file_count}")
    
    processed_markers = unified_store.get_markers("processed")
    assert len(processed_markers) == 2, f"Expected 2 processed markers, got {len(processed_markers)}"
    print(f"✓ Processed marker count: {len(processed_markers)}")
    
    duplicate_markers = unified_store.get_markers("duplicate")
    assert len(duplicate_markers) == 1, f"Expected 1 duplicate marker, got {len(duplicate_markers)}"
    print(f"✓ Duplicate marker count: {len(duplicate_markers)}")
    
    # Test get_all_markers_by_type
    all_markers = unified_store.get_all_markers_by_type(["processed", "duplicate"])
    assert len(all_markers["processed"]) == 2, "Wrong number of processed markers"
    assert len(all_markers["duplicate"]) == 1, "Wrong number of duplicate markers"
    print(f"✓ get_all_markers_by_type works correctly")
    
    print("✅ Combined operations test PASSED")


def test_metadata_operations():
    """Test metadata operations"""
    print("\n" + "=" * 60)
    print("TEST: Metadata Operations")
    print("=" * 60)
    
    # Set metadata
    result = unified_store.set_metadata('test_key', 'test_value')
    assert result, "Failed to set metadata"
    print("✓ Set metadata: test_key = test_value")
    
    # Get metadata
    value = unified_store.get_metadata('test_key')
    assert value == 'test_value', f"Expected 'test_value', got '{value}'"
    print(f"✓ Retrieved metadata: test_key = {value}")
    
    # Test last sync timestamp
    unified_store.set_metadata('last_sync_timestamp', str(time.time()))
    timestamp = unified_store.get_last_sync_timestamp()
    assert timestamp is not None, "Last sync timestamp is None"
    print(f"✓ Last sync timestamp: {timestamp}")
    
    print("✅ Metadata operations test PASSED")


def test_backward_compatibility():
    """Test that file_store and marker_store modules still work via import"""
    print("\n" + "=" * 60)
    print("TEST: Backward Compatibility")
    print("=" * 60)
    
    # Test file_store import
    import file_store
    
    # Override paths for file_store too
    file_store.CONFIG_DIR = TEST_CONFIG_DIR
    
    test_file = "/test/compat/file.cbz"
    file_store.add_file(test_file, last_modified=time.time(), file_size=512)
    assert file_store.has_file(test_file), "file_store.add_file/has_file not working"
    print("✓ file_store module works via import")
    
    # Test marker_store import
    import marker_store
    
    # Override paths for marker_store too
    marker_store.CONFIG_DIR = TEST_CONFIG_DIR
    
    test_marker = "/test/compat/marker_file.cbz"
    marker_store.add_marker(test_marker, "processed")
    assert marker_store.has_marker(test_marker, "processed"), "marker_store.add_marker/has_marker not working"
    print("✓ marker_store module works via import")
    
    # Verify they're using the same database
    assert unified_store.has_file(test_file), "file_store not writing to unified database"
    assert unified_store.has_marker(test_marker, "processed"), "marker_store not writing to unified database"
    print("✓ Both modules write to the same unified database")
    
    print("✅ Backward compatibility test PASSED")


def test_migration():
    """Test migration from old databases"""
    print("\n" + "=" * 60)
    print("TEST: Migration from Old Databases")
    print("=" * 60)
    
    # Create temporary old-style databases
    old_marker_dir = os.path.join(TEST_CONFIG_DIR, 'markers')
    old_file_dir = os.path.join(TEST_CONFIG_DIR, 'file_store')
    os.makedirs(old_marker_dir, exist_ok=True)
    os.makedirs(old_file_dir, exist_ok=True)
    
    # Create old marker database
    import sqlite3
    old_marker_db = os.path.join(old_marker_dir, 'markers.db')
    conn = sqlite3.connect(old_marker_db)
    cursor = conn.cursor()
    cursor.execute('''
        CREATE TABLE markers (
            filepath TEXT NOT NULL,
            marker_type TEXT NOT NULL,
            PRIMARY KEY (filepath, marker_type)
        )
    ''')
    cursor.execute("INSERT INTO markers VALUES ('/old/file1.cbz', 'processed')")
    cursor.execute("INSERT INTO markers VALUES ('/old/file2.cbz', 'duplicate')")
    conn.commit()
    conn.close()
    print(f"✓ Created old marker database with 2 markers")
    
    # Create old file database
    old_file_db = os.path.join(old_file_dir, 'files.db')
    conn = sqlite3.connect(old_file_db)
    cursor = conn.cursor()
    cursor.execute('''
        CREATE TABLE files (
            filepath TEXT PRIMARY KEY,
            last_modified REAL,
            file_size INTEGER,
            added_timestamp REAL
        )
    ''')
    cursor.execute("INSERT INTO files VALUES ('/old/file1.cbz', 1234567890.0, 1024, 1234567890.0)")
    cursor.execute("INSERT INTO files VALUES ('/old/file2.cbz', 1234567891.0, 2048, 1234567891.0)")
    cursor.execute('''
        CREATE TABLE metadata (
            key TEXT PRIMARY KEY,
            value TEXT
        )
    ''')
    cursor.execute("INSERT INTO metadata VALUES ('test_meta', 'old_value')")
    conn.commit()
    conn.close()
    print(f"✓ Created old file database with 2 files and 1 metadata entry")
    
    # Run migration
    migrated_markers, migrated_files = unified_store.migrate_from_old_databases()
    print(f"✓ Migration completed: markers={migrated_markers}, files={migrated_files}")
    
    # Verify migration results
    assert unified_store.has_marker('/old/file1.cbz', 'processed'), "Marker not migrated"
    assert unified_store.has_marker('/old/file2.cbz', 'duplicate'), "Marker not migrated"
    print(f"✓ Markers migrated successfully")
    
    assert unified_store.has_file('/old/file1.cbz'), "File not migrated"
    assert unified_store.has_file('/old/file2.cbz'), "File not migrated"
    print(f"✓ Files migrated successfully")
    
    meta_value = unified_store.get_metadata('test_meta')
    assert meta_value == 'old_value', f"Metadata not migrated correctly, got '{meta_value}'"
    print(f"✓ Metadata migrated successfully")
    
    # Verify old databases were backed up
    assert not os.path.exists(old_marker_db), "Old marker database not renamed"
    assert not os.path.exists(old_file_db), "Old file database not renamed"
    print(f"✓ Old databases backed up")
    
    print("✅ Migration test PASSED")


def main():
    """Run all tests"""
    print("\n" + "=" * 60)
    print("UNIFIED STORE MODULE TESTS")
    print("=" * 60)
    print(f"Using test config directory: {TEST_CONFIG_DIR}")
    
    try:
        test_unified_database_structure()
        test_file_operations()
        test_marker_operations()
        test_combined_operations()
        test_metadata_operations()
        test_backward_compatibility()
        test_migration()
        
        print("\n" + "=" * 60)
        print("✅ ALL TESTS PASSED")
        print("=" * 60)
        print(f"\nUnified database location: {unified_store.DB_PATH}")
        print("Benefits:")
        print("  • Single database file instead of two separate databases")
        print("  • Shared connection pool and WAL mode")
        print("  • Simpler backup and maintenance")
        print("  • Better atomicity for operations involving both files and markers")
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
