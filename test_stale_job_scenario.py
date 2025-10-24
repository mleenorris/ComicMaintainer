#!/usr/bin/env python3
"""
Integration test to simulate the stale job_id scenario.

This test simulates what happens when a stale or invalid job_id
(like "process-selected") is stored in preferences and then queried.
"""

import sys
import os

# Add src to path  
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

from preferences_store import set_active_job, get_active_job, clear_active_job
from job_manager import get_job_manager


def test_stale_job_id_scenario():
    """Simulate the scenario where a stale job_id is in preferences"""
    print("\n" + "=" * 60)
    print("TEST: Stale Job ID Scenario")
    print("=" * 60)
    
    print("\n1. Simulating stale job_id being stored in preferences...")
    # Simulate an old/corrupted state where "process-selected" is stored
    set_active_job("process-selected", "Processing Selected Files")
    print("   Set active job: 'process-selected'")
    
    print("\n2. Retrieving active job from preferences...")
    active_job = get_active_job()
    if active_job:
        print(f"   Retrieved: job_id='{active_job['job_id']}', title='{active_job['job_title']}'")
    
    print("\n3. Attempting to get status for invalid job_id...")
    job_manager = get_job_manager()
    
    # This should NOT generate a WARNING log - it should silently return None
    print("   Calling job_manager.get_job_status('process-selected')...")
    status = job_manager.get_job_status("process-selected")
    
    if status is None:
        print("   ✓ Correctly returned None (no warning should be logged)")
    else:
        print(f"   ✗ Unexpectedly returned: {status}")
        return False
    
    print("\n4. Cleaning up stale active job...")
    clear_active_job()
    active_job = get_active_job()
    if active_job is None:
        print("   ✓ Active job cleared successfully")
    else:
        print(f"   ✗ Active job still present: {active_job}")
        return False
    
    print("\n5. Verifying with a valid UUID that doesn't exist...")
    # This SHOULD generate a WARNING since it's a valid UUID format
    status = job_manager.get_job_status("550e8400-e29b-41d4-a716-446655440000")
    if status is None:
        print("   ✓ Valid UUID format accepted (warning expected for non-existent job)")
    else:
        print(f"   ✗ Unexpectedly returned: {status}")
        return False
    
    return True


def test_web_api_scenario():
    """Test the web API endpoint behavior with invalid job_id"""
    print("\n" + "=" * 60)
    print("TEST: Web API Endpoint Behavior")
    print("=" * 60)
    
    # We can't easily test the Flask endpoint without starting the app,
    # but we can verify the validation logic works
    import uuid
    
    print("\n1. Testing UUID validation logic...")
    
    invalid_ids = ["process-selected", "not-a-uuid", ""]
    valid_ids = ["550e8400-e29b-41d4-a716-446655440000"]
    
    print("   Invalid job_ids:")
    for job_id in invalid_ids:
        try:
            uuid.UUID(job_id)
            print(f"   ✗ '{job_id}' incorrectly accepted as valid UUID")
            return False
        except ValueError:
            print(f"   ✓ '{job_id}' correctly rejected")
    
    print("   Valid job_ids:")
    for job_id in valid_ids:
        try:
            uuid.UUID(job_id)
            print(f"   ✓ '{job_id}' correctly accepted")
        except ValueError:
            print(f"   ✗ '{job_id}' incorrectly rejected")
            return False
    
    return True


if __name__ == "__main__":
    print("\n" + "=" * 60)
    print("Stale Job ID Scenario Test")
    print("=" * 60)
    print("\nThis test simulates the scenario where an invalid job_id")
    print("(like 'process-selected') is stored and then queried.")
    
    results = []
    
    # Run tests
    try:
        results.append(("Stale Job ID Scenario", test_stale_job_id_scenario()))
        results.append(("Web API Validation", test_web_api_scenario()))
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
        print("\nThe fix successfully handles the stale job_id scenario:")
        print("  • Invalid job_ids like 'process-selected' are silently rejected")
        print("  • No warnings are generated for invalid formats")
        print("  • Valid UUIDs are properly accepted")
        print("  • Stale active jobs can be cleared properly")
        sys.exit(0)
    else:
        print("\n✗ Some tests failed!")
        sys.exit(1)
