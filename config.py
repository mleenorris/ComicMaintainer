import json
import os
import logging

# Store config in CACHE_DIR for persistence
CACHE_DIR = os.environ.get('CACHE_DIR', '/app/cache')
CONFIG_FILE = os.path.join(CACHE_DIR, 'config.json')
DEFAULT_FILENAME_FORMAT = '{series} - Chapter {issue}.cbz'
DEFAULT_WATCHER_ENABLED = True
DEFAULT_LOG_MAX_BYTES = 5 * 1024 * 1024  # 5MB default

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
        # Ensure cache directory exists
        os.makedirs(CACHE_DIR, exist_ok=True)
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
