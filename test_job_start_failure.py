#!/usr/bin/env python3
"""
Test script to verify that job start failures are properly handled.

This test verifies that when a job fails to start (e.g., job not found or 
already processing), the start_job method raises an exception instead of 
failing silently.
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


def test_start_nonexistent_job():
    """Test that starting a non-existent job raises RuntimeError"""
    print("\n" + "=" * 60)
    print("TEST: Start Non-Existent Job")
    print("=" * 60)
    
    job_manager = get_job_manager()
    
    # Try to start a job that doesn't exist
    fake_job_id = "550e8400-e29b-41d4-a716-446655440000"
    
    def process_item(filepath):
        return JobResult(item=filepath, success=True)
    
    print(f"\nAttempting to start non-existent job: {fake_job_id}")
    
    try:
        job_manager.start_job(fake_job_id, process_item, ["/test/file.cbz"])
        print("   ✗ FAIL: Expected RuntimeError but none was raised")
        return False
    except RuntimeError as e:
        print(f"   ✓ PASS: Correctly raised RuntimeError: {e}")
        if "not found" not in str(e).lower():
            print(f"   ⚠ Warning: Expected 'not found' in error message")
            return False
        return True
    except Exception as e:
        print(f"   ✗ FAIL: Unexpected exception type: {type(e).__name__}: {e}")
        return False


def test_start_already_processing_job():
    """Test that starting an already-processing job raises RuntimeError"""
    print("\n" + "=" * 60)
    print("TEST: Start Already-Processing Job")
    print("=" * 60)
    
    job_manager = get_job_manager()
    
    # Create a job
    items = ["/test/file1.cbz", "/test/file2.cbz"]
    job_id = job_manager.create_job(items)
    print(f"\nCreated job: {job_id}")
    
    # Manually update the job status to PROCESSING (simulate it already running)
    job_store.update_job_status(job_id, JobStatus.PROCESSING.value)
    print("   Updated job status to PROCESSING")
    
    def process_item(filepath):
        return JobResult(item=filepath, success=True)
    
    print(f"\nAttempting to start already-processing job: {job_id}")
    
    try:
        job_manager.start_job(job_id, process_item, items)
        print("   ✗ FAIL: Expected RuntimeError but none was raised")
        return False
    except RuntimeError as e:
        print(f"   ✓ PASS: Correctly raised RuntimeError: {e}")
        if "processing" not in str(e).lower():
            print(f"   ⚠ Warning: Expected 'processing' in error message")
            return False
        return True
    except Exception as e:
        print(f"   ✗ FAIL: Unexpected exception type: {type(e).__name__}: {e}")
        return False


def test_start_completed_job():
    """Test that starting a completed job raises RuntimeError"""
    print("\n" + "=" * 60)
    print("TEST: Start Completed Job")
    print("=" * 60)
    
    job_manager = get_job_manager()
    
    # Create a job
    items = ["/test/file1.cbz"]
    job_id = job_manager.create_job(items)
    print(f"\nCreated job: {job_id}")
    
    # Manually update the job status to COMPLETED
    job_store.update_job_status(job_id, JobStatus.COMPLETED.value)
    print("   Updated job status to COMPLETED")
    
    def process_item(filepath):
        return JobResult(item=filepath, success=True)
    
    print(f"\nAttempting to start completed job: {job_id}")
    
    try:
        job_manager.start_job(job_id, process_item, items)
        print("   ✗ FAIL: Expected RuntimeError but none was raised")
        return False
    except RuntimeError as e:
        print(f"   ✓ PASS: Correctly raised RuntimeError: {e}")
        if "completed" not in str(e).lower():
            print(f"   ⚠ Warning: Expected 'completed' in error message")
            return False
        return True
    except Exception as e:
        print(f"   ✗ FAIL: Unexpected exception type: {type(e).__name__}: {e}")
        return False


def test_normal_job_start_succeeds():
    """Test that starting a valid queued job succeeds without exception"""
    print("\n" + "=" * 60)
    print("TEST: Normal Job Start Succeeds")
    print("=" * 60)
    
    job_manager = get_job_manager()
    
    # Create a job
    items = ["/test/file1.cbz"]
    job_id = job_manager.create_job(items)
    print(f"\nCreated job: {job_id}")
    
    # Verify job is in QUEUED state
    job = job_store.get_job(job_id)
    print(f"   Job status: {job['status']}")
    
    if job['status'] != JobStatus.QUEUED.value:
        print(f"   ✗ FAIL: Job should be QUEUED, but is {job['status']}")
        return False
    
    def process_item(filepath):
        return JobResult(item=filepath, success=True)
    
    print(f"\nAttempting to start valid queued job: {job_id}")
    
    try:
        job_manager.start_job(job_id, process_item, items)
        print("   ✓ PASS: Job started successfully without exception")
        
        # Verify job status changed to PROCESSING
        import time
        time.sleep(0.1)  # Give it a moment to update
        job = job_store.get_job(job_id)
        if job['status'] == JobStatus.PROCESSING.value:
            print(f"   ✓ Job status correctly updated to PROCESSING")
        else:
            print(f"   ⚠ Warning: Expected status PROCESSING, got {job['status']}")
        
        # Cancel the job to clean up
        job_manager.cancel_job(job_id)
        
        return True
    except Exception as e:
        print(f"   ✗ FAIL: Unexpected exception: {type(e).__name__}: {e}")
        return False


if __name__ == "__main__":
    print("\n" + "=" * 60)
    print("Job Start Failure Handling Test Suite")
    print("=" * 60)
    print("\nThis test suite verifies that the start_job method properly")
    print("raises RuntimeError when it cannot start a job, preventing")
    print("silent failures that could confuse the frontend.")
    
    results = []
    
    # Run tests
    try:
        results.append(("Start Non-Existent Job", test_start_nonexistent_job()))
        results.append(("Start Already-Processing Job", test_start_already_processing_job()))
        results.append(("Start Completed Job", test_start_completed_job()))
        results.append(("Normal Job Start Succeeds", test_normal_job_start_succeeds()))
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
        print("\nThe fix successfully handles job start failures:")
        print("  • Non-existent jobs raise RuntimeError")
        print("  • Already-processing jobs raise RuntimeError")
        print("  • Completed jobs raise RuntimeError")
        print("  • Valid queued jobs start without exception")
        sys.exit(0)
    else:
        print("\n✗ Some tests failed!")
        sys.exit(1)
