"""
Centralized error handling and logging module.

Provides utilities for:
- Extensive debug logging
- Error tracking and reporting
- Automatic GitHub issue creation on errors (when configured)
"""

import os
import sys
import logging
import traceback
import json
from datetime import datetime
from typing import Optional, Dict, Any


# Configuration from environment variables
DEBUG_MODE = os.environ.get('DEBUG_MODE', 'false').lower() == 'true'
GITHUB_TOKEN = os.environ.get('GITHUB_TOKEN', '')
GITHUB_REPO = os.environ.get('GITHUB_REPOSITORY', 'mleenorris/ComicMaintainer')
GITHUB_ISSUE_ASSIGNEE = os.environ.get('GITHUB_ISSUE_ASSIGNEE', 'copilot')

# GitHub API URL - enforce HTTPS for security
# Always use secure HTTPS connection, no override allowed
GITHUB_API_URL = 'https://api.github.com'




def setup_debug_logging(logger_name: str = None) -> logging.Logger:
    """
    Setup debug logging configuration.
    
    Args:
        logger_name: Name of the logger (default: root logger)
        
    Returns:
        Configured logger instance
    """
    logger = logging.getLogger(logger_name) if logger_name else logging.getLogger()
    
    # Set debug level if DEBUG_MODE is enabled
    if DEBUG_MODE and logger.level > logging.DEBUG:
        logger.setLevel(logging.DEBUG)
        logging.info(f"Debug logging enabled for {logger_name or 'root logger'}")
    
    return logger


def log_debug(message: str, **kwargs):
    """
    Log a debug message with optional context.
    
    Args:
        message: Debug message to log
        **kwargs: Additional context to include in the log
    """
    if DEBUG_MODE:
        context = f" | Context: {json.dumps(kwargs)}" if kwargs else ""
        logging.debug(f"{message}{context}")


def log_error_with_context(
    error: Exception,
    context: str = "",
    additional_info: Optional[Dict[str, Any]] = None,
    create_github_issue: bool = True
):
    """
    Log an error with full context and optionally create a GitHub issue.
    
    Args:
        error: The exception that occurred
        context: Description of what was being done when the error occurred
        additional_info: Additional information to include in the log
        create_github_issue: Whether to attempt creating a GitHub issue
    """
    # Generate error ID for tracking
    error_type = type(error).__name__
    error_message = str(error)
    error_id = f"{error_type}:{hash(error_message) % 10000}"
    
    # Build comprehensive error log
    log_parts = [
        f"ERROR [{error_id}]: {error_type}: {error_message}"
    ]
    
    if context:
        log_parts.append(f"Context: {context}")
    
    if additional_info:
        log_parts.append(f"Additional Info: {json.dumps(additional_info, default=str)}")
    
    # Get traceback
    tb = traceback.format_exc()
    log_parts.append(f"Traceback:\n{tb}")
    
    # Log the error
    full_log = "\n".join(log_parts)
    logging.error(full_log)
    
    # Debug log if enabled
    if DEBUG_MODE:
        logging.debug(f"Error details - ID: {error_id}, Type: {error_type}, Context: {context}")
    
    # Create GitHub issue if configured
    if create_github_issue and GITHUB_TOKEN:
        try:
            _create_github_issue(
                error_type=error_type,
                error_message=error_message,
                context=context,
                traceback_text=tb,
                additional_info=additional_info,
                error_id=error_id
            )
        except Exception as issue_error:
            logging.warning(f"Failed to create GitHub issue for error {error_id}: {issue_error}")


def _create_github_issue(
    error_type: str,
    error_message: str,
    context: str,
    traceback_text: str,
    additional_info: Optional[Dict[str, Any]],
    error_id: str
):
    """
    Create a GitHub issue for an error.
    
    Args:
        error_type: Type of the exception
        error_message: Error message
        context: Context description
        traceback_text: Full traceback
        additional_info: Additional information
        error_id: Unique error identifier
    """
    try:
        import requests
    except ImportError:
        logging.warning("requests library not available - cannot create GitHub issues")
        return
    
    # Build issue title
    title = f"[Auto-Generated] {error_type}: {error_message[:60]}"
    if len(error_message) > 60:
        title += "..."
    
    # Build issue body
    body_parts = [
        "## Automated Error Report",
        f"**Error ID:** `{error_id}`",
        f"**Error Type:** `{error_type}`",
        f"**Timestamp:** {datetime.utcnow().isoformat()}Z",
        "",
        "### Error Message",
        f"```\n{error_message}\n```",
    ]
    
    if context:
        body_parts.extend([
            "",
            "### Context",
            context
        ])
    
    if additional_info:
        body_parts.extend([
            "",
            "### Additional Information",
            f"```json\n{json.dumps(additional_info, indent=2, default=str)}\n```"
        ])
    
    body_parts.extend([
        "",
        "### Traceback",
        f"```python\n{traceback_text}\n```",
        "",
        "---",
        "*This issue was automatically created by the error handling system.*"
    ])
    
    body = "\n".join(body_parts)
    
    # Create the issue via GitHub API
    url = f"{GITHUB_API_URL}/repos/{GITHUB_REPO}/issues"
    headers = {
        "Authorization": f"token {GITHUB_TOKEN}",
        "Accept": "application/vnd.github.v3+json",
        "Content-Type": "application/json"
    }
    
    payload = {
        "title": title,
        "body": body,
        "labels": ["bug", "auto-generated"],
        "assignees": [GITHUB_ISSUE_ASSIGNEE] if GITHUB_ISSUE_ASSIGNEE else []
    }
    
    log_debug(f"Creating GitHub issue for error {error_id}", url=url, title=title)
    
    try:
        response = requests.post(url, headers=headers, json=payload, timeout=10)
        response.raise_for_status()
        issue_data = response.json()
        issue_url = issue_data.get('html_url', 'N/A')
        logging.info(f"GitHub issue created for error {error_id}: {issue_url}")
        log_debug(f"Issue creation response", issue_number=issue_data.get('number'), url=issue_url)
    except requests.exceptions.RequestException as req_error:
        logging.warning(f"Failed to create GitHub issue: {req_error}")
        log_debug(f"GitHub API error details", error=str(req_error), status_code=getattr(req_error.response, 'status_code', None))





def log_function_entry(func_name: str, **kwargs):
    """
    Log entry into a function with parameters (debug only).
    
    Args:
        func_name: Name of the function
        **kwargs: Function parameters to log
    """
    if DEBUG_MODE:
        params = f" with params: {json.dumps(kwargs, default=str)}" if kwargs else ""
        logging.debug(f"ENTER {func_name}{params}")


def log_function_exit(func_name: str, result: Any = None):
    """
    Log exit from a function with result (debug only).
    
    Args:
        func_name: Name of the function
        result: Result to log (optional)
    """
    if DEBUG_MODE:
        result_str = f" -> {result}" if result is not None else ""
        logging.debug(f"EXIT {func_name}{result_str}")


def safe_execute(func, *args, context: str = "", create_issue: bool = True, **kwargs):
    """
    Execute a function with error handling and logging.
    
    Args:
        func: Function to execute
        *args: Positional arguments for the function
        context: Context description for error logging
        create_issue: Whether to create GitHub issue on error
        **kwargs: Keyword arguments for the function
        
    Returns:
        Result of the function, or None if an error occurred
    """
    func_name = getattr(func, '__name__', str(func))
    log_function_entry(func_name, args=args, kwargs=kwargs)
    
    try:
        result = func(*args, **kwargs)
        log_function_exit(func_name, result=result)
        return result
    except Exception as e:
        log_error_with_context(
            e,
            context=context or f"Executing {func_name}",
            additional_info={"args": str(args), "kwargs": str(kwargs)},
            create_github_issue=create_issue
        )
        return None
