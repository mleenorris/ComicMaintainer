"""
Shared file operations module for recording file changes across the application.

This module provides common functionality for tracking file additions, removals,
and renames in the file store database.
"""

import logging
import file_store
from error_handler import log_function_entry, log_function_exit, log_error_with_context


def record_file_change(change_type, old_path=None, new_path=None):
    """Record a file change directly in the file store.
    
    This function updates the file store database to track file system changes.
    It is used by the watcher, web interface, and file processing modules to
    maintain a synchronized view of the comic file library.
    
    Args:
        change_type: Type of change - 'add', 'remove', or 'rename'
        old_path: Original file path (required for 'remove' and 'rename')
        new_path: New file path (required for 'add' and 'rename')
        
    Example:
        >>> record_file_change('add', new_path='/comics/Batman.cbz')
        >>> record_file_change('rename', old_path='/comics/old.cbz', new_path='/comics/new.cbz')
        >>> record_file_change('remove', old_path='/comics/deleted.cbz')
    """
    log_function_entry("record_file_change", change_type=change_type, 
                      old_path=old_path, new_path=new_path)
    
    try:
        if change_type == 'add' and new_path:
            file_store.add_file(new_path)
            logging.info(f"Added file to store: {new_path}")
        elif change_type == 'remove' and old_path:
            file_store.remove_file(old_path)
            logging.info(f"Removed file from store: {old_path}")
        elif change_type == 'rename' and old_path and new_path:
            file_store.rename_file(old_path, new_path)
            logging.info(f"Renamed file in store: {old_path} -> {new_path}")
        
        log_function_exit("record_file_change", result="success")
    except Exception as e:
        log_error_with_context(
            e,
            context=f"Recording file change: {change_type}",
            additional_info={"old_path": old_path, "new_path": new_path}
        )
        logging.error(f"Error recording file change: {e}")
