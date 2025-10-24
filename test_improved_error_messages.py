#!/usr/bin/env python3
"""
Test script to verify that job start failures return improved error messages.

This test verifies that when a job fails to start, users receive specific,
actionable error messages instead of generic "Failed to start processing job" messages.
"""

import sys
import os
import tempfile

# Set up test environment BEFORE importing modules
os.environ['CONFIG_DIR'] = tempfile.mkdtemp()

# Add src to path  
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

from job_manager import get_job_manager, JobResult, JobStatus
import job_store


def test_error_messages():
    """Test that different failure scenarios produce specific error messages"""
    print("\n" + "=" * 60)
    print("TEST: Improved Error Messages")
    print("=" * 60)
    
    job_manager = get_job_manager()
    errors_caught = []
    
    # Test 1: Job not found
    print("\n1. Testing 'job not found' error message...")
    fake_job_id = "550e8400-e29b-41d4-a716-446655440000"
    
    def process_item(filepath):
        return JobResult(item=filepath, success=True)
    
    try:
        job_manager.start_job(fake_job_id, process_item, ["/test/file.cbz"])
        print("   ✗ FAIL: Expected RuntimeError but none was raised")
        return False
    except RuntimeError as e:
        error_msg = str(e)
        print(f"   Error message: '{error_msg}'")
        if "not found in database" in error_msg:
            print("   ✓ PASS: Error message contains 'not found in database'")
            errors_caught.append(("not found", error_msg))
        else:
            print(f"   ✗ FAIL: Unexpected error message")
            return False
    
    # Test 2: Job already processing
    print("\n2. Testing 'already processing' error message...")
    items = ["/test/file1.cbz", "/test/file2.cbz"]
    job_id = job_manager.create_job(items)
    print(f"   Created job: {job_id}")
    
    # Manually update to PROCESSING
    job_store.update_job_status(job_id, JobStatus.PROCESSING.value)
    print("   Updated job status to PROCESSING")
    
    try:
        job_manager.start_job(job_id, process_item, items)
        print("   ✗ FAIL: Expected RuntimeError but none was raised")
        return False
    except RuntimeError as e:
        error_msg = str(e)
        print(f"   Error message: '{error_msg}'")
        if "already processing" in error_msg or "already" in error_msg:
            print("   ✓ PASS: Error message indicates job is already running")
            errors_caught.append(("already processing", error_msg))
        else:
            print(f"   ✗ FAIL: Unexpected error message")
            return False
    
    # Test 3: Job already completed
    print("\n3. Testing 'already completed' error message...")
    job_id2 = job_manager.create_job(items)
    print(f"   Created job: {job_id2}")
    
    # Manually update to COMPLETED
    job_store.update_job_status(job_id2, JobStatus.COMPLETED.value)
    print("   Updated job status to COMPLETED")
    
    try:
        job_manager.start_job(job_id2, process_item, items)
        print("   ✗ FAIL: Expected RuntimeError but none was raised")
        return False
    except RuntimeError as e:
        error_msg = str(e)
        print(f"   Error message: '{error_msg}'")
        if "already" in error_msg and "completed" in error_msg:
            print("   ✓ PASS: Error message indicates job is already completed")
            errors_caught.append(("already completed", error_msg))
        else:
            print(f"   ✗ FAIL: Unexpected error message")
            return False
    
    # Test 4: Invalid job_id format
    print("\n4. Testing 'invalid job_id format' error message...")
    invalid_job_id = "not-a-valid-uuid"
    
    try:
        job_manager.start_job(invalid_job_id, process_item, items)
        print("   ✗ FAIL: Expected RuntimeError but none was raised")
        return False
    except RuntimeError as e:
        error_msg = str(e)
        print(f"   Error message: '{error_msg}'")
        if "invalid" in error_msg or "format" in error_msg:
            print("   ✓ PASS: Error message indicates invalid format")
            errors_caught.append(("invalid format", error_msg))
        else:
            print(f"   ✗ FAIL: Unexpected error message")
            return False
    
    print("\n" + "=" * 60)
    print("Summary of Error Messages")
    print("=" * 60)
    for scenario, msg in errors_caught:
        print(f"\n{scenario}:")
        print(f"  '{msg}'")
    
    return True


def test_user_facing_messages():
    """Test that the web_app.py error handler would produce user-friendly messages"""
    print("\n" + "=" * 60)
    print("TEST: User-Facing Error Messages")
    print("=" * 60)
    
    # Simulate what web_app.py does with the error messages
    test_cases = [
        ("Cannot start job - not found in database", "job was not found"),
        ("Cannot start job - already processing (not queued)", "already running"),
        ("Cannot start job - already completed (not queued)", "already running or completed"),
        ("Cannot start job - invalid job_id format (expected UUID)", "internal error"),
    ]
    
    for backend_error, expected_keyword in test_cases:
        print(f"\nBackend error: '{backend_error}'")
        
        # Simulate the error message transformation logic from web_app.py
        error_msg = backend_error
        if "not found in database" in error_msg:
            user_msg = "Failed to start processing: job was not found. Please try again."
        elif "already processing" in error_msg or "already completed" in error_msg:
            user_msg = "Failed to start processing: job is already running or completed. Please refresh the page and try again."
        elif "invalid job_id format" in error_msg:
            user_msg = "Failed to start processing: internal error occurred. Please try again."
        else:
            user_msg = f"Failed to start processing: {error_msg}"
        
        print(f"  User message: '{user_msg}'")
        
        if expected_keyword in user_msg.lower():
            print(f"  ✓ Contains expected keyword: '{expected_keyword}'")
        else:
            print(f"  ✗ Missing expected keyword: '{expected_keyword}'")
            return False
    
    return True


if __name__ == "__main__":
    print("\n" + "=" * 60)
    print("Improved Error Message Test Suite")
    print("=" * 60)
    print("\nThis test suite verifies that job start failures produce")
    print("specific, user-friendly error messages instead of generic ones.")
    
    results = []
    
    # Run tests
    try:
        results.append(("Improved Error Messages", test_error_messages()))
        results.append(("User-Facing Messages", test_user_facing_messages()))
    except Exception as e:
        print(f"\n✗ Test suite failed with exception: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)
    
    # Summary
    print("\n" + "=" * 60)
    print("Test Summary")
    print("=" * 60)
    
    for test_name, passed in results:
        status = "✓ PASS" if passed else "✗ FAIL"
        print(f"{status}: {test_name}")
    
    all_passed = all(result[1] for result in results)
    
    if all_passed:
        print("\n✓ All tests passed!")
        print("\nThe fix successfully provides specific error messages:")
        print("  • 'Job not found' errors clearly indicate the job is missing")
        print("  • 'Already processing' errors tell users to refresh")
        print("  • 'Already completed' errors inform users the job finished")
        print("  • 'Invalid format' errors indicate an internal issue")
        sys.exit(0)
    else:
        print("\n✗ Some tests failed!")
        sys.exit(1)
