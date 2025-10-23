"""
Test performance optimizations to ensure they work correctly.
"""
import os
import sys
import tempfile
import shutil

# Add src directory to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

def test_config_db_cache_size():
    """Test that DB_CACHE_SIZE_MB configuration works"""
    from config import get_db_cache_size_mb, DEFAULT_DB_CACHE_SIZE_MB
    
    # Test default value
    if 'DB_CACHE_SIZE_MB' in os.environ:
        del os.environ['DB_CACHE_SIZE_MB']
    
    cache_size = get_db_cache_size_mb()
    assert cache_size == DEFAULT_DB_CACHE_SIZE_MB, f"Expected {DEFAULT_DB_CACHE_SIZE_MB}, got {cache_size}"
    print(f"✓ Default DB cache size: {cache_size}MB")
    
    # Test environment variable
    os.environ['DB_CACHE_SIZE_MB'] = '128'
    cache_size = get_db_cache_size_mb()
    assert cache_size == 128, f"Expected 128, got {cache_size}"
    print(f"✓ Custom DB cache size from env: {cache_size}MB")
    
    # Clean up
    del os.environ['DB_CACHE_SIZE_MB']


def test_regex_compilation():
    """Test that regex patterns are compiled"""
    try:
        from process_file import _CHAPTER_KEYWORD_PATTERN, _NUMBER_PATTERN
        
        # Check patterns are compiled regex objects
        assert hasattr(_CHAPTER_KEYWORD_PATTERN, 'search'), "Pattern should be compiled"
        assert hasattr(_NUMBER_PATTERN, 'search'), "Pattern should be compiled"
        print("✓ Regex patterns are pre-compiled")
    except ImportError as e:
        print(f"⚠ Skipping regex test (missing dependency: {e})")


def test_parse_chapter_number():
    """Test chapter number parsing with compiled patterns"""
    try:
        from process_file import parse_chapter_number
        
        # Test various formats
        test_cases = [
            ("Batman - Chapter 5.cbz", "5"),
            ("Manga Ch 71.4.cbz", "71.4"),
            ("Series - 123.cbz", "123"),
            ("Comic Chapter 01.cbz", "01"),
        ]
        
        for filename, expected in test_cases:
            result = parse_chapter_number(filename)
            assert result == expected, f"Expected {expected}, got {result} for {filename}"
            print(f"✓ Parsed '{filename}' -> '{result}'")
    except ImportError as e:
        print(f"⚠ Skipping parse test (missing dependency: {e})")


def test_batch_marker_operations():
    """Test batch marker add/remove operations"""
    # Set up temporary config directory
    temp_dir = tempfile.mkdtemp()
    orig_config_dir = None
    
    try:
        # Mock CONFIG_DIR for testing
        import unified_store
        orig_config_dir = unified_store.CONFIG_DIR
        unified_store.CONFIG_DIR = temp_dir
        unified_store.STORE_DIR = os.path.join(temp_dir, 'store')
        unified_store.DB_PATH = os.path.join(unified_store.STORE_DIR, 'test.db')
        
        # Reinitialize database
        unified_store._db_initialized = False
        unified_store.init_db()
        
        # Test batch add
        test_files = [f'/test/file{i}.cbz' for i in range(10)]
        added = unified_store.batch_add_markers(test_files, 'processed')
        assert added == 10, f"Expected 10 markers added, got {added}"
        print(f"✓ Batch added {added} markers")
        
        # Verify markers were added
        markers = unified_store.get_markers('processed')
        assert len(markers) == 10, f"Expected 10 markers, got {len(markers)}"
        print(f"✓ Verified {len(markers)} markers exist")
        
        # Test batch remove
        removed = unified_store.batch_remove_markers(test_files[:5], 'processed')
        assert removed == 5, f"Expected 5 markers removed, got {removed}"
        print(f"✓ Batch removed {removed} markers")
        
        # Verify markers were removed
        markers = unified_store.get_markers('processed')
        assert len(markers) == 5, f"Expected 5 markers remaining, got {len(markers)}"
        print(f"✓ Verified {len(markers)} markers remain")
        
    finally:
        # Clean up
        if orig_config_dir:
            unified_store.CONFIG_DIR = orig_config_dir
            unified_store.STORE_DIR = os.path.join(orig_config_dir, 'store')
            unified_store.DB_PATH = os.path.join(unified_store.STORE_DIR, 'comicmaintainer.db')
        shutil.rmtree(temp_dir, ignore_errors=True)


def test_database_pragmas():
    """Test that database pragmas are applied correctly"""
    import tempfile
    import sqlite3
    
    temp_dir = tempfile.mkdtemp()
    try:
        # Mock CONFIG_DIR for testing
        import unified_store
        orig_config_dir = unified_store.CONFIG_DIR
        unified_store.CONFIG_DIR = temp_dir
        unified_store.STORE_DIR = os.path.join(temp_dir, 'store')
        unified_store.DB_PATH = os.path.join(unified_store.STORE_DIR, 'test.db')
        
        # Reinitialize database
        unified_store._db_initialized = False
        unified_store.init_db()
        
        # Get a connection and check pragmas
        with unified_store.get_db_connection() as conn:
            cursor = conn.cursor()
            
            # Check WAL mode
            cursor.execute('PRAGMA journal_mode')
            journal_mode = cursor.fetchone()[0]
            assert journal_mode.lower() == 'wal', f"Expected WAL mode, got {journal_mode}"
            print(f"✓ Journal mode: {journal_mode}")
            
            # Check synchronous mode
            cursor.execute('PRAGMA synchronous')
            synchronous = cursor.fetchone()[0]
            assert synchronous == 1, f"Expected NORMAL (1), got {synchronous}"  # 1 = NORMAL
            print(f"✓ Synchronous mode: NORMAL")
            
            # Check cache size (should be negative = KB)
            cursor.execute('PRAGMA cache_size')
            cache_size = cursor.fetchone()[0]
            assert cache_size < 0, f"Expected negative cache_size (KB), got {cache_size}"
            print(f"✓ Cache size: {abs(cache_size) // 1024}MB")
            
            # Check temp_store
            cursor.execute('PRAGMA temp_store')
            temp_store = cursor.fetchone()[0]
            assert temp_store == 2, f"Expected MEMORY (2), got {temp_store}"  # 2 = MEMORY
            print(f"✓ Temp store: MEMORY")
        
        # Restore original config
        unified_store.CONFIG_DIR = orig_config_dir
        unified_store.STORE_DIR = os.path.join(orig_config_dir, 'store')
        unified_store.DB_PATH = os.path.join(unified_store.STORE_DIR, 'comicmaintainer.db')
        
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


if __name__ == '__main__':
    print("Testing performance optimizations...\n")
    
    try:
        test_config_db_cache_size()
        print()
        
        test_regex_compilation()
        print()
        
        test_parse_chapter_number()
        print()
        
        test_batch_marker_operations()
        print()
        
        test_database_pragmas()
        print()
        
        print("✅ All performance optimization tests passed!")
        sys.exit(0)
    except Exception as e:
        print(f"\n❌ Test failed: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)
