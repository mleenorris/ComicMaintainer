"""
Event Broadcasting System for Real-Time Updates

This module provides a unified Server-Sent Events (SSE) broadcasting system
to replace polling mechanisms with real-time push notifications.

Event Types:
- watcher_status: Watcher service status changed
- file_processed: File has been processed by watcher or web interface
- job_updated: Batch job status changed
"""

import time
import json
import logging
import threading
from queue import Queue, Empty
from typing import Dict, Any, Set, Optional
from dataclasses import dataclass, asdict


@dataclass
class Event:
    """Represents an event to be broadcast"""
    type: str
    data: Dict[str, Any]
    timestamp: float = None
    
    def __post_init__(self):
        if self.timestamp is None:
            self.timestamp = time.time()
    
    def to_sse_format(self) -> str:
        """Convert event to SSE format"""
        event_data = {
            'type': self.type,
            'data': self.data,
            'timestamp': self.timestamp
        }
        return f"data: {json.dumps(event_data)}\n\n"


class EventBroadcaster:
    """
    Singleton class for broadcasting events to multiple clients via SSE
    
    Thread-safe event broadcasting with automatic client management
    """
    _instance = None
    _lock = threading.Lock()
    
    def __new__(cls):
        if cls._instance is None:
            with cls._lock:
                if cls._instance is None:
                    cls._instance = super().__new__(cls)
                    cls._instance._initialized = False
        return cls._instance
    
    def __init__(self):
        if self._initialized:
            return
        
        self._initialized = True
        self._clients: Set[Queue] = set()
        self._clients_lock = threading.Lock()
        self._event_count = 0
        # Store last event of each type. For job_updated events, key is (event_type, job_id)
        # to track each job separately. For other events, key is just event_type.
        self._last_events: Dict[Any, Event] = {}
        
        logging.info("EventBroadcaster initialized")
    
    def subscribe(self) -> Queue:
        """
        Subscribe a new client to receive events
        
        Returns:
            Queue: Event queue for the client
        """
        client_queue = Queue(maxsize=100)  # Buffer up to 100 events
        
        with self._clients_lock:
            self._clients.add(client_queue)
            client_count = len(self._clients)
        
        logging.info(f"Client subscribed to events (total: {client_count})")
        
        # Send last known state for each event type
        for event in self._last_events.values():
            try:
                client_queue.put_nowait(event)
            except:
                pass  # Queue full, skip
        
        return client_queue
    
    def unsubscribe(self, client_queue: Queue):
        """
        Unsubscribe a client from receiving events
        
        Args:
            client_queue: The client's event queue
        """
        with self._clients_lock:
            self._clients.discard(client_queue)
            client_count = len(self._clients)
        
        logging.info(f"Client unsubscribed from events (remaining: {client_count})")
    
    def broadcast(self, event_type: str, data: Dict[str, Any]):
        """
        Broadcast an event to all subscribed clients
        
        Args:
            event_type: Type of event (e.g., 'watcher_status', 'file_processed', 'job_updated')
            data: Event data dictionary
        """
        event = Event(type=event_type, data=data)
        
        # Store as last event of this type
        # For job_updated events, use a composite key (event_type, job_id) to track
        # each job separately. This prevents multiple jobs from overwriting each other's status.
        if event_type == 'job_updated' and 'job_id' in data:
            storage_key = (event_type, data['job_id'])
        else:
            storage_key = event_type
        
        self._last_events[storage_key] = event
        
        with self._clients_lock:
            dead_clients = set()
            
            for client_queue in self._clients:
                try:
                    # Non-blocking put - drop event if queue is full
                    client_queue.put_nowait(event)
                except:
                    # Queue is full or client is dead
                    dead_clients.add(client_queue)
            
            # Clean up dead clients
            for dead_client in dead_clients:
                self._clients.discard(dead_client)
            
            active_clients = len(self._clients)
        
        self._event_count += 1
        logging.debug(f"Broadcast event '{event_type}' to {active_clients} clients (total events: {self._event_count})")
    
    def get_client_count(self) -> int:
        """Get the number of active subscribed clients"""
        with self._clients_lock:
            return len(self._clients)
    
    def get_event_count(self) -> int:
        """Get the total number of events broadcast"""
        return self._event_count
    
    def get_last_event(self, event_type: str) -> Optional[Event]:
        """
        Get the last event of a specific type.
        
        Note: For job_updated events, use a composite key (event_type, job_id)
        to retrieve job-specific events.
        """
        return self._last_events.get(event_type)


# Global broadcaster instance
_broadcaster = EventBroadcaster()


def get_broadcaster() -> EventBroadcaster:
    """Get the global EventBroadcaster instance"""
    return _broadcaster





def broadcast_watcher_status(running: bool, enabled: bool):
    """
    Broadcast watcher service status
    
    Args:
        running: Whether the watcher is currently running
        enabled: Whether the watcher is enabled
    """
    get_broadcaster().broadcast('watcher_status', {
        'running': running,
        'enabled': enabled
    })


def broadcast_file_processed(filepath: str, success: bool, error: Optional[str] = None):
    """
    Broadcast that a file has been processed
    
    Args:
        filepath: Path to the processed file
        success: Whether processing succeeded
        error: Error message if processing failed
    """
    import os
    get_broadcaster().broadcast('file_processed', {
        'filepath': filepath,
        'filename': os.path.basename(filepath),
        'success': success,
        'error': error
    })


def broadcast_job_updated(job_id: str, status: str, progress: Dict[str, Any]):
    """
    Broadcast batch job status update
    
    Args:
        job_id: Job identifier
        status: Job status (running, completed, failed, cancelled)
        progress: Progress information dictionary
    """
    get_broadcaster().broadcast('job_updated', {
        'job_id': job_id,
        'status': status,
        'progress': progress
    })


def event_stream_generator(client_queue: Queue, timeout: int = 30):
    """
    Generator function for SSE streaming
    
    Args:
        client_queue: Queue to receive events from
        timeout: Timeout in seconds for keeping connection alive
    
    Yields:
        SSE formatted event strings
    """
    try:
        # Send initial heartbeat
        yield ": heartbeat\n\n"
        
        last_heartbeat = time.time()
        
        while True:
            try:
                # Wait for event with timeout
                event = client_queue.get(timeout=5)
                yield event.to_sse_format()
                last_heartbeat = time.time()
                
            except Empty:
                # No event received, send heartbeat to keep connection alive
                current_time = time.time()
                if current_time - last_heartbeat > 15:
                    yield ": heartbeat\n\n"
                    last_heartbeat = current_time
                
                # Check if we should close due to inactivity
                if current_time - last_heartbeat > timeout:
                    logging.debug("SSE connection timeout, closing")
                    break
                    
    except GeneratorExit:
        logging.debug("SSE client disconnected")
    except Exception as e:
        logging.error(f"Error in SSE stream: {e}")
