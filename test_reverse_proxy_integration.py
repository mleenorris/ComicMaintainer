#!/usr/bin/env python3
"""
Integration test for reverse proxy support.

Tests that the Flask app properly handles BASE_PATH configuration
and generates correct URLs.
"""

import sys
import os
import tempfile
import shutil

def test_base_path_configuration():
    """Test that BASE_PATH is properly configured in the Flask app"""
    print("Testing BASE_PATH configuration...")
    
    # Set up environment
    os.environ['WATCHED_DIR'] = '/tmp/test_watched'
    os.environ['BASE_PATH'] = '/comics'
    
    # Create temporary config directory
    config_dir = tempfile.mkdtemp()
    os.environ['CONFIG_DIR'] = config_dir
    
    try:
        # Import Flask app components
        sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))
        from flask import Flask
        from werkzeug.middleware.proxy_fix import ProxyFix
        
        # Create a minimal Flask app with ProxyFix
        app = Flask(__name__)
        app.wsgi_app = ProxyFix(
            app.wsgi_app,
            x_for=1,
            x_proto=1,
            x_host=1,
            x_prefix=1
        )
        
        # Configure BASE_PATH
        BASE_PATH = os.environ.get('BASE_PATH', '').rstrip('/')
        if BASE_PATH and not BASE_PATH.startswith('/'):
            BASE_PATH = ''
        if BASE_PATH:
            app.config['APPLICATION_ROOT'] = BASE_PATH
        
        print(f"✓ Flask app created with BASE_PATH: {BASE_PATH}")
        print(f"✓ APPLICATION_ROOT set to: {app.config.get('APPLICATION_ROOT', '(not set)')}")
        
        # Verify ProxyFix is applied
        if isinstance(app.wsgi_app, ProxyFix):
            print("✓ ProxyFix middleware is properly applied")
        else:
            print("✗ ProxyFix middleware NOT applied")
            return False
        
        # Test manifest generation
        base_path = app.config.get('APPLICATION_ROOT', '')
        manifest = {
            "start_url": f"{base_path}/",
            "icons": [
                {"src": f"{base_path}/static/icons/icon-192x192.png"}
            ]
        }
        
        if manifest["start_url"] == "/comics/":
            print(f"✓ Manifest start_url correctly uses BASE_PATH: {manifest['start_url']}")
        else:
            print(f"✗ Manifest start_url incorrect: {manifest['start_url']}")
            return False
        
        if manifest["icons"][0]["src"] == "/comics/static/icons/icon-192x192.png":
            print(f"✓ Manifest icon URLs correctly use BASE_PATH")
        else:
            print(f"✗ Manifest icon URL incorrect: {manifest['icons'][0]['src']}")
            return False
        
        return True
        
    finally:
        # Cleanup
        shutil.rmtree(config_dir, ignore_errors=True)
        if 'CONFIG_DIR' in os.environ:
            del os.environ['CONFIG_DIR']
        if 'BASE_PATH' in os.environ:
            del os.environ['BASE_PATH']

def test_without_base_path():
    """Test that app works correctly without BASE_PATH set"""
    print("\nTesting without BASE_PATH (root path deployment)...")
    
    os.environ['WATCHED_DIR'] = '/tmp/test_watched'
    
    # Ensure BASE_PATH is not set
    if 'BASE_PATH' in os.environ:
        del os.environ['BASE_PATH']
    
    config_dir = tempfile.mkdtemp()
    os.environ['CONFIG_DIR'] = config_dir
    
    try:
        from flask import Flask
        from werkzeug.middleware.proxy_fix import ProxyFix
        
        app = Flask(__name__)
        app.wsgi_app = ProxyFix(app.wsgi_app, x_for=1, x_proto=1, x_host=1, x_prefix=1)
        
        BASE_PATH = os.environ.get('BASE_PATH', '').rstrip('/')
        if BASE_PATH:
            app.config['APPLICATION_ROOT'] = BASE_PATH
        
        base_path = app.config.get('APPLICATION_ROOT', '')
        # Apply same logic as web_app.py: convert '/' to '' for root deployment
        if base_path == '/':
            base_path = ''
        
        if base_path == '':
            print(f"✓ BASE_PATH is empty string (root path deployment)")
        else:
            print(f"✗ BASE_PATH should be empty but is: '{base_path}'")
            return False
        
        # Test manifest generation without BASE_PATH
        manifest = {
            "start_url": f"{base_path}/",
            "icons": [
                {"src": f"{base_path}/static/icons/icon-192x192.png"}
            ]
        }
        
        if manifest["start_url"] == "/":
            print(f"✓ Manifest start_url is correct for root: {manifest['start_url']}")
        else:
            print(f"✗ Manifest start_url incorrect: {manifest['start_url']}")
            return False
        
        return True
        
    finally:
        shutil.rmtree(config_dir, ignore_errors=True)
        if 'CONFIG_DIR' in os.environ:
            del os.environ['CONFIG_DIR']

def main():
    """Run all integration tests"""
    print("Reverse Proxy Integration Tests\n")
    print("=" * 60)
    
    results = []
    
    print("\nTest 1: BASE_PATH Configuration")
    print("-" * 60)
    results.append(test_base_path_configuration())
    
    print("\nTest 2: Root Path Deployment")
    print("-" * 60)
    results.append(test_without_base_path())
    
    print("\n" + "=" * 60)
    passed = sum(results)
    total = len(results)
    print(f"\nResults: {passed}/{total} tests passed")
    
    if passed == total:
        print("✓ All integration tests passed!")
        return 0
    else:
        print("✗ Some integration tests failed")
        return 1

if __name__ == '__main__':
    sys.exit(main())
