#!/usr/bin/env python3
"""
Test that static files (CSS, JS) are served correctly with proper cache headers.
"""
import os
import sys
import tempfile

# Add src directory to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

# Set up environment before importing web_app
os.environ['WATCHED_DIR'] = tempfile.mkdtemp()
os.environ.setdefault('WEB_PORT', '5000')

# Import Flask test client
from web_app import app

# Cache duration constants (in seconds)
CACHE_DURATION_ONE_YEAR = 31536000  # 365 days
CACHE_DURATION_ONE_DAY = 86400      # 1 day

# Size thresholds for HTML file (in bytes)
MAX_HTML_SIZE = 60 * 1024  # 60KB

def test_index_page_loads():
    """Test that the index page loads successfully"""
    with app.test_client() as client:
        response = client.get('/')
        assert response.status_code == 200
        assert b'Comic Maintainer' in response.data
        # Check that CSS link is present
        assert b'/static/css/main.css' in response.data
        # Check that JS script is present
        assert b'/static/js/main.js' in response.data
        print("✓ Index page loads correctly with external CSS and JS references")

def test_css_file_served():
    """Test that CSS file is served with cache headers"""
    with app.test_client() as client:
        response = client.get('/static/css/main.css')
        assert response.status_code == 200
        assert 'Cache-Control' in response.headers
        # CSS should have long cache
        assert f'max-age={CACHE_DURATION_ONE_YEAR}' in response.headers['Cache-Control']
        assert 'immutable' in response.headers['Cache-Control']
        # Check that CSS content is present
        assert b':root' in response.data or b'body' in response.data
        print("✓ CSS file is served with proper cache headers")

def test_js_file_served():
    """Test that JavaScript file is served with cache headers"""
    with app.test_client() as client:
        response = client.get('/static/js/main.js')
        assert response.status_code == 200
        assert 'Cache-Control' in response.headers
        # JS should have long cache
        assert f'max-age={CACHE_DURATION_ONE_YEAR}' in response.headers['Cache-Control']
        assert 'immutable' in response.headers['Cache-Control']
        # Check that JS content is present
        assert b'function' in response.data or b'const' in response.data
        print("✓ JS file is served with proper cache headers")

def test_icon_file_cache():
    """Test that icon files have shorter cache duration"""
    with app.test_client() as client:
        # Test with manifest.json as an example
        response = client.get('/static/manifest.json')
        if response.status_code == 200:
            assert 'Cache-Control' in response.headers
            # Icons should have shorter cache (1 day)
            assert f'max-age={CACHE_DURATION_ONE_DAY}' in response.headers['Cache-Control']
            print("✓ Icon/manifest files have appropriate cache headers")
        else:
            print("⚠ Skipping icon cache test (manifest.json not found)")

def test_html_size_reduction():
    """Test that HTML page is significantly smaller than before"""
    with app.test_client() as client:
        response = client.get('/')
        html_size = len(response.data)
        # Original was 217KB, new should be around 44KB
        assert html_size < MAX_HTML_SIZE, f"HTML size {html_size} bytes is too large (should be < {MAX_HTML_SIZE / 1024}KB)"
        print(f"✓ HTML size reduced to {html_size / 1024:.1f}KB (was ~217KB)")

if __name__ == '__main__':
    print("Testing static file serving and cache headers...")
    test_index_page_loads()
    test_css_file_served()
    test_js_file_served()
    test_icon_file_cache()
    test_html_size_reduction()
    print("\n✓ All tests passed!")
