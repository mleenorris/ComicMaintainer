#!/usr/bin/env python3
"""
Test to verify reverse proxy path handling in templates and routes.

This test verifies that:
1. BASE_PATH is properly injected into templates
2. Manifest.json is generated dynamically with BASE_PATH
3. Service worker is served dynamically with BASE_PATH
4. All URLs use the apiUrl helper function
"""

import sys
import os
import re

def test_template_base_path():
    """Test that BASE_PATH is declared in the template"""
    template_path = os.path.join(os.path.dirname(__file__), 'templates', 'index.html')
    
    with open(template_path, 'r') as f:
        content = f.read()
    
    # Check for BASE_PATH constant declaration
    if "const BASE_PATH = '{{ base_path }}';" in content:
        print("✓ BASE_PATH constant declared in template")
        result1 = True
    else:
        print("✗ BASE_PATH constant NOT found in template")
        result1 = False
    
    # Check for apiUrl helper function
    if "function apiUrl(path)" in content:
        print("✓ apiUrl helper function found")
        result2 = True
    else:
        print("✗ apiUrl helper function NOT found")
        result2 = False
    
    # Check that manifest uses template variable
    if 'href="{{ base_path }}/manifest.json"' in content:
        print("✓ Manifest link uses BASE_PATH template variable")
        result3 = True
    else:
        print("✗ Manifest link does NOT use BASE_PATH template variable")
        result3 = False
    
    # Check that icons use template variables
    icon_pattern = r'href="\{\{ base_path \}\}/static/icons/'
    if re.search(icon_pattern, content):
        print("✓ Icon links use BASE_PATH template variable")
        result4 = True
    else:
        print("✗ Icon links do NOT use BASE_PATH template variable")
        result4 = False
    
    # Check that fetch calls use apiUrl
    # Should find many instances of fetch(apiUrl('/api
    fetch_count = content.count("fetch(apiUrl('/api")
    if fetch_count >= 30:  # We expect at least 30 API calls
        print(f"✓ Found {fetch_count} fetch calls using apiUrl helper")
        result5 = True
    else:
        print(f"✗ Only found {fetch_count} fetch calls using apiUrl (expected >= 30)")
        result5 = False
    
    # Check that EventSource uses apiUrl
    if "EventSource(apiUrl('/api/events/stream'))" in content:
        print("✓ EventSource uses apiUrl helper")
        result6 = True
    else:
        print("✗ EventSource does NOT use apiUrl helper")
        result6 = False
    
    # Check that service worker registration uses apiUrl
    if "serviceWorker.register(apiUrl('/sw.js'))" in content:
        print("✓ Service worker registration uses apiUrl helper")
        result7 = True
    else:
        print("✗ Service worker registration does NOT use apiUrl helper")
        result7 = False
    
    return all([result1, result2, result3, result4, result5, result6, result7])

def test_web_app_routes():
    """Test that web_app.py has dynamic routes for manifest and service worker"""
    web_app_path = os.path.join(os.path.dirname(__file__), 'src', 'web_app.py')
    
    with open(web_app_path, 'r') as f:
        content = f.read()
    
    # Check that index route passes base_path
    if "render_template('index.html', base_path=base_path)" in content:
        print("✓ Index route passes base_path to template")
        result1 = True
    else:
        print("✗ Index route does NOT pass base_path to template")
        result1 = False
    
    # Check that manifest is generated dynamically
    if '@app.route(\'/manifest.json\')' in content and 'return jsonify(manifest)' in content:
        print("✓ Manifest.json is generated dynamically")
        result2 = True
    else:
        print("✗ Manifest.json is NOT generated dynamically")
        result2 = False
    
    # Check that manifest uses base_path
    if '"start_url": f"{base_path}/"' in content:
        print("✓ Manifest uses base_path for URLs")
        result3 = True
    else:
        print("✗ Manifest does NOT use base_path for URLs")
        result3 = False
    
    # Check that service worker route exists
    if '@app.route(\'/sw.js\')' in content:
        print("✓ Service worker route exists")
        result4 = True
    else:
        print("✗ Service worker route does NOT exist")
        result4 = False
    
    # Check that service worker injects BASE_PATH
    if 'const BASE_PATH = \'{base_path}\'' in content or "const BASE_PATH = '{base_path}'" in content:
        print("✓ Service worker injects BASE_PATH")
        result5 = True
    else:
        print("✗ Service worker does NOT inject BASE_PATH")
        result5 = False
    
    return all([result1, result2, result3, result4, result5])

def test_no_hardcoded_paths():
    """Test that there are no hardcoded absolute paths in template"""
    template_path = os.path.join(os.path.dirname(__file__), 'templates', 'index.html')
    
    with open(template_path, 'r') as f:
        content = f.read()
    
    # Look for problematic patterns (hardcoded /api/ or /static/ paths)
    # Exclude comments and the apiUrl function definition
    lines = content.split('\n')
    problematic_lines = []
    
    for i, line in enumerate(lines, 1):
        # Skip comments and function definitions
        if '//' in line or 'function apiUrl' in line or 'const BASE_PATH' in line:
            continue
        
        # Check for hardcoded fetch('/api paths (should use apiUrl)
        if re.search(r"fetch\(['\"]\/api", line) and 'apiUrl' not in line:
            problematic_lines.append(f"Line {i}: {line.strip()}")
        
        # Check for hardcoded href="/static or src="/static (should use template vars)
        if re.search(r'(href|src)=["\']\/static', line) and '{{' not in line:
            problematic_lines.append(f"Line {i}: {line.strip()}")
    
    if not problematic_lines:
        print("✓ No hardcoded absolute paths found")
        return True
    else:
        print("✗ Found hardcoded absolute paths:")
        for line in problematic_lines[:5]:  # Show first 5
            print(f"  {line}")
        if len(problematic_lines) > 5:
            print(f"  ... and {len(problematic_lines) - 5} more")
        return False

def main():
    """Run all tests"""
    print("Testing Reverse Proxy Path Handling\n")
    print("=" * 60)
    
    tests = [
        ("Template BASE_PATH Support", test_template_base_path),
        ("Web App Dynamic Routes", test_web_app_routes),
        ("No Hardcoded Paths", test_no_hardcoded_paths),
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
