import json
import os
import logging

CONFIG_FILE = 'config.json'
DEFAULT_FILENAME_FORMAT = '{series} - Chapter {issue}.cbz'

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
        'filename_format': DEFAULT_FILENAME_FORMAT
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
