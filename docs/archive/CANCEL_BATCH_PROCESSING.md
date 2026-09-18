# Cancel Batch Processing Feature

## Overview

Users can now cancel batch processing jobs that are running in the background. A "Cancel" button has been added to the processing modal that appears when batch operations are in progress.

## User Interface

When a batch processing job is running, the progress modal displays:
- Progress bar showing current status
- File count (e.g., "25 / 100 files")
- Percentage complete
- Scrollable list of processed files with success/error indicators
- **Cancel button** (red, visible during processing)
- Close button (only visible when job is complete)

### Cancel Button Behavior

- **During Processing**: The Cancel button is visible and active
- **Confirmation**: Clicking Cancel shows a confirmation dialog to prevent accidental cancellation
- **After Cancellation**: 
  - The modal closes
  - A warning message displays: "Job was cancelled"
  - Any files already processed remain processed
  - Files not yet processed are skipped
- **On Completion**: The Cancel button is hidden and replaced with a Close button

## Technical Implementation

### Frontend (templates/index.html)

1. **Global State**:
   - `currentJobId`: Tracks the active job ID for cancellation
   - `hasActiveJob`: Boolean flag for preventing accidental page navigation

2. **UI Elements**:
   ```html
   <button class="btn btn-danger" id="progressCancelBtn" 
           onclick="cancelCurrentJob()" style="display: none;">
       Cancel
   </button>
   ```

3. **JavaScript Functions**:
   - `cancelCurrentJob()`: Handles cancel button clicks
     - Shows confirmation dialog
     - Calls `/api/jobs/<job_id>/cancel` endpoint
     - Displays success/error messages
   - `pollJobStatus()`: Updated to track currentJobId
   - `showProgressModal()`: Shows cancel button initially
   - `completeProgress()`: Hides cancel button on completion
   - `closeProgressModal()`: Hides cancel button on close

### Backend (already implemented)

The backend already supported job cancellation:

1. **Job Manager** (`src/job_manager.py`):
   - `cancel_job(job_id)`: Marks job as cancelled in database
   - Sets job status to `JobStatus.CANCELLED`
   - Clears active job from preferences
   - Returns True on success, False if job not found or already completed

2. **Web API** (`src/web_app.py`):
   - `POST /api/jobs/<job_id>/cancel`: Endpoint to cancel a job
   - Returns `{'success': True}` on successful cancellation
   - Returns error message if job not found or already completed

3. **Job Processing** (`src/job_manager.py`):
   - Jobs are processed asynchronously in thread pool
   - Already running tasks may complete even after cancellation
   - New tasks won't start after job is cancelled

## Usage Examples

### Cancelling a Batch Job

1. Start a batch operation (e.g., "Process All Files")
2. Progress modal appears showing:
   - "Processing Files..." title
   - Progress bar and file count
   - **Red "Cancel" button**
3. Click the Cancel button
4. Confirm cancellation in the dialog
5. Modal closes and displays: "Job was cancelled"
6. Refresh the file list to see which files were processed

### Resuming After Page Refresh

If the page is refreshed during batch processing:
- The system checks for active jobs on the server
- If a job is still running, it automatically resumes polling
- If the job was cancelled, a message is displayed
- The cancel button works the same way after resuming

## Error Handling

### Network Errors
- If the cancel request fails due to network issues
- Error message displays: "Failed to cancel job: <error message>"
- Job continues running on the server
- User can try cancelling again

### Job Not Found
- If the job was already cleaned up or deleted
- Error message displays: "Job not found or already completed"
- Modal closes automatically

### Server Errors
- If the server returns an error (500, etc.)
- Error message displays with HTTP status code
- User can retry or refresh the page

## Testing Recommendations

### Manual Testing

1. **Basic Cancel Test**:
   - Start "Process All Files" with a large library
   - Click Cancel after a few seconds
   - Verify modal closes and cancellation message appears
   - Check that some files were processed and others were not

2. **Confirmation Dialog Test**:
   - Start a batch job
   - Click Cancel
   - Click "Cancel" in confirmation dialog (cancel the cancel)
   - Verify job continues running

3. **Complete vs Cancel Test**:
   - Start a batch job with just a few files
   - Let it complete naturally
   - Verify Cancel button is hidden
   - Verify Close button is shown

4. **Page Refresh Test**:
   - Start a batch job
   - Cancel it
   - Refresh the page
   - Verify the cancelled job message appears

### Edge Cases

- **Multiple rapid cancel clicks**: Only first cancel is processed
- **Cancel during network error**: Error handling displays appropriate message
- **Cancel after completion**: Cancel button is already hidden
- **Cancel after failure**: Cancel button is already hidden

## API Reference

### Cancel Job Endpoint

**Endpoint**: `POST /api/jobs/<job_id>/cancel`

**Request**:
```
POST /api/jobs/550e8400-e29b-41d4-a716-446655440000/cancel
```

**Success Response** (200 OK):
```json
{
    "success": true
}
```

**Error Response** (400 Bad Request):
```json
{
    "error": "Job not found or already completed"
}
```

## Future Enhancements

Potential improvements for the cancel functionality:

1. **Graceful Shutdown**: 
   - Allow currently processing files to complete
   - Show "Cancelling..." status while waiting

2. **Partial Results**:
   - Display summary of completed vs cancelled files
   - Option to view list of unprocessed files

3. **Resume After Cancel**:
   - Add option to resume cancelled jobs
   - Only process files that weren't completed

4. **Cancel All**:
   - Button to cancel all running batch jobs
   - Useful if multiple jobs are queued

## Related Files

- `templates/index.html`: Frontend implementation
- `src/job_manager.py`: Job cancellation backend logic
- `src/web_app.py`: Cancel API endpoint
- `src/job_store.py`: Job status persistence
- `docs/FIX_BATCH_JOBS_RETRY_LOGIC.md`: Related batch processing documentation
