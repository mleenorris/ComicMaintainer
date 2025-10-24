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
from error_handler import (
    setup_debug_logging, log_debug, log_error_with_context,
    log_function_entry, log_function_exit
)

# Setup debug logging
setup_debug_logging()
log_debug("job_manager module initialized")


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
        log_function_entry("JobManager.__init__", max_workers=max_workers)
        self.max_workers = max_workers
        # Executor for managing jobs (job orchestration)
        self.executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix="job-mgr")
        self._cleanup_timer = None
        self._schedule_cleanup()
        log_debug("JobManager initialized", max_workers=max_workers)
        log_function_exit("JobManager.__init__")
    
    def create_job(self, items: List[str]) -> str:
        """
        Create a new job.
        
        Args:
            items: List of items to process
            
        Returns:
            Job ID
        """
        log_function_entry("create_job", items_count=len(items))
        job_id = str(uuid.uuid4())
        created_at = time.time()
        log_debug("Creating new job", job_id=job_id, items_count=len(items))
        
        if job_store.create_job(job_id, len(items), created_at):
            logging.info(f"[JOB {job_id}] Created new job with {len(items)} items (queued)")
            log_function_exit("create_job", result=job_id)
            return job_id
        else:
            error_msg = f"Failed to create job {job_id}"
            logging.error(f"[JOB {job_id}] {error_msg} in database")
            log_error_with_context(
                RuntimeError(error_msg),
                context=f"Creating job with {len(items)} items",
                additional_info={"job_id": job_id, "items_count": len(items)}
            )
            raise RuntimeError(error_msg)
    
    def start_job(self, job_id: str, process_func: Callable[[str], JobResult], items: List[str]):
        """
        Start processing a job in the background.
        
        Args:
            job_id: Job ID
            process_func: Function to process each item (must accept item and return JobResult)
            items: List of items to process
            
        Raises:
            RuntimeError: If job cannot be started (not found or not in QUEUED status)
        """
        log_function_entry("start_job", job_id=job_id, items_count=len(items))
        
        job = job_store.get_job(job_id)
        if not job:
            error_msg = f"Cannot start job - not found in database"
            logging.error(f"[JOB {job_id}] {error_msg}")
            log_debug("Job not found in database", job_id=job_id)
            raise RuntimeError(error_msg)
        
        log_debug("Retrieved job from database", job_id=job_id, status=job.get('status'))
        
        if job['status'] != JobStatus.QUEUED.value:
            error_msg = f"Cannot start job - already {job['status']} (not queued)"
            logging.warning(f"[JOB {job_id}] {error_msg}")
            log_debug("Job not in QUEUED status", job_id=job_id, current_status=job['status'])
            raise RuntimeError(error_msg)
        
        started_at = time.time()
        log_debug("Updating job status to PROCESSING", job_id=job_id, started_at=started_at)
        job_store.update_job_status(job_id, JobStatus.PROCESSING.value, started_at=started_at)
        
        # Broadcast status change to PROCESSING
        self._broadcast_job_progress(job_id, JobStatus.PROCESSING.value, 0, len(items), 0, 0)
        
        # Submit job to thread pool
        log_debug("Submitting job to executor", job_id=job_id)
        self.executor.submit(self._process_job, job_id, process_func, items)
        logging.info(f"[JOB {job_id}] Job submitted to worker pool for async processing")
        log_function_exit("start_job")
    
    def _broadcast_job_progress(self, job_id: str, status: str, processed: int, total: int, success: int, errors: int):
        """
        Broadcast job progress update via SSE.
        
        Args:
            job_id: Job ID
            status: Current job status
            processed: Number of items processed
            total: Total number of items
            success: Number of successful items
            errors: Number of failed items
        """
        try:
            from event_broadcaster import broadcast_job_updated
            broadcast_job_updated(
                job_id=job_id,
                status=status,
                progress={
                    'processed': processed,
                    'total': total,
                    'success': success,
                    'errors': errors,
                    'percentage': (processed / total * 100) if total > 0 else 0
                }
            )
            log_debug("Broadcast job progress", job_id=job_id, processed=processed, total=total)
        except Exception as e:
            # Don't fail job processing if broadcast fails, but log prominently
            # This helps diagnose cases where the frontend appears stuck
            logging.error(f"[JOB {job_id}] CRITICAL: Failed to broadcast progress update - frontend may appear stuck! Error: {e}")
            # Try to log stack trace for debugging
            import traceback
            logging.error(f"[JOB {job_id}] Broadcast failure stack trace:\n{traceback.format_exc()}")
    
    def _clear_active_job_if_current(self, job_id: str):
        """
        Clear the active job from preferences if it matches this job_id.
        This prevents stale job references from persisting after job completion.
        
        Args:
            job_id: Job ID to check against active job
        """
        try:
            from preferences_store import get_active_job, clear_active_job
            active_job = get_active_job()
            
            if active_job and active_job.get('job_id') == job_id:
                clear_active_job()
                logging.info(f"[JOB {job_id}] Cleared active job from preferences (job completed/failed)")
        except Exception as e:
            logging.error(f"[JOB {job_id}] Error clearing active job: {e}")
    
    def _process_job(self, job_id: str, process_func: Callable[[str], JobResult], items: List[str]):
        """
        Process job items concurrently.
        
        Args:
            job_id: Job ID
            process_func: Function to process each item
            items: List of items to process
        """
        log_function_entry("_process_job", job_id=job_id, items_count=len(items))
        
        # Create a separate executor for processing items to avoid deadlock
        # The job manager executor runs _process_job, and this executor runs the items
        # This prevents the thread pool from deadlocking when trying to submit items
        # to the same pool that's running the job orchestration
        item_executor = ThreadPoolExecutor(max_workers=self.max_workers, thread_name_prefix=f"job-{job_id[:8]}")
        
        try:
            logging.info(f"[JOB {job_id}] Starting processing of {len(items)} items with {self.max_workers} workers")
            log_debug("Submitting items to executor", job_id=job_id, workers=self.max_workers, items_count=len(items))
            
            # Submit all items for processing to the dedicated item executor
            futures = {item_executor.submit(process_func, item): item for item in items}
            log_debug("All items submitted to executor", job_id=job_id, futures_count=len(futures))
            
            # Track progress
            completed_count = 0
            success_count = 0
            error_count = 0
            
            # Process results as they complete
            for future in as_completed(futures):
                item = futures[future]
                log_debug("Processing completed item", job_id=job_id, item=item, completed=completed_count+1, total=len(items))
                
                try:
                    result = future.result()
                    log_debug("Got result from future", job_id=job_id, item=item, success=result.success)
                    
                    job_store.add_job_result(
                        job_id, 
                        result.item, 
                        result.success, 
                        result.error, 
                        result.details
                    )
                    completed_count += 1
                    
                    if result.success:
                        success_count += 1
                    else:
                        error_count += 1
                    
                    log_debug("Item processed", job_id=job_id, completed=completed_count, success=success_count, errors=error_count)
                    
                    # Broadcast progress update after each item (real-time callback)
                    self._broadcast_job_progress(
                        job_id=job_id,
                        status=JobStatus.PROCESSING.value,
                        processed=completed_count,
                        total=len(items),
                        success=success_count,
                        errors=error_count
                    )
                    
                    # Log progress every 10 items or on last item
                    if completed_count % 10 == 0 or completed_count == len(items):
                        logging.info(f"[JOB {job_id}] Progress: {completed_count}/{len(items)} items processed "
                                   f"({success_count} success, {error_count} errors)")
                        log_debug("Progress update", job_id=job_id, completed=completed_count, total=len(items))
                except Exception as e:
                    log_error_with_context(
                        e,
                        context=f"Processing item in job {job_id}: {item}",
                        additional_info={"job_id": job_id, "item": item},
                        create_github_issue=True
                    )
                    logging.error(f"[JOB {job_id}] Error processing item {item}: {e}")
                    job_store.add_job_result(job_id, item, False, str(e))
                    completed_count += 1
                    error_count += 1
                    
                    # Broadcast progress update for error case too
                    self._broadcast_job_progress(
                        job_id=job_id,
                        status=JobStatus.PROCESSING.value,
                        processed=completed_count,
                        total=len(items),
                        success=success_count,
                        errors=error_count
                    )
            
            # Mark job as completed
            completed_at = time.time()
            log_debug("Job processing complete, updating status", job_id=job_id, success=success_count, errors=error_count)
            job_store.update_job_status(job_id, JobStatus.COMPLETED.value, completed_at=completed_at)
            logging.info(f"[JOB {job_id}] Completed: {success_count} succeeded, {error_count} failed out of {len(items)} items")
            
            # Broadcast final completion status - retry multiple times to ensure frontend receives it
            for attempt in range(3):
                try:
                    self._broadcast_job_progress(
                        job_id=job_id,
                        status=JobStatus.COMPLETED.value,
                        processed=len(items),
                        total=len(items),
                        success=success_count,
                        errors=error_count
                    )
                    break  # Success, exit retry loop
                except Exception as e:
                    if attempt < 2:
                        logging.warning(f"[JOB {job_id}] Completion broadcast attempt {attempt+1} failed, retrying... Error: {e}")
                        time.sleep(0.5)  # Brief delay before retry
                    else:
                        logging.error(f"[JOB {job_id}] All completion broadcast attempts failed! Frontend may not detect completion.")
            
            # Clear active job from preferences if this job is the active one
            # This ensures stale job references don't persist after completion
            self._clear_active_job_if_current(job_id)
            log_function_exit("_process_job", result="completed")
        
        except Exception as e:
            log_error_with_context(
                e,
                context=f"Fatal error during job processing: {job_id}",
                additional_info={"job_id": job_id, "items_count": len(items)},
                create_github_issue=True
            )
            logging.error(f"[JOB {job_id}] Fatal error during processing: {e}")
            completed_at = time.time()
            job_store.update_job_status(
                job_id, 
                JobStatus.FAILED.value, 
                completed_at=completed_at, 
                error=str(e)
            )
            
            # Broadcast failure status
            self._broadcast_job_progress(
                job_id=job_id,
                status=JobStatus.FAILED.value,
                processed=completed_count,
                total=len(items),
                success=success_count,
                errors=error_count
            )
            
            # Clear active job from preferences if this job is the active one
            # This ensures stale job references don't persist after failure
            self._clear_active_job_if_current(job_id)
            log_function_exit("_process_job", result="failed")
        
        finally:
            # Always shutdown the item executor to free resources
            log_debug("Shutting down item executor", job_id=job_id)
            item_executor.shutdown(wait=True)
            log_debug("Item executor shut down", job_id=job_id)
    
    def get_job_status(self, job_id: str) -> Optional[Dict[str, Any]]:
        """
        Get job status.
        
        Args:
            job_id: Job ID
            
        Returns:
            Job status dictionary or None if not found
        """
        # Validate job_id format (should be a UUID)
        try:
            import uuid
            uuid.UUID(job_id)
        except ValueError:
            # Invalid job_id format - likely stale data or incorrect usage
            log_debug("Invalid job_id format (expected UUID)", job_id=job_id)
            return None
        
        status = job_store.get_job(job_id)
        if status:
            logging.debug(f"[JOB {job_id}] Status check: {status['status']} - {status['processed_items']}/{status['total_items']} items")
        else:
            logging.warning(f"[JOB {job_id}] Status check failed - job not found")
        return status
    
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
            logging.warning(f"[JOB {job_id}] Cannot cancel job - not found")
            return False
        
        if job['status'] in (JobStatus.COMPLETED.value, JobStatus.FAILED.value, JobStatus.CANCELLED.value):
            logging.info(f"[JOB {job_id}] Cannot cancel job - already {job['status']}")
            return False
        
        completed_at = time.time()
        job_store.update_job_status(job_id, JobStatus.CANCELLED.value, completed_at=completed_at)
        logging.info(f"[JOB {job_id}] Job cancelled (was {job['status']})")
        
        # Broadcast cancellation status
        self._broadcast_job_progress(
            job_id=job_id,
            status=JobStatus.CANCELLED.value,
            processed=job['processed_items'],
            total=job['total_items'],
            success=0,  # Don't have detailed success/error counts at this point
            errors=0
        )
        
        # Clear active job from preferences if this job is the active one
        self._clear_active_job_if_current(job_id)
        
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
            logging.info(f"[JOB {job_id}] Job deleted from history")
            return True
        logging.warning(f"[JOB {job_id}] Cannot delete job - not found")
        return False
    
    def list_jobs(self) -> List[Dict[str, Any]]:
        """
        List all jobs.
        
        Returns:
            List of job status dictionaries
        """
        return job_store.list_jobs()
    
    def _schedule_cleanup(self):
        """Schedule the next cleanup run using timer (event-based)"""
        self._cleanup_timer = threading.Timer(300.0, self._cleanup_old_jobs)
        self._cleanup_timer.daemon = True
        self._cleanup_timer.start()
    
    def _cleanup_old_jobs(self):
        """
        Clean up old completed jobs and reschedule.
        Uses event-based timer instead of polling with sleep.
        """
        try:
            cutoff_time = time.time() - 86400  # 24 hours
            deleted = job_store.cleanup_old_jobs(cutoff_time)
            
            if deleted > 0:
                logging.info(f"[JOB CLEANUP] Removed {deleted} old completed jobs (>24 hours)")
            else:
                logging.debug(f"[JOB CLEANUP] No old jobs to clean up")
        
        except Exception as e:
            logging.error(f"[JOB CLEANUP] Error during cleanup: {e}")
        
        finally:
            # Reschedule for next run (5 minutes)
            self._schedule_cleanup()
    
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
