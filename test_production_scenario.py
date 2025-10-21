#!/usr/bin/env python3
"""
Production scenario test for unified database.

This test simulates what would happen when an existing installation
upgrades to the new unified database version.
"""

import sys
import os
import tempfile
import shutil
import sqlite3
import time

# Add src to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

def create_test_environment():
    """Create a test environment simulating an existing installation"""
    test_dir = tempfile.mkdtemp(prefix='test_production_')
    
    # Create old-style database directories
    marker_dir = os.path.join(test_dir, 'markers')
    file_store_dir = os.path.join(test_dir, 'file_store')
    os.makedirs(marker_dir)
    os.makedirs(file_store_dir)
    
    # Create old marker database with sample data
    marker_db = os.path.join(marker_dir, 'markers.db')
    conn = sqlite3.connect(marker_db)
    cursor = conn.cursor()
    cursor.execute('''
        CREATE TABLE markers (
            filepath TEXT NOT NULL,
            marker_type TEXT NOT NULL,
            PRIMARY KEY (filepath, marker_type)
        )
    ''')
    # Add sample markers
    sample_markers = [
        ('/comics/Batman/Batman #001.cbz', 'processed'),
        ('/comics/Batman/Batman #002.cbz', 'processed'),
        ('/comics/Batman/Batman #003.cbz', 'processed'),
        ('/comics/Superman/Superman #001.cbz', 'processed'),
        ('/comics/duplicates/Batman #001 (1).cbz', 'duplicate'),
        ('/comics/temp/modified.cbz', 'web_modified'),
    ]
    for filepath, marker_type in sample_markers:
        cursor.execute('INSERT INTO markers VALUES (?, ?)', (filepath, marker_type))
    conn.commit()
    conn.close()
    
    # Create old file store database with sample data
    file_db = os.path.join(file_store_dir, 'files.db')
    conn = sqlite3.connect(file_db)
    cursor = conn.cursor()
    cursor.execute('''
        CREATE TABLE files (
            filepath TEXT PRIMARY KEY,
            last_modified REAL,
            file_size INTEGER,
            added_timestamp REAL
        )
    ''')
    cursor.execute('''
        CREATE TABLE metadata (
            key TEXT PRIMARY KEY,
            value TEXT
        )
    ''')
    # Add sample files
    sample_files = [
        ('/comics/Batman/Batman #001.cbz', 1234567890.0, 1024000, 1234567890.0),
        ('/comics/Batman/Batman #002.cbz', 1234567891.0, 1025000, 1234567891.0),
        ('/comics/Batman/Batman #003.cbz', 1234567892.0, 1026000, 1234567892.0),
        ('/comics/Superman/Superman #001.cbz', 1234567893.0, 2048000, 1234567893.0),
        ('/comics/duplicates/Batman #001 (1).cbz', 1234567894.0, 1024000, 1234567894.0),
    ]
    for filepath, last_modified, file_size, added_timestamp in sample_files:
        cursor.execute('INSERT INTO files VALUES (?, ?, ?, ?)',
                      (filepath, last_modified, file_size, added_timestamp))
    
    # Add sample metadata
    cursor.execute("INSERT INTO metadata VALUES ('last_sync_timestamp', '1234567895.0')")
    cursor.execute("INSERT INTO metadata VALUES ('version', '1.0')")
    conn.commit()
    conn.close()
    
    return test_dir, marker_db, file_db


def test_production_migration():
    """Test the migration process in a production-like scenario"""
    print("\n" + "=" * 70)
    print("PRODUCTION SCENARIO TEST: Upgrade with Existing Databases")
    print("=" * 70)
    
    # Step 1: Create test environment with old databases
    print("\n[Step 1] Creating test environment with existing databases...")
    test_dir, old_marker_db, old_file_db = create_test_environment()
    print(f"✓ Created test environment: {test_dir}")
    print(f"  • Old marker database: {old_marker_db}")
    print(f"  • Old file store database: {old_file_db}")
    
    # Verify old databases exist
    assert os.path.exists(old_marker_db), "Old marker database not created"
    assert os.path.exists(old_file_db), "Old file store database not created"
    print("✓ Old databases verified")
    
    # Step 2: Import new modules (simulating service startup after upgrade)
    print("\n[Step 2] Starting service with new unified database code...")
    
    import unified_store
    import file_store
    import marker_store
    import markers
    
    # Override paths to use test directory
    unified_store.CONFIG_DIR = test_dir
    unified_store.STORE_DIR = os.path.join(test_dir, 'store')
    unified_store.DB_PATH = os.path.join(unified_store.STORE_DIR, 'comicmaintainer.db')
    unified_store._db_initialized = False
    
    print("✓ Modules imported")
    
    # Step 3: Initialize database (triggers migration)
    print("\n[Step 3] Initializing database (migration should happen automatically)...")
    start_time = time.time()
    unified_store.init_db()
    migration_time = time.time() - start_time
    print(f"✓ Database initialized in {migration_time:.3f} seconds")
    
    # Step 4: Trigger migration
    print("\n[Step 4] Running migration from old databases...")
    migrated_markers, migrated_files = unified_store.migrate_from_old_databases()
    print(f"✓ Migration completed:")
    print(f"  • Markers migrated: {migrated_markers}")
    print(f"  • Files migrated: {migrated_files}")
    
    # Step 5: Verify migration results
    print("\n[Step 5] Verifying migration results...")
    
    # Check that unified database exists
    assert os.path.exists(unified_store.DB_PATH), "Unified database not created"
    print(f"✓ Unified database created: {unified_store.DB_PATH}")
    
    # Check that old databases were backed up
    marker_backups = [f for f in os.listdir(os.path.dirname(old_marker_db)) 
                     if f.startswith('markers.db.migrated')]
    file_backups = [f for f in os.listdir(os.path.dirname(old_file_db)) 
                   if f.startswith('files.db.migrated')]
    
    if migrated_markers:
        assert len(marker_backups) > 0, "Marker database not backed up"
        print(f"✓ Old marker database backed up: {marker_backups[0]}")
    
    if migrated_files:
        assert len(file_backups) > 0, "File database not backed up"
        print(f"✓ Old file store database backed up: {file_backups[0]}")
    
    # Verify all markers were migrated
    processed_markers = unified_store.get_markers('processed')
    duplicate_markers = unified_store.get_markers('duplicate')
    web_modified_markers = unified_store.get_markers('web_modified')
    
    print(f"\n[Markers Verification]")
    print(f"  • Processed markers: {len(processed_markers)}")
    print(f"  • Duplicate markers: {len(duplicate_markers)}")
    print(f"  • Web modified markers: {len(web_modified_markers)}")
    
    assert len(processed_markers) == 4, f"Expected 4 processed markers, got {len(processed_markers)}"
    assert len(duplicate_markers) == 1, f"Expected 1 duplicate marker, got {len(duplicate_markers)}"
    assert len(web_modified_markers) == 1, f"Expected 1 web_modified marker, got {len(web_modified_markers)}"
    print("✓ All markers migrated correctly")
    
    # Verify all files were migrated
    all_files = unified_store.get_all_files()
    file_count = unified_store.get_file_count()
    
    print(f"\n[Files Verification]")
    print(f"  • Total files: {file_count}")
    
    assert file_count == 5, f"Expected 5 files, got {file_count}"
    print("✓ All files migrated correctly")
    
    # Verify metadata was migrated
    last_sync = unified_store.get_metadata('last_sync_timestamp')
    version = unified_store.get_metadata('version')
    
    print(f"\n[Metadata Verification]")
    print(f"  • last_sync_timestamp: {last_sync}")
    print(f"  • version: {version}")
    
    assert last_sync == '1234567895.0', "Metadata not migrated correctly"
    assert version == '1.0', "Metadata not migrated correctly"
    print("✓ Metadata migrated correctly")
    
    # Step 6: Test that all APIs still work
    print("\n[Step 6] Testing APIs with unified database...")
    
    # Test file operations
    test_file = '/comics/NewComic/Issue #001.cbz'
    unified_store.add_file(test_file, time.time(), 3000000)
    assert unified_store.has_file(test_file), "File operation failed"
    print("✓ File operations work")
    
    # Test marker operations
    unified_store.add_marker(test_file, 'processed')
    assert unified_store.has_marker(test_file, 'processed'), "Marker operation failed"
    print("✓ Marker operations work")
    
    # Test that markers.py still works
    markers.CONFIG_DIR = test_dir
    markers.MARKERS_DIR = os.path.join(test_dir, 'markers')
    markers.mark_file_duplicate(test_file)
    assert markers.is_file_duplicate(test_file), "markers.py function failed"
    print("✓ markers.py functions work")
    
    # Test combined query
    all_marker_data = unified_store.get_all_markers_by_type(['processed', 'duplicate'])
    assert test_file in all_marker_data['processed'], "Combined query failed"
    assert test_file in all_marker_data['duplicate'], "Combined query failed"
    print("✓ Combined queries work")
    
    # Step 7: Verify database structure
    print("\n[Step 7] Verifying unified database structure...")
    conn = sqlite3.connect(unified_store.DB_PATH)
    cursor = conn.cursor()
    
    # Check tables
    cursor.execute("SELECT name FROM sqlite_master WHERE type='table'")
    tables = {row[0] for row in cursor.fetchall()}
    expected_tables = {'files', 'markers', 'metadata'}
    assert expected_tables.issubset(tables), f"Missing tables. Expected {expected_tables}, got {tables}"
    print(f"✓ Tables present: {', '.join(sorted(tables))}")
    
    # Check indexes
    cursor.execute("SELECT name FROM sqlite_master WHERE type='index'")
    indexes = {row[0] for row in cursor.fetchall()}
    expected_indexes = {
        'idx_files_last_modified',
        'idx_files_added_timestamp',
        'idx_markers_filepath',
        'idx_markers_type'
    }
    assert expected_indexes.issubset(indexes), "Missing indexes"
    print(f"✓ Indexes present: {len(indexes)} indexes")
    
    # Check WAL mode
    cursor.execute("PRAGMA journal_mode")
    journal_mode = cursor.fetchone()[0]
    assert journal_mode.lower() == 'wal', f"WAL mode not enabled, got {journal_mode}"
    print(f"✓ WAL mode enabled: {journal_mode}")
    
    conn.close()
    
    # Step 8: Final statistics
    print("\n[Step 8] Final Statistics...")
    final_file_count = unified_store.get_file_count()
    final_processed = len(unified_store.get_markers('processed'))
    final_duplicate = len(unified_store.get_markers('duplicate'))
    final_web_modified = len(unified_store.get_markers('web_modified'))
    
    print(f"  • Total files in database: {final_file_count}")
    print(f"  • Processed markers: {final_processed}")
    print(f"  • Duplicate markers: {final_duplicate}")
    print(f"  • Web modified markers: {final_web_modified}")
    
    # Calculate database size
    db_size = os.path.getsize(unified_store.DB_PATH)
    print(f"  • Database size: {db_size / 1024:.1f} KB")
    
    print("\n" + "=" * 70)
    print("✅ PRODUCTION SCENARIO TEST PASSED")
    print("=" * 70)
    print("\nSummary:")
    print("  • Automatic migration from old databases ✅")
    print("  • All data preserved ✅")
    print("  • Old databases backed up ✅")
    print("  • All APIs work correctly ✅")
    print("  • Database structure correct ✅")
    print("  • No data loss ✅")
    print("  • No breaking changes ✅")
    print(f"\nMigration completed in {migration_time:.3f} seconds")
    
    # Cleanup
    shutil.rmtree(test_dir)
    print(f"\nCleaned up test directory: {test_dir}")
    
    return True


if __name__ == '__main__':
    try:
        test_production_migration()
        sys.exit(0)
    except AssertionError as e:
        print(f"\n❌ TEST FAILED: {e}")
        sys.exit(1)
    except Exception as e:
        print(f"\n❌ ERROR: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)
