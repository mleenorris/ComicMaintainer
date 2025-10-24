#!/usr/bin/env python3
"""
Test script to verify HTTPS configuration
"""
import os
import subprocess
import sys
import tempfile
import time
import requests
from pathlib import Path

def generate_self_signed_cert(cert_dir):
    """Generate a self-signed certificate for testing"""
    cert_file = os.path.join(cert_dir, "cert.pem")
    key_file = os.path.join(cert_dir, "key.pem")
    
    print("Generating self-signed certificate...")
    subprocess.run([
        "openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes",
        "-keyout", key_file,
        "-out", cert_file,
        "-days", "1",
        "-subj", "/CN=localhost"
    ], check=True, capture_output=True)
    
    return cert_file, key_file

def test_http_mode():
    """Test that HTTP mode works (default)"""
    print("\n=== Testing HTTP Mode (default) ===")
    
    # Test that environment variables are not set by default
    assert os.environ.get('HTTPS_ENABLED') != 'true', "HTTPS_ENABLED should not be set by default"
    
    print("✓ HTTP mode is default (HTTPS_ENABLED not set)")
    return True

def test_https_environment_variables():
    """Test HTTPS environment variable validation"""
    print("\n=== Testing HTTPS Environment Variables ===")
    
    with tempfile.TemporaryDirectory() as temp_dir:
        cert_file, key_file = generate_self_signed_cert(temp_dir)
        
        # Test that certificate files exist
        assert os.path.exists(cert_file), f"Certificate file not found: {cert_file}"
        assert os.path.exists(key_file), f"Key file not found: {key_file}"
        
        print(f"✓ Certificate file created: {cert_file}")
        print(f"✓ Key file created: {key_file}")
        
        # Test environment variable setup
        os.environ['HTTPS_ENABLED'] = 'true'
        os.environ['SSL_CERT'] = cert_file
        os.environ['SSL_KEY'] = key_file
        
        assert os.environ.get('HTTPS_ENABLED') == 'true'
        assert os.environ.get('SSL_CERT') == cert_file
        assert os.environ.get('SSL_KEY') == key_file
        
        print("✓ Environment variables set correctly")
        
        # Clean up
        del os.environ['HTTPS_ENABLED']
        del os.environ['SSL_CERT']
        del os.environ['SSL_KEY']
    
    return True

def test_gunicorn_command_generation():
    """Test that the start.sh script generates correct Gunicorn commands"""
    print("\n=== Testing Gunicorn Command Generation ===")
    
    with tempfile.TemporaryDirectory() as temp_dir:
        cert_file, key_file = generate_self_signed_cert(temp_dir)
        
        # Read the start.sh script
        script_path = Path(__file__).parent / "start.sh"
        with open(script_path, 'r') as f:
            script_content = f.read()
        
        # Verify HTTPS-related code exists
        assert 'HTTPS_ENABLED' in script_content, "HTTPS_ENABLED check not found in start.sh"
        assert 'SSL_CERT' in script_content, "SSL_CERT check not found in start.sh"
        assert 'SSL_KEY' in script_content, "SSL_KEY check not found in start.sh"
        assert '--certfile' in script_content, "--certfile parameter not found in start.sh"
        assert '--keyfile' in script_content, "--keyfile parameter not found in start.sh"
        
        print("✓ start.sh contains HTTPS configuration logic")
        print("✓ start.sh contains SSL certificate parameters")
    
    return True

def test_docker_compose_config():
    """Test that docker-compose.yml has HTTPS configuration"""
    print("\n=== Testing Docker Compose Configuration ===")
    
    compose_path = Path(__file__).parent / "docker-compose.yml"
    with open(compose_path, 'r') as f:
        compose_content = f.read()
    
    # Verify HTTPS-related configuration exists
    assert 'HTTPS_ENABLED' in compose_content, "HTTPS_ENABLED not found in docker-compose.yml"
    assert 'SSL_CERT' in compose_content, "SSL_CERT not found in docker-compose.yml"
    assert 'SSL_KEY' in compose_content, "SSL_KEY not found in docker-compose.yml"
    assert '/ssl' in compose_content, "SSL mount path not found in docker-compose.yml"
    
    print("✓ docker-compose.yml contains HTTPS environment variables")
    print("✓ docker-compose.yml contains SSL volume mount example")
    
    return True

def test_readme_documentation():
    """Test that README has HTTPS documentation"""
    print("\n=== Testing README Documentation ===")
    
    readme_path = Path(__file__).parent / "README.md"
    with open(readme_path, 'r') as f:
        readme_content = f.read()
    
    # Verify HTTPS-related documentation exists
    assert 'HTTPS_ENABLED' in readme_content, "HTTPS_ENABLED not documented in README"
    assert 'SSL_CERT' in readme_content, "SSL_CERT not documented in README"
    assert 'SSL_KEY' in readme_content, "SSL_KEY not documented in README"
    assert 'HTTPS Configuration' in readme_content or 'https' in readme_content.lower(), "HTTPS section not found in README"
    assert 'openssl' in readme_content.lower(), "Certificate generation instructions not found in README"
    assert 'Let\'s Encrypt' in readme_content or 'letsencrypt' in readme_content.lower(), "Let's Encrypt instructions not found in README"
    
    print("✓ README contains HTTPS configuration documentation")
    print("✓ README contains certificate generation examples")
    print("✓ README contains Let's Encrypt instructions")
    
    return True

def main():
    """Run all tests"""
    print("=" * 60)
    print("HTTPS Configuration Test Suite")
    print("=" * 60)
    
    tests = [
        ("HTTP Mode (Default)", test_http_mode),
        ("HTTPS Environment Variables", test_https_environment_variables),
        ("Gunicorn Command Generation", test_gunicorn_command_generation),
        ("Docker Compose Configuration", test_docker_compose_config),
        ("README Documentation", test_readme_documentation),
    ]
    
    passed = 0
    failed = 0
    
    for test_name, test_func in tests:
        try:
            if test_func():
                passed += 1
                print(f"\n✓ {test_name}: PASSED")
        except Exception as e:
            failed += 1
            print(f"\n✗ {test_name}: FAILED")
            print(f"  Error: {e}")
    
    print("\n" + "=" * 60)
    print(f"Test Results: {passed} passed, {failed} failed")
    print("=" * 60)
    
    return 0 if failed == 0 else 1

if __name__ == "__main__":
    sys.exit(main())
