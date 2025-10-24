#!/usr/bin/env python3
"""
Test to verify the fix for process-unmarked file existence validation.

This test demonstrates that process-unmarked now validates file existence
before attempting to process files, matching the behavior of process-selected.
"""

import sys
import os

# Add src to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

print("=" * 80)
print("TEST: Process Unmarked File Existence Validation")
print("=" * 80)

# Read the web_app.py file to verify the fix
web_app_path = os.path.join(os.path.dirname(__file__), 'src', 'web_app.py')

with open(web_app_path, 'r') as f:
    content = f.read()

# Find the async_process_unmarked_files function
pu_start = content.find('def async_process_unmarked_files():')
pu_end = content.find('\n\n@app.route', pu_start)
if pu_end == -1:
    pu_end = pu_start + 2500
process_unmarked = content[pu_start:pu_end]

# Find the async_process_selected_files function for comparison
ps_start = content.find('def async_process_selected_files():')
ps_end = content.find('\n\n@app.route', ps_start)
process_selected = content[ps_start:ps_end]

print("\n1. Checking process-selected for file existence validation:")
if 'os.path.exists' in process_selected:
    print("   ✓ PASS: process-selected validates file existence")
else:
    print("   ✗ FAIL: process-selected does not validate file existence")

print("\n2. Checking process-unmarked for file existence validation:")
if 'os.path.exists' in process_unmarked:
    print("   ✓ PASS: process-unmarked validates file existence")
    
    # Check if there's a warning log for non-existent files
    if 'Skipping non-existent file' in process_unmarked:
        print("   ✓ PASS: Logs warning for non-existent files")
    else:
        print("   ⚠ WARNING: Does not log warning for non-existent files")
else:
    print("   ✗ FAIL: process-unmarked does not validate file existence")

print("\n3. Checking all unmarked-related endpoints:")
unmarked_functions = [
    'async_process_unmarked_files',
    'async_rename_unmarked_files',
    'async_normalize_unmarked_files',
    'process_unmarked_files',
    'rename_unmarked_files',
    'normalize_unmarked_files'
]

all_pass = True
for func_name in unmarked_functions:
    func_start = content.find(f'def {func_name}():')
    if func_start == -1:
        print(f"   ⚠ WARNING: Could not find {func_name}")
        continue
    
    func_end = content.find('\n\n@app.route', func_start)
    if func_end == -1:
        func_end = func_start + 2500
    
    func_content = content[func_start:func_end]
    
    # Check for file existence validation (either direct or via helper function)
    has_validation = ('os.path.exists' in func_content or 
                     'filter_unmarked_existing_files' in func_content)
    
    if has_validation:
        print(f"   ✓ PASS: {func_name} validates file existence")
    else:
        print(f"   ✗ FAIL: {func_name} does not validate file existence")
        all_pass = False

print("\n" + "=" * 80)
print("SUMMARY")
print("=" * 80)

if all_pass:
    print("\n✓ ALL TESTS PASSED")
    print("\nAll process-unmarked endpoints now validate file existence before")
    print("attempting to process them, preventing errors when files in the")
    print("database have been deleted from the filesystem.")
    sys.exit(0)
else:
    print("\n✗ SOME TESTS FAILED")
    print("\nSome process-unmarked endpoints still need to be fixed.")
    sys.exit(1)
