"""
Shared logging configuration module for the application.

This module provides common functionality for setting up logging across all
application modules (watcher, web interface, and file processing).
"""

import os
import sys
import logging
from logging.handlers import RotatingFileHandler
from config import get_log_max_bytes


CONFIG_DIR = os.environ.get('CONFIG_DIR_OVERRIDE', '/Config')
LOG_DIR = os.path.join(CONFIG_DIR, 'Log')


def setup_logging(module_name, use_rotation=True):
    """Set up logging configuration for a module.
    
    This function configures both console and file logging with the specified
    module name as a prefix in log messages. It handles log rotation and ensures
    the log directory exists.
    
    Args:
        module_name: Name of the module (e.g., 'WATCHER', 'WEBPAGE', 'PROCESSOR')
        use_rotation: Whether to use rotating file handler (default: True)
        
    Returns:
        The configured log handler for further customization if needed
        
    Example:
        >>> setup_logging('WATCHER')
        >>> logging.info('Watcher started')
    """
    # Ensure log directory exists
    os.makedirs(LOG_DIR, exist_ok=True)
    
    # Set up logging to stdout first
    # Initialize basic logging first to avoid issues with get_log_max_bytes() logging errors
    logging.basicConfig(
        level=logging.INFO,
        format=f'%(asctime)s [{module_name}] %(levelname)s %(message)s',
        handlers=[
            logging.StreamHandler(sys.stdout)
        ]
    )
    
    # Explicitly set root logger level to INFO (in case it was already configured by imports)
    logging.getLogger().setLevel(logging.INFO)
    
    # Now safely get log max bytes (which may log warnings)
    if use_rotation:
        log_max_bytes = get_log_max_bytes()
        log_handler = RotatingFileHandler(
            os.path.join(LOG_DIR, "ComicMaintainer.log"),
            maxBytes=log_max_bytes,
            backupCount=3
        )
    else:
        log_handler = logging.FileHandler(os.path.join(LOG_DIR, "ComicMaintainer.log"))
    
    log_handler.setLevel(logging.INFO)
    log_handler.setFormatter(logging.Formatter(f'%(asctime)s [{module_name}] %(levelname)s %(message)s'))
    
    # Add the file handler to the root logger
    logging.getLogger().addHandler(log_handler)
    
    return log_handler
