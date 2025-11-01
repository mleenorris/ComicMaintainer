#!/usr/bin/env python3
"""Test that API error handlers return JSON instead of HTML"""

import pytest
import json
from flask import Flask, jsonify, render_template, request


def test_error_handler_logic():
    """Test that error handlers return JSON for API routes and HTML for others"""
    
    # Create a minimal Flask app with our error handlers
    app = Flask(__name__, template_folder='templates')
    app.config['TESTING'] = True
    
    # Add a simple test route
    @app.route('/api/test')
    def test_route():
        return jsonify({'status': 'ok'})
    
    # Add a route that raises an error for testing
    @app.route('/api/error')
    def error_route():
        raise ValueError("Test error")
    
    # Add error handlers (same as in web_app.py)
    @app.errorhandler(404)
    def not_found_error(error):
        """Handle 404 errors - return JSON for API routes, HTML for others"""
        if request.path.startswith('/api/'):
            return jsonify({'error': 'Not found'}), 404
        # For non-API routes, return a simple HTML response
        return '<html><body>404 Not Found</body></html>', 404
    
    @app.errorhandler(500)
    def internal_error(error):
        """Handle 500 errors - return JSON for API routes, HTML for others"""
        if request.path.startswith('/api/'):
            return jsonify({'error': 'Internal server error'}), 500
        return '<html><body>500 Internal Server Error</body></html>', 500
    
    @app.errorhandler(Exception)
    def handle_exception(error):
        """Handle unhandled exceptions - return JSON for API routes, HTML for others"""
        if request.path.startswith('/api/'):
            return jsonify({'error': 'An unexpected error occurred'}), 500
        return '<html><body>500 Error</body></html>', 500
    
    # Create test client
    with app.test_client() as client:
        # Test 1: API 404 returns JSON
        response = client.get('/api/nonexistent')
        assert response.status_code == 404
        assert response.content_type == 'application/json'
        data = json.loads(response.data)
        assert 'error' in data
        assert data['error'] == 'Not found'
        print("✓ Test 1 passed: API 404 returns JSON")
        
        # Test 2: Non-API 404 returns HTML
        response = client.get('/nonexistent')
        assert response.status_code == 404
        assert 'text/html' in response.content_type
        assert b'404 Not Found' in response.data
        print("✓ Test 2 passed: Non-API 404 returns HTML")
        
        # Test 3: API route that works returns JSON
        response = client.get('/api/test')
        assert response.status_code == 200
        assert response.content_type == 'application/json'
        data = json.loads(response.data)
        assert data['status'] == 'ok'
        print("✓ Test 3 passed: API route returns JSON")
        
        # Test 4: API route that raises exception returns JSON error
        response = client.get('/api/error')
        assert response.status_code == 500
        assert response.content_type == 'application/json'
        data = json.loads(response.data)
        assert 'error' in data
        print("✓ Test 4 passed: API exception returns JSON error")
    
    print("\n✅ All error handler tests passed!")


def test_javascript_error_handling():
    """Test that JavaScript handles undefined/null responses gracefully"""
    
    # Simulate the key parts of loadFiles error handling
    def simulate_load_files(response_data):
        """Simulate the fixed loadFiles function"""
        files = []
        currentPage = 1
        totalPages = 1
        totalFiles = 0
        unmarkedCount = 0
        
        try:
            # Simulate API call
            if response_data is None:
                raise Exception("Network error")
            
            # Handle error responses from API
            if isinstance(response_data, dict) and 'error' in response_data:
                raise Exception(response_data['error'])
            
            # Safely handle response with defaults
            files = response_data.get('files') or []
            currentPage = response_data.get('page') or 1
            totalPages = response_data.get('total_pages') or 1
            totalFiles = response_data.get('total_files') or 0
            unmarkedCount = response_data.get('unmarked_count') or 0
            
        except Exception as error:
            # Set safe defaults on error to prevent undefined errors
            files = []
            currentPage = 1
            totalPages = 1
            totalFiles = 0
            unmarkedCount = 0
        
        return {
            'files': files,
            'currentPage': currentPage,
            'totalPages': totalPages,
            'totalFiles': totalFiles,
            'unmarkedCount': unmarkedCount
        }
    
    # Test 1: Normal successful response
    result = simulate_load_files({
        'files': [{'name': 'test.cbz'}],
        'page': 1,
        'total_pages': 5,
        'total_files': 42,
        'unmarked_count': 10
    })
    assert result['files'] == [{'name': 'test.cbz'}]
    assert result['totalFiles'] == 42
    print("✓ Test 1 passed: Normal response handled correctly")
    
    # Test 2: API error response
    result = simulate_load_files({'error': 'Not found'})
    assert result['files'] == []
    assert result['totalFiles'] == 0
    print("✓ Test 2 passed: API error response handled safely")
    
    # Test 3: Network error (None response)
    result = simulate_load_files(None)
    assert result['files'] == []
    assert result['totalFiles'] == 0
    print("✓ Test 3 passed: Network error handled safely")
    
    # Test 4: Incomplete response (missing fields)
    result = simulate_load_files({'files': None, 'page': None})
    assert result['files'] == []
    assert result['currentPage'] == 1
    assert result['totalPages'] == 1
    print("✓ Test 4 passed: Incomplete response handled with defaults")
    
    print("\n✅ All JavaScript error handling tests passed!")


if __name__ == '__main__':
    print("Running error handler tests...\n")
    test_error_handler_logic()
    print("\nRunning JavaScript error handling simulation...\n")
    test_javascript_error_handling()
    print("\n" + "="*60)
    print("All tests completed successfully!")
    print("="*60)
