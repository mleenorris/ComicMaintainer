# Cancel Button Flow Diagram

## Visual State Flow

```
┌─────────────────────────────────────────────────────────────┐
│                     User Starts Batch Job                    │
│              (Process/Rename/Normalize Files)                │
└───────────────────────────┬─────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│                    Progress Modal Opens                      │
│  ┌──────────────────────────────────────────────────────┐  │
│  │ Processing Files...                          [-]     │  │
│  ├──────────────────────────────────────────────────────┤  │
│  │ Progress Bar: ████████░░░░░░░░░░░░░░ 40%           │  │
│  │ 10 / 25 files                              40%       │  │
│  │                                                       │  │
│  │ ✅ file1.cbz                                         │  │
│  │ ✅ file2.cbz                                         │  │
│  │ ✅ file3.cbz                                         │  │
│  │ ...                                                   │  │
│  ├──────────────────────────────────────────────────────┤  │
│  │              [Cancel]                                 │  │ ← Red Button Visible
│  └──────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
                            │
                            │ User clicks Cancel
                            ▼
┌─────────────────────────────────────────────────────────────┐
│                  Confirmation Dialog                         │
│  ┌──────────────────────────────────────────────────────┐  │
│  │  Are you sure you want to cancel the current         │  │
│  │  batch processing job?                                │  │
│  │                                                       │  │
│  │                     [Cancel] [OK]                     │  │
│  └──────────────────────────────────────────────────────┘  │
└─────────────┬──────────────────────────┬────────────────────┘
              │                          │
    User clicks Cancel       User clicks OK
    (Don't cancel)            (Do cancel)
              │                          │
              ▼                          ▼
┌──────────────────────┐      ┌──────────────────────┐
│  Job Continues       │      │ POST /api/jobs/      │
│  Processing          │      │     {id}/cancel      │
│                      │      └──────────┬───────────┘
│  [Cancel] still      │                 │
│  visible             │                 ▼
└──────────────────────┘      ┌──────────────────────┐
                              │ Backend Marks Job    │
                              │ as CANCELLED         │
                              └──────────┬───────────┘
                                         │
                                         ▼
                              ┌──────────────────────┐
                              │ Polling Detects      │
                              │ Cancelled Status     │
                              └──────────┬───────────┘
                                         │
                                         ▼
┌─────────────────────────────────────────────────────────────┐
│                     Modal Closes                             │
│                                                              │
│  ⚠️ Job was cancelled                                       │
│                                                              │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│               File List Refreshes                            │
│  - Processed files remain marked (✅)                       │
│  - Unprocessed files remain unmarked (⚠️)                  │
└─────────────────────────────────────────────────────────────┘
```

## State Transitions

### Button Visibility States

```
┌─────────────────────────────────────────────────────────┐
│ Job State      │ Cancel Button │ Close Button │ Modal   │
├────────────────┼───────────────┼──────────────┼─────────┤
│ Starting       │    Hidden     │    Hidden    │  Open   │
│ Processing     │    VISIBLE    │    Hidden    │  Open   │
│ Cancelling     │    VISIBLE*   │    Hidden    │  Open   │
│ Completed      │    Hidden     │   VISIBLE    │  Open   │
│ Failed         │    Hidden     │    Hidden    │ Closed  │
│ Cancelled      │    Hidden     │    Hidden    │ Closed  │
└────────────────┴───────────────┴──────────────┴─────────┘

* Button stays visible briefly while cancellation request is in flight
```

## Code Flow Diagram

```
┌────────────────────────────────────────────────────────────┐
│                   JavaScript Functions                      │
└────────────────────────────────────────────────────────────┘

User clicks "Process All Files"
         │
         ▼
  processAllFilesAsync()
         │
         ├──> POST /api/jobs/process-all
         │    (Creates job, returns job_id)
         │
         ├──> showProgressModal(title)
         │    │
         │    ├──> Sets: progressCancelBtn.display = 'inline-block'
         │    └──> Sets: progressCloseBtn.display = 'none'
         │
         └──> pollJobStatus(jobId, title)
              │
              ├──> Sets: currentJobId = jobId
              ├──> Sets: hasActiveJob = true
              │
              └──> [Polling Loop] ─────┐
                                       │
User clicks Cancel                     │
         │                             │
         ▼                             │
  cancelCurrentJob()                   │
         │                             │
         ├──> Confirms with user       │
         │                             │
         ├──> POST /api/jobs/{id}/cancel
         │                             │
         └──> Shows message            │
                                       │
                                       ▼
                        GET /api/jobs/{id}
                                       │
                     ┌─────────────────┴─────────────────┐
                     │                                   │
               status == 'cancelled'            status == 'processing'
                     │                                   │
                     ▼                                   │
          closeProgressModal()                           │
                     │                                   │
          clearActiveJobOnServer()                       │
                     │                                   │
          currentJobId = null                            │
          hasActiveJob = false                           │
                     │                                   │
                     └─────────────> [Exit Loop] <───────┘
```

## API Call Sequence

```
Frontend                         Backend
   │                                │
   │  POST /api/jobs/process-all   │
   ├──────────────────────────────>│
   │                                │ job_manager.create_job()
   │                                │ job_manager.start_job()
   │  ← {job_id, total_items}       │
   │<───────────────────────────────┤
   │                                │
   │  [Start Polling]               │
   │                                │
   │  GET /api/jobs/{id}            │
   ├──────────────────────────────>│
   │  ← {status: 'processing', ...} │
   │<───────────────────────────────┤
   │                                │
   │  [User clicks Cancel]          │
   │                                │
   │  POST /api/jobs/{id}/cancel    │
   ├──────────────────────────────>│
   │                                │ job_manager.cancel_job()
   │                                │ job_store.update_job_status()
   │  ← {success: true}             │
   │<───────────────────────────────┤
   │                                │
   │  [Continue Polling]            │
   │                                │
   │  GET /api/jobs/{id}            │
   ├──────────────────────────────>│
   │  ← {status: 'cancelled', ...}  │
   │<───────────────────────────────┤
   │                                │
   │  [Exit Polling Loop]           │
   │                                │
```

## Component Interaction

```
┌─────────────────────────────────────────────────────────────┐
│                         Browser UI                           │
│                                                              │
│  ┌────────────────────────────────────────────────────┐    │
│  │            Progress Modal                           │    │
│  │  ┌──────────────────────────────────────────────┐  │    │
│  │  │ Title                                 [-]    │  │    │
│  │  ├──────────────────────────────────────────────┤  │    │
│  │  │ Progress Bar + Details                       │  │    │
│  │  ├──────────────────────────────────────────────┤  │    │
│  │  │ [Cancel] <─── onclick="cancelCurrentJob()"  │  │    │
│  │  └──────────────────────────────────────────────┘  │    │
│  └────────────────────────────────────────────────────┘    │
│                           │                                  │
│                           │ JavaScript                       │
│                           │ cancelCurrentJob()               │
│                           │                                  │
└───────────────────────────┼──────────────────────────────────┘
                            │
                            │ fetch('/api/jobs/{id}/cancel')
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│                       Flask Web App                          │
│                      (src/web_app.py)                        │
│                                                              │
│  @app.route('/api/jobs/<job_id>/cancel', methods=['POST'])  │
│  def cancel_job(job_id):                                    │
│      job_manager.cancel_job(job_id)  ───────┐               │
└────────────────────────────────────────────┼────────────────┘
                                             │
                                             ▼
┌─────────────────────────────────────────────────────────────┐
│                      Job Manager                             │
│                   (src/job_manager.py)                       │
│                                                              │
│  def cancel_job(self, job_id: str) -> bool:                 │
│      job = job_store.get_job(job_id)                        │
│      job_store.update_job_status(job_id, 'cancelled') ──┐   │
│      clear_active_job()                                  │   │
└──────────────────────────────────────────────────────────┼───┘
                                                           │
                                                           ▼
┌─────────────────────────────────────────────────────────────┐
│                       Job Store                              │
│                    (src/job_store.py)                        │
│                                                              │
│  SQLite Database: /Config/jobs.db                           │
│                                                              │
│  UPDATE jobs SET status = 'cancelled',                      │
│                  completed_at = ?                           │
│  WHERE job_id = ?                                           │
└─────────────────────────────────────────────────────────────┘
```

## Error Handling Flow

```
cancelCurrentJob()
       │
       ├──> if (!currentJobId)
       │         └─> Error: "No active job to cancel"
       │
       ├──> if (!confirm())
       │         └─> Return (user cancelled the cancel)
       │
       ├──> fetch('/api/jobs/{id}/cancel')
       │         │
       │         ├──> HTTP 200 OK
       │         │    └─> Success: "Job cancelled"
       │         │
       │         ├──> HTTP 404 Not Found
       │         │    └─> Error: "Job not found"
       │         │
       │         ├──> HTTP 400 Bad Request
       │         │    └─> Error: "Job not found or already completed"
       │         │
       │         └──> Network Error
       │              └─> Error: "Failed to cancel job: <message>"
       │
       └──> catch (error)
                └─> Error: "Failed to cancel job: <message>"
```

## Browser Console Messages

### Successful Cancellation
```
[CANCEL] Cancelling job 550e8400-e29b-41d4-a716-446655440000...
[CANCEL] Job 550e8400-e29b-41d4-a716-446655440000 cancelled successfully
[JOB 550e8400-e29b-41d4-a716-446655440000] Job was cancelled
```

### Failed Cancellation
```
[CANCEL] Cancelling job 550e8400-e29b-41d4-a716-446655440000...
[CANCEL] Error cancelling job 550e8400-e29b-41d4-a716-446655440000: Job not found or already completed
```

## Files Modified

- `templates/index.html`: Frontend implementation
  - Added `currentJobId` global variable
  - Added `cancelCurrentJob()` function
  - Added cancel button to progress modal
  - Updated `showProgressModal()`, `completeProgress()`, `closeProgressModal()`
  - Updated `pollJobStatus()` to track and clear `currentJobId`

## Files Not Modified (Backend Already Supports Cancel)

- `src/job_manager.py`: Already has `cancel_job()` method
- `src/web_app.py`: Already has `/api/jobs/<job_id>/cancel` endpoint
- `src/job_store.py`: Already supports updating job status to 'cancelled'
