#!/usr/bin/env python3
"""
Simple test to verify reverse proxy configuration changes.

This test verifies that the ProxyFix middleware import and basic configuration is correct.
"""

import sys
import os

def test_proxyfix_import():
    """Test that ProxyFix can be imported"""
    try:
        from werkzeug.middleware.proxy_fix import ProxyFix
        print("✓ ProxyFix import successful")
        return True
    except ImportError as e:
        print(f"✗ Failed to import ProxyFix: {e}")
        return False

def test_web_app_syntax():
    """Test that web_app.py has valid Python syntax"""
    import py_compile
    import tempfile
    
    try:
        web_app_path = os.path.join(os.path.dirname(__file__), 'src', 'web_app.py')
        
        # Compile to check syntax
        with tempfile.NamedTemporaryFile(suffix='.pyc', delete=True) as tmp:
            py_compile.compile(web_app_path, tmp.name, doraise=True)
        
        print("✓ web_app.py syntax is valid")
        return True
    except py_compile.PyCompileError as e:
        print(f"✗ Syntax error in web_app.py: {e}")
        return False

def test_proxyfix_in_code():
    """Test that ProxyFix is used in web_app.py"""
    web_app_path = os.path.join(os.path.dirname(__file__), 'src', 'web_app.py')
    
    with open(web_app_path, 'r') as f:
        content = f.read()
    
    checks = [
        ('from werkzeug.middleware.proxy_fix import ProxyFix', 'ProxyFix import'),
        ('app.wsgi_app = ProxyFix', 'ProxyFix middleware applied'),
        ('x_for=1', 'X-Forwarded-For configured'),
        ('x_proto=1', 'X-Forwarded-Proto configured'),
        ('x_host=1', 'X-Forwarded-Host configured'),
        ('x_prefix=1', 'X-Forwarded-Prefix configured'),
        ("BASE_PATH = os.environ.get('BASE_PATH'", 'BASE_PATH environment variable support'),
    ]
    
    all_passed = True
    for check, description in checks:
        if check in content:
            print(f"✓ {description} found")
        else:
            print(f"✗ {description} NOT found")
            all_passed = False
    
    return all_passed

def test_gunicorn_config():
    """Test that gunicorn is configured with forwarded-allow-ips"""
    start_sh_path = os.path.join(os.path.dirname(__file__), 'start.sh')
    
    with open(start_sh_path, 'r') as f:
        content = f.read()
    
    if '--forwarded-allow-ips=' in content:
        print("✓ Gunicorn forwarded-allow-ips configured")
        return True
    else:
        print("✗ Gunicorn forwarded-allow-ips NOT configured")
        return False

def test_documentation_exists():
    """Test that reverse proxy documentation exists"""
    doc_path = os.path.join(os.path.dirname(__file__), 'docs', 'REVERSE_PROXY.md')
    
    if os.path.exists(doc_path):
        with open(doc_path, 'r') as f:
            content = f.read()
        
        required_sections = [
            'Nginx Configuration',
            'Traefik Configuration',
            'Apache Configuration',
            'Caddy Configuration',
            'BASE_PATH',
            'X-Forwarded',
        ]
        
        all_found = True
        for section in required_sections:
            if section in content:
                print(f"✓ Documentation contains '{section}'")
            else:
                print(f"✗ Documentation missing '{section}'")
                all_found = False
        
        return all_found
    else:
        print("✗ REVERSE_PROXY.md documentation not found")
        return False

def main():
    """Run all tests"""
    print("Testing Reverse Proxy Configuration\n")
    print("=" * 60)
    
    tests = [
        ("ProxyFix Import", test_proxyfix_import),
        ("Web App Syntax", test_web_app_syntax),
        ("ProxyFix Usage", test_proxyfix_in_code),
        ("Gunicorn Config", test_gunicorn_config),
        ("Documentation", test_documentation_exists),
    ]
    
    results = []
    for name, test_func in tests:
        print(f"\n{name}:")
        print("-" * 60)
        result = test_func()
        results.append(result)
    
    print("\n" + "=" * 60)
    passed = sum(results)
    total = len(results)
    print(f"\nResults: {passed}/{total} tests passed")
    
    if passed == total:
        print("✓ All tests passed!")
        return 0
    else:
        print("✗ Some tests failed")
        return 1

if __name__ == '__main__':
    sys.exit(main())
