"""
Job manager for asynchronous file processing.
Handles background processing jobs with status tracking and concurrent execution.
"""
import threading
import uuid
import time
import logging
import os
from concurrent.futures import ThreadPoolExecutor, as_completed
from typing import Dict, List, Callable, Any, Optional
from dataclasses import dataclass, field, asdict
from enum import Enum
from job_store import create_job_store, JobStore


class JobStatus(Enum):
    """Job execution status"""
    QUEUED = "queued"
    PROCESSING = "processing"
    COMPLETED = "completed"
    FAILED = "failed"
    CANCELLED = "cancelled"


@dataclass
class JobResult:
    """Result of processing a single item in a job"""
    item: str
    success: bool
    error: Optional[str] = None
    details: Dict[str, Any] = field(default_factory=dict)


@dataclass
class Job:
    """Background processing job"""
    job_id: str
    status: JobStatus
    total_items: int
    processed_items: int = 0
    results: List[JobResult] = field(default_factory=list)
    error: Optional[str] = None
    created_at: float = field(default_factory=time.time)
    started_at: Optional[float] = None
    completed_at: Optional[float] = None
    
    def to_dict(self) -> Dict[str, Any]:
        """Convert Job to dictionary for storage"""
        return {
            'job_id': self.job_id,
            'status': self.status.value,
            'total_items': self.total_items,
            'processed_items': self.processed_items,
            'results': [
                {
                    'item': r.item,
                    'success': r.success,
                    'error': r.error,
                    'details': r.details
                }
                for r in self.results
            ],
            'error': self.error,
            'created_at': self.created_at,
            'started_at': self.started_at,
            'completed_at': self.completed_at
        }
    
    @staticmethod
    def from_dict(data: Dict[str, Any]) -> 'Job':
        """Create Job from dictionary"""
        results = [
            JobResult(
                item=r['item'],
                success=r['success'],
                error=r.get('error'),
                details=r.get('details', {})
            )
            for r in data.get('results', [])
        ]
        
        return Job(
            job_id=data['job_id'],
            status=JobStatus(data['status']),
            total_items=data['total_items'],
            processed_items=data.get('processed_items', 0),
            results=results,
            error=data.get('error'),
            created_at=data.get('created_at', time.time()),
            started_at=data.get('started_at'),
            completed_at=data.get('completed_at')
        )


class JobManager:
    """
    Manages background jobs with concurrent execution.
    Uses thread pool for I/O-bound file processing operations.
    """
    
    def __init__(self, max_workers: int = 4, job_store: Optional[JobStore] = None):
        """
        Initialize job manager.
        
        Args:
            max_workers: Maximum number of concurrent workers
            job_store: Backend storage for job state (defaults to in-memory)
        """
        self.max_workers = max_workers
        
        # Use provided job store or create one based on environment
        if job_store is None:
            backend = os.environ.get('JOB_STORE_BACKEND', 'memory').lower()
            redis_url = os.environ.get('REDIS_URL', 'redis://localhost:6379/0')
            
            try:
                self.job_store = create_job_store(backend=backend, redis_url=redis_url if backend == 'redis' else None)
                if backend == 'redis':
                    logging.info(f"Using Redis backend for job storage at {redis_url}")
                else:
                    logging.info("Using in-memory backend for job storage")
            except Exception as e:
                logging.warning(f"Failed to create {backend} job store, falling back to in-memory: {e}")
                self.job_store = create_job_store(backend='memory')
        else:
            self.job_store = job_store
        
        self.lock = threading.Lock()
        self.executor = ThreadPoolExecutor(max_workers=max_workers)
        self._cleanup_thread = threading.Thread(target=self._cleanup_old_jobs, daemon=True)
        self._cleanup_thread.start()
    
    def create_job(self, items: List[str]) -> str:
        """
        Create a new job.
        
        Args:
            items: List of items to process
            
        Returns:
            Job ID
        """
        job_id = str(uuid.uuid4())
        job = Job(
            job_id=job_id,
            status=JobStatus.QUEUED,
            total_items=len(items)
        )
        
        with self.lock:
            self.job_store.save_job(job_id, job.to_dict())
        
        logging.info(f"Created job {job_id} with {len(items)} items")
        return job_id
    
    def start_job(self, job_id: str, process_func: Callable[[str], JobResult], items: List[str]):
        """
        Start processing a job in the background.
        
        Args:
            job_id: Job ID
            process_func: Function to process each item (must accept item and return JobResult)
            items: List of items to process
        """
        with self.lock:
            job_data = self.job_store.get_job(job_id)
            if not job_data:
                logging.error(f"Job {job_id} not found")
                return
            
            job = Job.from_dict(job_data)
            if job.status != JobStatus.QUEUED:
                logging.warning(f"Job {job_id} already started")
                return
            
            job.status = JobStatus.PROCESSING
            job.started_at = time.time()
            self.job_store.save_job(job_id, job.to_dict())
        
        # Submit job to thread pool
        self.executor.submit(self._process_job, job_id, process_func, items)
        logging.info(f"Started job {job_id}")
    
    def _process_job(self, job_id: str, process_func: Callable[[str], JobResult], items: List[str]):
        """
        Process job items concurrently.
        
        Args:
            job_id: Job ID
            process_func: Function to process each item
            items: List of items to process
        """
        try:
            # Submit all items for processing
            futures = {self.executor.submit(process_func, item): item for item in items}
            
            # Process results as they complete
            for future in as_completed(futures):
                item = futures[future]
                
                try:
                    result = future.result()
                    
                    with self.lock:
                        job_data = self.job_store.get_job(job_id)
                        if job_data:
                            job = Job.from_dict(job_data)
                            job.results.append(result)
                            job.processed_items = len(job.results)
                            self.job_store.save_job(job_id, job.to_dict())
                except Exception as e:
                    logging.error(f"Error processing item {item} in job {job_id}: {e}")
                    result = JobResult(item=item, success=False, error=str(e))
                    
                    with self.lock:
                        job_data = self.job_store.get_job(job_id)
                        if job_data:
                            job = Job.from_dict(job_data)
                            job.results.append(result)
                            job.processed_items = len(job.results)
                            self.job_store.save_job(job_id, job.to_dict())
            
            # Mark job as completed
            with self.lock:
                job_data = self.job_store.get_job(job_id)
                if job_data:
                    job = Job.from_dict(job_data)
                    job.status = JobStatus.COMPLETED
                    job.completed_at = time.time()
                    self.job_store.save_job(job_id, job.to_dict())
            
            logging.info(f"Completed job {job_id}")
        
        except Exception as e:
            logging.error(f"Fatal error processing job {job_id}: {e}")
            with self.lock:
                job_data = self.job_store.get_job(job_id)
                if job_data:
                    job = Job.from_dict(job_data)
                    job.status = JobStatus.FAILED
                    job.error = str(e)
                    job.completed_at = time.time()
                    self.job_store.save_job(job_id, job.to_dict())
    
    def get_job_status(self, job_id: str) -> Optional[Dict[str, Any]]:
        """
        Get job status.
        
        Args:
            job_id: Job ID
            
        Returns:
            Job status dictionary or None if not found
        """
        with self.lock:
            job_data = self.job_store.get_job(job_id)
            if not job_data:
                return None
            
            # Add computed progress field
            total_items = job_data.get('total_items', 0)
            processed_items = job_data.get('processed_items', 0)
            job_data['progress'] = processed_items / total_items if total_items > 0 else 0
            
            return job_data
    
    def cancel_job(self, job_id: str) -> bool:
        """
        Cancel a job (marks as cancelled, but running tasks may complete).
        
        Args:
            job_id: Job ID
            
        Returns:
            True if cancelled, False if not found or already completed
        """
        with self.lock:
            job_data = self.job_store.get_job(job_id)
            if not job_data:
                return False
            
            job = Job.from_dict(job_data)
            if job.status in (JobStatus.COMPLETED, JobStatus.FAILED, JobStatus.CANCELLED):
                return False
            
            job.status = JobStatus.CANCELLED
            job.completed_at = time.time()
            self.job_store.save_job(job_id, job.to_dict())
        
        logging.info(f"Cancelled job {job_id}")
        return True
    
    def delete_job(self, job_id: str) -> bool:
        """
        Delete a job from history.
        
        Args:
            job_id: Job ID
            
        Returns:
            True if deleted, False if not found
        """
        with self.lock:
            if self.job_store.delete_job(job_id):
                logging.info(f"Deleted job {job_id}")
                return True
        return False
    
    def list_jobs(self) -> List[Dict[str, Any]]:
        """
        List all jobs.
        
        Returns:
            List of job status dictionaries
        """
        with self.lock:
            jobs = self.job_store.list_jobs()
            return [
                {
                    'job_id': job.get('job_id'),
                    'status': job.get('status'),
                    'total_items': job.get('total_items'),
                    'processed_items': job.get('processed_items'),
                    'created_at': job.get('created_at'),
                    'started_at': job.get('started_at'),
                    'completed_at': job.get('completed_at')
                }
                for job in jobs
            ]
    
    def _cleanup_old_jobs(self):
        """
        Periodically clean up old completed jobs.
        Runs in a background thread.
        """
        while True:
            time.sleep(300)  # Run every 5 minutes
            
            try:
                cutoff_time = time.time() - 3600  # 1 hour
                
                with self.lock:
                    deleted_count = self.job_store.cleanup_old_jobs(cutoff_time)
                
                if deleted_count > 0:
                    logging.info(f"Cleaned up {deleted_count} old jobs")
            
            except Exception as e:
                logging.error(f"Error cleaning up old jobs: {e}")
    
    def shutdown(self):
        """Shutdown the job manager and wait for all jobs to complete."""
        logging.info("Shutting down job manager")
        self.executor.shutdown(wait=True)


# Global job manager instance
# NOTE: When using Redis backend (JOB_STORE_BACKEND=redis), job state is shared
# across all worker processes, enabling multi-worker deployments. With the default
# in-memory backend, the application should run with a single worker process.
_job_manager: Optional[JobManager] = None
_job_manager_lock = threading.Lock()


def get_job_manager(max_workers: int = 4) -> JobManager:
    """
    Get the global job manager instance (singleton).
    
    With Redis backend, job state is shared across all workers/instances.
    With in-memory backend, this is a per-process singleton.
    
    Args:
        max_workers: Maximum number of concurrent workers (only used on first call)
        
    Returns:
        JobManager instance
    """
    global _job_manager
    
    with _job_manager_lock:
        if _job_manager is None:
            _job_manager = JobManager(max_workers=max_workers)
        return _job_manager
