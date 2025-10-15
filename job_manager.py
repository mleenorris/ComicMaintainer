"""
Job manager for asynchronous file processing.
Handles background processing jobs with status tracking and concurrent execution.
"""
import threading
import uuid
import time
import logging
from concurrent.futures import ThreadPoolExecutor, as_completed
from typing import Dict, List, Callable, Any, Optional
from dataclasses import dataclass, field
from enum import Enum


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


class JobManager:
    """
    Manages background jobs with concurrent execution.
    Uses thread pool for I/O-bound file processing operations.
    """
    
    def __init__(self, max_workers: int = 4):
        """
        Initialize job manager.
        
        Args:
            max_workers: Maximum number of concurrent workers
        """
        self.max_workers = max_workers
        self.jobs: Dict[str, Job] = {}
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
            self.jobs[job_id] = job
        
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
            if job_id not in self.jobs:
                logging.error(f"Job {job_id} not found")
                return
            
            job = self.jobs[job_id]
            if job.status != JobStatus.QUEUED:
                logging.warning(f"Job {job_id} already started")
                return
            
            job.status = JobStatus.PROCESSING
            job.started_at = time.time()
        
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
                        if job_id in self.jobs:
                            job = self.jobs[job_id]
                            job.results.append(result)
                            job.processed_items = len(job.results)
                except Exception as e:
                    logging.error(f"Error processing item {item} in job {job_id}: {e}")
                    result = JobResult(item=item, success=False, error=str(e))
                    
                    with self.lock:
                        if job_id in self.jobs:
                            job = self.jobs[job_id]
                            job.results.append(result)
                            job.processed_items = len(job.results)
            
            # Mark job as completed
            with self.lock:
                if job_id in self.jobs:
                    job = self.jobs[job_id]
                    job.status = JobStatus.COMPLETED
                    job.completed_at = time.time()
            
            logging.info(f"Completed job {job_id}")
        
        except Exception as e:
            logging.error(f"Fatal error processing job {job_id}: {e}")
            with self.lock:
                if job_id in self.jobs:
                    job = self.jobs[job_id]
                    job.status = JobStatus.FAILED
                    job.error = str(e)
                    job.completed_at = time.time()
    
    def get_job_status(self, job_id: str) -> Optional[Dict[str, Any]]:
        """
        Get job status.
        
        Args:
            job_id: Job ID
            
        Returns:
            Job status dictionary or None if not found
        """
        with self.lock:
            if job_id not in self.jobs:
                return None
            
            job = self.jobs[job_id]
            return {
                'job_id': job.job_id,
                'status': job.status.value,
                'total_items': job.total_items,
                'processed_items': job.processed_items,
                'progress': job.processed_items / job.total_items if job.total_items > 0 else 0,
                'results': [
                    {
                        'item': r.item,
                        'success': r.success,
                        'error': r.error,
                        'details': r.details
                    }
                    for r in job.results
                ],
                'error': job.error,
                'created_at': job.created_at,
                'started_at': job.started_at,
                'completed_at': job.completed_at
            }
    
    def cancel_job(self, job_id: str) -> bool:
        """
        Cancel a job (marks as cancelled, but running tasks may complete).
        
        Args:
            job_id: Job ID
            
        Returns:
            True if cancelled, False if not found or already completed
        """
        with self.lock:
            if job_id not in self.jobs:
                return False
            
            job = self.jobs[job_id]
            if job.status in (JobStatus.COMPLETED, JobStatus.FAILED, JobStatus.CANCELLED):
                return False
            
            job.status = JobStatus.CANCELLED
            job.completed_at = time.time()
        
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
            if job_id in self.jobs:
                del self.jobs[job_id]
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
            return [
                {
                    'job_id': job.job_id,
                    'status': job.status.value,
                    'total_items': job.total_items,
                    'processed_items': job.processed_items,
                    'created_at': job.created_at,
                    'started_at': job.started_at,
                    'completed_at': job.completed_at
                }
                for job in self.jobs.values()
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
                    jobs_to_delete = [
                        job_id
                        for job_id, job in self.jobs.items()
                        if job.status in (JobStatus.COMPLETED, JobStatus.FAILED, JobStatus.CANCELLED)
                        and job.completed_at is not None
                        and job.completed_at < cutoff_time
                    ]
                    
                    for job_id in jobs_to_delete:
                        del self.jobs[job_id]
                
                if jobs_to_delete:
                    logging.info(f"Cleaned up {len(jobs_to_delete)} old jobs")
            
            except Exception as e:
                logging.error(f"Error cleaning up old jobs: {e}")
    
    def shutdown(self):
        """Shutdown the job manager and wait for all jobs to complete."""
        logging.info("Shutting down job manager")
        self.executor.shutdown(wait=True)


# Global job manager instance
_job_manager: Optional[JobManager] = None
_job_manager_lock = threading.Lock()


def get_job_manager(max_workers: int = 4) -> JobManager:
    """
    Get the global job manager instance (singleton).
    
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
