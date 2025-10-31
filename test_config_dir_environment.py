#!/usr/bin/env python3
"""
Test that CONFIG_DIR can be configured via environment variable.
This ensures the application can run in different environments (Docker, local, test).
"""
import os
import sys
import tempfile
import subprocess

def test_config_dir_environment():
    """Test that all modules respect CONFIG_DIR environment variable"""
    test_config_dir = tempfile.mkdtemp()
    
    # Test each module that uses CONFIG_DIR
    modules_to_test = [
        'config',
        'markers',
        'process_file',
        'watcher',
        'web_app',
        'unified_store',
        'job_store'
    ]
    
    print("Testing CONFIG_DIR environment variable support...")
    print(f"Test config directory: {test_config_dir}\n")
    
    for module in modules_to_test:
        # Create a test script that imports the module and checks CONFIG_DIR
        test_script = f"""
import os
import sys
sys.path.insert(0, 'src')
os.environ['CONFIG_DIR'] = '{test_config_dir}'
os.environ['WATCHED_DIR'] = '{test_config_dir}'  # Also set for modules that need it

# Mock comicapi for modules that import it
from unittest.mock import MagicMock
sys.modules['comicapi'] = MagicMock()
sys.modules['comicapi.comicarchive'] = MagicMock()
sys.modules['comicapi.genericmetadata'] = MagicMock()
sys.modules['comicapi._url'] = MagicMock()

module_name = '{module}'

try:
    mod = __import__(module_name)
    if hasattr(mod, 'CONFIG_DIR'):
        actual_config_dir = mod.CONFIG_DIR
        expected_config_dir = '{test_config_dir}'
        if actual_config_dir == expected_config_dir:
            print(f"✓ {{module_name}}: CONFIG_DIR correctly set to {{actual_config_dir}}")
            sys.exit(0)
        else:
            print(f"✗ {{module_name}}: CONFIG_DIR is {{actual_config_dir}}, expected {{expected_config_dir}}")
            sys.exit(1)
    else:
        print(f"✓ {{module_name}}: No CONFIG_DIR (module may not use it)")
        sys.exit(0)
except Exception as e:
    print(f"✗ {{module_name}}: Failed to import - {{e}}")
    import traceback
    traceback.print_exc()
    sys.exit(1)
"""
        
        # Run the test script
        result = subprocess.run(
            [sys.executable, '-c', test_script],
            cwd='/home/runner/work/ComicMaintainer/ComicMaintainer',
            capture_output=True,
            text=True,
            timeout=10
        )
        
        print(result.stdout, end='')
        if result.stderr and 'Traceback' in result.stderr:
            print(result.stderr, end='')
        
        if result.returncode != 0:
            print(f"\n❌ Test failed for module: {module}")
            return False
    
    print("\n✅ All modules respect CONFIG_DIR environment variable!")
    return True


def test_default_config_dir():
    """Test that CONFIG_DIR defaults to /Config when not set"""
    print("\nTesting default CONFIG_DIR value...")
    
    # Test without setting CONFIG_DIR
    test_script = """
import os
import sys
sys.path.insert(0, 'src')

# Make sure CONFIG_DIR is not set
if 'CONFIG_DIR' in os.environ:
    del os.environ['CONFIG_DIR']

# Mock comicapi
from unittest.mock import MagicMock
sys.modules['comicapi'] = MagicMock()
sys.modules['comicapi.comicarchive'] = MagicMock()

os.environ['WATCHED_DIR'] = '/tmp/test'

import config
if config.CONFIG_DIR == '/Config':
    print("✓ config: CONFIG_DIR defaults to /Config")
    sys.exit(0)
else:
    print(f"✗ config: CONFIG_DIR is {config.CONFIG_DIR}, expected /Config")
    sys.exit(1)
"""
    
    result = subprocess.run(
        [sys.executable, '-c', test_script],
        cwd='/home/runner/work/ComicMaintainer/ComicMaintainer',
        capture_output=True,
        text=True,
        timeout=10
    )
    
    print(result.stdout, end='')
    
    if result.returncode != 0:
        print("\n❌ Default CONFIG_DIR test failed")
        return False
    
    print("✅ Default CONFIG_DIR value is correct!")
    return True


if __name__ == '__main__':
    print("=" * 60)
    print("CONFIG_DIR Environment Variable Test Suite")
    print("=" * 60)
    print()
    
    success = True
    
    # Run tests
    if not test_config_dir_environment():
        success = False
    
    if not test_default_config_dir():
        success = False
    
    print("\n" + "=" * 60)
    if success:
        print("✅ ALL TESTS PASSED")
        print("=" * 60)
        sys.exit(0)
    else:
        print("❌ SOME TESTS FAILED")
        print("=" * 60)
        sys.exit(1)
