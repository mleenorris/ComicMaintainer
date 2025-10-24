#!/usr/bin/env python3
"""
Test suite for BIND_ADDRESS configuration support.

This test verifies that BIND_ADDRESS can be configured for local or remote access.
"""

import sys
import os
import subprocess
import tempfile


def test_env_validator_bind_address():
    """Test that env_validator.py includes BIND_ADDRESS"""
    validator_path = os.path.join(os.path.dirname(__file__), 'src', 'env_validator.py')
    
    with open(validator_path, 'r') as f:
        content = f.read()
    
    checks = [
        ('BIND_ADDRESS', 'BIND_ADDRESS environment variable'),
        ('0.0.0.0', 'Default bind address 0.0.0.0'),
        ('127.0.0.1', 'Localhost bind address 127.0.0.1'),
        ('Network interface to bind to', 'BIND_ADDRESS description'),
    ]
    
    all_passed = True
    for check, description in checks:
        if check in content:
            print(f"✓ {description} found in env_validator.py")
        else:
            print(f"✗ {description} NOT found in env_validator.py")
            all_passed = False
    
    return all_passed


def test_start_script_bind_address():
    """Test that start.sh uses BIND_ADDRESS"""
    start_sh_path = os.path.join(os.path.dirname(__file__), 'start.sh')
    
    with open(start_sh_path, 'r') as f:
        content = f.read()
    
    checks = [
        ('BIND_ADDRESS=${BIND_ADDRESS:-0.0.0.0}', 'BIND_ADDRESS variable with default'),
        ('--bind ${BIND_ADDRESS}:${WEB_PORT}', 'Gunicorn bind with BIND_ADDRESS'),
        ('127.0.0.1', 'Localhost reference in comments'),
    ]
    
    all_passed = True
    for check, description in checks:
        if check in content:
            print(f"✓ {description} found in start.sh")
        else:
            print(f"✗ {description} NOT found in start.sh")
            all_passed = False
    
    return all_passed


def test_web_app_bind_address():
    """Test that web_app.py uses BIND_ADDRESS"""
    web_app_path = os.path.join(os.path.dirname(__file__), 'src', 'web_app.py')
    
    with open(web_app_path, 'r') as f:
        content = f.read()
    
    checks = [
        ("bind_address = os.environ.get('BIND_ADDRESS'", 'BIND_ADDRESS environment variable'),
        ('app.run(host=bind_address', 'Flask app.run uses bind_address'),
    ]
    
    all_passed = True
    for check, description in checks:
        if check in content:
            print(f"✓ {description} found in web_app.py")
        else:
            print(f"✗ {description} NOT found in web_app.py")
            all_passed = False
    
    return all_passed


def test_docker_compose_bind_address():
    """Test that docker-compose.yml includes BIND_ADDRESS example"""
    compose_path = os.path.join(os.path.dirname(__file__), 'docker-compose.yml')
    
    with open(compose_path, 'r') as f:
        content = f.read()
    
    checks = [
        ('BIND_ADDRESS', 'BIND_ADDRESS environment variable'),
        ('0.0.0.0', 'All interfaces binding'),
        ('127.0.0.1', 'Localhost binding'),
    ]
    
    all_passed = True
    for check, description in checks:
        if check in content:
            print(f"✓ {description} found in docker-compose.yml")
        else:
            print(f"✗ {description} NOT found in docker-compose.yml")
            all_passed = False
    
    return all_passed


def test_readme_bind_address():
    """Test that README includes BIND_ADDRESS documentation"""
    readme_path = os.path.join(os.path.dirname(__file__), 'README.md')
    
    with open(readme_path, 'r') as f:
        content = f.read()
    
    checks = [
        ('BIND_ADDRESS', 'BIND_ADDRESS environment variable'),
        ('0.0.0.0', 'All interfaces binding'),
        ('127.0.0.1', 'Localhost binding'),
        ('reverse proxy', 'Reverse proxy mention'),
    ]
    
    all_passed = True
    for check, description in checks:
        if check in content:
            print(f"✓ {description} found in README.md")
        else:
            print(f"✗ {description} NOT found in README.md")
            all_passed = False
    
    return all_passed


def test_security_md_bind_address():
    """Test that SECURITY.md includes BIND_ADDRESS security guidance"""
    security_path = os.path.join(os.path.dirname(__file__), 'SECURITY.md')
    
    with open(security_path, 'r') as f:
        content = f.read()
    
    checks = [
        ('BIND_ADDRESS', 'BIND_ADDRESS environment variable'),
        ('127.0.0.1', 'Localhost binding'),
        ('Network Binding Configuration', 'Network binding section'),
    ]
    
    all_passed = True
    for check, description in checks:
        if check in content:
            print(f"✓ {description} found in SECURITY.md")
        else:
            print(f"✗ {description} NOT found in SECURITY.md")
            all_passed = False
    
    return all_passed


def test_bind_address_validation():
    """Test that BIND_ADDRESS validation works correctly"""
    import sys
    sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))
    
    # Test valid addresses
    valid_addresses = ['0.0.0.0', '127.0.0.1', '192.168.1.1', '10.0.0.1']
    # Note: Empty string is treated as "not set" and defaults to 0.0.0.0, which is valid behavior
    invalid_addresses = ['256.1.1.1', '1.1.1.256', 'localhost', 'invalid', '999.999.999.999']
    
    print("\nTesting valid IP addresses:")
    for addr in valid_addresses:
        os.environ['BIND_ADDRESS'] = addr
        os.environ['WATCHED_DIR'] = '/tmp'  # Required env var
        
        from env_validator import validate_env_vars
        is_valid, errors = validate_env_vars()
        
        bind_errors = [e for e in errors if 'BIND_ADDRESS' in e]
        if not bind_errors:
            print(f"  ✓ {addr} is accepted as valid")
        else:
            print(f"  ✗ {addr} incorrectly rejected: {bind_errors}")
            return False
    
    print("\nTesting invalid IP addresses:")
    for addr in invalid_addresses:
        os.environ['BIND_ADDRESS'] = addr
        os.environ['WATCHED_DIR'] = '/tmp'  # Required env var
        
        # Reload the module to get fresh validation
        import importlib
        import env_validator as ev
        importlib.reload(ev)
        
        is_valid, errors = ev.validate_env_vars()
        
        bind_errors = [e for e in errors if 'BIND_ADDRESS' in e]
        if bind_errors:
            print(f"  ✓ {addr} correctly rejected")
        else:
            print(f"  ✗ {addr} incorrectly accepted")
            return False
    
    return True


def main():
    """Run all tests"""
    print("Testing BIND_ADDRESS Configuration Support\n")
    print("=" * 60)
    
    tests = [
        ("env_validator.py BIND_ADDRESS", test_env_validator_bind_address),
        ("start.sh BIND_ADDRESS", test_start_script_bind_address),
        ("web_app.py BIND_ADDRESS", test_web_app_bind_address),
        ("docker-compose.yml BIND_ADDRESS", test_docker_compose_bind_address),
        ("README.md BIND_ADDRESS", test_readme_bind_address),
        ("SECURITY.md BIND_ADDRESS", test_security_md_bind_address),
        ("BIND_ADDRESS Validation", test_bind_address_validation),
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
