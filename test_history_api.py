#!/usr/bin/env python
"""Test processing history API endpoint"""
import sys
import os
import tempfile
import json

# Create temp directory first
temp_dir = tempfile.mkdtemp()
print(f"Using temp directory: {temp_dir}")

# Add src to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

# Set up environment before importing modules
os.environ['WATCHED_DIR'] = temp_dir

# Monkey-patch the CONFIG_DIR before importing modules
import unified_store
unified_store.CONFIG_DIR = temp_dir
unified_store.STORE_DIR = os.path.join(temp_dir, 'store')
unified_store.DB_PATH = os.path.join(unified_store.STORE_DIR, 'comicmaintainer.db')

# Now import web_app and other modules
from web_app import app

def test_history_api():
    """Test the /api/processing-history endpoint"""
    print("Testing processing history API endpoint...")
    
    # Initialize database
    unified_store.init_db()
    print("✓ Database initialized")
    
    # Add some test history entries
    unified_store.add_processing_history(
        filepath='/test/file1.cbz',
        operation_type='process',
        before_filename='old_file1.cbz',
        after_filename='new_file1.cbz',
        before_title='Chapter 1',
        after_title='Chapter 0001'
    )
    
    unified_store.add_processing_history(
        filepath='/test/file2.cbz',
        operation_type='rename',
        before_filename='file2.cbz',
        after_filename='renamed_file2.cbz'
    )
    print("✓ Added test history entries")
    
    # Test API endpoint
    with app.test_client() as client:
        # Test basic request
        response = client.get('/api/processing-history')
        assert response.status_code == 200, f"Expected 200, got {response.status_code}"
        print("✓ API endpoint responds with 200")
        
        data = json.loads(response.data)
        assert 'history' in data, "Response missing 'history' field"
        assert 'total' in data, "Response missing 'total' field"
        assert 'limit' in data, "Response missing 'limit' field"
        assert 'offset' in data, "Response missing 'offset' field"
        print("✓ API response has correct structure")
        
        assert data['total'] == 2, f"Expected 2 total entries, got {data['total']}"
        assert len(data['history']) == 2, f"Expected 2 history entries, got {len(data['history'])}"
        print("✓ API returns correct number of entries")
        
        # Test pagination
        response = client.get('/api/processing-history?limit=1&offset=0')
        assert response.status_code == 200
        data = json.loads(response.data)
        assert len(data['history']) == 1, "Expected 1 entry with limit=1"
        assert data['limit'] == 1
        assert data['offset'] == 0
        print("✓ API pagination works")
        
        # Test history entry structure
        entry = data['history'][0]
        required_fields = ['id', 'filepath', 'timestamp', 'operation_type',
                          'before_filename', 'after_filename', 'before_title', 'after_title',
                          'before_series', 'after_series', 'before_issue', 'after_issue']
        for field in required_fields:
            assert field in entry, f"History entry missing field: {field}"
        print("✓ History entry has all required fields")
    
    print("\n✅ All API tests passed!")
    
    # Cleanup
    import shutil
    shutil.rmtree(temp_dir)
    print("✓ Cleanup completed")

if __name__ == '__main__':
    try:
        test_history_api()
        sys.exit(0)
    except Exception as e:
        print(f"\n❌ Test failed: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)
