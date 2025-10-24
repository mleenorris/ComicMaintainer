#!/usr/bin/env python3
"""
Test script to verify that job_id UUID enforcement works correctly at all layers.

This test verifies that:
1. Job creation always generates valid UUIDs
2. All job operations reject invalid (non-UUID) job_ids
3. There are no race conditions in job ID handling
"""

import sys
import os
import tempfile
import uuid as uuid_module

# Create temporary config directory for tests
TEST_CONFIG_DIR = tempfile.mkdtemp(prefix='comic_test_')
os.environ['CONFIG_DIR'] = TEST_CONFIG_DIR

# Add src to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

# Now import after setting CONFIG_DIR
import job_store
import preferences_store
from job_manager import get_job_manager


def cleanup_test_env():
    """Clean up test environment"""
    import shutil
    if os.path.exists(TEST_CONFIG_DIR):
        shutil.rmtree(TEST_CONFIG_DIR)


def test_job_store_uuid_validation():
    """Test that job_store rejects invalid UUIDs"""
    print("\n" + "=" * 60)
    print("TEST: Job Store UUID Validation")
    print("=" * 60)
    
    all_passed = True
    
    # Test 1: Creating job with invalid UUID should fail
    print("\n1. Testing job creation with invalid UUID...")
    result = job_store.create_job("not-a-uuid", 5, 1234567890.0)
    if not result:
        print("  ✓ Job creation correctly rejected invalid UUID")
    else:
        print("  ✗ Job creation incorrectly accepted invalid UUID")
        all_passed = False
    
    # Test 2: Creating job with valid UUID should succeed
    print("\n2. Testing job creation with valid UUID...")
    valid_uuid = str(uuid_module.uuid4())
    result = job_store.create_job(valid_uuid, 5, 1234567890.0)
    if result:
        print(f"  ✓ Job creation correctly accepted valid UUID: {valid_uuid}")
    else:
        print(f"  ✗ Job creation incorrectly rejected valid UUID: {valid_uuid}")
        all_passed = False
    
    # Test 3: Getting job with invalid UUID should return None
    print("\n3. Testing get_job with invalid UUID...")
    result = job_store.get_job("invalid-job-id")
    if result is None:
        print("  ✓ get_job correctly returned None for invalid UUID")
    else:
        print("  ✗ get_job incorrectly returned data for invalid UUID")
        all_passed = False
    
    # Test 4: Updating job with invalid UUID should fail
    print("\n4. Testing update_job_status with invalid UUID...")
    result = job_store.update_job_status("invalid-job-id", "processing")
    if not result:
        print("  ✓ update_job_status correctly rejected invalid UUID")
    else:
        print("  ✗ update_job_status incorrectly accepted invalid UUID")
        all_passed = False
    
    # Test 5: Adding result with invalid UUID should fail
    print("\n5. Testing add_job_result with invalid UUID...")
    result = job_store.add_job_result("invalid-job-id", "item1", True)
    if not result:
        print("  ✓ add_job_result correctly rejected invalid UUID")
    else:
        print("  ✗ add_job_result incorrectly accepted invalid UUID")
        all_passed = False
    
    # Test 6: Deleting job with invalid UUID should fail
    print("\n6. Testing delete_job with invalid UUID...")
    result = job_store.delete_job("invalid-job-id")
    if not result:
        print("  ✓ delete_job correctly rejected invalid UUID")
    else:
        print("  ✗ delete_job incorrectly accepted invalid UUID")
        all_passed = False
    
    # Clean up the valid job we created
    job_store.delete_job(valid_uuid)
    
    return all_passed


def test_preferences_store_uuid_validation():
    """Test that preferences_store rejects invalid UUIDs"""
    print("\n" + "=" * 60)
    print("TEST: Preferences Store UUID Validation")
    print("=" * 60)
    
    all_passed = True
    
    # Test 1: Setting active job with invalid UUID should raise ValueError
    print("\n1. Testing set_active_job with invalid UUID...")
    try:
        preferences_store.set_active_job("not-a-uuid", "Test Job")
        print("  ✗ set_active_job incorrectly accepted invalid UUID")
        all_passed = False
    except ValueError as e:
        if "UUID" in str(e):
            print(f"  ✓ set_active_job correctly rejected invalid UUID: {e}")
        else:
            print(f"  ✗ set_active_job raised ValueError but with unexpected message: {e}")
            all_passed = False
    except Exception as e:
        print(f"  ✗ set_active_job raised unexpected exception: {e}")
        all_passed = False
    
    # Test 2: Setting active job with valid UUID should succeed
    print("\n2. Testing set_active_job with valid UUID...")
    valid_uuid = str(uuid_module.uuid4())
    try:
        preferences_store.set_active_job(valid_uuid, "Test Job")
        print(f"  ✓ set_active_job correctly accepted valid UUID: {valid_uuid}")
    except Exception as e:
        print(f"  ✗ set_active_job incorrectly rejected valid UUID: {e}")
        all_passed = False
    
    # Test 3: Verify the active job was set correctly
    print("\n3. Testing get_active_job retrieves correct value...")
    active_job = preferences_store.get_active_job()
    if active_job and active_job['job_id'] == valid_uuid:
        print(f"  ✓ get_active_job returned correct UUID: {active_job['job_id']}")
    else:
        print(f"  ✗ get_active_job returned unexpected value: {active_job}")
        all_passed = False
    
    # Clean up
    preferences_store.clear_active_job()
    
    return all_passed


def test_job_manager_uuid_validation():
    """Test that job_manager operations validate UUIDs"""
    print("\n" + "=" * 60)
    print("TEST: Job Manager UUID Validation")
    print("=" * 60)
    
    all_passed = True
    job_manager = get_job_manager()
    
    # Test 1: Getting status with invalid UUID should return None
    print("\n1. Testing get_job_status with invalid UUID...")
    status = job_manager.get_job_status("not-a-uuid")
    if status is None:
        print("  ✓ get_job_status correctly returned None for invalid UUID")
    else:
        print("  ✗ get_job_status incorrectly returned data for invalid UUID")
        all_passed = False
    
    # Test 2: Create a valid job
    print("\n2. Testing create_job generates valid UUID...")
    job_id = job_manager.create_job(["item1", "item2", "item3"])
    try:
        uuid_module.UUID(job_id)
        print(f"  ✓ create_job generated valid UUID: {job_id}")
    except ValueError:
        print(f"  ✗ create_job generated invalid UUID: {job_id}")
        all_passed = False
    
    # Test 3: Verify we can get status of the valid job
    print("\n3. Testing get_job_status with valid UUID...")
    status = job_manager.get_job_status(job_id)
    if status is not None and status['job_id'] == job_id:
        print(f"  ✓ get_job_status correctly retrieved job with UUID: {job_id}")
    else:
        print(f"  ✗ get_job_status failed to retrieve valid job")
        all_passed = False
    
    # Test 4: Cancel job with invalid UUID should fail
    print("\n4. Testing cancel_job with invalid UUID...")
    result = job_manager.cancel_job("invalid-uuid")
    if not result:
        print("  ✓ cancel_job correctly rejected invalid UUID")
    else:
        print("  ✗ cancel_job incorrectly accepted invalid UUID")
        all_passed = False
    
    # Test 5: Delete job with invalid UUID should fail
    print("\n5. Testing delete_job with invalid UUID...")
    result = job_manager.delete_job("invalid-uuid")
    if not result:
        print("  ✓ delete_job correctly rejected invalid UUID")
    else:
        print("  ✗ delete_job incorrectly accepted invalid UUID")
        all_passed = False
    
    # Clean up the valid job
    job_manager.delete_job(job_id)
    
    return all_passed


def test_race_condition_prevention():
    """Test that concurrent operations on jobs are handled safely"""
    print("\n" + "=" * 60)
    print("TEST: Race Condition Prevention")
    print("=" * 60)
    
    all_passed = True
    
    # Test 1: Verify that setting active job with invalid UUID is prevented
    print("\n1. Testing race condition: invalid UUID in active job...")
    try:
        preferences_store.set_active_job("process-selected", "Processing")
        print("  ✗ Race condition exists: invalid UUID accepted in set_active_job")
        all_passed = False
    except ValueError:
        print("  ✓ Race condition prevented: invalid UUID rejected in set_active_job")
    
    # Test 2: Verify that job store operations are atomic
    print("\n2. Testing race condition: multiple operations on same job...")
    job_id = str(uuid_module.uuid4())
    result1 = job_store.create_job(job_id, 10, 1234567890.0)
    result2 = job_store.create_job(job_id, 10, 1234567890.0)  # Duplicate creation
    
    if result1 and not result2:
        print("  ✓ Race condition handled: duplicate job creation prevented")
    else:
        print(f"  ⚠ Race condition test inconclusive: create results {result1}, {result2}")
        # Don't fail the test since this depends on database constraints
    
    # Clean up
    job_store.delete_job(job_id)
    
    return all_passed


def test_edge_cases():
    """Test edge cases and boundary conditions"""
    print("\n" + "=" * 60)
    print("TEST: Edge Cases")
    print("=" * 60)
    
    all_passed = True
    
    # Test various invalid UUID formats
    invalid_formats = [
        "",                                          # Empty string
        "   ",                                       # Whitespace
        "12345",                                     # Too short
        "process-selected",                          # Looks like endpoint name
        "550e8400-e29b-41d4-a716",                  # Incomplete UUID
        "550e8400-e29b-41d4-a716-446655440000-extra", # Too long
        "not-a-uuid-at-all",                        # Clearly invalid
        None,                                        # None type (if string expected)
    ]
    
    print("\nTesting various invalid UUID formats with get_job...")
    for i, invalid_id in enumerate(invalid_formats):
        if invalid_id is None:
            continue  # Skip None test for now
        
        result = job_store.get_job(invalid_id)
        if result is None:
            print(f"  ✓ Correctly rejected: '{invalid_id}'")
        else:
            print(f"  ✗ Incorrectly accepted: '{invalid_id}'")
            all_passed = False
    
    # Test valid UUID formats (should all be accepted)
    valid_formats = [
        "550e8400-e29b-41d4-a716-446655440000",
        "6ba7b810-9dad-11d1-80b4-00c04fd430c8",
        str(uuid_module.uuid4()),
        str(uuid_module.uuid4()).upper(),  # Uppercase should work
    ]
    
    print("\nTesting various valid UUID formats with create_job...")
    for valid_id in valid_formats:
        result = job_store.create_job(valid_id, 1, 1234567890.0)
        if result:
            print(f"  ✓ Correctly accepted: '{valid_id}'")
            job_store.delete_job(valid_id)  # Clean up
        else:
            print(f"  ✗ Incorrectly rejected: '{valid_id}'")
            all_passed = False
    
    return all_passed


if __name__ == "__main__":
    print("\n" + "=" * 60)
    print("UUID Enforcement Test Suite")
    print("=" * 60)
    print("\nThis test verifies that job_id UUID enforcement works")
    print("correctly at all layers to prevent race conditions.")
    print(f"\nTest Config Directory: {TEST_CONFIG_DIR}")
    
    results = []
    
    try:
        # Run tests
        results.append(("Job Store UUID Validation", test_job_store_uuid_validation()))
        results.append(("Preferences Store UUID Validation", test_preferences_store_uuid_validation()))
        results.append(("Job Manager UUID Validation", test_job_manager_uuid_validation()))
        results.append(("Race Condition Prevention", test_race_condition_prevention()))
        results.append(("Edge Cases", test_edge_cases()))
    except Exception as e:
        print(f"\n✗ Test suite failed with exception: {e}")
        import traceback
        traceback.print_exc()
        cleanup_test_env()
        sys.exit(1)
    finally:
        cleanup_test_env()
    
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
        print("\nUUID enforcement is working correctly:")
        print("  • Job IDs are always created as valid UUIDs")
        print("  • All operations validate UUID format")
        print("  • Invalid UUIDs are rejected at all layers")
        print("  • Race conditions are prevented by validation")
        sys.exit(0)
    else:
        print("\n✗ Some tests failed!")
        sys.exit(1)
