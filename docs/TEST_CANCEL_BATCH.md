# Test Plan: Cancel Button for Batch Processing

## Manual Test Checklist

### Setup
- [ ] Build Docker image with the new changes
- [ ] Start the container
- [ ] Open the web interface in a browser
- [ ] Have at least 10-20 comic files in the watched directory

### Test 1: Basic Cancel Functionality
**Expected Result**: Job cancels and UI updates correctly

Steps:
1. Click "Process" dropdown → "Process All Files" (or "Process Unmarked")
2. Wait for progress modal to appear
3. **Verify**: Red "Cancel" button is visible in modal footer
4. Wait until 2-3 files are processed
5. Click the "Cancel" button
6. **Verify**: Confirmation dialog appears asking "Are you sure you want to cancel?"
7. Click "OK" in confirmation dialog
8. **Verify**: 
   - Modal closes
   - Warning message appears: "Job was cancelled"
   - Console shows: `[CANCEL] Job <id> cancelled successfully`
9. Refresh the file list
10. **Verify**: Some files show as processed (✅), others remain unmarked (⚠️)

### Test 2: Cancel Confirmation Dialog
**Expected Result**: Cancelling the cancel keeps job running

Steps:
1. Start a batch job (Process All Files)
2. Click "Cancel" button
3. Click "Cancel" in the confirmation dialog (decline cancellation)
4. **Verify**: 
   - Dialog closes
   - Progress modal remains open
   - Job continues processing
   - Files continue being processed

### Test 3: Button State Management
**Expected Result**: Cancel button only visible during processing

Steps:
1. Start a small batch job (3-5 files)
2. **Verify**: Cancel button is visible, Close button is hidden
3. Let the job complete naturally (don't cancel)
4. **Verify**: 
   - Cancel button becomes hidden
   - Close button appears
   - Success message appears

### Test 4: Page Refresh During Processing
**Expected Result**: Cancel button works after page refresh

Steps:
1. Start a large batch job
2. Wait for 2-3 files to process
3. Refresh the browser page (F5)
4. **Verify**: 
   - Progress modal reappears automatically
   - Cancel button is visible
   - Progress continues from where it left off
5. Click Cancel
6. **Verify**: Job cancels successfully

### Test 5: Minimize and Restore
**Expected Result**: Cancel button accessible after minimize/restore

Steps:
1. Start a batch job
2. Click the minimize button (−) on the progress modal
3. **Verify**: Modal minimizes to indicator button in header
4. Click the indicator button to restore modal
5. **Verify**: Cancel button is still visible
6. Click Cancel
7. **Verify**: Job cancels successfully

### Test 6: Error Handling
**Expected Result**: Appropriate errors shown for edge cases

#### Test 6a: Cancel non-existent job
Steps:
1. Start a batch job
2. Let it complete
3. Open browser console
4. Run: `cancelCurrentJob()` (after job is complete)
5. **Verify**: Shows error or warning (job already complete)

#### Test 6b: Network error during cancel
This is hard to test manually, but should be tested in development with network throttling.

### Test 7: Multiple Cancel Attempts
**Expected Result**: Multiple clicks handled gracefully

Steps:
1. Start a batch job
2. Click Cancel button rapidly 5 times
3. Click OK in the first confirmation dialog that appears
4. **Verify**: 
   - Only one cancellation request is sent
   - No error messages appear
   - Job cancels normally

### Test 8: Different Batch Operations
**Expected Result**: Cancel works for all batch operation types

Repeat Test 1 for each operation:
- [ ] Process All Files
- [ ] Process Selected Files (select 5-10 files first)
- [ ] Process Unmarked Files
- [ ] Rename All Files
- [ ] Rename Selected Files
- [ ] Rename Unmarked Files
- [ ] Normalize All Files
- [ ] Normalize Selected Files
- [ ] Normalize Unmarked Files

### Browser Compatibility
Test on multiple browsers:
- [ ] Chrome/Chromium
- [ ] Firefox
- [ ] Safari (if available)
- [ ] Edge
- [ ] Mobile browsers (Chrome, Safari)

### Visual Verification

Expected visual appearance:
- Cancel button should be **red** (btn-danger class)
- Cancel button should be aligned next to Close button
- Modal footer should have proper spacing
- Button text should be clearly readable
- No layout shifts when buttons show/hide

## Expected Console Output

When cancelling a job, you should see:
```
[CANCEL] Cancelling job <uuid>...
[CANCEL] Job <uuid> cancelled successfully
[JOB <uuid>] Job was cancelled
```

## Known Limitations

1. **Running Tasks**: Tasks already running in the thread pool may complete even after cancellation
2. **File Processing**: Files being actively processed when cancel is clicked will likely complete
3. **Cleanup**: The job is marked as cancelled but completed files remain marked as processed

## Troubleshooting

### Cancel Button Not Appearing
- Check browser console for JavaScript errors
- Verify HTML changes were properly deployed
- Check that progressCancelBtn element exists in DOM

### Cancel Not Working
- Check network tab for failed API requests
- Verify backend endpoint `/api/jobs/<id>/cancel` is accessible
- Check server logs for errors

### Modal Not Closing After Cancel
- Check if cancellation was successful in network tab
- Verify polling loop detects cancelled status
- Check for JavaScript errors in console
