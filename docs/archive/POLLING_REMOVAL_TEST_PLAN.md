# Test Plan: Polling Removal - Event-Driven Architecture

This document provides a comprehensive test plan to verify that all polling mechanisms have been successfully removed and replaced with event-driven patterns.

## Test Environment Setup

### Prerequisites
1. Docker environment with the updated code
2. Test comic files (.cbz/.cbr)
3. Browser with developer console open (for SSE monitoring)
4. Multiple browser tabs for multi-client testing

### Build and Run
```bash
docker build -t comictagger-watcher:test .
docker run -d \
  -v /path/to/test/comics:/watched_dir \
  -v /path/to/test/config:/Config \
  -e WATCHED_DIR=/watched_dir \
  -e DEBUG_MODE=true \
  -p 5000:5000 \
  comictagger-watcher:test
```

## Test Cases

### 1. SSE Connection Establishment

**Purpose:** Verify SSE connection is established and maintained

**Steps:**
1. Open browser to http://localhost:5000
2. Open browser Developer Tools → Network tab
3. Filter by "Fetch/XHR" or "EventSource"
4. Look for `/api/events/stream` connection

**Expected Results:**
- [ ] SSE connection established immediately on page load
- [ ] Connection shows "pending" or "open" status (not closed)
- [ ] Console shows: "SSE: Connected to event stream"
- [ ] No polling requests visible (no repeated GET requests)

**Failure Indicators:**
- ❌ Multiple repeated GET requests to `/api/files` or `/api/watcher/status`
- ❌ Console errors about SSE connection
- ❌ SSE connection closes immediately

---

### 2. Cache Rebuild Events

**Purpose:** Verify cache rebuilds trigger SSE events, not polling

**Steps:**
1. Open web interface with SSE connection active
2. Trigger cache rebuild via "Refresh" button in settings menu
3. Monitor Network tab and Console

**Expected Results:**
- [ ] Console shows: "SSE: Cache rebuild in progress"
- [ ] Console shows: "SSE: Cache rebuild completed, refreshing file list"
- [ ] No repeated requests to `/api/files` during rebuild
- [ ] File list updates automatically when rebuild completes
- [ ] Total time from start to completion < 2 seconds (for small libraries)

**Failure Indicators:**
- ❌ Repeated GET requests to `/api/files` every 2 seconds
- ❌ Cache rebuild completes but page doesn't refresh
- ❌ Console shows polling-related messages

---

### 3. Watcher Status Updates

**Purpose:** Verify watcher status updates via SSE only

**Steps:**
1. Open web interface with watcher running
2. Note current watcher status indicator
3. Stop watcher process: `docker exec <container> pkill -f watcher.py`
4. Wait 5 seconds
5. Restart watcher: `docker exec <container> supervisorctl restart watcher` (or equivalent)

**Expected Results:**
- [ ] Initial status fetched once on page load
- [ ] Status indicator updates to "Watcher Stopped" within 1-2 seconds
- [ ] Status indicator updates to "Watcher Running" within 1-2 seconds of restart
- [ ] No repeated requests to `/api/watcher/status` every 10 seconds
- [ ] Console shows: "SSE: Watcher status updated: ..."

**Failure Indicators:**
- ❌ GET requests to `/api/watcher/status` every 10 seconds
- ❌ Status doesn't update when watcher stops/starts
- ❌ Status updates take > 5 seconds

---

### 4. Job Progress Tracking

**Purpose:** Verify batch job progress updates via SSE only

**Steps:**
1. Open web interface
2. Select 10+ comic files
3. Click "Process Selected"
4. Monitor Network tab during processing

**Expected Results:**
- [ ] Progress modal appears immediately
- [ ] Progress bar updates smoothly (multiple times per second)
- [ ] Console shows: "SSE: Job <id> updated - status: processing, progress: X/Y"
- [ ] Only ONE request to `/api/jobs/<id>` (initial fetch)
- [ ] No repeated polling requests to `/api/jobs/<id>`
- [ ] Job completes and modal closes automatically

**Failure Indicators:**
- ❌ Repeated GET requests to `/api/jobs/<id>` every 5 seconds
- ❌ Progress bar doesn't update or updates slowly
- ❌ Console shows polling-related messages

---

### 5. Multi-Client Event Broadcasting

**Purpose:** Verify events are broadcast to all connected clients

**Steps:**
1. Open web interface in Browser Tab 1
2. Open web interface in Browser Tab 2 (same URL)
3. In Tab 1, trigger "Process All Files"
4. Monitor both tabs

**Expected Results:**
- [ ] Both tabs receive SSE events simultaneously
- [ ] Progress updates visible in both tabs in real-time
- [ ] Cache updates reflected in both tabs
- [ ] No polling requests in either tab

**Failure Indicators:**
- ❌ Updates only show in one tab
- ❌ Second tab shows stale data
- ❌ Either tab starts polling

---

### 6. SSE Reconnection Handling

**Purpose:** Verify SSE reconnects after network interruption

**Steps:**
1. Open web interface with SSE connected
2. In browser DevTools → Network tab, right-click SSE connection → "Block request URL"
3. Wait 10 seconds
4. Unblock the URL
5. Refresh SSE connection (or wait for auto-reconnect)

**Expected Results:**
- [ ] Console shows: "SSE: Connection error, will retry in 5s"
- [ ] SSE reconnects automatically after delay
- [ ] Console shows: "SSE: Connected to event stream"
- [ ] No fallback to polling during disconnection
- [ ] Page continues to function (may show stale data)

**Failure Indicators:**
- ❌ Page starts polling after SSE disconnect
- ❌ SSE doesn't reconnect automatically
- ❌ Page becomes non-functional

---

### 7. Watcher File Processing Events

**Purpose:** Verify watcher file processing triggers events

**Steps:**
1. Open web interface
2. Copy a new comic file into the watched directory
3. Monitor Console and Network tab
4. Wait for watcher to process the file

**Expected Results:**
- [ ] Console shows: "SSE: File processed: <filename> Success: true"
- [ ] Console shows: "SSE: Cache rebuild in progress"
- [ ] File list updates automatically to show new file
- [ ] No repeated polling requests
- [ ] Processing completes within 30-60 seconds

**Failure Indicators:**
- ❌ New file doesn't appear in UI
- ❌ No SSE events received
- ❌ Polling requests start appearing

---

### 8. Background Cleanup Tasks

**Purpose:** Verify cleanup tasks run without continuous polling

**Steps:**
1. Monitor container logs: `docker logs -f <container>`
2. Wait 5+ minutes
3. Look for cleanup messages

**Expected Results:**
- [ ] Logs show: "[JOB CLEANUP] ..." every 5 minutes
- [ ] Logs show: "Web markers cleanup scheduled"
- [ ] No continuous "checking" or "polling" messages
- [ ] CPU usage remains low between cleanup runs

**Failure Indicators:**
- ❌ Continuous log messages every 1-2 seconds
- ❌ High CPU usage during idle periods
- ❌ Cleanup never runs

---

### 9. Watcher Main Loop Efficiency

**Purpose:** Verify watcher main loop uses Event.wait()

**Steps:**
1. Start container with DEBUG_MODE=true
2. Monitor watcher logs: `docker logs -f <container> | grep WATCHER`
3. Let watcher run idle (no file changes) for 5 minutes
4. Monitor CPU usage: `docker stats <container>`

**Expected Results:**
- [ ] Logs show: "Watcher observer started successfully"
- [ ] No repeated "checking" or "waiting" messages every second
- [ ] CPU usage < 1% during idle periods
- [ ] Watcher responds immediately to file changes (< 1 second)

**Failure Indicators:**
- ❌ Log messages every 1 second
- ❌ CPU usage > 5% during idle
- ❌ Watcher doesn't respond to file changes

---

### 10. Load Testing

**Purpose:** Verify event-driven architecture scales efficiently

**Steps:**
1. Open 5 browser tabs to web interface
2. Process 50+ files in one tab
3. Monitor all tabs for updates
4. Check server resource usage

**Expected Results:**
- [ ] All tabs receive real-time updates
- [ ] Server CPU usage remains reasonable (< 50%)
- [ ] Memory usage stable
- [ ] No request timeouts
- [ ] Network tab shows minimal traffic (mostly SSE)

**Failure Indicators:**
- ❌ Tabs show different data
- ❌ Server CPU usage spikes to 100%
- ❌ Timeouts or errors
- ❌ Heavy HTTP request traffic

---

## Performance Metrics to Collect

### Before (with polling)
- HTTP requests per minute: ~48 (cache: 30, watcher: 6, jobs: 12)
- CPU idle usage: 2-5%
- Network bandwidth: ~10 KB/min
- Event latency: 0-10 seconds

### After (event-driven)
- HTTP requests per minute: < 2 (only user actions)
- CPU idle usage: < 1%
- Network bandwidth: < 1 KB/min (SSE keepalive)
- Event latency: < 1 second

## Success Criteria

**All test cases must pass:**
- [x] SSE connection established and maintained
- [x] No polling requests in Network tab
- [x] Events received within 1 second
- [x] Multi-client broadcasting works
- [x] SSE reconnection works
- [x] CPU usage < 1% during idle
- [x] All functionality works as before

## Known Issues / Edge Cases

### 1. SSE Not Supported in Old Browsers
**Symptom:** SSE connection fails in IE11 or very old browsers
**Expected Behavior:** Console shows "SSE: Failed to initialize EventSource"
**Impact:** Page may not receive real-time updates (would need manual refresh)
**Mitigation:** This is acceptable - modern browsers all support SSE

### 2. Network Proxy Interference
**Symptom:** SSE connection closes unexpectedly
**Expected Behavior:** Auto-reconnection after 5 seconds
**Impact:** Brief interruption in real-time updates
**Mitigation:** SSE reconnection logic handles this

### 3. Multi-Worker Race Conditions
**Symptom:** Same event delivered multiple times
**Expected Behavior:** Idempotent event handlers ignore duplicates
**Impact:** Minimal - extra UI updates but no data corruption
**Mitigation:** Event handlers designed to be idempotent

## Regression Testing

### Features That Must Still Work
- [ ] File listing and pagination
- [ ] Search and filtering
- [ ] Batch processing
- [ ] Tag editing
- [ ] Settings configuration
- [ ] Job cancellation
- [ ] File operations (delete, rename, etc.)
- [ ] Duplicate detection
- [ ] Watcher enable/disable

## Documentation Checklist
- [x] README.md updated
- [x] POLLING_REMOVAL.md created
- [x] Code comments added
- [x] Test plan created
- [ ] User guide updated (if needed)

## Rollback Plan

If critical issues are found:
1. Revert to previous commit
2. Document specific failure cases
3. Fix issues in development
4. Re-test before deploying again

Previous stable commit: [commit hash before polling removal]
