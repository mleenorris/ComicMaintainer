# Batch Job Retry Flow Diagram

## Before Fix: Single Try-Catch (Fragile)

```
┌─────────────────────────────────────────┐
│  pollJobStatus() starts                 │
│  hasActiveJob = true                    │
└────────────────┬────────────────────────┘
                 │
                 v
┌─────────────────────────────────────────┐
│  try {                                  │
│    while (true) {                       │
│      fetch job status                   │
│      if (!response.ok)                  │
│        throw error   ────────┐          │
│      update progress          │          │
│      if (completed) break     │          │
│      wait 500ms               │          │
│    }                          │          │
│  }                            │          │
└───────────────────────────────┼──────────┘
                                │
                                v
                    ┌───────────────────────┐
                    │  catch (error) {      │
                    │    close modal        │
                    │    clear job          │
                    │    hasActiveJob=false │
                    │  }                    │
                    └───────────────────────┘
                                │
                                v
                        ┌───────────────┐
                        │  JOB LOST!    │
                        │  ❌ No retry  │
                        │  ❌ Cleared   │
                        └───────────────┘
```

**Problems:**
- Any error stops everything
- No distinction between error types
- Job cleared even if still running
- No recovery mechanism

---

## After Fix: Retry Logic with Exponential Backoff (Robust)

```
┌──────────────────────────────────────────┐
│  pollJobStatus() starts                  │
│  hasActiveJob = true                     │
│  consecutiveErrors = 0                   │
│  maxConsecutiveErrors = 5                │
└────────────────┬─────────────────────────┘
                 │
                 v
        ┌────────────────────┐
        │  while (true) {    │
        └────────┬───────────┘
                 │
                 v
        ┌────────────────────┐
        │  try {             │
        └────────┬───────────┘
                 │
                 v
        ┌─────────────────────────┐
        │  fetch job status       │
        └────────┬────────────────┘
                 │
                 v
        ┌─────────────────────────┐
        │  response.ok?           │
        └────┬──────────────┬─────┘
             │ No           │ Yes
             v              v
    ┌────────────────┐   ┌──────────────────┐
    │ Status = 404?  │   │ Parse response   │
    └───┬────────┬───┘   │ consecutiveErr=0 │
        │ Yes    │ No    │ Update progress  │
        v        v       └──────────┬───────┘
    ┌──────┐  ┌──────┐            │
    │ Clear│  │>=500?│            v
    │ Exit │  └──┬───┘     ┌──────────────┐
    └──────┘     │ Yes     │ Completed?   │
                 v         └──┬───────┬───┘
        ┌───────────────┐     │ Yes   │ No
        │consecutiveErr++│     v       v
        └───────┬────────┘  ┌──────┐ ┌─────┐
                │           │Success│ │Wait │
                v           │ Exit  │ │500ms│
        ┌────────────────┐  └──────┘ └──┬──┘
        │ >= 5 retries?  │              │
        └───┬────────┬───┘              │
            │ Yes    │ No               │
            v        v                  │
    ┌───────────┐ ┌──────────────┐     │
    │ Give Up   │ │ Backoff Wait │     │
    │ Show Msg  │ │ 500ms*2^(n-1)│     │
    │ Close UI  │ └──────┬───────┘     │
    │ Keep Job! │        │             │
    └───────────┘        └─────────────┘
                                │
                                v
                        ┌───────────────┐
                        │  Continue     │
                        │  (retry)      │
                        └───────────────┘
```

**Improvements:**
- ✅ Retries up to 5 times
- ✅ Distinguishes error types
- ✅ Exponential backoff
- ✅ Job preserved on failure
- ✅ Can resume after refresh

---

## Error Type Handling

```
┌─────────────────────────────────────────┐
│           Error Occurred                │
└────────────────┬────────────────────────┘
                 │
                 v
        ┌────────────────┐
        │  Error Type?   │
        └────┬───────────┘
             │
      ┌──────┴──────┬─────────┬──────────┐
      v             v         v          v
┌──────────┐  ┌─────────┐ ┌──────┐ ┌─────────┐
│ 404      │  │ 5xx     │ │ 4xx  │ │ Network │
│ Not Found│  │ Server  │ │Client│ │ Error   │
└────┬─────┘  └────┬────┘ └───┬──┘ └────┬────┘
     │             │           │         │
     v             v           v         v
┌──────────┐  ┌─────────┐ ┌──────┐ ┌─────────┐
│Permanent │  │Transient│ │Perm. │ │Transient│
│Clear Job │  │ Retry   │ │Clear │ │ Retry   │
│Exit      │  │Backoff  │ │Exit  │ │Backoff  │
└──────────┘  └─────────┘ └──────┘ └─────────┘
```

---

## Exponential Backoff Timeline

```
Attempt 1: Immediate
           ↓
           [Fetch] → Error
           ↓
           Wait 500ms (2^0 * 500ms)
           ↓
Attempt 2: +500ms
           ↓
           [Fetch] → Error
           ↓
           Wait 1000ms (2^1 * 500ms)
           ↓
Attempt 3: +1500ms total
           ↓
           [Fetch] → Error
           ↓
           Wait 2000ms (2^2 * 500ms)
           ↓
Attempt 4: +3500ms total
           ↓
           [Fetch] → Error
           ↓
           Wait 4000ms (2^3 * 500ms)
           ↓
Attempt 5: +7500ms total
           ↓
           [Fetch] → Error
           ↓
           Wait 8000ms (2^4 * 500ms)
           ↓
Attempt 6: +15500ms total
           ↓
           [Fetch] → Error
           ↓
           Give up (~15.5 seconds total)
           Show error message
           Close modal
           KEEP job active on server ✅
```

**Benefits:**
- Gives network/server time to recover
- Avoids overwhelming server with requests
- Total wait time: ~15.5 seconds before giving up
- User can refresh to resume

---

## Job Resumption After Page Refresh

```
┌─────────────────────────────────────────┐
│  User starts batch job                  │
│  hasActiveJob = true                    │
│  Active job saved on server             │
└────────────────┬────────────────────────┘
                 │
                 v
┌─────────────────────────────────────────┐
│  User refreshes page                    │
│  (or network issue causes disconnect)   │
└────────────────┬────────────────────────┘
                 │
                 v
┌─────────────────────────────────────────┐
│  beforeunload event                     │
│  Shows warning if hasActiveJob=true     │
│  User chooses to leave                  │
└────────────────┬────────────────────────┘
                 │
                 v
┌─────────────────────────────────────────┐
│  Page reloads                           │
│  DOMContentLoaded fires                 │
└────────────────┬────────────────────────┘
                 │
                 v
┌─────────────────────────────────────────┐
│  checkAndResumeActiveJob()              │
│  Checks server for active job           │
└────────────────┬────────────────────────┘
                 │
                 v
        ┌────────────────┐
        │ Active job     │
        │ found?         │
        └───┬────────┬───┘
            │ No     │ Yes
            v        v
    ┌───────────┐ ┌──────────────────┐
    │ Normal    │ │ Fetch job status │
    │ page load │ └────────┬─────────┘
    └───────────┘          │
                           v
                  ┌────────────────┐
                  │ Still          │
                  │ processing?    │
                  └───┬────────┬───┘
                      │ Yes    │ No
                      v        v
              ┌──────────┐ ┌──────────┐
              │ Resume   │ │ Show     │
              │ polling  │ │ results  │
              │ Show UI  │ │ Clear job│
              └──────────┘ └──────────┘
```

**Key Points:**
- Job state persists on server (SQLite)
- Works across page refreshes
- Works across browser restarts
- Works across server restarts
- Automatic resumption

---

## State Diagram

```
                ┌──────────────┐
         ┌─────→│   No Job     │←──────┐
         │      │   Active     │       │
         │      └───────┬──────┘       │
         │              │              │
         │              │ Start Job    │
         │              v              │
         │      ┌──────────────┐       │
         │      │  Job Active  │       │
         │      │  Polling...  │       │
         │      └───┬──────────┘       │
         │          │                  │
         │          │ Error            │
         │          v                  │
         │   ┌─────────────┐           │
         │   │  Retrying   │           │
         │   │  (1-5)      │           │
         │   └─┬─────────┬─┘           │
         │     │         │             │
Completed│     │Success  │Max Retries  │404/Cancel
         │     v         v             │
         │   ┌─────┐   ┌─────┐         │
         └───│Done │   │Wait │         │
             └─────┘   │Resume│        │
                       └───┬──┘        │
                           │           │
                           │Refresh    │
                           └───────────┘
```

---

## Comparison: Error Scenarios

### Scenario: Network drops for 3 seconds

**Before Fix:**
```
0s:  Job starts polling
1s:  Network drops
1s:  Error caught → Job cleared → Modal closed ❌
4s:  Network restored (but job already cleared)
```

**After Fix:**
```
0s:  Job starts polling
1s:  Network drops
1s:  Retry 1/5 (wait 500ms)
1.5s: Retry 2/5 (wait 1000ms)
2.5s: Retry 3/5 (wait 2000ms)
4s:  Network restored
4.5s: Retry 3 succeeds → Polling resumes ✅
```

### Scenario: Server restarts during job

**Before Fix:**
```
0s:  Job starts
5s:  Server restarts
5s:  Error → Job cleared ❌
15s: Server back online (but job lost)
```

**After Fix:**
```
0s:  Job starts
5s:  Server restarts
5s:  Retry 1/5... 2/5... 3/5...
15s: Server back online
15s: Retry succeeds → Job continues ✅
```

---

## Summary

The retry logic makes the system **resilient** to:
- ✅ Transient network issues
- ✅ Server restarts
- ✅ Temporary API failures
- ✅ Page refreshes
- ✅ Browser restarts

While still handling permanent errors correctly:
- ✅ 404 (job deleted) → Clear and exit
- ✅ Max retries exceeded → Preserve job for manual resume
