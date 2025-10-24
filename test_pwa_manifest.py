#!/usr/bin/env python3
"""
Test PWA manifest and icon configuration.
Validates that the PWA setup meets all requirements for proper installation.
"""

import sys
import os
import json

# Minimum file size for icons and favicons (in bytes)
MIN_ICON_SIZE_BYTES = 100


def test_static_manifest_exists():
    """Test that static manifest.json exists and is valid JSON."""
    manifest_path = 'static/manifest.json'
    assert os.path.exists(manifest_path), f"Manifest file not found at {manifest_path}"
    
    with open(manifest_path, 'r') as f:
        manifest = json.load(f)
    
    print(f"✓ Static manifest.json is valid JSON")
    return manifest


def test_manifest_required_fields(manifest):
    """Test that manifest has all required fields."""
    required_fields = ['name', 'short_name', 'icons', 'start_url', 'display']
    
    for field in required_fields:
        assert field in manifest, f"Required field '{field}' missing from manifest"
    
    print(f"✓ All required manifest fields present: {', '.join(required_fields)}")


def test_manifest_icons(manifest):
    """Test that manifest has proper icon configuration."""
    icons = manifest.get('icons', [])
    
    assert len(icons) >= 2, f"Manifest should have at least 2 icons, found {len(icons)}"
    
    # Check that we have both standard and maskable icons
    purposes = set()
    sizes = set()
    
    for icon in icons:
        assert 'src' in icon, "Icon missing 'src' field"
        assert 'sizes' in icon, "Icon missing 'sizes' field"
        assert 'type' in icon, "Icon missing 'type' field"
        assert 'purpose' in icon, "Icon missing 'purpose' field"
        
        purposes.add(icon['purpose'])
        sizes.add(icon['sizes'])
    
    # Should have at least these purposes
    assert 'any' in purposes, "Manifest should have 'any' purpose icons"
    assert 'maskable' in purposes, "Manifest should have 'maskable' purpose icons"
    
    # Should have required sizes
    assert '192x192' in sizes, "Manifest should have 192x192 icons"
    assert '512x512' in sizes, "Manifest should have 512x512 icons"
    
    print(f"✓ Manifest has {len(icons)} icons with purposes: {', '.join(sorted(purposes))}")
    print(f"✓ Icon sizes: {', '.join(sorted(sizes))}")


def test_icon_files_exist(manifest):
    """Test that all icon files referenced in manifest actually exist."""
    icons = manifest.get('icons', [])
    
    for icon in icons:
        src = icon['src']
        # Remove leading slash and /static/ prefix for file path
        if src.startswith('/static/'):
            file_path = src[1:]  # Remove leading /
        elif src.startswith('/'):
            file_path = src[1:]
        else:
            file_path = src
        
        assert os.path.exists(file_path), f"Icon file not found: {file_path}"
        
        # Check file size is reasonable (not empty)
        file_size = os.path.getsize(file_path)
        assert file_size > MIN_ICON_SIZE_BYTES, f"Icon file too small ({file_size} bytes): {file_path}"
    
    print(f"✓ All {len(icons)} icon files exist and are non-empty")


def test_service_worker():
    """Test that service worker references the new icons."""
    sw_path = 'static/sw.js'
    assert os.path.exists(sw_path), f"Service worker not found at {sw_path}"
    
    with open(sw_path, 'r') as f:
        sw_content = f.read()
    
    # Check for cache name
    assert 'CACHE_NAME' in sw_content, "Service worker missing CACHE_NAME"
    
    # Check that maskable icons are in cache
    assert 'icon-192x192-maskable.png' in sw_content, "Service worker missing maskable 192x192 icon"
    assert 'icon-512x512-maskable.png' in sw_content, "Service worker missing maskable 512x512 icon"
    
    print(f"✓ Service worker configured correctly with maskable icons")


def test_favicon_files():
    """Test that favicon files exist."""
    favicons = [
        'static/icons/favicon-16x16.png',
        'static/icons/favicon-32x32.png',
        'static/icons/apple-touch-icon.png'
    ]
    
    for favicon in favicons:
        assert os.path.exists(favicon), f"Favicon not found: {favicon}"
        file_size = os.path.getsize(favicon)
        assert file_size > MIN_ICON_SIZE_BYTES, f"Favicon too small ({file_size} bytes): {favicon}"
    
    print(f"✓ All {len(favicons)} favicon files exist")


def main():
    """Run all tests."""
    print("=" * 70)
    print("Testing PWA Manifest and Icon Configuration")
    print("=" * 70)
    
    try:
        # Test manifest
        manifest = test_static_manifest_exists()
        test_manifest_required_fields(manifest)
        test_manifest_icons(manifest)
        test_icon_files_exist(manifest)
        
        # Test service worker
        test_service_worker()
        
        # Test favicons
        test_favicon_files()
        
        print("=" * 70)
        print("✓ All PWA configuration tests passed!")
        print("=" * 70)
        return 0
        
    except AssertionError as e:
        print(f"\n✗ Test failed: {e}")
        return 1
    except Exception as e:
        print(f"\n✗ Unexpected error: {e}")
        return 1


if __name__ == '__main__':
    sys.exit(main())
