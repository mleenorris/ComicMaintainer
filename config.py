import json
import os
import logging

CONFIG_FILE = 'config.json'
DEFAULT_FILENAME_FORMAT = '{series} - Chapter {issue}.cbz'
DEFAULT_WATCHER_ENABLED = True

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
        'watcher_enabled': DEFAULT_WATCHER_ENABLED
    }

def save_config(config):
    """Save configuration to file"""
    try:
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
