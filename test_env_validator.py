#!/usr/bin/env python3
"""
Test script for environment variable validator.
"""

import os
import sys
import tempfile

# Add src to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

from env_validator import validate_env_vars


def test_missing_required_var():
    """Test that missing required variables are detected"""
    print("\n" + "=" * 60)
    print("TEST: Missing Required Variable")
    print("=" * 60)
    
    # Clear WATCHED_DIR
    original = os.environ.pop('WATCHED_DIR', None)
    
    try:
        is_valid, errors = validate_env_vars()
        
        assert not is_valid, "Validation should fail when WATCHED_DIR is missing"
        assert any('WATCHED_DIR' in error for error in errors), "Error should mention WATCHED_DIR"
        
        print("✓ Missing required variable detected correctly")
    finally:
        # Restore original value
        if original:
            os.environ['WATCHED_DIR'] = original


def test_invalid_watched_dir():
    """Test that invalid WATCHED_DIR is detected"""
    print("\n" + "=" * 60)
    print("TEST: Invalid WATCHED_DIR")
    print("=" * 60)
    
    # Set WATCHED_DIR to non-existent path
    original = os.environ.get('WATCHED_DIR')
    os.environ['WATCHED_DIR'] = '/nonexistent/path'
    
    try:
        is_valid, errors = validate_env_vars()
        
        assert not is_valid, "Validation should fail for non-existent directory"
        assert any('does not exist' in error for error in errors), "Error should mention directory doesn't exist"
        
        print("✓ Invalid directory detected correctly")
    finally:
        # Restore original value
        if original:
            os.environ['WATCHED_DIR'] = original
        else:
            os.environ.pop('WATCHED_DIR', None)


def test_valid_config():
    """Test that valid configuration passes"""
    print("\n" + "=" * 60)
    print("TEST: Valid Configuration")
    print("=" * 60)
    
    # Create a temporary directory for testing
    with tempfile.TemporaryDirectory() as tmpdir:
        original = os.environ.get('WATCHED_DIR')
        os.environ['WATCHED_DIR'] = tmpdir
        
        try:
            is_valid, errors = validate_env_vars()
            
            assert is_valid, f"Validation should pass with valid config. Errors: {errors}"
            assert len(errors) == 0, "No errors should be present"
            
            print("✓ Valid configuration accepted")
        finally:
            # Restore original value
            if original:
                os.environ['WATCHED_DIR'] = original
            else:
                os.environ.pop('WATCHED_DIR', None)


def test_numeric_validation():
    """Test that numeric values are validated correctly"""
    print("\n" + "=" * 60)
    print("TEST: Numeric Value Validation")
    print("=" * 60)
    
    # Test invalid port number
    original_port = os.environ.get('WEB_PORT')
    os.environ['WEB_PORT'] = 'invalid'
    
    with tempfile.TemporaryDirectory() as tmpdir:
        os.environ['WATCHED_DIR'] = tmpdir
        
        try:
            is_valid, errors = validate_env_vars()
            
            assert not is_valid, "Validation should fail for invalid numeric value"
            assert any('WEB_PORT' in error and 'integer' in error for error in errors), \
                "Error should mention WEB_PORT and integer"
            
            print("✓ Invalid numeric value detected")
        finally:
            # Restore original value
            if original_port:
                os.environ['WEB_PORT'] = original_port
            else:
                os.environ.pop('WEB_PORT', None)
    
    # Test out of range port number
    os.environ['WEB_PORT'] = '99999'
    
    with tempfile.TemporaryDirectory() as tmpdir:
        os.environ['WATCHED_DIR'] = tmpdir
        
        try:
            is_valid, errors = validate_env_vars()
            
            assert not is_valid, "Validation should fail for out of range value"
            assert any('WEB_PORT' in error and 'between' in error for error in errors), \
                "Error should mention WEB_PORT and range"
            
            print("✓ Out of range value detected")
        finally:
            # Restore original value
            if original_port:
                os.environ['WEB_PORT'] = original_port
            else:
                os.environ.pop('WEB_PORT', None)


def test_optional_vars_get_defaults():
    """Test that optional variables get default values"""
    print("\n" + "=" * 60)
    print("TEST: Optional Variables Get Defaults")
    print("=" * 60)
    
    # Remove optional variables
    optional_vars = ['MAX_WORKERS', 'GUNICORN_WORKERS', 'PUID', 'PGID']
    originals = {}
    for var in optional_vars:
        originals[var] = os.environ.pop(var, None)
    
    with tempfile.TemporaryDirectory() as tmpdir:
        os.environ['WATCHED_DIR'] = tmpdir
        
        try:
            is_valid, errors = validate_env_vars()
            
            assert is_valid, "Validation should pass even without optional vars"
            
            # Check that defaults were set
            assert os.environ.get('MAX_WORKERS') == '4', "MAX_WORKERS should default to 4"
            assert os.environ.get('GUNICORN_WORKERS') == '2', "GUNICORN_WORKERS should default to 2"
            
            print("✓ Optional variables got default values")
        finally:
            # Restore original values
            for var, original in originals.items():
                if original:
                    os.environ[var] = original
                else:
                    os.environ.pop(var, None)


if __name__ == "__main__":
    print("\n" + "=" * 60)
    print("Environment Validator Test Suite")
    print("=" * 60)
    
    # Save original environment state
    env_backup = os.environ.copy()
    
    try:
        # Run tests
        test_missing_required_var()
        test_invalid_watched_dir()
        test_valid_config()
        test_numeric_validation()
        test_optional_vars_get_defaults()
        
        print("\n" + "=" * 60)
        print("✓ All tests passed!")
        print("=" * 60)
        
    except AssertionError as e:
        print("\n" + "=" * 60)
        print("✗ Test failed!")
        print("=" * 60)
        print(f"Error: {e}")
        sys.exit(1)
    finally:
        # Restore original environment
        os.environ.clear()
        os.environ.update(env_backup)
