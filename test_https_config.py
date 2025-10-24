#!/usr/bin/env python3
"""
Test suite for HTTPS configuration support.

This test verifies that HTTPS configuration is properly set up.
"""

import sys
import os
import subprocess
import tempfile

def test_start_script_ssl_support():
    """Test that start.sh includes SSL configuration logic"""
    start_sh_path = os.path.join(os.path.dirname(__file__), 'start.sh')
    
    with open(start_sh_path, 'r') as f:
        content = f.read()
    
    checks = [
        ('SSL_CERTFILE', 'SSL_CERTFILE environment variable check'),
        ('SSL_KEYFILE', 'SSL_KEYFILE environment variable check'),
        ('--certfile', 'Gunicorn certfile option'),
        ('--keyfile', 'Gunicorn keyfile option'),
        ('SSL_CA_CERTS', 'SSL_CA_CERTS environment variable check'),
        ('--ca-certs', 'Gunicorn ca-certs option'),
    ]
    
    all_passed = True
    for check, description in checks:
        if check in content:
            print(f"✓ {description} found")
        else:
            print(f"✗ {description} NOT found")
            all_passed = False
    
    return all_passed

def test_cert_generation_script_exists():
    """Test that certificate generation script exists and is executable"""
    script_path = os.path.join(os.path.dirname(__file__), 'generate_self_signed_cert.sh')
    
    if not os.path.exists(script_path):
        print("✗ generate_self_signed_cert.sh not found")
        return False
    
    print("✓ generate_self_signed_cert.sh exists")
    
    if not os.access(script_path, os.X_OK):
        print("✗ generate_self_signed_cert.sh not executable")
        return False
    
    print("✓ generate_self_signed_cert.sh is executable")
    
    # Check script content
    with open(script_path, 'r') as f:
        content = f.read()
    
    required_elements = [
        'openssl req',
        '-x509',
        '-newkey rsa:2048',
        'selfsigned.key',
        'selfsigned.crt',
    ]
    
    all_found = True
    for element in required_elements:
        if element in content:
            print(f"✓ Script contains '{element}'")
        else:
            print(f"✗ Script missing '{element}'")
            all_found = False
    
    return all_found

def test_dockerfile_openssl():
    """Test that Dockerfile includes openssl"""
    dockerfile_path = os.path.join(os.path.dirname(__file__), 'Dockerfile')
    
    with open(dockerfile_path, 'r') as f:
        content = f.read()
    
    if 'openssl' in content:
        print("✓ Dockerfile includes openssl")
        return True
    else:
        print("✗ Dockerfile missing openssl")
        return False

def test_docker_compose_ssl_example():
    """Test that docker-compose.yml includes SSL configuration example"""
    compose_path = os.path.join(os.path.dirname(__file__), 'docker-compose.yml')
    
    with open(compose_path, 'r') as f:
        content = f.read()
    
    checks = [
        ('SSL_CERTFILE', 'SSL_CERTFILE example'),
        ('SSL_KEYFILE', 'SSL_KEYFILE example'),
        ('SSL_CA_CERTS', 'SSL_CA_CERTS example'),
    ]
    
    all_passed = True
    for check, description in checks:
        if check in content:
            print(f"✓ docker-compose.yml contains {description}")
        else:
            print(f"✗ docker-compose.yml missing {description}")
            all_passed = False
    
    return all_passed

def test_readme_https_documentation():
    """Test that README includes HTTPS documentation"""
    readme_path = os.path.join(os.path.dirname(__file__), 'README.md')
    
    with open(readme_path, 'r') as f:
        content = f.read()
    
    checks = [
        ('## HTTPS Configuration', 'HTTPS Configuration section'),
        ('SSL_CERTFILE', 'SSL_CERTFILE documentation'),
        ('SSL_KEYFILE', 'SSL_KEYFILE documentation'),
        ('Self-Signed Certificates', 'Self-signed certificate section'),
        ('Let\'s Encrypt', 'Let\'s Encrypt mention'),
        ('generate_self_signed_cert.sh', 'Certificate generation script mention'),
    ]
    
    all_passed = True
    for check, description in checks:
        if check in content:
            print(f"✓ README contains {description}")
        else:
            print(f"✗ README missing {description}")
            all_passed = False
    
    return all_passed

def test_https_setup_guide_exists():
    """Test that HTTPS setup guide exists"""
    guide_path = os.path.join(os.path.dirname(__file__), 'docs', 'HTTPS_SETUP.md')
    
    if not os.path.exists(guide_path):
        print("✗ docs/HTTPS_SETUP.md not found")
        return False
    
    print("✓ docs/HTTPS_SETUP.md exists")
    
    with open(guide_path, 'r') as f:
        content = f.read()
    
    required_sections = [
        '# HTTPS Setup Guide',
        'Direct HTTPS Setup',
        'Self-Signed Certificates',
        'Let\'s Encrypt',
        'Security Considerations',
        'Troubleshooting',
    ]
    
    all_found = True
    for section in required_sections:
        if section in content:
            print(f"✓ Guide contains '{section}' section")
        else:
            print(f"✗ Guide missing '{section}' section")
            all_found = False
    
    return all_found

def test_cert_generation_script_syntax():
    """Test that certificate generation script has valid bash syntax"""
    script_path = os.path.join(os.path.dirname(__file__), 'generate_self_signed_cert.sh')
    
    try:
        result = subprocess.run(
            ['bash', '-n', script_path],
            capture_output=True,
            text=True
        )
        
        if result.returncode == 0:
            print("✓ Certificate generation script has valid bash syntax")
            return True
        else:
            print(f"✗ Certificate generation script has syntax errors: {result.stderr}")
            return False
    except Exception as e:
        print(f"✗ Error checking script syntax: {e}")
        return False

def test_start_script_syntax():
    """Test that start.sh has valid bash syntax"""
    script_path = os.path.join(os.path.dirname(__file__), 'start.sh')
    
    try:
        result = subprocess.run(
            ['bash', '-n', script_path],
            capture_output=True,
            text=True
        )
        
        if result.returncode == 0:
            print("✓ start.sh has valid bash syntax")
            return True
        else:
            print(f"✗ start.sh has syntax errors: {result.stderr}")
            return False
    except Exception as e:
        print(f"✗ Error checking script syntax: {e}")
        return False

def main():
    """Run all tests"""
    print("Testing HTTPS Configuration Support\n")
    print("=" * 60)
    
    tests = [
        ("Start Script SSL Support", test_start_script_ssl_support),
        ("Certificate Generation Script", test_cert_generation_script_exists),
        ("Dockerfile OpenSSL", test_dockerfile_openssl),
        ("Docker Compose SSL Example", test_docker_compose_ssl_example),
        ("README HTTPS Documentation", test_readme_https_documentation),
        ("HTTPS Setup Guide", test_https_setup_guide_exists),
        ("Certificate Script Syntax", test_cert_generation_script_syntax),
        ("Start Script Syntax", test_start_script_syntax),
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
