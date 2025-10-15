"""
Job storage backends for persisting job state.
Supports in-memory storage (default) and Redis backend.
"""
import json
import logging
import time
from abc import ABC, abstractmethod
from typing import Dict, List, Optional, Any
from dataclasses import asdict
from enum import Enum


class JobStore(ABC):
    """Abstract interface for job storage backends"""
    
    @abstractmethod
    def save_job(self, job_id: str, job_data: Dict[str, Any]) -> bool:
        """Save or update a job"""
        pass
    
    @abstractmethod
    def get_job(self, job_id: str) -> Optional[Dict[str, Any]]:
        """Retrieve a job by ID"""
        pass
    
    @abstractmethod
    def delete_job(self, job_id: str) -> bool:
        """Delete a job"""
        pass
    
    @abstractmethod
    def list_jobs(self) -> List[Dict[str, Any]]:
        """List all jobs"""
        pass
    
    @abstractmethod
    def cleanup_old_jobs(self, cutoff_time: float) -> int:
        """Clean up jobs older than cutoff_time, returns count of deleted jobs"""
        pass


class InMemoryJobStore(JobStore):
    """In-memory job storage (default, original behavior)"""
    
    def __init__(self):
        self.jobs: Dict[str, Dict[str, Any]] = {}
    
    def save_job(self, job_id: str, job_data: Dict[str, Any]) -> bool:
        """Save or update a job in memory"""
        self.jobs[job_id] = job_data
        return True
    
    def get_job(self, job_id: str) -> Optional[Dict[str, Any]]:
        """Retrieve a job from memory"""
        return self.jobs.get(job_id)
    
    def delete_job(self, job_id: str) -> bool:
        """Delete a job from memory"""
        if job_id in self.jobs:
            del self.jobs[job_id]
            return True
        return False
    
    def list_jobs(self) -> List[Dict[str, Any]]:
        """List all jobs in memory"""
        return list(self.jobs.values())
    
    def cleanup_old_jobs(self, cutoff_time: float) -> int:
        """Clean up old completed jobs from memory"""
        jobs_to_delete = [
            job_id
            for job_id, job in self.jobs.items()
            if job.get('status') in ('completed', 'failed', 'cancelled')
            and job.get('completed_at') is not None
            and job.get('completed_at', 0) < cutoff_time
        ]
        
        for job_id in jobs_to_delete:
            del self.jobs[job_id]
        
        return len(jobs_to_delete)


class RedisJobStore(JobStore):
    """Redis-based job storage for multi-worker/multi-instance deployments"""
    
    def __init__(self, redis_url: str = "redis://localhost:6379/0", key_prefix: str = "comicmaintainer:job:"):
        """
        Initialize Redis job store.
        
        Args:
            redis_url: Redis connection URL (e.g., redis://localhost:6379/0)
            key_prefix: Prefix for Redis keys
        """
        try:
            import redis
            self.redis_client = redis.from_url(redis_url, decode_responses=True)
            self.key_prefix = key_prefix
            # Test connection
            self.redis_client.ping()
            logging.info(f"Connected to Redis at {redis_url}")
        except ImportError:
            raise ImportError("redis package is required for RedisJobStore. Install with: pip install redis")
        except Exception as e:
            raise ConnectionError(f"Failed to connect to Redis: {e}")
    
    def _make_key(self, job_id: str) -> str:
        """Generate Redis key for a job"""
        return f"{self.key_prefix}{job_id}"
    
    def save_job(self, job_id: str, job_data: Dict[str, Any]) -> bool:
        """Save or update a job in Redis"""
        try:
            key = self._make_key(job_id)
            # Serialize job data to JSON
            serialized = json.dumps(job_data)
            # Store in Redis with 24-hour expiration
            self.redis_client.setex(key, 86400, serialized)
            return True
        except Exception as e:
            logging.error(f"Error saving job {job_id} to Redis: {e}")
            return False
    
    def get_job(self, job_id: str) -> Optional[Dict[str, Any]]:
        """Retrieve a job from Redis"""
        try:
            key = self._make_key(job_id)
            data = self.redis_client.get(key)
            if data:
                return json.loads(data)
            return None
        except Exception as e:
            logging.error(f"Error retrieving job {job_id} from Redis: {e}")
            return None
    
    def delete_job(self, job_id: str) -> bool:
        """Delete a job from Redis"""
        try:
            key = self._make_key(job_id)
            result = self.redis_client.delete(key)
            return result > 0
        except Exception as e:
            logging.error(f"Error deleting job {job_id} from Redis: {e}")
            return False
    
    def list_jobs(self) -> List[Dict[str, Any]]:
        """List all jobs from Redis"""
        try:
            # Get all keys matching our prefix
            pattern = f"{self.key_prefix}*"
            keys = self.redis_client.keys(pattern)
            
            jobs = []
            for key in keys:
                data = self.redis_client.get(key)
                if data:
                    try:
                        job = json.loads(data)
                        jobs.append(job)
                    except json.JSONDecodeError:
                        logging.warning(f"Failed to decode job data for key {key}")
            
            return jobs
        except Exception as e:
            logging.error(f"Error listing jobs from Redis: {e}")
            return []
    
    def cleanup_old_jobs(self, cutoff_time: float) -> int:
        """Clean up old completed jobs from Redis"""
        try:
            pattern = f"{self.key_prefix}*"
            keys = self.redis_client.keys(pattern)
            
            deleted_count = 0
            for key in keys:
                data = self.redis_client.get(key)
                if data:
                    try:
                        job = json.loads(data)
                        if (job.get('status') in ('completed', 'failed', 'cancelled') and
                            job.get('completed_at') is not None and
                            job.get('completed_at', 0) < cutoff_time):
                            self.redis_client.delete(key)
                            deleted_count += 1
                    except json.JSONDecodeError:
                        logging.warning(f"Failed to decode job data for key {key}")
            
            return deleted_count
        except Exception as e:
            logging.error(f"Error cleaning up old jobs from Redis: {e}")
            return 0


def create_job_store(backend: str = "memory", redis_url: Optional[str] = None) -> JobStore:
    """
    Factory function to create the appropriate job store.
    
    Args:
        backend: Type of backend ("memory" or "redis")
        redis_url: Redis connection URL (required if backend is "redis")
    
    Returns:
        JobStore instance
    """
    if backend.lower() == "redis":
        if not redis_url:
            raise ValueError("redis_url is required when backend is 'redis'")
        return RedisJobStore(redis_url=redis_url)
    else:
        return InMemoryJobStore()
