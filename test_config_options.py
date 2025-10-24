#!/usr/bin/env python3
"""
Test suite for new HTTPS and proxy configuration options.

This test verifies that all new configuration functions work correctly.
"""

import sys
import os
import json
import tempfile
import shutil

# Add src to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

def test_config_functions():
    """Test that all new config functions are available and work"""
    from config import (
        get_ssl_certfile, set_ssl_certfile, DEFAULT_SSL_CERTFILE,
        get_ssl_keyfile, set_ssl_keyfile, DEFAULT_SSL_KEYFILE,
        get_ssl_ca_certs, set_ssl_ca_certs, DEFAULT_SSL_CA_CERTS,
        get_base_path, set_base_path, DEFAULT_BASE_PATH,
        get_proxy_x_for, set_proxy_x_for, DEFAULT_PROXY_X_FOR,
        get_proxy_x_proto, set_proxy_x_proto, DEFAULT_PROXY_X_PROTO,
        get_proxy_x_host, set_proxy_x_host, DEFAULT_PROXY_X_HOST,
        get_proxy_x_prefix, set_proxy_x_prefix, DEFAULT_PROXY_X_PREFIX
    )
    
    print("✓ All config functions imported successfully")
    
    # Test defaults
    assert get_ssl_certfile() == DEFAULT_SSL_CERTFILE, "SSL certfile default mismatch"
    assert get_ssl_keyfile() == DEFAULT_SSL_KEYFILE, "SSL keyfile default mismatch"
    assert get_ssl_ca_certs() == DEFAULT_SSL_CA_CERTS, "SSL CA certs default mismatch"
    assert get_base_path() == DEFAULT_BASE_PATH, "Base path default mismatch"
    assert get_proxy_x_for() == DEFAULT_PROXY_X_FOR, "Proxy X-For default mismatch"
    assert get_proxy_x_proto() == DEFAULT_PROXY_X_PROTO, "Proxy X-Proto default mismatch"
    assert get_proxy_x_host() == DEFAULT_PROXY_X_HOST, "Proxy X-Host default mismatch"
    assert get_proxy_x_prefix() == DEFAULT_PROXY_X_PREFIX, "Proxy X-Prefix default mismatch"
    
    print("✓ All default values correct")
    
    return True

def test_config_file_persistence():
    """Test that config values persist to file"""
    # Create a temporary config directory
    with tempfile.TemporaryDirectory() as tmpdir:
        # Set up temporary config
        config_file = os.path.join(tmpdir, 'config.json')
        
        # Temporarily override CONFIG_FILE
        import config as config_module
        original_config_file = config_module.CONFIG_FILE
        original_config_dir = config_module.CONFIG_DIR
        
        try:
            config_module.CONFIG_FILE = config_file
            config_module.CONFIG_DIR = tmpdir
            
            from config import (
                set_ssl_certfile, get_ssl_certfile,
                set_base_path, get_base_path,
                set_proxy_x_for, get_proxy_x_for
            )
            
            # Test SSL certfile
            test_certfile = "/test/path/cert.crt"
            assert set_ssl_certfile(test_certfile), "Failed to set SSL certfile"
            assert get_ssl_certfile() == test_certfile, "SSL certfile not persisted"
            print("✓ SSL certfile persists correctly")
            
            # Test base path
            test_base_path = "/comics"
            assert set_base_path(test_base_path), "Failed to set base path"
            assert get_base_path() == test_base_path, "Base path not persisted"
            print("✓ Base path persists correctly")
            
            # Test proxy setting
            test_x_for = 2
            assert set_proxy_x_for(test_x_for), "Failed to set proxy X-For"
            assert get_proxy_x_for() == test_x_for, "Proxy X-For not persisted"
            print("✓ Proxy X-For persists correctly")
            
            # Verify file contents
            with open(config_file, 'r') as f:
                config_data = json.load(f)
            
            assert config_data['ssl_certfile'] == test_certfile, "SSL certfile not in config file"
            assert config_data['base_path'] == test_base_path, "Base path not in config file"
            assert config_data['proxy_x_for'] == test_x_for, "Proxy X-For not in config file"
            print("✓ Config file contains correct values")
            
        finally:
            # Restore original config paths
            config_module.CONFIG_FILE = original_config_file
            config_module.CONFIG_DIR = original_config_dir
    
    return True

def test_environment_variable_priority():
    """Test that environment variables take priority over config file"""
    import config as config_module
    
    # Set environment variable
    os.environ['SSL_CERTFILE'] = '/env/cert.crt'
    os.environ['PROXY_X_FOR'] = '3'
    
    try:
        from config import get_ssl_certfile, get_proxy_x_for
        
        # Reload to pick up env vars
        import importlib
        importlib.reload(config_module)
        
        # Check that env vars are used
        certfile = get_ssl_certfile()
        x_for = get_proxy_x_for()
        
        assert certfile == '/env/cert.crt', f"Expected env var value, got {certfile}"
        assert x_for == 3, f"Expected env var value 3, got {x_for}"
        
        print("✓ Environment variables take priority")
        
    finally:
        # Clean up
        del os.environ['SSL_CERTFILE']
        del os.environ['PROXY_X_FOR']
    
    return True

def test_web_app_imports():
    """Test that web_app.py can import all new config functions"""
    # Check that web_app.py imports are correct
    web_app_path = os.path.join(os.path.dirname(__file__), 'src', 'web_app.py')
    
    with open(web_app_path, 'r') as f:
        content = f.read()
    
    required_imports = [
        'get_ssl_certfile', 'set_ssl_certfile',
        'get_ssl_keyfile', 'set_ssl_keyfile',
        'get_ssl_ca_certs', 'set_ssl_ca_certs',
        'get_base_path', 'set_base_path',
        'get_proxy_x_for', 'set_proxy_x_for',
        'get_proxy_x_proto', 'set_proxy_x_proto',
        'get_proxy_x_host', 'set_proxy_x_host',
        'get_proxy_x_prefix', 'set_proxy_x_prefix'
    ]
    
    for func in required_imports:
        if func not in content:
            print(f"✗ web_app.py missing import: {func}")
            return False
    
    print("✓ web_app.py imports all new config functions")
    return True

def test_web_app_api_endpoints():
    """Test that web_app.py has all required API endpoints"""
    web_app_path = os.path.join(os.path.dirname(__file__), 'src', 'web_app.py')
    
    with open(web_app_path, 'r') as f:
        content = f.read()
    
    required_endpoints = [
        "'/api/settings/ssl-certfile'",
        "'/api/settings/ssl-keyfile'",
        "'/api/settings/ssl-ca-certs'",
        "'/api/settings/base-path'",
        "'/api/settings/proxy-x-for'",
        "'/api/settings/proxy-x-proto'",
        "'/api/settings/proxy-x-host'",
        "'/api/settings/proxy-x-prefix'"
    ]
    
    for endpoint in required_endpoints:
        if endpoint not in content:
            print(f"✗ web_app.py missing endpoint: {endpoint}")
            return False
    
    print("✓ web_app.py has all required API endpoints")
    return True

def test_web_app_uses_config_for_proxy():
    """Test that web_app.py uses config functions for ProxyFix"""
    web_app_path = os.path.join(os.path.dirname(__file__), 'src', 'web_app.py')
    
    with open(web_app_path, 'r') as f:
        content = f.read()
    
    # Check that ProxyFix uses config functions
    checks = [
        ('x_for=get_proxy_x_for()', 'ProxyFix uses get_proxy_x_for()'),
        ('x_proto=get_proxy_x_proto()', 'ProxyFix uses get_proxy_x_proto()'),
        ('x_host=get_proxy_x_host()', 'ProxyFix uses get_proxy_x_host()'),
        ('x_prefix=get_proxy_x_prefix()', 'ProxyFix uses get_proxy_x_prefix()'),
        ('BASE_PATH = get_base_path()', 'BASE_PATH uses get_base_path()')
    ]
    
    all_passed = True
    for check, description in checks:
        if check in content:
            print(f"✓ {description}")
        else:
            print(f"✗ {description}")
            all_passed = False
    
    return all_passed

def test_html_has_new_settings():
    """Test that index.html has all new settings fields"""
    html_path = os.path.join(os.path.dirname(__file__), 'templates', 'index.html')
    
    with open(html_path, 'r') as f:
        content = f.read()
    
    required_fields = [
        'id="sslCertfile"',
        'id="sslKeyfile"',
        'id="sslCaCerts"',
        'id="basePath"',
        'id="proxyXFor"',
        'id="proxyXProto"',
        'id="proxyXHost"',
        'id="proxyXPrefix"'
    ]
    
    all_found = True
    for field in required_fields:
        if field in content:
            print(f"✓ HTML contains {field}")
        else:
            print(f"✗ HTML missing {field}")
            all_found = False
    
    return all_found

def test_documentation_updated():
    """Test that documentation mentions new configuration options"""
    docs_to_check = [
        ('README.md', ['PROXY_X_FOR', 'PROXY_X_PROTO', 'Settings UI']),
        ('docs/REVERSE_PROXY.md', ['PROXY_X_FOR', 'Configuration Methods']),
        ('docs/HTTPS_SETUP.md', ['Settings UI', 'Config File'])
    ]
    
    all_passed = True
    for doc_file, required_terms in docs_to_check:
        doc_path = os.path.join(os.path.dirname(__file__), doc_file)
        
        if not os.path.exists(doc_path):
            print(f"✗ {doc_file} not found")
            all_passed = False
            continue
        
        with open(doc_path, 'r') as f:
            content = f.read()
        
        for term in required_terms:
            if term in content:
                print(f"✓ {doc_file} mentions '{term}'")
            else:
                print(f"✗ {doc_file} missing '{term}'")
                all_passed = False
    
    return all_passed

def main():
    """Run all tests"""
    print("Testing HTTPS and Proxy Configuration Options\n")
    print("=" * 60)
    
    tests = [
        ("Config Functions Available", test_config_functions),
        ("Config File Persistence", test_config_file_persistence),
        ("Environment Variable Priority", test_environment_variable_priority),
        ("Web App Imports", test_web_app_imports),
        ("Web App API Endpoints", test_web_app_api_endpoints),
        ("Web App Uses Config", test_web_app_uses_config_for_proxy),
        ("HTML Settings Fields", test_html_has_new_settings),
        ("Documentation Updated", test_documentation_updated),
    ]
    
    results = []
    for name, test_func in tests:
        print(f"\n{name}:")
        print("-" * 60)
        try:
            result = test_func()
            results.append(result)
        except Exception as e:
            print(f"✗ Test failed with exception: {e}")
            results.append(False)
    
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
