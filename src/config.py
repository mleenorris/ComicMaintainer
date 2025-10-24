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
DEFAULT_SSL_CERTFILE = ''  # Default SSL certificate file path
DEFAULT_SSL_KEYFILE = ''  # Default SSL key file path
DEFAULT_SSL_CA_CERTS = ''  # Default SSL CA certificates file path
DEFAULT_BASE_PATH = ''  # Default base path for subdirectory deployments
DEFAULT_PROXY_X_FOR = 1  # Default number of proxies to trust for X-Forwarded-For
DEFAULT_PROXY_X_PROTO = 1  # Default number of proxies to trust for X-Forwarded-Proto
DEFAULT_PROXY_X_HOST = 1  # Default number of proxies to trust for X-Forwarded-Host
DEFAULT_PROXY_X_PREFIX = 1  # Default number of proxies to trust for X-Forwarded-Prefix

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

def get_ssl_certfile():
    """Get the SSL certificate file path"""
    # Check environment variable first
    env_value = os.environ.get('SSL_CERTFILE')
    if env_value:
        return env_value
    
    # Fall back to config file
    config = get_config()
    return config.get('ssl_certfile', DEFAULT_SSL_CERTFILE)

def set_ssl_certfile(certfile):
    """Set the SSL certificate file path"""
    config = get_config()
    config['ssl_certfile'] = certfile
    return save_config(config)

def get_ssl_keyfile():
    """Get the SSL key file path"""
    # Check environment variable first
    env_value = os.environ.get('SSL_KEYFILE')
    if env_value:
        return env_value
    
    # Fall back to config file
    config = get_config()
    return config.get('ssl_keyfile', DEFAULT_SSL_KEYFILE)

def set_ssl_keyfile(keyfile):
    """Set the SSL key file path"""
    config = get_config()
    config['ssl_keyfile'] = keyfile
    return save_config(config)

def get_ssl_ca_certs():
    """Get the SSL CA certificates file path"""
    # Check environment variable first
    env_value = os.environ.get('SSL_CA_CERTS')
    if env_value:
        return env_value
    
    # Fall back to config file
    config = get_config()
    return config.get('ssl_ca_certs', DEFAULT_SSL_CA_CERTS)

def set_ssl_ca_certs(ca_certs):
    """Set the SSL CA certificates file path"""
    config = get_config()
    config['ssl_ca_certs'] = ca_certs
    return save_config(config)

def get_base_path():
    """Get the base path for subdirectory deployments"""
    # Check environment variable first
    env_value = os.environ.get('BASE_PATH')
    if env_value:
        return env_value.rstrip('/')
    
    # Fall back to config file
    config = get_config()
    base_path = config.get('base_path', DEFAULT_BASE_PATH)
    return base_path.rstrip('/') if base_path else ''

def set_base_path(base_path):
    """Set the base path for subdirectory deployments"""
    config = get_config()
    config['base_path'] = base_path.rstrip('/') if base_path else ''
    return save_config(config)

def get_proxy_x_for():
    """Get the number of proxies to trust for X-Forwarded-For"""
    # Check environment variable first
    env_value = os.environ.get('PROXY_X_FOR')
    if env_value:
        try:
            return int(env_value)
        except ValueError:
            logging.warning(f"Invalid PROXY_X_FOR environment variable: {env_value}")
    
    # Fall back to config file
    config = get_config()
    return config.get('proxy_x_for', DEFAULT_PROXY_X_FOR)

def set_proxy_x_for(x_for):
    """Set the number of proxies to trust for X-Forwarded-For"""
    try:
        x_for = int(x_for)
        if x_for < 0:
            return False
        config = get_config()
        config['proxy_x_for'] = x_for
        return save_config(config)
    except (ValueError, TypeError):
        return False

def get_proxy_x_proto():
    """Get the number of proxies to trust for X-Forwarded-Proto"""
    # Check environment variable first
    env_value = os.environ.get('PROXY_X_PROTO')
    if env_value:
        try:
            return int(env_value)
        except ValueError:
            logging.warning(f"Invalid PROXY_X_PROTO environment variable: {env_value}")
    
    # Fall back to config file
    config = get_config()
    return config.get('proxy_x_proto', DEFAULT_PROXY_X_PROTO)

def set_proxy_x_proto(x_proto):
    """Set the number of proxies to trust for X-Forwarded-Proto"""
    try:
        x_proto = int(x_proto)
        if x_proto < 0:
            return False
        config = get_config()
        config['proxy_x_proto'] = x_proto
        return save_config(config)
    except (ValueError, TypeError):
        return False

def get_proxy_x_host():
    """Get the number of proxies to trust for X-Forwarded-Host"""
    # Check environment variable first
    env_value = os.environ.get('PROXY_X_HOST')
    if env_value:
        try:
            return int(env_value)
        except ValueError:
            logging.warning(f"Invalid PROXY_X_HOST environment variable: {env_value}")
    
    # Fall back to config file
    config = get_config()
    return config.get('proxy_x_host', DEFAULT_PROXY_X_HOST)

def set_proxy_x_host(x_host):
    """Set the number of proxies to trust for X-Forwarded-Host"""
    try:
        x_host = int(x_host)
        if x_host < 0:
            return False
        config = get_config()
        config['proxy_x_host'] = x_host
        return save_config(config)
    except (ValueError, TypeError):
        return False

def get_proxy_x_prefix():
    """Get the number of proxies to trust for X-Forwarded-Prefix"""
    # Check environment variable first
    env_value = os.environ.get('PROXY_X_PREFIX')
    if env_value:
        try:
            return int(env_value)
        except ValueError:
            logging.warning(f"Invalid PROXY_X_PREFIX environment variable: {env_value}")
    
    # Fall back to config file
    config = get_config()
    return config.get('proxy_x_prefix', DEFAULT_PROXY_X_PREFIX)

def set_proxy_x_prefix(x_prefix):
    """Set the number of proxies to trust for X-Forwarded-Prefix"""
    try:
        x_prefix = int(x_prefix)
        if x_prefix < 0:
            return False
        config = get_config()
        config['proxy_x_prefix'] = x_prefix
        return save_config(config)
    except (ValueError, TypeError):
        return False
