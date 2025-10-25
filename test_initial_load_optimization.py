"""
Test for initial page load optimization.
Verifies that the DOMContentLoaded event handler correctly parallelizes
API calls to reduce initial file list display time.
"""
import os
import sys
import tempfile
import shutil
import time
import re

# Add src directory to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

import pytest
from flask import Flask
from flask.testing import FlaskClient
import unified_store
import marker_store


def setup_test_env():
    """Setup test environment with temporary directories"""
    test_dir = tempfile.mkdtemp(prefix='test_load_opt_')
    watched_dir = os.path.join(test_dir, 'watched')
    config_dir = os.path.join(test_dir, 'config')
    os.makedirs(watched_dir, exist_ok=True)
    os.makedirs(config_dir, exist_ok=True)
    
    # Set environment variables
    os.environ['WATCHED_DIR'] = watched_dir
    os.environ['CONFIG_DIR'] = config_dir
    
    # Create some test files
    for i in range(10):
        test_file = os.path.join(watched_dir, f'test_{i}.cbz')
        with open(test_file, 'wb') as f:
            f.write(b'test data')
    
    return test_dir, watched_dir, config_dir


def cleanup_test_env(test_dir):
    """Cleanup test environment"""
    if os.path.exists(test_dir):
        shutil.rmtree(test_dir)


def test_parallel_initialization_in_javascript():
    """
    Test that the JavaScript DOMContentLoaded handler uses parallel operations.
    This is a static analysis test that checks the code structure.
    """
    js_path = os.path.join(os.path.dirname(__file__), 'static', 'js', 'main.js')
    
    with open(js_path, 'r') as f:
        js_content = f.read()
    
    # Find the DOMContentLoaded handler
    dom_loaded_match = re.search(
        r"document\.addEventListener\('DOMContentLoaded',\s*async\s*function\(\)\s*\{(.*?)\n\s*\}\);",
        js_content,
        re.DOTALL
    )
    
    assert dom_loaded_match, "Could not find DOMContentLoaded event handler"
    
    handler_body = dom_loaded_match.group(1)
    
    # Check that loadFiles() is called without await
    # This ensures files start loading immediately
    assert 'loadFiles()' in handler_body or 'loadFiles(1)' in handler_body, \
        "loadFiles() should be called in DOMContentLoaded handler"
    
    # The optimization: check that we don't have sequential await statements
    # before loadFiles() that would block it
    
    # Split by first loadFiles call (either with or without argument)
    if 'loadFiles()' in handler_body:
        before_load_files = handler_body.split('loadFiles()')[0]
    else:
        before_load_files = handler_body.split('loadFiles(')[0]
    
    # Count await statements before loadFiles
    await_count_before = before_load_files.count('await')
    
    # After optimization, there should be no await statements before loadFiles()
    # because we parallelize everything
    assert await_count_before == 0, \
        f"Found {await_count_before} await statements before loadFiles(). " \
        "This blocks file loading. Use parallel promises instead."
    
    # Check that getPreferences is called (but not awaited before loadFiles)
    assert 'getPreferences()' in handler_body, \
        "getPreferences() should still be called"
    
    # Check that checkAndResumeActiveJob is called (but not awaited before loadFiles)
    assert 'checkAndResumeActiveJob()' in handler_body, \
        "checkAndResumeActiveJob() should still be called"
    
    print("✅ JavaScript initialization is properly parallelized")
    print("✅ loadFiles() is called without blocking on preferences or job check")


def test_api_endpoints_available():
    """
    Test that the API endpoints needed for parallel initialization are available.
    """
    test_dir = None
    try:
        test_dir, watched_dir, config_dir = setup_test_env()
        
        # Import web app after setting env vars
        from web_app import app
        
        client = app.test_client()
        
        # Test preferences endpoint
        response = client.get('/api/preferences')
        assert response.status_code == 200, "Preferences endpoint should return 200"
        
        # Test active job endpoint
        response = client.get('/api/active-job')
        assert response.status_code == 200, "Active job endpoint should return 200"
        
        # Test files endpoint
        response = client.get('/api/files')
        assert response.status_code == 200, "Files endpoint should return 200"
        
        print("✅ All required API endpoints are available")
        
    finally:
        if test_dir:
            cleanup_test_env(test_dir)


def test_preferences_applied_asynchronously():
    """
    Verify that preferences are applied correctly even when loaded asynchronously.
    This tests the .then() handler that applies preferences after they're fetched.
    """
    js_path = os.path.join(os.path.dirname(__file__), 'static', 'js', 'main.js')
    
    with open(js_path, 'r') as f:
        js_content = f.read()
    
    # Check that there's a .then() handler for preferences
    assert 'prefsPromise.then' in js_content or 'prefsPromise.then(' in js_content, \
        "Should have a .then() handler to apply preferences asynchronously"
    
    # Check that perPage is still being set (flexible whitespace matching)
    import re
    assert re.search(r'perPage\s*=\s*prefs\.perPage', js_content), \
        "Should still set perPage from preferences"
    
    # Check that filterMode is still being set (flexible whitespace matching)
    assert re.search(r'filterMode\s*=\s*prefs\.filterMode', js_content), \
        "Should still set filterMode from preferences"
    
    print("✅ Preferences are applied asynchronously without blocking file load")


def test_no_regression_in_functionality():
    """
    Ensure that all critical initialization steps are still present.
    """
    js_path = os.path.join(os.path.dirname(__file__), 'static', 'js', 'main.js')
    
    with open(js_path, 'r') as f:
        js_content = f.read()
    
    # Find DOMContentLoaded
    assert "addEventListener('DOMContentLoaded'" in js_content, \
        "DOMContentLoaded listener should exist"
    
    # Check all required initialization functions are called
    required_calls = [
        'initTheme()',
        'loadVersion()',
        'initEventSource()',
        'updateWatcherStatus()',
        'loadFiles()',
        'getPreferences()',
        'checkAndResumeActiveJob()'
    ]
    
    for call in required_calls:
        assert call in js_content, f"Required initialization call '{call}' is missing"
    
    print("✅ All critical initialization functions are still present")


if __name__ == '__main__':
    print("\n=== Testing Initial Load Optimization ===\n")
    
    # Run static analysis tests
    test_parallel_initialization_in_javascript()
    print()
    
    test_preferences_applied_asynchronously()
    print()
    
    test_no_regression_in_functionality()
    print()
    
    # Run API tests (optional - requires ComicTagger dependencies)
    try:
        test_api_endpoints_available()
        print()
    except (ImportError, ModuleNotFoundError) as e:
        print(f"⚠️  Skipping API endpoint test (missing dependencies: {e})")
        print()
    
    print("\n=== All Tests Passed ✅ ===")
