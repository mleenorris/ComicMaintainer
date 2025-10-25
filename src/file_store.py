"""
SQLite-based file list storage for tracking comic files.
This module now wraps the unified_store module for backward compatibility.

DEPRECATED: Use unified_store module directly for new code.
This module exists only for backward compatibility.
"""
import unified_store
from unified_store import (
    init_db,
    add_file,
    remove_file,
    rename_file,
    has_file,
    get_all_files,
    get_all_files_with_metadata,
    get_file_count,
    clear_all_files,
    batch_add_files,
    batch_remove_files,
    sync_with_filesystem,
    set_metadata,
    get_metadata,
    get_last_sync_timestamp,
    migrate_from_old_databases,
    get_files_paginated
)

# For backward compatibility, allow setting these values
CONFIG_DIR = unified_store.CONFIG_DIR
FILE_STORE_DIR = unified_store.STORE_DIR
DB_PATH = unified_store.DB_PATH

# Re-export for backward compatibility
__all__ = [
    'init_db',
    'add_file',
    'remove_file',
    'rename_file',
    'has_file',
    'get_all_files',
    'get_all_files_with_metadata',
    'get_file_count',
    'clear_all_files',
    'batch_add_files',
    'batch_remove_files',
    'sync_with_filesystem',
    'set_metadata',
    'get_metadata',
    'get_last_sync_timestamp',
    'get_files_paginated',
    'CONFIG_DIR',
    'FILE_STORE_DIR',
    'DB_PATH',
]

# Trigger migration on first import
_migration_done = False
if not _migration_done:
    migrate_from_old_databases()
    _migration_done = True
