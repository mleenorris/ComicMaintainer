#!/usr/bin/env python3
"""Test version.py to ensure version format is correct."""

import sys
import re
import os

# Add src to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

from version import __version__


def test_version_format():
    """Test that version follows semantic versioning format."""
    # Semantic versioning regex: MAJOR.MINOR.PATCH
    # where each component is a number
    pattern = r'^\d+\.\d+\.\d+$'
    
    assert re.match(pattern, __version__), (
        f"Version '{__version__}' does not follow semantic versioning format (MAJOR.MINOR.PATCH)"
    )
    print(f"✓ Version format is valid: {__version__}")


def test_version_components():
    """Test that version components are valid integers."""
    parts = __version__.split('.')
    
    assert len(parts) == 3, (
        f"Version '{__version__}' must have exactly 3 components (MAJOR.MINOR.PATCH)"
    )
    
    major, minor, patch = parts
    
    # Check that each component can be converted to int
    try:
        major_int = int(major)
        minor_int = int(minor)
        patch_int = int(patch)
    except ValueError as e:
        raise AssertionError(
            f"Version components must be integers: {e}"
        )
    
    # Check that components are non-negative
    assert major_int >= 0, f"Major version must be non-negative: {major_int}"
    assert minor_int >= 0, f"Minor version must be non-negative: {minor_int}"
    assert patch_int >= 0, f"Patch version must be non-negative: {patch_int}"
    
    print(f"✓ Version components are valid: major={major_int}, minor={minor_int}, patch={patch_int}")


def test_version_is_string():
    """Test that __version__ is a string."""
    assert isinstance(__version__, str), (
        f"__version__ must be a string, got {type(__version__)}"
    )
    print(f"✓ Version is a string: '{__version__}'")


if __name__ == '__main__':
    print("Testing version.py...")
    print()
    
    try:
        test_version_is_string()
        test_version_format()
        test_version_components()
        print()
        print("All version tests passed! ✓")
        sys.exit(0)
    except AssertionError as e:
        print()
        print(f"Test failed: {e}")
        sys.exit(1)
