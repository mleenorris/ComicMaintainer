"""
Environment variable validation module for ComicMaintainer.

This module provides utilities to validate required environment variables
and configuration settings at startup.
"""

import os
import sys
import logging
from typing import Dict, List, Tuple, Optional


def validate_env_vars() -> Tuple[bool, List[str]]:
    """
    Validate required and optional environment variables.
    
    Returns:
        Tuple of (is_valid, error_messages)
    """
    errors = []
    warnings = []
    
    # Required environment variables
    required_vars = {
        'WATCHED_DIR': 'Directory to watch for comic files'
    }
    
    # Optional environment variables with defaults
    optional_vars = {
        'PROCESS_SCRIPT': ('/app/process_file.py', 'Script to run for processing'),
        'WEB_PORT': ('5000', 'Port for the web interface'),
        'GUNICORN_WORKERS': ('2', 'Number of Gunicorn worker processes'),
        'PUID': ('99', 'User ID to run as'),
        'PGID': ('100', 'Group ID to run as'),
        'MAX_WORKERS': ('4', 'Number of concurrent worker threads'),
        'LOG_MAX_BYTES': ('5242880', 'Maximum log file size in bytes'),
    }
    
    # Check required variables
    for var, description in required_vars.items():
        value = os.environ.get(var)
        if not value:
            errors.append(f"Required environment variable '{var}' is not set ({description})")
        elif var == 'WATCHED_DIR':
            # Additional validation for WATCHED_DIR
            if not os.path.exists(value):
                errors.append(f"WATCHED_DIR '{value}' does not exist")
            elif not os.path.isdir(value):
                errors.append(f"WATCHED_DIR '{value}' is not a directory")
            elif not os.access(value, os.R_OK):
                errors.append(f"WATCHED_DIR '{value}' is not readable")
    
    # Check optional variables and set defaults
    for var, (default, description) in optional_vars.items():
        value = os.environ.get(var)
        if not value:
            warnings.append(f"Optional variable '{var}' not set, using default: {default} ({description})")
            os.environ[var] = default
    
    # Validate numeric values
    numeric_vars = {
        'WEB_PORT': (1, 65535),
        'GUNICORN_WORKERS': (1, 32),
        'PUID': (0, 65535),
        'PGID': (0, 65535),
        'MAX_WORKERS': (1, 64),
        'LOG_MAX_BYTES': (1024, 1073741824),  # 1KB to 1GB
    }
    
    for var, (min_val, max_val) in numeric_vars.items():
        value = os.environ.get(var)
        if value:
            try:
                num_value = int(value)
                if num_value < min_val or num_value > max_val:
                    errors.append(f"Environment variable '{var}' must be between {min_val} and {max_val}, got {num_value}")
            except ValueError:
                errors.append(f"Environment variable '{var}' must be a valid integer, got '{value}'")
    
    # Check DUPLICATE_DIR if set
    duplicate_dir = os.environ.get('DUPLICATE_DIR')
    if duplicate_dir:
        if not os.path.exists(duplicate_dir):
            warnings.append(f"DUPLICATE_DIR '{duplicate_dir}' does not exist (will be created if needed)")
        elif not os.path.isdir(duplicate_dir):
            errors.append(f"DUPLICATE_DIR '{duplicate_dir}' exists but is not a directory")
        elif not os.access(duplicate_dir, os.W_OK):
            errors.append(f"DUPLICATE_DIR '{duplicate_dir}' is not writable")
    
    # Check DEBUG_MODE
    debug_mode = os.environ.get('DEBUG_MODE', 'false').lower()
    if debug_mode not in ['true', 'false', '1', '0', 'yes', 'no']:
        warnings.append(f"DEBUG_MODE has invalid value '{debug_mode}', should be true/false")
    
    # Check GitHub issue creation settings
    github_token = os.environ.get('GITHUB_TOKEN', '')
    github_repo = os.environ.get('GITHUB_REPOSITORY', 'mleenorris/ComicMaintainer')
    github_assignee = os.environ.get('GITHUB_ISSUE_ASSIGNEE', 'copilot')
    
    if github_token:
        # If GITHUB_TOKEN is set, validate the repository format
        if '/' not in github_repo:
            errors.append(f"GITHUB_REPOSITORY must be in 'owner/repo' format, got '{github_repo}'")
        else:
            repo_parts = github_repo.split('/')
            if len(repo_parts) != 2 or not repo_parts[0] or not repo_parts[1]:
                errors.append(f"GITHUB_REPOSITORY must be in 'owner/repo' format with non-empty owner and repo, got '{github_repo}'")
        
        # Validate token format (basic check)
        if not github_token.startswith('ghp_') and not github_token.startswith('github_pat_'):
            warnings.append(f"GITHUB_TOKEN doesn't match expected format (should start with 'ghp_' or 'github_pat_')")
        
        # Validate assignee is not empty
        if not github_assignee:
            warnings.append("GITHUB_ISSUE_ASSIGNEE is empty - issues will be created without assignee")
    
    # Log warnings
    for warning in warnings:
        logging.warning(warning)
    
    # Return validation results
    is_valid = len(errors) == 0
    return is_valid, errors


def print_env_summary():
    """Print a summary of current environment configuration."""
    print("\n" + "=" * 70)
    print("ComicMaintainer Environment Configuration")
    print("=" * 70)
    
    config = {
        'WATCHED_DIR': os.environ.get('WATCHED_DIR', 'NOT SET'),
        'DUPLICATE_DIR': os.environ.get('DUPLICATE_DIR', 'NOT SET'),
        'WEB_PORT': os.environ.get('WEB_PORT', '5000'),
        'GUNICORN_WORKERS': os.environ.get('GUNICORN_WORKERS', '2'),
        'MAX_WORKERS': os.environ.get('MAX_WORKERS', '4'),
        'PUID': os.environ.get('PUID', '99'),
        'PGID': os.environ.get('PGID', '100'),
        'DEBUG_MODE': os.environ.get('DEBUG_MODE', 'false'),
        'LOG_MAX_BYTES': f"{int(os.environ.get('LOG_MAX_BYTES', 5242880)) / 1024 / 1024:.1f}MB",
    }
    
    for key, value in config.items():
        print(f"  {key:20} = {value}")
    
    # Show GitHub issue creation configuration if enabled
    github_token = os.environ.get('GITHUB_TOKEN', '')
    if github_token:
        print("\n  GitHub Issue Creation: ENABLED")
        print(f"    Repository:        {os.environ.get('GITHUB_REPOSITORY', 'mleenorris/ComicMaintainer')}")
        print(f"    Assignee:          {os.environ.get('GITHUB_ISSUE_ASSIGNEE', 'copilot')}")
        print(f"    Token:             {'*' * 8}...{github_token[-4:] if len(github_token) >= 4 else '***'}")
    else:
        print("\n  GitHub Issue Creation: DISABLED (GITHUB_TOKEN not set)")
    
    print("=" * 70 + "\n")


def validate_and_exit_on_error():
    """
    Validate environment variables and exit if validation fails.
    
    This function should be called at application startup.
    """
    is_valid, errors = validate_env_vars()
    
    if not is_valid:
        print("\n" + "=" * 70, file=sys.stderr)
        print("ERROR: Environment validation failed", file=sys.stderr)
        print("=" * 70, file=sys.stderr)
        for error in errors:
            print(f"  ✗ {error}", file=sys.stderr)
        print("=" * 70 + "\n", file=sys.stderr)
        print("Please fix the above errors and try again.", file=sys.stderr)
        print("See README.md for environment variable documentation.\n", file=sys.stderr)
        sys.exit(1)
    
    # Print configuration summary
    print_env_summary()


if __name__ == "__main__":
    # For testing
    logging.basicConfig(level=logging.INFO)
    validate_and_exit_on_error()
    print("✓ All environment variables are valid")
