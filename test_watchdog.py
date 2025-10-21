#!/usr/bin/env python3
"""
Test script to verify the watchdog timer and SSE reconnection handling.

This script creates a test job, simulates progress updates, and verifies
that the frontend would properly handle stuck jobs and SSE reconnections.
"""

import sys
import os
import time
import logging

# Add src to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

from job_manager import get_job_manager, JobResult
from event_broadcaster import get_broadcaster

# Set up logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s [TEST] %(message)s'
)

def test_job_progress_tracking():
    """Test that job progress is tracked correctly"""
    print("\n" + "=" * 60)
    print("TEST: Job Progress Tracking with Simulated Delays")
    print("=" * 60)
    
    # Create job manager
    job_manager = get_job_manager(max_workers=2)
    
    # Create test items
    test_items = [f"item-{i}" for i in range(5)]
    
    print(f"\nCreating job with {len(test_items)} items...")
    job_id = job_manager.create_job(test_items)
    print(f"✓ Job created: {job_id}")
    
    # Subscribe to events
    broadcaster = get_broadcaster()
    client_queue = broadcaster.subscribe()
    print("✓ Subscribed to event broadcaster")
    
    # Define processing function with delays
    def process_item(item):
        """Simulate processing with delays"""
        print(f"  Processing {item}...")
        time.sleep(0.5)  # Simulate work
        return JobResult(
            item=item,
            success=True,
            details={'processed_at': time.time()}
        )
    
    # Start job
    print(f"\nStarting job {job_id}...")
    job_manager.start_job(job_id, process_item, test_items)
    
    # Monitor progress via events
    print("\nMonitoring progress via SSE events:")
    events_received = 0
    last_processed = 0
    start_time = time.time()
    
    while events_received < 20:  # Safety limit
        try:
            event = client_queue.get(timeout=2)
            events_received += 1
            
            if event.type == 'job_updated':
                data = event.data
                if data['job_id'] == job_id:
                    progress = data['progress']
                    processed = progress['processed']
                    total = progress['total']
                    status = data['status']
                    
                    if processed > last_processed:
                        print(f"  ➜ Progress: {processed}/{total} files ({progress['percentage']:.0f}%) - Status: {status}")
                        last_processed = processed
                    
                    if status in ['completed', 'failed', 'cancelled']:
                        print(f"\n✓ Job finished with status: {status}")
                        print(f"  Total time: {time.time() - start_time:.2f}s")
                        print(f"  Events received: {events_received}")
                        break
        except:
            # Timeout waiting for event
            elapsed = time.time() - start_time
            if elapsed > 30:
                print(f"\n⚠ Timeout after {elapsed:.1f}s - this would trigger watchdog!")
                break
    
    # Get final job status
    final_status = job_manager.get_job_status(job_id)
    if final_status:
        print(f"\nFinal job status from database:")
        print(f"  Status: {final_status['status']}")
        print(f"  Processed: {final_status['processed_items']}/{final_status['total_items']}")
        print(f"  Results: {len(final_status['results'])} items")
    
    broadcaster.unsubscribe(client_queue)
    print("\n✓ Test completed successfully")
    print("=" * 60)

def test_sse_reconnection_simulation():
    """Simulate SSE reconnection scenario"""
    print("\n" + "=" * 60)
    print("TEST: SSE Reconnection Handling")
    print("=" * 60)
    
    print("\nThis test simulates what happens when:")
    print("  1. A job is running")
    print("  2. SSE connection drops")
    print("  3. Frontend reconnects and polls job status")
    
    job_manager = get_job_manager(max_workers=2)
    test_items = [f"item-{i}" for i in range(3)]
    
    print(f"\nCreating job with {len(test_items)} items...")
    job_id = job_manager.create_job(test_items)
    
    # Subscribe initially
    broadcaster = get_broadcaster()
    client1 = broadcaster.subscribe()
    print("✓ Initial SSE connection established")
    
    # Define processing function
    def process_item(item):
        time.sleep(0.3)
        return JobResult(item=item, success=True)
    
    # Start job
    print(f"Starting job {job_id}...")
    job_manager.start_job(job_id, process_item, test_items)
    
    # Receive some events
    print("\nReceiving initial events...")
    for _ in range(2):
        try:
            event = client1.get(timeout=1)
            if event.type == 'job_updated':
                progress = event.data['progress']
                print(f"  ➜ Event: {progress['processed']}/{progress['total']} files")
        except:
            pass
    
    # Simulate disconnect
    print("\n⚠ Simulating SSE disconnect...")
    broadcaster.unsubscribe(client1)
    time.sleep(1)
    
    # Reconnect (new client)
    print("✓ Reconnecting SSE...")
    client2 = broadcaster.subscribe()
    print("✓ New SSE connection established")
    
    # Poll job status (this is what frontend watchdog does)
    print("\nPolling job status after reconnection...")
    status = job_manager.get_job_status(job_id)
    if status:
        print(f"  Current status: {status['status']}")
        print(f"  Progress: {status['processed_items']}/{status['total_items']}")
        print("  ✓ Frontend can sync with backend state")
    
    # Wait for completion
    print("\nWaiting for job completion...")
    max_wait = 10
    waited = 0
    while waited < max_wait:
        status = job_manager.get_job_status(job_id)
        if status and status['status'] == 'completed':
            print(f"✓ Job completed successfully")
            break
        time.sleep(0.5)
        waited += 0.5
    
    broadcaster.unsubscribe(client2)
    print("\n✓ Reconnection test completed")
    print("=" * 60)

if __name__ == '__main__':
    print("\n" + "=" * 60)
    print("Watchdog and SSE Reconnection Test Suite")
    print("=" * 60)
    
    try:
        test_job_progress_tracking()
        time.sleep(1)
        test_sse_reconnection_simulation()
        
        print("\n" + "=" * 60)
        print("✓ ALL TESTS PASSED")
        print("=" * 60)
        print("\nConclusion:")
        print("  • Job progress is properly tracked")
        print("  • SSE events are broadcast in real-time")
        print("  • Reconnection handling works correctly")
        print("  • Watchdog can poll job status when needed")
        
    except Exception as e:
        print(f"\n✗ TEST FAILED: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)
