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
import job_store


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
    Job state is stored in SQLite for cross-process sharing.
    """
    
    def __init__(self, max_workers: int = 4):
        """
        Initialize job manager.
        
        Args:
            max_workers: Maximum number of concurrent workers
        """
        self.max_workers = max_workers
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
        created_at = time.time()
        
        if job_store.create_job(job_id, len(items), created_at):
            logging.info(f"Created job {job_id} with {len(items)} items")
            return job_id
        else:
            raise RuntimeError(f"Failed to create job {job_id}")
    
    def start_job(self, job_id: str, process_func: Callable[[str], JobResult], items: List[str]):
        """
        Start processing a job in the background.
        
        Args:
            job_id: Job ID
            process_func: Function to process each item (must accept item and return JobResult)
            items: List of items to process
        """
        job = job_store.get_job(job_id)
        if not job:
            logging.error(f"Job {job_id} not found")
            return
        
        if job['status'] != JobStatus.QUEUED.value:
            logging.warning(f"Job {job_id} already started")
            return
        
        started_at = time.time()
        job_store.update_job_status(job_id, JobStatus.PROCESSING.value, started_at=started_at)
        
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
                    job_store.add_job_result(
                        job_id, 
                        result.item, 
                        result.success, 
                        result.error, 
                        result.details
                    )
                except Exception as e:
                    logging.error(f"Error processing item {item} in job {job_id}: {e}")
                    job_store.add_job_result(job_id, item, False, str(e))
            
            # Mark job as completed
            completed_at = time.time()
            job_store.update_job_status(job_id, JobStatus.COMPLETED.value, completed_at=completed_at)
            logging.info(f"Completed job {job_id}")
        
        except Exception as e:
            logging.error(f"Fatal error processing job {job_id}: {e}")
            completed_at = time.time()
            job_store.update_job_status(
                job_id, 
                JobStatus.FAILED.value, 
                completed_at=completed_at, 
                error=str(e)
            )
    
    def get_job_status(self, job_id: str) -> Optional[Dict[str, Any]]:
        """
        Get job status.
        
        Args:
            job_id: Job ID
            
        Returns:
            Job status dictionary or None if not found
        """
        return job_store.get_job(job_id)
    
    def cancel_job(self, job_id: str) -> bool:
        """
        Cancel a job (marks as cancelled, but running tasks may complete).
        
        Args:
            job_id: Job ID
            
        Returns:
            True if cancelled, False if not found or already completed
        """
        job = job_store.get_job(job_id)
        if not job:
            return False
        
        if job['status'] in (JobStatus.COMPLETED.value, JobStatus.FAILED.value, JobStatus.CANCELLED.value):
            return False
        
        completed_at = time.time()
        job_store.update_job_status(job_id, JobStatus.CANCELLED.value, completed_at=completed_at)
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
        if job_store.delete_job(job_id):
            logging.info(f"Deleted job {job_id}")
            return True
        return False
    
    def list_jobs(self) -> List[Dict[str, Any]]:
        """
        List all jobs.
        
        Returns:
            List of job status dictionaries
        """
        return job_store.list_jobs()
    
    def _cleanup_old_jobs(self):
        """
        Periodically clean up old completed jobs.
        Runs in a background thread.
        """
        while True:
            time.sleep(300)  # Run every 5 minutes
            
            try:
                cutoff_time = time.time() - 86400  # 24 hours
                deleted = job_store.cleanup_old_jobs(cutoff_time)
                
                if deleted > 0:
                    logging.info(f"Cleaned up {deleted} old jobs")
            
            except Exception as e:
                logging.error(f"Error cleaning up old jobs: {e}")
    
    def shutdown(self):
        """Shutdown the job manager and wait for all jobs to complete."""
        logging.info("Shutting down job manager")
        self.executor.shutdown(wait=True)


# Global job manager instance
# Job state is now stored in SQLite, so multiple worker processes can share job data.
_job_manager: Optional[JobManager] = None
_job_manager_lock = threading.Lock()


def get_job_manager(max_workers: int = 4) -> JobManager:
    """
    Get the global job manager instance (singleton per process).
    
    Job state is stored in SQLite database, enabling multiple Gunicorn workers
    to share job information and avoid "job not found" errors.
    
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
