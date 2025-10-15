# Fix Summary: Website Stuck After Watcher Runs

## Problem
The website could become stuck or unavailable after the watcher service encountered issues or exited unexpectedly. This affected the user experience as the web interface would become inaccessible even though it should continue operating independently.

## Root Cause
The `start.sh` script used a simple `wait` command to manage both background processes:

```bash
# BEFORE (Issue)
python /app/watcher.py &
WATCHER_PID=$!

gunicorn ... web_app:app &
WEB_PID=$!

wait $WATCHER_PID $WEB_PID  # ❌ Waits for both, but no recovery if one fails
```

Issues with this approach:
1. **No independent monitoring**: Both processes treated equally despite web server being the primary service
2. **No visibility**: When watcher exits, there's no logging or indication of what happened
3. **Poor resilience**: If watcher crashes early or repeatedly, container behavior is unclear
4. **Container lifecycle unclear**: Not obvious which service drives the container lifetime

## Solution
Implemented an active monitoring loop that treats the web server as the primary service:

```bash
# AFTER (Fix)
python /app/watcher.py &
WATCHER_PID=$!

gunicorn ... web_app:app &
WEB_PID=$!

# Function to check if a process is running
is_running() {
    kill -0 "$1" 2>/dev/null
}

# Monitor both processes, keep container alive as long as web server runs
WATCHER_WARNED=0
while is_running $WEB_PID; do
    # Check if watcher died and log it once, but don't exit
    if ! is_running $WATCHER_PID && [ $WATCHER_WARNED -eq 0 ]; then
        echo "Warning: Watcher process (PID $WATCHER_PID) has exited, but web server is still running"
        WATCHER_WARNED=1
    fi
    sleep 5
done

echo "Web server has exited, shutting down container"
exit 1
```

This ensures:
1. ✅ **Web server stays alive**: Container continues as long as web server is running
2. ✅ **Clear logging**: When watcher exits, a warning is logged (once)
3. ✅ **Primary service priority**: Container lifecycle tied to web server, not watcher
4. ✅ **Better observability**: Easy to see which process caused container exit
5. ✅ **No restart loops**: Explicit decision not to auto-restart to avoid masking issues

## Files Changed
1. **start.sh** (18 lines changed)
   - Replaced simple `wait` with monitoring loop
   - Added `is_running()` helper function
   - Added process health checking with 5-second intervals
   - Added single-warning logic for watcher exit

## Impact
✅ **Low Risk Change**
- Changes isolated to process management in start.sh
- No changes to application logic
- Backwards compatible
- No breaking changes to APIs or data formats

✅ **High Value**
- Significantly improves container stability
- Better user experience - web interface stays available
- Clear logging helps with troubleshooting
- Professional, production-ready behavior

## Testing Recommendations

### Manual Testing in Docker
1. Build and run the container normally
2. Access the web interface at http://localhost:5000
3. Verify both services are running: `docker exec <container> ps aux | grep -E 'python|gunicorn'`
4. Simulate watcher crash: `docker exec <container> pkill -f watcher.py`
5. **Expected:** Web interface still accessible, logs show watcher warning
6. Verify container is still running: `docker ps`

### What to Verify
- ✅ Web interface remains accessible after watcher exits
- ✅ Warning message appears in container logs when watcher exits
- ✅ Warning appears only once (not repeatedly)
- ✅ Container stays running as long as web server is healthy
- ✅ Container exits when web server stops
- ✅ Both services start normally on container startup

## Technical Details

### Process Monitoring Strategy
The new approach uses the `kill -0 $PID` signal to check if a process exists without actually killing it:
- Returns 0 if process exists
- Returns 1 if process doesn't exist
- Non-intrusive health check

### Container Lifecycle
```
Container Start
    ↓
Start Watcher (background)
    ↓
Start Web Server (background)
    ↓
Monitor Loop (every 5s)
    ├─ Is Web Server running? → YES → Continue monitoring
    │                        → NO  → Exit container
    ↓
Log watcher exit if detected (once)
    ↓
Continue monitoring web server
```

### Design Decisions
1. **Why not restart watcher automatically?**
   - Avoids restart loops if watcher has persistent issues
   - Makes problems visible instead of hiding them
   - Allows manual intervention and debugging

2. **Why prioritize web server over watcher?**
   - Web interface is the primary user-facing service
   - Users can manually trigger processing from web UI
   - Watcher is supplementary automation

3. **Why 5-second monitoring interval?**
   - Balance between responsiveness and CPU usage
   - Fast enough to detect issues quickly
   - Doesn't create excessive log noise

## Related Code
- `start.sh` - Process management and monitoring
- `entrypoint.sh` - Container initialization and user setup
- `watcher.py` - File system monitoring service (infinite loop)
- `web_app.py` - Flask web application
- Docker container orchestration

## Future Enhancements
Consider:
- Health check endpoints for both services
- Graceful shutdown handling with SIGTERM
- Optional watcher restart with backoff
- Prometheus metrics for process health
- External health check probe for Kubernetes/Docker health checks
