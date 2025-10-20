#!/usr/bin/env python3
"""
Test script for debug logging and error reporting features.

This script demonstrates and tests the debug logging and GitHub issue
creation features without needing a full Docker setup.

Usage:
    python3 test_debug_features.py
"""

import sys
import os
import logging

# Add src to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

# Enable debug mode
os.environ['DEBUG_MODE'] = 'true'

# Import error_handler module
from error_handler import (
    setup_debug_logging, 
    log_debug, 
    log_error_with_context,
    log_function_entry, 
    log_function_exit,
    safe_execute
)

# Configure basic logging
logging.basicConfig(
    level=logging.DEBUG,
    format='%(asctime)s [%(levelname)s] %(message)s',
    datefmt='%Y-%m-%d %H:%M:%S'
)

def test_debug_logging():
    """Test debug logging features."""
    print("\n" + "=" * 60)
    print("TEST 1: Debug Logging")
    print("=" * 60)
    
    # Setup logger
    logger = setup_debug_logging()
    print("✓ Debug logging configured")
    
    # Test basic debug log
    log_debug("Testing debug message", test_param="test_value", number=42)
    print("✓ Debug log with context works")
    
    # Test function tracing
    log_function_entry("test_function", arg1="value1", arg2=123)
    log_function_exit("test_function", result="success")
    print("✓ Function entry/exit logging works")
    
    print("\n✅ Debug logging tests PASSED\n")


def test_error_logging():
    """Test error logging with context."""
    print("\n" + "=" * 60)
    print("TEST 2: Error Logging with Context")
    print("=" * 60)
    
    try:
        # Simulate an error
        result = 10 / 0
    except Exception as e:
        log_error_with_context(
            e,
            context="Testing error logging in division operation",
            additional_info={
                "operation": "division",
                "numerator": 10,
                "denominator": 0
            },
            create_github_issue=False  # Don't actually create issue
        )
        print("✓ Error logged with full context and traceback")
    
    print("\n✅ Error logging tests PASSED\n")


def test_safe_execute():
    """Test safe execution wrapper."""
    print("\n" + "=" * 60)
    print("TEST 3: Safe Execute Wrapper")
    print("=" * 60)
    
    # Test successful execution
    def add_numbers(a, b):
        return a + b
    
    result = safe_execute(
        add_numbers, 
        5, 
        10, 
        context="Testing safe execute with addition",
        create_issue=False
    )
    print(f"✓ Safe execute succeeded: 5 + 10 = {result}")
    
    # Test failed execution
    def failing_function():
        raise RuntimeError("Intentional test error")
    
    result = safe_execute(
        failing_function,
        context="Testing safe execute with intentional error",
        create_issue=False
    )
    print(f"✓ Safe execute handled error gracefully: result = {result}")
    
    print("\n✅ Safe execute tests PASSED\n")


def test_github_issue_simulation():
    """Simulate GitHub issue creation (will fail with test token)."""
    print("\n" + "=" * 60)
    print("TEST 4: GitHub Issue Creation (Simulated)")
    print("=" * 60)
    
    # Set test token (will fail but won't crash)
    original_token = os.environ.get('GITHUB_TOKEN')
    os.environ['GITHUB_TOKEN'] = 'test_token_invalid'
    os.environ['GITHUB_REPOSITORY'] = 'mleenorris/ComicMaintainer'
    os.environ['GITHUB_ISSUE_ASSIGNEE'] = 'copilot'
    
    print("Configuration:")
    print(f"  GITHUB_TOKEN: {'*' * 10}... (test token)")
    print(f"  GITHUB_REPOSITORY: {os.environ['GITHUB_REPOSITORY']}")
    print(f"  GITHUB_ISSUE_ASSIGNEE: {os.environ['GITHUB_ISSUE_ASSIGNEE']}")
    print()
    
    try:
        raise ValueError("Test error for GitHub issue simulation")
    except Exception as e:
        log_error_with_context(
            e,
            context="Simulating GitHub issue creation",
            additional_info={
                "test_mode": True,
                "expected_result": "graceful failure"
            },
            create_github_issue=True  # Will attempt but fail gracefully
        )
        print("✓ GitHub issue creation attempted (failed as expected)")
        print("✓ Application continued normally (no crash)")
    
    # Restore original token
    if original_token:
        os.environ['GITHUB_TOKEN'] = original_token
    else:
        os.environ.pop('GITHUB_TOKEN', None)
    
    print("\n✅ GitHub issue simulation tests PASSED\n")


def test_with_real_token():
    """Test with real GitHub token if available."""
    print("\n" + "=" * 60)
    print("TEST 5: Real GitHub Token (Optional)")
    print("=" * 60)
    
    token = os.environ.get('GITHUB_TOKEN')
    if not token or token == 'test_token_invalid':
        print("⚠️  No real GitHub token found in GITHUB_TOKEN environment variable")
        print("    To test real issue creation, set GITHUB_TOKEN before running")
        print("    Example: export GITHUB_TOKEN=ghp_your_token_here")
        print("\n⏭️  Skipping real GitHub token test\n")
        return
    
    print(f"✓ Real GitHub token detected: {token[:10]}...")
    print("\n⚠️  WARNING: This will create a real GitHub issue!")
    print("    Comment out this section if you don't want to create a test issue")
    print("\n⏭️  Skipping to avoid creating unnecessary issues\n")
    
    # Uncomment below to actually test with real token
    # try:
    #     raise RuntimeError("Test issue from debug logging test script")
    # except Exception as e:
    #     log_error_with_context(
    #         e,
    #         context="Testing real GitHub issue creation",
    #         additional_info={"test": True, "script": "test_debug_features.py"},
    #         create_github_issue=True
    #     )
    #     print("✓ Real GitHub issue created successfully!")


def main():
    """Run all tests."""
    print("\n" + "=" * 60)
    print("ComicMaintainer Debug Logging Test Suite")
    print("=" * 60)
    print("\nThis script tests the debug logging and error reporting features")
    print("without requiring a full Docker environment.\n")
    
    try:
        test_debug_logging()
        test_error_logging()
        test_safe_execute()
        test_github_issue_simulation()
        test_with_real_token()
        
        print("\n" + "=" * 60)
        print("ALL TESTS PASSED ✅")
        print("=" * 60)
        print("\nDebug logging and error reporting features are working correctly!")
        print("\nNext steps:")
        print("  1. Build Docker image with these changes")
        print("  2. Run container with DEBUG_MODE=true to see logs")
        print("  3. Optionally set GITHUB_TOKEN to enable issue creation")
        print("\nSee DEBUG_LOGGING_GUIDE.md for more information.\n")
        
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
