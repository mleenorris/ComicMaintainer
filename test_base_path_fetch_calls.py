#!/usr/bin/env python3
"""
Test to verify that all fetch() calls in the web interface template use the apiUrl() helper.
This ensures proper BASE_PATH compatibility for reverse proxy deployments.
"""
import re


def test_fetch_calls_use_api_url():
    """Verify that all fetch() calls use apiUrl() helper for BASE_PATH compatibility."""
    
    # Read the template file
    with open('templates/index.html', 'r') as f:
        content = f.read()
    
    # Pattern to find fetch calls with direct API paths (not using apiUrl)
    # This matches patterns like: fetch('/api/...') or fetch(`/api/...`)
    direct_fetch_pattern = r'fetch\s*\(\s*[`\'"]\/api\/'
    
    # Find all matches
    matches = re.finditer(direct_fetch_pattern, content)
    direct_fetches = []
    
    for match in matches:
        # Get line number for better error reporting
        line_num = content[:match.start()].count('\n') + 1
        # Get the matched text
        matched_text = match.group(0)
        direct_fetches.append((line_num, matched_text))
    
    # Assert no direct fetch calls exist
    if direct_fetches:
        error_msg = "Found fetch() calls that don't use apiUrl() helper:\n"
        for line_num, text in direct_fetches:
            error_msg += f"  Line {line_num}: {text}\n"
        error_msg += "\nAll API fetch calls should use apiUrl() for BASE_PATH compatibility."
        assert False, error_msg
    
    print("✓ All fetch() calls properly use apiUrl() helper")
    assert True


def test_api_url_helper_exists():
    """Verify that the apiUrl() helper function exists in the template."""
    
    with open('templates/index.html', 'r') as f:
        content = f.read()
    
    # Check for apiUrl function definition
    assert 'function apiUrl(path)' in content, "apiUrl() helper function not found"
    assert 'return BASE_PATH + path' in content, "apiUrl() doesn't return BASE_PATH + path"
    
    print("✓ apiUrl() helper function exists and is properly defined")
    assert True


def test_fetch_calls_count():
    """Count and verify expected number of apiUrl() wrapped fetch calls."""
    
    with open('templates/index.html', 'r') as f:
        content = f.read()
    
    # Pattern to find fetch calls using apiUrl helper
    api_url_fetch_pattern = r'fetch\s*\(\s*apiUrl\s*\('
    
    matches = list(re.finditer(api_url_fetch_pattern, content))
    count = len(matches)
    
    # We expect at least 13 fetch calls to use apiUrl (based on our fixes)
    # This includes:
    # 1. /api/files (loadFiles)
    # 2. /api/file/.../tags GET (viewTags)
    # 3. /api/file/.../tags POST (saveTags)
    # 4. /api/jobs/{jobId} GET (checkJobStatus)
    # 5. /api/jobs/.../cancel POST (cancelJob)
    # 6. /api/jobs/{activeJobId} GET (resumeActiveJob)
    # 7. /api/process-file/... (processFile)
    # 8. /api/rename-file/... (renameFile)
    # 9. /api/normalize-file/... (normalizeFile)
    # 10. /api/delete-file/... (deleteFile - first instance)
    # 11. /api/processing-history (loadHistory)
    # 12. /api/logs (loadLogs)
    # 13. /api/delete-file/... (deleteFile - second instance in batch delete)
    
    assert count >= 13, f"Expected at least 13 apiUrl() wrapped fetch calls, found {count}"
    
    print(f"✓ Found {count} fetch() calls properly wrapped with apiUrl()")
    assert True


if __name__ == '__main__':
    print("Testing BASE_PATH compatibility for fetch() calls...")
    print()
    
    test_api_url_helper_exists()
    test_fetch_calls_use_api_url()
    test_fetch_calls_count()
    
    print()
    print("All tests passed! ✓")
