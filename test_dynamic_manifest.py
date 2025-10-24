#!/usr/bin/env python3
"""
Test that the dynamic manifest endpoint in web_app.py includes the required fields for Android PWA installation.
"""

import sys
import ast
import re


def test_manifest_code():
    """Test that the manifest generation code includes the required fields."""
    
    # Read the web_app.py file
    with open('src/web_app.py', 'r') as f:
        content = f.read()
    
    # Find the manifest route handler
    manifest_start = content.find('@app.route(\'/manifest.json\')')
    assert manifest_start != -1, "Could not find /manifest.json route"
    
    # Extract the manifest dict section
    manifest_section_start = content.find('manifest = {', manifest_start)
    assert manifest_section_start != -1, "Could not find manifest dict"
    
    # Find the corresponding closing brace
    manifest_section_end = content.find('return jsonify(manifest)', manifest_section_start)
    assert manifest_section_end != -1, "Could not find end of manifest dict"
    
    manifest_section = content[manifest_section_start:manifest_section_end]
    
    # Check that required fields are present in the manifest dict (case-insensitive, flexible whitespace)
    # Look for 'id' field with any whitespace around colons and quotes
    id_pattern = r'["\']id["\']\s*:\s*f["\']'
    assert re.search(id_pattern, manifest_section), \
        "Missing 'id' field in manifest dict"
    
    # Look for prefer_related_applications with False value
    pref_pattern = r'["\']prefer_related_applications["\']\s*:\s*False'
    assert re.search(pref_pattern, manifest_section), \
        "Missing 'prefer_related_applications': False in manifest dict"
    
    print("✓ Dynamic manifest generation includes 'id' field")
    print("✓ Dynamic manifest generation includes 'prefer_related_applications': False")
    print("\n✓ All dynamic manifest code checks passed!")
    return 0


if __name__ == '__main__':
    try:
        sys.exit(test_manifest_code())
    except AssertionError as e:
        print(f"\n✗ Test failed: {e}")
        sys.exit(1)
    except Exception as e:
        print(f"\n✗ Unexpected error: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)
