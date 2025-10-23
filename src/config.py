import json
import os
import logging

# Store config in /Config for persistence
CONFIG_DIR = '/Config'
CONFIG_FILE = os.path.join(CONFIG_DIR, 'config.json')
DEFAULT_FILENAME_FORMAT = '{series} - Chapter {issue}'
DEFAULT_WATCHER_ENABLED = True
DEFAULT_LOG_MAX_BYTES = 5 * 1024 * 1024  # 5MB default
DEFAULT_MAX_WORKERS = 4  # Default number of concurrent workers
DEFAULT_ISSUE_NUMBER_PADDING = 4  # Default padding for issue numbers
DEFAULT_DB_CACHE_SIZE_MB = 64  # Default SQLite cache size in MB
DEFAULT_GITHUB_TOKEN = ''  # Default GitHub token
DEFAULT_GITHUB_REPOSITORY = 'mleenorris/ComicMaintainer'  # Default GitHub repository
DEFAULT_GITHUB_ISSUE_ASSIGNEE = 'copilot'  # Default GitHub issue assignee

def get_config():
    """Get the current configuration"""
    if os.path.exists(CONFIG_FILE):
        try:
            with open(CONFIG_FILE, 'r') as f:
                return json.load(f)
        except Exception as e:
            logging.error(f"Error reading config file: {e}")
    
    # Return default config
    return {
        'filename_format': DEFAULT_FILENAME_FORMAT,
        'watcher_enabled': DEFAULT_WATCHER_ENABLED,
        'log_max_bytes': DEFAULT_LOG_MAX_BYTES
    }

def save_config(config):
    """Save configuration to file"""
    try:
        # Ensure config directory exists
        os.makedirs(CONFIG_DIR, exist_ok=True)
        with open(CONFIG_FILE, 'w') as f:
            json.dump(config, f, indent=2)
        return True
    except Exception as e:
        logging.error(f"Error saving config file: {e}")
        return False

def get_filename_format():
    """Get the filename format setting"""
    config = get_config()
    return config.get('filename_format', DEFAULT_FILENAME_FORMAT)

def set_filename_format(format_string):
    """Set the filename format setting"""
    config = get_config()
    config['filename_format'] = format_string
    return save_config(config)

def get_watcher_enabled():
    """Get the watcher enabled setting"""
    config = get_config()
    return config.get('watcher_enabled', DEFAULT_WATCHER_ENABLED)

def set_watcher_enabled(enabled):
    """Set the watcher enabled setting"""
    config = get_config()
    config['watcher_enabled'] = enabled
    return save_config(config)

def get_log_max_bytes():
    """Get the log max bytes setting"""
    # Check environment variable first
    env_value = os.environ.get('LOG_MAX_BYTES')
    if env_value:
        try:
            return int(env_value)
        except ValueError:
            logging.warning(f"Invalid LOG_MAX_BYTES environment variable: {env_value}")
    
    # Fall back to config file
    config = get_config()
    return config.get('log_max_bytes', DEFAULT_LOG_MAX_BYTES)

def set_log_max_bytes(max_bytes):
    """Set the log max bytes setting"""
    config = get_config()
    config['log_max_bytes'] = max_bytes
    return save_config(config)

def get_max_workers():
    """Get the max workers setting"""
    # Check environment variable first
    env_value = os.environ.get('MAX_WORKERS')
    if env_value:
        try:
            return int(env_value)
        except ValueError:
            logging.warning(f"Invalid MAX_WORKERS environment variable: {env_value}")
    
    # Fall back to default
    return DEFAULT_MAX_WORKERS

def get_issue_number_padding():
    """Get the issue number padding setting"""
    config = get_config()
    padding = config.get('issue_number_padding', DEFAULT_ISSUE_NUMBER_PADDING)
    try:
        padding = int(padding)
        if padding < 0:
            return DEFAULT_ISSUE_NUMBER_PADDING
        return padding
    except (ValueError, TypeError):
        return DEFAULT_ISSUE_NUMBER_PADDING

def set_issue_number_padding(padding):
    """Set the issue number padding setting"""
    try:
        padding = int(padding)
        if padding < 0:
            return False
        config = get_config()
        config['issue_number_padding'] = padding
        return save_config(config)
    except (ValueError, TypeError):
        return False

def get_db_cache_size_mb():
    """Get the database cache size setting in MB"""
    # Check environment variable first
    env_value = os.environ.get('DB_CACHE_SIZE_MB')
    if env_value:
        try:
            return int(env_value)
        except ValueError:
            logging.warning(f"Invalid DB_CACHE_SIZE_MB environment variable: {env_value}")
    
    # Fall back to default
    return DEFAULT_DB_CACHE_SIZE_MB
def get_github_token():
    """Get the GitHub token setting"""
    # Check environment variable first
    env_value = os.environ.get('GITHUB_TOKEN')
    if env_value:
        return env_value
    
    # Fall back to config file
    config = get_config()
    return config.get('github_token', DEFAULT_GITHUB_TOKEN)

def set_github_token(token):
    """Set the GitHub token setting"""
    config = get_config()
    config['github_token'] = token
    return save_config(config)

def get_github_repository():
    """Get the GitHub repository setting"""
    # Check environment variable first
    env_value = os.environ.get('GITHUB_REPOSITORY')
    if env_value:
        return env_value
    
    # Fall back to config file
    config = get_config()
    return config.get('github_repository', DEFAULT_GITHUB_REPOSITORY)

def set_github_repository(repository):
    """Set the GitHub repository setting"""
    config = get_config()
    config['github_repository'] = repository
    return save_config(config)

def get_github_issue_assignee():
    """Get the GitHub issue assignee setting"""
    # Check environment variable first
    env_value = os.environ.get('GITHUB_ISSUE_ASSIGNEE')
    if env_value:
        return env_value
    
    # Fall back to config file
    config = get_config()
    return config.get('github_issue_assignee', DEFAULT_GITHUB_ISSUE_ASSIGNEE)

def set_github_issue_assignee(assignee):
    """Set the GitHub issue assignee setting"""
    config = get_config()
    config['github_issue_assignee'] = assignee
    return save_config(config)
