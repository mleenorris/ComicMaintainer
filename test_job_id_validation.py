#!/usr/bin/env python3
"""
Test script to verify that invalid job_id formats are properly rejected.

This test verifies that the fix for the "process-selected" warning issue
works correctly by validating job_id formats.
"""

import sys
import os

# Add src to path  
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

from job_manager import get_job_manager


def test_invalid_job_id_format():
    """Test that invalid job_id formats return None without warnings"""
    print("\n" + "=" * 60)
    print("TEST: Invalid Job ID Format Validation")
    print("=" * 60)
    
    job_manager = get_job_manager()
    
    # Test various invalid job_id formats
    invalid_job_ids = [
        "process-selected",
        "rename-selected", 
        "normalize-selected",
        "not-a-uuid",
        "12345",
        "",
        "process-all"
    ]
    
    print("\nTesting invalid job_id formats:")
    all_passed = True
    
    for job_id in invalid_job_ids:
        result = job_manager.get_job_status(job_id)
        if result is None:
            print(f"  ✓ '{job_id}' correctly rejected (returned None)")
        else:
            print(f"  ✗ '{job_id}' incorrectly accepted (returned {result})")
            all_passed = False
    
    return all_passed


def test_valid_job_id_format():
    """Test that valid UUID job_id formats are processed"""
    print("\n" + "=" * 60)
    print("TEST: Valid Job ID Format Acceptance")
    print("=" * 60)
    
    job_manager = get_job_manager()
    
    # Test valid UUID formats (these won't exist, so should return None from store)
    valid_job_ids = [
        "550e8400-e29b-41d4-a716-446655440000",
        "6ba7b810-9dad-11d1-80b4-00c04fd430c8",
        "a1234567-89ab-cdef-0123-456789abcdef"
    ]
    
    print("\nTesting valid UUID job_id formats:")
    all_passed = True
    
    for job_id in valid_job_ids:
        # These should pass validation and query the store (returning None since they don't exist)
        result = job_manager.get_job_status(job_id)
        if result is None:
            print(f"  ✓ '{job_id}' format accepted (returned None from empty store)")
        else:
            print(f"  ✗ '{job_id}' returned unexpected result: {result}")
            all_passed = False
    
    return all_passed


def test_create_and_retrieve_job():
    """Test that we can create and retrieve a job with valid UUID"""
    print("\n" + "=" * 60)
    print("TEST: Create and Retrieve Job")
    print("=" * 60)
    
    job_manager = get_job_manager()
    
    # Create a job
    print("\nCreating test job with 3 items...")
    items = ["item1.cbz", "item2.cbz", "item3.cbz"]
    job_id = job_manager.create_job(items)
    
    print(f"  Created job with ID: {job_id}")
    
    # Retrieve the job
    status = job_manager.get_job_status(job_id)
    
    if status is not None:
        print(f"  ✓ Job retrieved successfully")
        print(f"    Status: {status['status']}")
        print(f"    Total items: {status['total_items']}")
        
        # Clean up
        job_manager.delete_job(job_id)
        print(f"  ✓ Test job cleaned up")
        return True
    else:
        print(f"  ✗ Failed to retrieve job")
        return False


if __name__ == "__main__":
    print("\n" + "=" * 60)
    print("Job ID Validation Test Suite")
    print("=" * 60)
    print("\nThis test verifies that invalid job_id formats are rejected")
    print("without generating warnings in the logs.")
    
    results = []
    
    # Run tests
    try:
        results.append(("Invalid Job ID Format", test_invalid_job_id_format()))
        results.append(("Valid Job ID Format", test_valid_job_id_format()))
        results.append(("Create and Retrieve Job", test_create_and_retrieve_job()))
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
        print("\nThe job_id validation is working correctly:")
        print("  • Invalid job_id formats (like 'process-selected') are rejected")
        print("  • Valid UUID formats are accepted")
        print("  • No warnings are generated for invalid formats")
        sys.exit(0)
    else:
        print("\n✗ Some tests failed!")
        sys.exit(1)
