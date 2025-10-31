#!/usr/bin/env python3
"""
Test script to verify the refactored modules work correctly.

This test ensures that:
1. file_operations.record_file_change works as expected
2. logging_setup.setup_logging works as expected
3. All modules can import and use the shared functions
"""

import sys
import os
import tempfile
import shutil
import logging

# Add src to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

# Set up temporary config directory for tests
TEST_CONFIG_DIR = tempfile.mkdtemp(prefix='test_refactor_')
os.environ['CONFIG_DIR_OVERRIDE'] = TEST_CONFIG_DIR

def test_file_operations_module():
    """Test that file_operations module can be imported and works"""
    print("\n" + "=" * 60)
    print("TEST: File Operations Module")
    print("=" * 60)
    
    try:
        # Import after setting test config
        import unified_store
        import file_store
        from file_operations import record_file_change
        
        # Override CONFIG_DIR in unified_store module
        unified_store.CONFIG_DIR = TEST_CONFIG_DIR
        unified_store.STORE_DIR = os.path.join(TEST_CONFIG_DIR, 'store')
        unified_store.DB_PATH = os.path.join(unified_store.STORE_DIR, 'comicmaintainer.db')
        unified_store._db_initialized = False
        
        # Initialize file store
        file_store.init_db()
        
        # Test adding a file
        test_file = "/test/comics/Batman.cbz"
        record_file_change('add', new_path=test_file)
        
        # Verify file was added
        files = file_store.get_all_files()
        assert test_file in files, f"File not found in store: {test_file}"
        print(f"✓ Successfully added file: {test_file}")
        
        # Test renaming a file
        new_path = "/test/comics/Batman_001.cbz"
        record_file_change('rename', old_path=test_file, new_path=new_path)
        
        # Verify file was renamed
        files = file_store.get_all_files()
        assert new_path in files, f"Renamed file not found: {new_path}"
        assert test_file not in files, f"Old file still exists: {test_file}"
        print(f"✓ Successfully renamed file: {test_file} -> {new_path}")
        
        # Test removing a file
        record_file_change('remove', old_path=new_path)
        
        # Verify file was removed
        files = file_store.get_all_files()
        assert new_path not in files, f"File still exists after removal: {new_path}"
        print(f"✓ Successfully removed file: {new_path}")
        
        print("✅ File operations module test PASSED")
        return True
        
    except Exception as e:
        print(f"❌ File operations module test FAILED: {e}")
        import traceback
        traceback.print_exc()
        return False


def test_logging_setup_module():
    """Test that logging_setup module works correctly"""
    print("\n" + "=" * 60)
    print("TEST: Logging Setup Module")
    print("=" * 60)
    
    try:
        from logging_setup import setup_logging
        
        # Clear any existing handlers
        logger = logging.getLogger()
        for handler in logger.handlers[:]:
            logger.removeHandler(handler)
        
        # Test setup with rotation
        log_handler = setup_logging('TEST', use_rotation=True)
        
        # Verify handler was added
        assert log_handler is not None, "Handler not returned"
        print("✓ Logging setup with rotation successful")
        
        # Test logging
        logging.info("Test log message")
        print("✓ Test log message written")
        
        # Verify log file exists
        log_file = os.path.join(TEST_CONFIG_DIR, 'Log', 'ComicMaintainer.log')
        assert os.path.exists(log_file), f"Log file not created: {log_file}"
        print(f"✓ Log file created: {log_file}")
        
        # Clear handlers for next test
        for handler in logger.handlers[:]:
            logger.removeHandler(handler)
        
        # Test setup without rotation
        log_handler = setup_logging('TEST2', use_rotation=False)
        assert log_handler is not None, "Handler not returned"
        print("✓ Logging setup without rotation successful")
        
        print("✅ Logging setup module test PASSED")
        return True
        
    except Exception as e:
        print(f"❌ Logging setup module test FAILED: {e}")
        import traceback
        traceback.print_exc()
        return False


def test_module_imports():
    """Test that all modules can import the shared functions"""
    print("\n" + "=" * 60)
    print("TEST: Module Imports")
    print("=" * 60)
    
    try:
        # Test that process_file can be imported (will import file_operations and logging_setup)
        print("Testing process_file imports...")
        # Note: We can't fully import process_file without comicapi installed
        # but we can verify the modules exist
        from file_operations import record_file_change
        from logging_setup import setup_logging
        print("✓ file_operations and logging_setup modules can be imported")
        
        # Verify functions are callable
        assert callable(record_file_change), "record_file_change is not callable"
        assert callable(setup_logging), "setup_logging is not callable"
        print("✓ Functions are callable")
        
        print("✅ Module imports test PASSED")
        return True
        
    except Exception as e:
        print(f"❌ Module imports test FAILED: {e}")
        import traceback
        traceback.print_exc()
        return False


def run_tests():
    """Run all tests"""
    print("\n" + "=" * 60)
    print("REFACTORING TESTS")
    print("=" * 60)
    
    results = []
    results.append(test_logging_setup_module())
    results.append(test_file_operations_module())
    results.append(test_module_imports())
    
    print("\n" + "=" * 60)
    if all(results):
        print("✅ ALL TESTS PASSED")
    else:
        print("❌ SOME TESTS FAILED")
    print("=" * 60)
    
    # Cleanup
    print(f"\nCleaning up test directory: {TEST_CONFIG_DIR}")
    shutil.rmtree(TEST_CONFIG_DIR)
    
    return all(results)


if __name__ == "__main__":
    success = run_tests()
    sys.exit(0 if success else 1)
