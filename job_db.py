"""
Database backend for job state persistence.
Uses PostgreSQL for shared job storage across multiple workers.
"""
import os
import json
import logging
from datetime import datetime
from typing import Dict, List, Optional, Any
from sqlalchemy import create_engine, Column, String, Integer, Float, Text, Enum as SQLEnum
from sqlalchemy.ext.declarative import declarative_base
from sqlalchemy.orm import sessionmaker, Session
from enum import Enum

Base = declarative_base()


class JobStatus(Enum):
    """Job execution status"""
    QUEUED = "queued"
    PROCESSING = "processing"
    COMPLETED = "completed"
    FAILED = "failed"
    CANCELLED = "cancelled"


class JobModel(Base):
    """SQLAlchemy model for jobs"""
    __tablename__ = 'jobs'
    
    job_id = Column(String(36), primary_key=True)
    status = Column(SQLEnum(JobStatus), nullable=False)
    total_items = Column(Integer, nullable=False)
    processed_items = Column(Integer, default=0)
    results = Column(Text, default='[]')  # JSON array of results
    error = Column(Text, nullable=True)
    created_at = Column(Float, nullable=False)
    started_at = Column(Float, nullable=True)
    completed_at = Column(Float, nullable=True)


class JobDatabase:
    """PostgreSQL backend for job storage"""
    
    def __init__(self, connection_string: Optional[str] = None):
        """
        Initialize database connection.
        
        Args:
            connection_string: PostgreSQL connection string 
                               (default: from DATABASE_URL env var)
        """
        if connection_string is None:
            connection_string = os.environ.get('DATABASE_URL')
        
        if not connection_string:
            raise ValueError("DATABASE_URL environment variable not set")
        
        self.engine = create_engine(connection_string, pool_pre_ping=True)
        self.SessionLocal = sessionmaker(bind=self.engine)
        logging.info(f"Connected to PostgreSQL database")
    
    def initialize_schema(self):
        """Create tables if they don't exist"""
        Base.metadata.create_all(self.engine)
        logging.info("Database schema initialized")
    
    def create_job(self, job_id: str, total_items: int, created_at: float) -> bool:
        """
        Create a new job.
        
        Args:
            job_id: Job ID
            total_items: Total number of items to process
            created_at: Creation timestamp
            
        Returns:
            True if created successfully
        """
        try:
            session = self.SessionLocal()
            try:
                job = JobModel(
                    job_id=job_id,
                    status=JobStatus.QUEUED,
                    total_items=total_items,
                    processed_items=0,
                    results='[]',
                    created_at=created_at
                )
                session.add(job)
                session.commit()
                return True
            finally:
                session.close()
        except Exception as e:
            logging.error(f"Error creating job {job_id}: {e}")
            return False
    
    def get_job(self, job_id: str) -> Optional[Dict[str, Any]]:
        """
        Get job by ID.
        
        Args:
            job_id: Job ID
            
        Returns:
            Job dictionary or None if not found
        """
        try:
            session = self.SessionLocal()
            try:
                job = session.query(JobModel).filter_by(job_id=job_id).first()
                if not job:
                    return None
                
                return {
                    'job_id': job.job_id,
                    'status': job.status.value,
                    'total_items': job.total_items,
                    'processed_items': job.processed_items,
                    'results': json.loads(job.results),
                    'error': job.error,
                    'created_at': job.created_at,
                    'started_at': job.started_at,
                    'completed_at': job.completed_at
                }
            finally:
                session.close()
        except Exception as e:
            logging.error(f"Error getting job {job_id}: {e}")
            return None
    
    def update_job_status(self, job_id: str, status: JobStatus, 
                         started_at: Optional[float] = None,
                         completed_at: Optional[float] = None,
                         error: Optional[str] = None) -> bool:
        """
        Update job status.
        
        Args:
            job_id: Job ID
            status: New status
            started_at: Optional start timestamp
            completed_at: Optional completion timestamp
            error: Optional error message
            
        Returns:
            True if updated successfully
        """
        try:
            session = self.SessionLocal()
            try:
                job = session.query(JobModel).filter_by(job_id=job_id).first()
                if not job:
                    return False
                
                job.status = status
                if started_at is not None:
                    job.started_at = started_at
                if completed_at is not None:
                    job.completed_at = completed_at
                if error is not None:
                    job.error = error
                
                session.commit()
                return True
            finally:
                session.close()
        except Exception as e:
            logging.error(f"Error updating job {job_id} status: {e}")
            return False
    
    def add_job_result(self, job_id: str, result: Dict[str, Any]) -> bool:
        """
        Add a result to a job and increment processed count.
        
        Args:
            job_id: Job ID
            result: Result dictionary
            
        Returns:
            True if added successfully
        """
        try:
            session = self.SessionLocal()
            try:
                job = session.query(JobModel).filter_by(job_id=job_id).first()
                if not job:
                    return False
                
                results = json.loads(job.results)
                results.append(result)
                job.results = json.dumps(results)
                job.processed_items = len(results)
                
                session.commit()
                return True
            finally:
                session.close()
        except Exception as e:
            logging.error(f"Error adding result to job {job_id}: {e}")
            return False
    
    def list_jobs(self) -> List[Dict[str, Any]]:
        """
        List all jobs.
        
        Returns:
            List of job dictionaries
        """
        try:
            session = self.SessionLocal()
            try:
                jobs = session.query(JobModel).all()
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
                    for job in jobs
                ]
            finally:
                session.close()
        except Exception as e:
            logging.error(f"Error listing jobs: {e}")
            return []
    
    def delete_job(self, job_id: str) -> bool:
        """
        Delete a job.
        
        Args:
            job_id: Job ID
            
        Returns:
            True if deleted successfully
        """
        try:
            session = self.SessionLocal()
            try:
                job = session.query(JobModel).filter_by(job_id=job_id).first()
                if not job:
                    return False
                
                session.delete(job)
                session.commit()
                return True
            finally:
                session.close()
        except Exception as e:
            logging.error(f"Error deleting job {job_id}: {e}")
            return False
    
    def cleanup_old_jobs(self, cutoff_time: float) -> int:
        """
        Delete old completed/failed/cancelled jobs.
        
        Args:
            cutoff_time: Timestamp cutoff (jobs older than this are deleted)
            
        Returns:
            Number of jobs deleted
        """
        try:
            session = self.SessionLocal()
            try:
                deleted = session.query(JobModel).filter(
                    JobModel.status.in_([
                        JobStatus.COMPLETED,
                        JobStatus.FAILED,
                        JobStatus.CANCELLED
                    ]),
                    JobModel.completed_at.isnot(None),
                    JobModel.completed_at < cutoff_time
                ).delete(synchronize_session=False)
                
                session.commit()
                return deleted
            finally:
                session.close()
        except Exception as e:
            logging.error(f"Error cleaning up old jobs: {e}")
            return 0


# Global database instance
_db_instance: Optional[JobDatabase] = None


def get_job_database() -> JobDatabase:
    """
    Get the global job database instance (singleton).
    
    Returns:
        JobDatabase instance
    """
    global _db_instance
    
    if _db_instance is None:
        _db_instance = JobDatabase()
        _db_instance.initialize_schema()
    
    return _db_instance
