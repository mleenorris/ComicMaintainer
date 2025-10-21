#!/usr/bin/env python3
"""
Test script to verify that job-specific events are properly tracked.

This test verifies that when multiple jobs run, their status updates
don't overwrite each other in the _last_events dictionary.
"""

import sys
import os
import time

# Add src to path  
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

from event_broadcaster import get_broadcaster, broadcast_job_updated


def test_multiple_jobs_dont_overwrite():
    """Test that multiple jobs maintain separate status"""
    print("\n" + "=" * 60)
    print("TEST: Multiple Jobs Don't Overwrite Each Other")
    print("=" * 60)
    
    broadcaster = get_broadcaster()
    
    # Clear existing events
    broadcaster._last_events.clear()
    
    job1_id = "job-001"
    job2_id = "job-002"
    
    print(f"\nSimulating 2 concurrent jobs: {job1_id} and {job2_id}")
    
    # Simulate job 1 at 50% progress
    broadcast_job_updated(
        job_id=job1_id,
        status='processing',
        progress={
            'processed': 5,
            'total': 10,
            'success': 5,
            'errors': 0,
            'percentage': 50
        }
    )
    print(f"  ➜ {job1_id}: 5/10 files (50%)")
    
    # Simulate job 2 at 20% progress
    broadcast_job_updated(
        job_id=job2_id,
        status='processing',
        progress={
            'processed': 2,
            'total': 10,
            'success': 2,
            'errors': 0,
            'percentage': 20
        }
    )
    print(f"  ➜ {job2_id}: 2/10 files (20%)")
    
    # Check _last_events dictionary
    print("\n" + "-" * 60)
    print("Checking _last_events dictionary:")
    print("-" * 60)
    
    print(f"Dictionary size: {len(broadcaster._last_events)}")
    
    # Look for job-specific keys
    job1_key_found = False
    job2_key_found = False
    job1_progress = None
    job2_progress = None
    
    for key, event in broadcaster._last_events.items():
        print(f"  Key: {key}")
        if event.type == 'job_updated':
            job_id = event.data.get('job_id')
            progress = event.data.get('progress', {})
            print(f"    Job ID: {job_id}")
            print(f"    Progress: {progress.get('processed')}/{progress.get('total')}")
            
            if job_id == job1_id:
                job1_key_found = True
                job1_progress = progress.get('processed')
            elif job_id == job2_id:
                job2_key_found = True
                job2_progress = progress.get('processed')
    
    # Verify results
    print("\n" + "-" * 60)
    print("Results:")
    print("-" * 60)
    
    if job1_key_found and job2_key_found:
        print(f"✓ Both jobs found in _last_events")
        print(f"  {job1_id}: {job1_progress}/10 files")
        print(f"  {job2_id}: {job2_progress}/10 files")
        
        if job1_progress == 5 and job2_progress == 2:
            print("✓ Both jobs retain their correct progress")
            print("✓ Jobs did NOT overwrite each other")
            return True
        else:
            print("✗ FAILED: Job progress values are incorrect")
            return False
    else:
        print(f"✗ FAILED: Not all jobs found in _last_events")
        print(f"  Job 1 found: {job1_key_found}")
        print(f"  Job 2 found: {job2_key_found}")
        return False


def test_new_subscriber_gets_job_specific_status():
    """Test that new subscribers get job-specific status for all active jobs"""
    print("\n" + "=" * 60)
    print("TEST: New Subscriber Gets Job-Specific Status")
    print("=" * 60)
    
    broadcaster = get_broadcaster()
    
    # Clear existing events
    broadcaster._last_events.clear()
    
    job1_id = "job-101"
    job2_id = "job-102"
    
    print(f"\nSimulating 2 concurrent jobs before subscription")
    
    # Simulate job 1 progress
    broadcast_job_updated(
        job_id=job1_id,
        status='processing',
        progress={'processed': 7, 'total': 10, 'success': 7, 'errors': 0, 'percentage': 70}
    )
    print(f"  ➜ {job1_id}: 7/10 files")
    
    # Simulate job 2 progress
    broadcast_job_updated(
        job_id=job2_id,
        status='processing',
        progress={'processed': 3, 'total': 10, 'success': 3, 'errors': 0, 'percentage': 30}
    )
    print(f"  ➜ {job2_id}: 3/10 files")
    
    # Now subscribe a new client
    print("\nNew client subscribing...")
    client_queue = broadcaster.subscribe()
    
    # Collect events sent to new subscriber
    time.sleep(0.1)  # Give events time to arrive
    received_events = {}
    
    while True:
        try:
            event = client_queue.get_nowait()
            if event.type == 'job_updated':
                job_id = event.data.get('job_id')
                progress = event.data.get('progress', {})
                received_events[job_id] = progress
                print(f"  Received: {job_id} at {progress.get('processed')}/{progress.get('total')}")
        except:
            break
    
    broadcaster.unsubscribe(client_queue)
    
    # Verify results
    print("\n" + "-" * 60)
    print("Results:")
    print("-" * 60)
    
    if len(received_events) == 2:
        print(f"✓ New subscriber received status for {len(received_events)} jobs")
        
        if job1_id in received_events and job2_id in received_events:
            job1_received = received_events[job1_id].get('processed')
            job2_received = received_events[job2_id].get('processed')
            
            print(f"  {job1_id}: {job1_received}/10 files")
            print(f"  {job2_id}: {job2_received}/10 files")
            
            if job1_received == 7 and job2_received == 3:
                print("✓ Received correct progress for both jobs")
                print("✓ New subscribers get complete job status")
                return True
            else:
                print("✗ FAILED: Received incorrect progress")
                return False
        else:
            print("✗ FAILED: Not all jobs received")
            return False
    else:
        print(f"✗ FAILED: Expected 2 jobs, received {len(received_events)}")
        return False


def test_single_job_multiple_updates():
    """Test that a single job's updates properly overwrite previous ones"""
    print("\n" + "=" * 60)
    print("TEST: Single Job Multiple Updates")
    print("=" * 60)
    
    broadcaster = get_broadcaster()
    broadcaster._last_events.clear()
    
    job_id = "job-201"
    
    print(f"\nSimulating multiple updates for {job_id}")
    
    # Send multiple updates
    for i in range(1, 11):
        broadcast_job_updated(
            job_id=job_id,
            status='processing',
            progress={'processed': i, 'total': 10, 'success': i, 'errors': 0, 'percentage': i * 10}
        )
        if i % 3 == 0:
            print(f"  ➜ Update: {i}/10 files")
    
    print(f"  ➜ Final update: 10/10 files")
    
    # Check that only the latest update is stored
    print("\n" + "-" * 60)
    print("Checking _last_events:")
    print("-" * 60)
    
    job_entries = [event for event in broadcaster._last_events.values() 
                   if event.type == 'job_updated' and event.data.get('job_id') == job_id]
    
    if len(job_entries) == 1:
        print(f"✓ Only 1 entry for {job_id} in _last_events")
        progress = job_entries[0].data.get('progress', {})
        processed = progress.get('processed')
        
        if processed == 10:
            print(f"✓ Entry contains the latest update: {processed}/10 files")
            print("✓ Previous updates were properly overwritten")
            return True
        else:
            print(f"✗ FAILED: Expected 10/10, got {processed}/10")
            return False
    else:
        print(f"✗ FAILED: Expected 1 entry, found {len(job_entries)}")
        return False


if __name__ == "__main__":
    print("\n" + "=" * 60)
    print("Job-Specific Event Test Suite")
    print("=" * 60)
    print("\nThis test verifies that job-specific events are properly")
    print("tracked and don't overwrite each other.")
    
    results = []
    
    # Run tests
    results.append(("Multiple Jobs Don't Overwrite", test_multiple_jobs_dont_overwrite()))
    results.append(("New Subscriber Gets Job Status", test_new_subscriber_gets_job_specific_status()))
    results.append(("Single Job Multiple Updates", test_single_job_multiple_updates()))
    
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
        print("\nThe job-specific event tracking is working correctly:")
        print("  • Multiple jobs maintain separate status")
        print("  • New subscribers get status for all active jobs")
        print("  • Single job updates properly overwrite previous ones")
        sys.exit(0)
    else:
        print("\n✗ Some tests failed!")
        sys.exit(1)
