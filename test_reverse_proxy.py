#!/usr/bin/env python3
"""
Test suite for reverse proxy support in ComicMaintainer.

Tests that the application properly handles X-Forwarded-* headers
and works correctly behind a reverse proxy.
"""

import unittest
import sys
import os

# Add src directory to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

class TestReverseProxySupport(unittest.TestCase):
    """Test cases for reverse proxy functionality"""
    
    def setUp(self):
        """Set up test environment"""
        # Import here to avoid import errors if dependencies aren't installed
        try:
            from flask import Flask
            from werkzeug.middleware.proxy_fix import ProxyFix
            self.flask_available = True
        except ImportError:
            self.flask_available = False
            self.skipTest("Flask not available")
    
    def test_proxyfix_middleware_available(self):
        """Test that ProxyFix middleware is available"""
        from werkzeug.middleware.proxy_fix import ProxyFix
        self.assertIsNotNone(ProxyFix)
    
    def test_proxyfix_configuration(self):
        """Test that ProxyFix can be configured correctly"""
        from flask import Flask
        from werkzeug.middleware.proxy_fix import ProxyFix
        
        app = Flask(__name__)
        app.wsgi_app = ProxyFix(
            app.wsgi_app,
            x_for=1,
            x_proto=1,
            x_host=1,
            x_prefix=1
        )
        
        self.assertIsInstance(app.wsgi_app, ProxyFix)
    
    def test_proxyfix_headers_handling(self):
        """Test that ProxyFix properly handles X-Forwarded-* headers"""
        from flask import Flask, request
        from werkzeug.middleware.proxy_fix import ProxyFix
        from werkzeug.test import Client
        
        app = Flask(__name__)
        app.wsgi_app = ProxyFix(
            app.wsgi_app,
            x_for=1,
            x_proto=1,
            x_host=1,
            x_prefix=1
        )
        
        @app.route('/test')
        def test_route():
            return {
                'host': request.host,
                'scheme': request.scheme,
                'remote_addr': request.remote_addr,
                'script_root': request.script_root,
            }
        
        client = Client(app)
        
        # Test without proxy headers (should use defaults)
        response = client.get('/test')
        self.assertEqual(response.status_code, 200)
        
        # Test with proxy headers
        response = client.get(
            '/test',
            headers={
                'X-Forwarded-For': '192.168.1.100',
                'X-Forwarded-Proto': 'https',
                'X-Forwarded-Host': 'example.com',
                'X-Forwarded-Prefix': '/comics',
            }
        )
        self.assertEqual(response.status_code, 200)
        data = response.get_json()
        
        # Verify headers were processed
        self.assertEqual(data['host'], 'example.com')
        self.assertEqual(data['scheme'], 'https')
        self.assertEqual(data['remote_addr'], '192.168.1.100')
        self.assertEqual(data['script_root'], '/comics')
    
    def test_path_prefix_handling(self):
        """Test that path prefixes work correctly"""
        from flask import Flask, request, url_for
        from werkzeug.middleware.proxy_fix import ProxyFix
        from werkzeug.test import Client
        
        app = Flask(__name__)
        app.wsgi_app = ProxyFix(
            app.wsgi_app,
            x_for=1,
            x_proto=1,
            x_host=1,
            x_prefix=1
        )
        
        @app.route('/api/test')
        def test_route():
            return {'url': request.url, 'base_url': request.base_url}
        
        client = Client(app)
        
        # Test with path prefix
        response = client.get(
            '/api/test',
            headers={
                'X-Forwarded-Host': 'example.com',
                'X-Forwarded-Proto': 'https',
                'X-Forwarded-Prefix': '/comics',
            }
        )
        self.assertEqual(response.status_code, 200)
        data = response.get_json()
        
        # Verify URL includes prefix
        self.assertIn('https://example.com', data['url'])
        self.assertIn('/comics/api/test', data['url'])
    
    def test_relative_urls_in_responses(self):
        """Test that the app uses relative URLs (works with any path prefix)"""
        # This is more of a documentation test - the app already uses relative URLs
        # We verify this by checking that API routes don't include absolute URLs
        
        # Read web_app.py and verify no hardcoded absolute URLs
        web_app_path = os.path.join(os.path.dirname(__file__), 'src', 'web_app.py')
        if os.path.exists(web_app_path):
            with open(web_app_path, 'r') as f:
                content = f.read()
                
                # Check that routes are defined with relative paths
                self.assertIn("@app.route('/api/", content)
                self.assertIn("@app.route('/health'", content)
                
                # Verify no absolute URLs in responses (except for redirects)
                # Note: This is a simple check - real verification needs runtime testing
                self.assertNotIn('return "http://', content)
                self.assertNotIn('return "https://', content)


class TestWebAppProxyConfiguration(unittest.TestCase):
    """Test that web_app.py is properly configured for reverse proxy"""
    
    def test_web_app_has_proxyfix(self):
        """Test that web_app.py imports and configures ProxyFix"""
        web_app_path = os.path.join(os.path.dirname(__file__), 'src', 'web_app.py')
        
        if not os.path.exists(web_app_path):
            self.skipTest("web_app.py not found")
        
        with open(web_app_path, 'r') as f:
            content = f.read()
        
        # Verify ProxyFix is imported
        self.assertIn('from werkzeug.middleware.proxy_fix import ProxyFix', content)
        
        # Verify ProxyFix is applied to app
        self.assertIn('ProxyFix', content)
        self.assertIn('app.wsgi_app', content)
        
        # Verify all required parameters are configured
        self.assertIn('x_for=', content)
        self.assertIn('x_proto=', content)
        self.assertIn('x_host=', content)
        self.assertIn('x_prefix=', content)


def run_tests():
    """Run all tests"""
    # Create test suite
    loader = unittest.TestLoader()
    suite = unittest.TestSuite()
    
    # Add test cases
    suite.addTests(loader.loadTestsFromTestCase(TestReverseProxySupport))
    suite.addTests(loader.loadTestsFromTestCase(TestWebAppProxyConfiguration))
    
    # Run tests
    runner = unittest.TextTestRunner(verbosity=2)
    result = runner.run(suite)
    
    # Return exit code
    return 0 if result.wasSuccessful() else 1


if __name__ == '__main__':
    sys.exit(run_tests())
