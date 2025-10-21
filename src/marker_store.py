"""
SQLite-based marker storage for tracking file processing status.
This module now wraps the unified_store module for backward compatibility.

DEPRECATED: Use unified_store module directly for new code.
This module exists only for backward compatibility.
"""
from unified_store import (
    init_db,
    add_marker,
    remove_marker,
    has_marker,
    get_markers,
    get_all_markers_by_type,
    cleanup_markers,
    migrate_from_old_databases
)

# Re-export for backward compatibility
__all__ = [
    'init_db',
    'add_marker',
    'remove_marker',
    'has_marker',
    'get_markers',
    'get_all_markers_by_type',
    'cleanup_markers',
]

# Trigger migration on first import
_migration_done = False
if not _migration_done:
    migrate_from_old_databases()
    _migration_done = True
