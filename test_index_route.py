#!/usr/bin/env python3
"""
Test the index route to ensure the main page loads correctly.
This is a minimal test that mocks comictagger dependencies.
"""
import os
import sys
import tempfile
from unittest.mock import MagicMock, patch

# Mock comicapi before importing web_app
sys.modules['comicapi'] = MagicMock()
sys.modules['comicapi.comicarchive'] = MagicMock()
sys.modules['comicapi.genericmetadata'] = MagicMock()
sys.modules['comicapi._url'] = MagicMock()

# Add src directory to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

# Set up environment before importing web_app
test_watched_dir = tempfile.mkdtemp()
test_config_dir = tempfile.mkdtemp()
os.environ['WATCHED_DIR'] = test_watched_dir
os.environ['CONFIG_DIR'] = test_config_dir  # Override default /Config
os.environ.setdefault('WEB_PORT', '5000')

# Import Flask test client
from web_app import app

def test_index_route():
    """Test that the index route loads successfully"""
    with app.test_client() as client:
        response = client.get('/')
        
        print(f"Status Code: {response.status_code}")
        print(f"Content-Type: {response.headers.get('Content-Type')}")
        print(f"Content Length: {len(response.data)} bytes")
        
        # Check response
        assert response.status_code == 200, f"Expected 200, got {response.status_code}"
        assert b'Comic Maintainer' in response.data, "Expected 'Comic Maintainer' in response"
        assert b'<!DOCTYPE html>' in response.data, "Expected HTML document"
        
        print("✓ Index route loads correctly")
        print("✓ Page contains 'Comic Maintainer' text")
        print("✓ Response is valid HTML")
        return True

def test_manifest_route():
    """Test that the manifest.json route works"""
    with app.test_client() as client:
        response = client.get('/manifest.json')
        
        print(f"\nManifest Status Code: {response.status_code}")
        
        assert response.status_code == 200, f"Expected 200, got {response.status_code}"
        assert response.is_json, "Expected JSON response"
        
        data = response.get_json()
        assert 'name' in data, "Expected 'name' field in manifest"
        assert data['name'] == 'Comic Maintainer', f"Expected 'Comic Maintainer', got {data['name']}"
        
        print("✓ Manifest route loads correctly")
        print(f"✓ Manifest name: {data['name']}")
        return True

def test_api_health():
    """Test that the health check endpoint works"""
    with app.test_client() as client:
        response = client.get('/health')
        
        print(f"\nHealth Check Status Code: {response.status_code}")
        
        # Health check might return 503 if watched dir doesn't exist, but should respond
        assert response.status_code in [200, 503], f"Expected 200 or 503, got {response.status_code}"
        assert response.is_json, "Expected JSON response"
        
        data = response.get_json()
        assert 'status' in data, "Expected 'status' field in health check"
        assert 'version' in data, "Expected 'version' field in health check"
        
        print(f"✓ Health check endpoint responds")
        print(f"✓ Status: {data['status']}")
        print(f"✓ Version: {data['version']}")
        return True

if __name__ == '__main__':
    print("=" * 60)
    print("Testing Main Page Accessibility")
    print("=" * 60)
    
    try:
        test_index_route()
        test_manifest_route()
        test_api_health()
        
        print("\n" + "=" * 60)
        print("✅ ALL TESTS PASSED - Main page is working!")
        print("=" * 60)
        sys.exit(0)
    except AssertionError as e:
        print(f"\n❌ TEST FAILED: {e}")
        sys.exit(1)
    except Exception as e:
        print(f"\n❌ ERROR: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)
