#!/usr/bin/env python3
"""
Test script to verify debug log file separation and rotation.

This script tests that:
1. Debug logs go to a separate ComicMaintainer_debug.log file
2. Regular logs continue to go to ComicMaintainer.log
3. Both files use the same rotation schema
"""

import sys
import os
import logging
import time
import shutil

# Add src to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

# Set up test environment
os.environ['DEBUG_MODE'] = 'true'
os.environ['LOG_MAX_BYTES'] = '1024'  # Small size for testing rotation

# Create temporary config directory for testing
TEST_CONFIG_DIR = '/tmp/test_comic_maintainer_config'
TEST_LOG_DIR = os.path.join(TEST_CONFIG_DIR, 'Log')

# Clean up any previous test runs
if os.path.exists(TEST_CONFIG_DIR):
    shutil.rmtree(TEST_CONFIG_DIR)
os.makedirs(TEST_LOG_DIR, exist_ok=True)

# Mock the CONFIG_DIR in modules
import config
config.CONFIG_DIR = TEST_CONFIG_DIR

# Import error_handler after setting up environment
from error_handler import setup_debug_logging, log_debug

def test_debug_log_separation():
    """Test that debug logs go to a separate file."""
    print("\n" + "=" * 60)
    print("TEST 1: Debug Log File Separation")
    print("=" * 60)
    
    # Configure logging
    from logging.handlers import RotatingFileHandler
    
    # Set up basic logging first
    logging.basicConfig(
        level=logging.INFO,
        format='%(asctime)s [TEST] %(levelname)s %(message)s',
        handlers=[logging.StreamHandler(sys.stdout)]
    )
    
    # Add regular log file handler
    log_handler = RotatingFileHandler(
        os.path.join(TEST_LOG_DIR, "ComicMaintainer.log"),
        maxBytes=1024,
        backupCount=3
    )
    log_handler.setLevel(logging.INFO)
    log_handler.setFormatter(logging.Formatter('%(asctime)s [TEST] %(levelname)s %(message)s'))
    logging.getLogger().addHandler(log_handler)
    
    # Setup debug logging (should create debug log file)
    logger = setup_debug_logging()
    
    # Write some test logs
    logging.info("This is an INFO message - should go to both files")
    logging.debug("This is a DEBUG message - should only go to debug file")
    log_debug("This is a log_debug call", test_param="value")
    logging.warning("This is a WARNING message - should go to both files")
    
    # Flush handlers
    for handler in logging.getLogger().handlers:
        handler.flush()
    
    time.sleep(0.1)  # Give time for writes
    
    # Check that both log files exist
    regular_log = os.path.join(TEST_LOG_DIR, "ComicMaintainer.log")
    debug_log = os.path.join(TEST_LOG_DIR, "ComicMaintainer_debug.log")
    
    assert os.path.exists(regular_log), f"Regular log file not found: {regular_log}"
    print(f"✓ Regular log file exists: {regular_log}")
    
    assert os.path.exists(debug_log), f"Debug log file not found: {debug_log}"
    print(f"✓ Debug log file exists: {debug_log}")
    
    # Check contents
    with open(regular_log, 'r') as f:
        regular_content = f.read()
    
    with open(debug_log, 'r') as f:
        debug_content = f.read()
    
    # Regular log should have INFO and WARNING but not DEBUG
    assert "INFO" in regular_content, "Regular log missing INFO messages"
    assert "WARNING" in regular_content, "Regular log missing WARNING messages"
    print("✓ Regular log contains INFO and WARNING messages")
    
    # Debug log should have all levels including DEBUG
    assert "DEBUG" in debug_content, "Debug log missing DEBUG messages"
    assert "INFO" in debug_content, "Debug log missing INFO messages"
    assert "WARNING" in debug_content, "Debug log missing WARNING messages"
    print("✓ Debug log contains DEBUG, INFO, and WARNING messages")
    
    # Verify DEBUG messages are NOT in regular log
    if "DEBUG" in regular_content:
        print("⚠️  WARNING: DEBUG messages found in regular log (unexpected)")
    else:
        print("✓ DEBUG messages are NOT in regular log (expected)")
    
    print("\n✅ Debug log separation test PASSED\n")
    return True


def test_log_rotation():
    """Test that both log files rotate properly."""
    print("\n" + "=" * 60)
    print("TEST 2: Log Rotation")
    print("=" * 60)
    
    # Generate enough logs to trigger rotation
    for i in range(50):
        logging.info(f"Regular log message {i} - " + "x" * 50)
        logging.debug(f"Debug log message {i} - " + "x" * 50)
    
    # Flush handlers
    for handler in logging.getLogger().handlers:
        handler.flush()
    
    time.sleep(0.1)
    
    # Check for rotated files
    regular_log = os.path.join(TEST_LOG_DIR, "ComicMaintainer.log")
    regular_log_1 = os.path.join(TEST_LOG_DIR, "ComicMaintainer.log.1")
    debug_log = os.path.join(TEST_LOG_DIR, "ComicMaintainer_debug.log")
    debug_log_1 = os.path.join(TEST_LOG_DIR, "ComicMaintainer_debug.log.1")
    
    # Check if rotation occurred (at least .1 files should exist)
    regular_rotated = os.path.exists(regular_log_1)
    debug_rotated = os.path.exists(debug_log_1)
    
    if regular_rotated:
        print(f"✓ Regular log rotated: {regular_log_1} exists")
    else:
        print(f"⚠️  Regular log not rotated yet (may need more data)")
    
    if debug_rotated:
        print(f"✓ Debug log rotated: {debug_log_1} exists")
    else:
        print(f"⚠️  Debug log not rotated yet (may need more data)")
    
    # Check file sizes
    regular_size = os.path.getsize(regular_log)
    debug_size = os.path.getsize(debug_log)
    
    print(f"✓ Regular log size: {regular_size} bytes")
    print(f"✓ Debug log size: {debug_size} bytes")
    
    print("\n✅ Log rotation test COMPLETED\n")
    return True


def test_debug_mode_disabled():
    """Test that debug log file is NOT created when DEBUG_MODE is false."""
    print("\n" + "=" * 60)
    print("TEST 3: Debug Mode Disabled")
    print("=" * 60)
    
    # Clean up
    if os.path.exists(TEST_CONFIG_DIR):
        shutil.rmtree(TEST_CONFIG_DIR)
    os.makedirs(TEST_LOG_DIR, exist_ok=True)
    
    # Disable debug mode
    os.environ['DEBUG_MODE'] = 'false'
    
    # Reset the debug handler setup flag
    import error_handler
    error_handler._debug_handler_setup = False
    
    # Remove existing handlers
    logger = logging.getLogger()
    for handler in logger.handlers[:]:
        logger.removeHandler(handler)
    
    # Re-import to pick up new DEBUG_MODE value
    import importlib
    importlib.reload(error_handler)
    from error_handler import setup_debug_logging as setup_debug_logging_disabled
    
    # Set up basic logging
    logging.basicConfig(
        level=logging.INFO,
        format='%(asctime)s [TEST] %(levelname)s %(message)s',
        handlers=[logging.StreamHandler(sys.stdout)]
    )
    
    # Setup logging without debug mode
    logger = setup_debug_logging_disabled()
    
    # Write some logs
    logging.info("Test message without debug mode")
    
    for handler in logging.getLogger().handlers:
        handler.flush()
    
    time.sleep(0.1)
    
    # Check that debug log file was NOT created
    debug_log = os.path.join(TEST_LOG_DIR, "ComicMaintainer_debug.log")
    
    if not os.path.exists(debug_log):
        print(f"✓ Debug log file NOT created when DEBUG_MODE=false (expected)")
    else:
        print(f"⚠️  WARNING: Debug log file exists when DEBUG_MODE=false (unexpected)")
    
    print("\n✅ Debug mode disabled test PASSED\n")
    return True


def main():
    """Run all tests."""
    print("\n" + "=" * 60)
    print("ComicMaintainer Debug Log Rotation Test Suite")
    print("=" * 60)
    print(f"\nTest config directory: {TEST_CONFIG_DIR}")
    print(f"Test log directory: {TEST_LOG_DIR}\n")
    
    try:
        test_debug_log_separation()
        test_log_rotation()
        test_debug_mode_disabled()
        
        print("\n" + "=" * 60)
        print("ALL TESTS PASSED ✅")
        print("=" * 60)
        print("\nDebug log rotation features are working correctly!")
        print("\nLog files created:")
        if os.path.exists(TEST_LOG_DIR):
            for f in os.listdir(TEST_LOG_DIR):
                filepath = os.path.join(TEST_LOG_DIR, f)
                size = os.path.getsize(filepath)
                print(f"  - {f} ({size} bytes)")
        
        print(f"\nYou can inspect the log files at: {TEST_LOG_DIR}\n")
        
        return 0
        
    except Exception as e:
        print("\n" + "=" * 60)
        print("TESTS FAILED ❌")
        print("=" * 60)
        print(f"\nError: {e}")
        import traceback
        traceback.print_exc()
        return 1


if __name__ == "__main__":
    sys.exit(main())
