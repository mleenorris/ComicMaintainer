# Fix: Job ID Validation to Prevent Spurious Warnings

## Problem Statement

The application logs showed spurious warnings for invalid job_ids:

```
2025-10-24 02:30:23,250 [WEBPAGE] WARNING [JOB process-selected] Status check failed - job not found
2025-10-24 02:30:23,251 [WEBPAGE] WARNING [API] Job process-selected not found
```

### Root Cause

The warnings occurred when invalid job_ids (like "process-selected", which appears to be an endpoint name) were stored in the active_job preferences or passed to the API. This could happen due to:

1. **Stale data** - Old or corrupted data in the preferences database
2. **Client-side bugs** - Frontend code accidentally using endpoint names instead of UUIDs
3. **Race conditions** - Timing issues during job creation/tracking

Valid job_ids should be UUIDs (e.g., `550e8400-e29b-41d4-a716-446655440000`), not endpoint names like "process-selected".

## Solution

Added UUID format validation at three levels to silently reject invalid job_ids without generating warnings:

### 1. Backend API Layer (`src/web_app.py`)

```python
@app.route('/api/jobs/<job_id>', methods=['GET'])
def get_job_status(job_id):
    """API endpoint to get job status"""
    import uuid
    
    # Validate job_id format (should be a UUID)
    try:
        uuid.UUID(job_id)
    except ValueError:
        # Invalid job_id format - likely stale data or incorrect usage
        logging.debug(f"[API] Invalid job_id format: {job_id} (expected UUID)")
        return jsonify({'error': 'Job not found'}), 404
    
    # ... rest of the function
```

**Effect**: Invalid job_ids return 404 with a debug log (not a warning), preventing log spam.

### 2. Job Manager Layer (`src/job_manager.py`)

```python
def get_job_status(self, job_id: str) -> Optional[Dict[str, Any]]:
    """Get job status."""
    # Validate job_id format (should be a UUID)
    try:
        import uuid
        uuid.UUID(job_id)
    except ValueError:
        # Invalid job_id format - likely stale data or incorrect usage
        log_debug("Invalid job_id format (expected UUID)", job_id=job_id)
        return None
    
    # ... rest of the function
```

**Effect**: Invalid job_ids return None with debug logging, preventing warnings from propagating.

### 3. Frontend Layer (`templates/index.html`)

```javascript
async function checkAndResumeActiveJob() {
    const activeJob = await getActiveJobFromServer();
    
    if (!activeJob || !activeJob.job_id) {
        return; // No active job
    }
    
    const activeJobId = activeJob.job_id;
    
    // Validate job_id format (should be a UUID)
    const uuidRegex = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
    if (!uuidRegex.test(activeJobId)) {
        console.warn(`[JOB RESUME] Invalid job_id format: ${activeJobId} (expected UUID) - clearing stale job`);
        await clearActiveJobOnServer();
        return;
    }
    
    // ... rest of the function
}
```

**Effect**: Stale active jobs with invalid formats are automatically cleared on page load.

## Testing

### Unit Tests (`test_job_id_validation.py`)

Tests that verify the validation logic works correctly:

```bash
$ python3 test_job_id_validation.py
============================================================
✓ PASS: Invalid Job ID Format
✓ PASS: Valid Job ID Format  
✓ PASS: Create and Retrieve Job
```

**Key verifications:**
- Invalid formats like "process-selected" are rejected ✓
- Valid UUID formats are accepted ✓
- No warnings generated for invalid formats ✓

### Integration Tests (`test_stale_job_scenario.py`)

Tests that simulate the exact problem scenario:

```bash
$ python3 test_stale_job_scenario.py
============================================================
✓ PASS: Stale Job ID Scenario
✓ PASS: Web API Validation
```

**Key verifications:**
- Storing "process-selected" in preferences ✓
- Querying it produces NO warnings ✓
- Valid UUIDs still produce warnings when not found (expected) ✓
- Stale jobs can be cleared properly ✓

## Expected Behavior After Fix

### Before Fix
```
WARNING [JOB process-selected] Status check failed - job not found
WARNING [API] Job process-selected not found
```

### After Fix
```
DEBUG [API] Invalid job_id format: process-selected (expected UUID)
DEBUG Invalid job_id format (expected UUID), job_id=process-selected
```

**Result**: Spurious warnings eliminated while maintaining proper error reporting for valid job queries.

## Impact

1. **Log Clarity**: Production logs no longer contain spurious warnings for invalid job_ids
2. **User Experience**: No impact - invalid job_ids are still rejected with 404 responses
3. **Debugging**: Debug logs still available for troubleshooting invalid job_id usage
4. **Robustness**: System automatically cleans up stale active jobs with invalid formats

## Future Considerations

1. **Monitoring**: Track frequency of invalid job_id requests to identify client-side bugs
2. **Database Migration**: Consider one-time cleanup of preferences database to remove any stale entries
3. **API Documentation**: Document that job_id must be a valid UUID format
