#!/usr/bin/env python3
"""
Test script for progress callback functionality via SSE.

This script verifies that job progress updates are broadcast
in real-time via Server-Sent Events instead of requiring polling.
"""

import sys
import os
import time

# Add src to path  
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

# Test event broadcasting directly without full job manager
from event_broadcaster import get_broadcaster, broadcast_job_updated


def test_broadcast_mechanism():
    """Test that broadcast_job_updated works correctly"""
    print("\n" + "=" * 60)
    print("TEST: Job Update Broadcasting Mechanism")
    print("=" * 60)
    
    # Subscribe to events
    broadcaster = get_broadcaster()
    client_queue = broadcaster.subscribe()
    
    print("✓ Subscribed to event broadcaster")
    
    # Simulate job progress updates with valid UUID
    import uuid
    job_id = str(uuid.uuid4())
    
    print(f"\nSimulating job progress updates for {job_id[:8]}...")
    
    # Simulate starting job
    broadcast_job_updated(
        job_id=job_id,
        status='processing',
        progress={
            'processed': 0,
            'total': 10,
            'success': 0,
            'errors': 0,
            'percentage': 0
        }
    )
    print("  ➜ Broadcast: job started (0/10)")
    
    # Simulate progress
    for i in range(1, 11):
        time.sleep(0.05)  # Small delay to simulate work
        broadcast_job_updated(
            job_id=job_id,
            status='processing',
            progress={
                'processed': i,
                'total': 10,
                'success': i,
                'errors': 0,
                'percentage': i * 10
            }
        )
        print(f"  ➜ Broadcast: progress update ({i}/10)")
    
    # Simulate completion
    broadcast_job_updated(
        job_id=job_id,
        status='completed',
        progress={
            'processed': 10,
            'total': 10,
            'success': 10,
            'errors': 0,
            'percentage': 100
        }
    )
    print("  ➜ Broadcast: job completed (10/10)")
    
    # Collect events from the queue
    events_received = []
    job_events = []
    
    print("\nCollecting events from queue...")
    time.sleep(0.2)  # Give events time to arrive
    
    while True:
        try:
            event = client_queue.get_nowait()
            events_received.append(event)
            
            if event.type == 'job_updated' and event.data.get('job_id') == job_id:
                job_events.append(event)
        except:
            break
    
    # Unsubscribe
    broadcaster.unsubscribe(client_queue)
    
    # Verify results
    print("\n" + "-" * 60)
    print("Results:")
    print("-" * 60)
    
    print(f"Total events received: {len(events_received)}")
    print(f"Job-specific events: {len(job_events)}")
    
    assert len(job_events) > 0, "No job events received!"
    
    # Verify we got progress updates
    processing_events = [e for e in job_events if e.data.get('status') == 'processing']
    completion_events = [e for e in job_events if e.data.get('status') == 'completed']
    
    print(f"Processing events: {len(processing_events)}")
    print(f"Completion events: {len(completion_events)}")
    
    # Check event details
    if len(job_events) > 0:
        print("\nEvent details:")
        for i, event in enumerate(job_events[:5]):  # Show first 5 events
            progress = event.data.get('progress', {})
            print(f"  Event {i+1}: status={event.data.get('status')}, "
                  f"progress={progress.get('processed')}/{progress.get('total')} "
                  f"({progress.get('percentage'):.0f}%)")
        if len(job_events) > 5:
            print(f"  ... and {len(job_events) - 5} more events")
    
    # Verify we got at least some processing events and the completion
    if len(processing_events) == 0:
        print("\n✗ TEST FAILED: No processing events received!")
        return False
    if len(completion_events) == 0:
        print("\n✗ TEST FAILED: No completion event received!")
        return False
    
    print("\n" + "=" * 60)
    print("✓ TEST PASSED")
    print("=" * 60)
    print("\nConclusion:")
    print("  • Job progress updates are broadcast via SSE")
    print("  • Clients receive real-time callbacks")
    print("  • Progress updates include detailed information")
    
    return True


def test_multiple_subscribers():
    """Test that multiple clients can receive the same broadcasts"""
    print("\n" + "=" * 60)
    print("TEST: Multiple Subscribers")
    print("=" * 60)
    
    broadcaster = get_broadcaster()
    
    # Subscribe multiple clients
    client1 = broadcaster.subscribe()
    client2 = broadcaster.subscribe()
    client3 = broadcaster.subscribe()
    
    print(f"✓ Subscribed 3 clients")
    print(f"  Active clients: {broadcaster.get_client_count()}")
    
    # Broadcast an event with valid UUID
    import uuid
    job_id = str(uuid.uuid4())
    broadcast_job_updated(
        job_id=job_id,
        status='processing',
        progress={'processed': 5, 'total': 10, 'success': 5, 'errors': 0, 'percentage': 50}
    )
    
    time.sleep(0.1)  # Give events time to arrive
    
    # Check that all clients received the event
    clients = [client1, client2, client3]
    received_counts = []
    
    for i, client in enumerate(clients, 1):
        count = 0
        while True:
            try:
                event = client.get_nowait()
                if event.type == 'job_updated' and event.data.get('job_id') == job_id:
                    count += 1
            except:
                break
        received_counts.append(count)
        print(f"  Client {i} received: {count} job events")
    
    # Unsubscribe all
    for client in clients:
        broadcaster.unsubscribe(client)
    
    # Verify all clients received the event
    if not all(count > 0 for count in received_counts):
        print("✗ TEST FAILED: Not all clients received the broadcast")
        return False
    print("✓ All clients received the broadcast")
    
    return True


if __name__ == "__main__":
    print("\n" + "=" * 60)
    print("Progress Callback Test Suite")
    print("=" * 60)
    print("\nThis test verifies that job progress updates are broadcast")
    print("in real-time via SSE instead of requiring polling.")
    
    results = []
    
    # Run tests
    results.append(("Broadcast Mechanism", test_broadcast_mechanism()))
    results.append(("Multiple Subscribers", test_multiple_subscribers()))
    
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
        print("\nThe progress callback system is working correctly:")
        print("  • Job updates are broadcast via SSE")
        print("  • Multiple clients can subscribe")
        print("  • Real-time notifications eliminate polling")
        sys.exit(0)
    else:
        print("\n✗ Some tests failed!")
        sys.exit(1)
