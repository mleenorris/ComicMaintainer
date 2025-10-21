# Batch Processing Status Update Fix

## Problem

Batch processing would stop updating the status window after a certain number of files were processed. This was caused by an issue in the event broadcasting system where job-specific updates were overwriting each other.

## Root Cause

The `EventBroadcaster` class stored events in a `_last_events` dictionary using only the `event_type` as the key. For `job_updated` events, this meant:

1. **All job updates shared the same dictionary entry**: Every job's progress update used the key `'job_updated'`, causing updates to overwrite each other
2. **Multiple concurrent jobs interfered**: If two jobs ran simultaneously, their status updates would overwrite each other in `_last_events`
3. **New subscribers only saw the most recent update**: When a client disconnected and reconnected (e.g., page refresh), they would only receive the single most recent `job_updated` event from `_last_events`, losing all job-specific context

### Code Example - Before Fix

```python
# Before: All job updates used the same key
self._last_events['job_updated'] = event  # Overwrites previous job updates!

# If Job A broadcasts: processed=5/10
# Then Job B broadcasts: processed=2/10
# _last_events['job_updated'] now contains only Job B's status!
```

## Solution

Changed the key structure for `job_updated` events to include the `job_id`, creating a composite key `(event_type, job_id)`:

```python
# After: Each job has its own key
if event_type == 'job_updated' and 'job_id' in data:
    storage_key = (event_type, data['job_id'])  # Composite key!
else:
    storage_key = event_type

self._last_events[storage_key] = event
```

### Benefits

1. **Job isolation**: Each job maintains its own status in `_last_events`
2. **Multiple concurrent jobs**: Jobs no longer interfere with each other
3. **Better reconnection**: New subscribers receive status for all active jobs
4. **Backward compatibility**: Non-job events continue to use simple string keys

## Changes Made

### 1. `src/event_broadcaster.py`

#### Modified `broadcast()` method:
- Added logic to use composite keys `(event_type, job_id)` for `job_updated` events
- Other event types continue to use simple string keys

#### Updated type hints:
- Changed `_last_events` from `Dict[str, Event]` to `Dict[Any, Event]` to accommodate both string and tuple keys
- Added documentation explaining the dual key structure

#### Updated documentation:
- Added comments explaining when composite keys are used
- Updated `get_last_event()` docstring to note that it doesn't work for job-specific lookups

### 2. `test_job_specific_events.py` (New Test File)

Created comprehensive tests to verify:
- Multiple jobs don't overwrite each other's status
- New subscribers receive status for all active jobs
- Single job updates properly overwrite previous ones (desired behavior)

## Test Results

All tests pass, including:
- `test_progress_callbacks.py`: Existing SSE/job progress tests
- `test_job_specific_events.py`: New tests for job-specific event tracking
- `test_file_store.py`: Existing file store tests
- `test_debug_features.py`: Existing debug feature tests

## Impact

This fix ensures that:
1. **Status windows update correctly**: All job progress events are properly tracked
2. **Multiple jobs work simultaneously**: No interference between concurrent batch operations
3. **Page refreshes don't lose context**: Reconnecting clients get complete job status
4. **Processing continues uninterrupted**: No more stopping after a certain number of files

## Example Scenarios

### Scenario 1: Multiple Concurrent Jobs
```
Job A processes 10 files (job-001)
Job B processes 5 files (job-002)

Before fix:
- _last_events['job_updated'] = Job B status
- Job A's status is lost!

After fix:
- _last_events[('job_updated', 'job-001')] = Job A status
- _last_events[('job_updated', 'job-002')] = Job B status
- Both jobs tracked separately ✓
```

### Scenario 2: Client Reconnection
```
Job processing 100 files
Client refreshes page after 50 files processed

Before fix:
- Client only receives last broadcasted event
- May not match their expected state
- UI shows incorrect progress

After fix:
- Client receives job-specific status: 50/100
- UI shows correct progress immediately ✓
```

### Scenario 3: Single Job Many Updates
```
Processing 20 files with updates after each file

Before fix:
- Each update overwrites the previous in _last_events
- OK for single job (only need latest)

After fix:
- Each update still overwrites the previous
- Uses job-specific key (event_type, job_id)
- Same behavior, better isolation ✓
```

## Migration Notes

This is a **backward-compatible change**:
- Existing non-job events continue to work unchanged
- No API changes required
- No configuration changes needed
- All existing tests pass without modification

## References

- Original issue: "batch processing stops updating the status window after 10 files are processed and never proceeds"
- Event Broadcasting System: `docs/EVENT_BROADCASTING_SYSTEM.md`
- Progress Callbacks: `docs/PROGRESS_CALLBACKS.md`
