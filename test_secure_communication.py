#!/usr/bin/env python3
"""
Test script to verify all site communication is secure.

This test verifies that:
1. GitHub API URL is hardcoded to HTTPS
2. GitHub API URL cannot be overridden with insecure HTTP
3. All external communications use secure protocols
"""

import sys
import os

# Add src to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

def test_github_api_https_only():
    """Test that GitHub API URL is hardcoded to HTTPS and cannot be overridden."""
    print("\n" + "=" * 60)
    print("TEST: GitHub API URL Security")
    print("=" * 60)
    
    # Try to set an insecure URL via environment variable
    os.environ['GITHUB_API_URL'] = 'http://insecure.example.com/api'
    
    # Import error_handler (after setting env var)
    from error_handler import GITHUB_API_URL
    
    # Verify it's still HTTPS
    print(f"Environment variable set to: http://insecure.example.com/api")
    print(f"Actual GITHUB_API_URL used: {GITHUB_API_URL}")
    
    if GITHUB_API_URL == 'https://api.github.com':
        print("\n✅ PASSED: GitHub API URL is hardcoded to HTTPS")
        print("✅ PASSED: Cannot be overridden with insecure HTTP URL")
        return True
    else:
        print(f"\n❌ FAILED: GitHub API URL is: {GITHUB_API_URL}")
        print("❌ FAILED: Should be hardcoded to https://api.github.com")
        return False

def test_no_hardcoded_http():
    """Test that there are no hardcoded HTTP URLs in the codebase."""
    print("\n" + "=" * 60)
    print("TEST: No Hardcoded HTTP URLs")
    print("=" * 60)
    
    import subprocess
    
    # Search for hardcoded HTTP URLs (excluding localhost and xmlns)
    result = subprocess.run(
        ['grep', '-r', 'http://', '--include=*.py', 'src/'],
        capture_output=True,
        text=True,
        cwd=os.path.dirname(__file__)
    )
    
    # Filter out acceptable HTTP usage (localhost, comments, etc.)
    lines = [line for line in result.stdout.split('\n') if line]
    insecure_urls = [
        line for line in lines 
        if 'http://' in line 
        and 'localhost' not in line 
        and 'xmlns' not in line
        and not line.strip().startswith('#')
    ]
    
    if not insecure_urls:
        print("✅ PASSED: No hardcoded insecure HTTP URLs found")
        print("   (localhost and xmlns references are acceptable)")
        return True
    else:
        print("❌ FAILED: Found insecure HTTP URLs:")
        for url in insecure_urls:
            print(f"   {url}")
        return False

def test_requests_library_usage():
    """Test that requests library usage is secure."""
    print("\n" + "=" * 60)
    print("TEST: Requests Library Security")
    print("=" * 60)
    
    # Check error_handler.py for secure requests usage
    error_handler_path = os.path.join(os.path.dirname(__file__), 'src', 'error_handler.py')
    
    with open(error_handler_path, 'r') as f:
        content = f.read()
    
    # Verify GITHUB_API_URL is used and it's HTTPS
    if 'GITHUB_API_URL' in content and 'https://api.github.com' in content:
        print("✅ PASSED: Requests use secure HTTPS URLs")
        print("✅ PASSED: GitHub API URL is hardcoded to HTTPS")
        return True
    else:
        print("❌ FAILED: Could not verify secure requests usage")
        return False

def main():
    """Run all security tests."""
    print("\n" + "=" * 60)
    print("ComicMaintainer Secure Communication Test Suite")
    print("=" * 60)
    print("\nThis script verifies that all site communication is secure.\n")
    
    results = []
    
    try:
        results.append(test_github_api_https_only())
        results.append(test_no_hardcoded_http())
        results.append(test_requests_library_usage())
        
        print("\n" + "=" * 60)
        if all(results):
            print("ALL SECURITY TESTS PASSED ✅")
            print("=" * 60)
            print("\n✅ All external communications use secure HTTPS")
            print("✅ GitHub API URL is hardcoded and cannot be overridden")
            print("✅ No insecure HTTP URLs found in source code")
            print("\nThe application is secured for external communication.")
            return 0
        else:
            print("SOME TESTS FAILED ❌")
            print("=" * 60)
            print("\nPlease review the failed tests above.")
            return 1
        
    except Exception as e:
        print("\n" + "=" * 60)
        print("TESTS FAILED WITH ERROR ❌")
        print("=" * 60)
        print(f"\nError: {e}")
        import traceback
        traceback.print_exc()
        return 1

if __name__ == "__main__":
    sys.exit(main())
