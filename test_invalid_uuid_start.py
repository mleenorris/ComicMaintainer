#!/usr/bin/env python3
"""
Test that start_job raises RuntimeError when given an invalid UUID.

This test verifies the fix for the "processing files always fails to start" issue
where start_job would silently return instead of raising an exception for invalid UUIDs.
"""

import sys
import os
import tempfile

# Set up test environment BEFORE importing modules
os.environ['CONFIG_DIR'] = tempfile.mkdtemp()

# Add src to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

from job_manager import JobManager, JobResult


def test_invalid_uuid_raises_runtime_error():
    """Test that start_job raises RuntimeError for invalid UUID"""
    print("\n" + "=" * 60)
    print("Testing: start_job with invalid UUID")
    print("=" * 60)
    
    job_manager = JobManager(max_workers=1)
    
    # Test with various invalid UUIDs
    invalid_uuids = [
        "not-a-uuid",
        "12345",
        "invalid-uuid-format",
        "",
        "abc-def-ghi",
    ]
    
    def dummy_process(item):
        return JobResult(item=item, success=True)
    
    for invalid_uuid in invalid_uuids:
        print(f"\nTesting with invalid UUID: '{invalid_uuid}'")
        
        try:
            job_manager.start_job(invalid_uuid, dummy_process, ["test.cbz"])
            print(f"   ✗ FAIL: No exception raised for invalid UUID: {invalid_uuid}")
            return False
        except RuntimeError as e:
            expected_msg = "Cannot start job - invalid job_id format (expected UUID)"
            if expected_msg in str(e):
                print(f"   ✓ PASS: Correctly raised RuntimeError with message: {e}")
            else:
                print(f"   ✗ FAIL: Wrong error message: {e}")
                return False
        except Exception as e:
            print(f"   ✗ FAIL: Wrong exception type: {type(e).__name__}: {e}")
            return False
    
    return True


if __name__ == "__main__":
    print("\n" + "=" * 60)
    print("Invalid UUID Job Start Test")
    print("=" * 60)
    print("\nThis test verifies that start_job raises RuntimeError")
    print("when given an invalid UUID instead of silently returning.")
    
    success = test_invalid_uuid_raises_runtime_error()
    
    print("\n" + "=" * 60)
    if success:
        print("✓ Test PASSED")
        print("\nThe fix correctly raises RuntimeError for invalid UUIDs,")
        print("preventing silent failures that would confuse the frontend.")
    else:
        print("✗ Test FAILED")
        sys.exit(1)
    print("=" * 60 + "\n")
