# Batch Processing Flow with Recovery Mechanisms

## Normal Operation Flow

```
User Initiates Batch Process
         │
         ▼
┌────────────────────┐
│  Create Job        │
│  (Backend)         │
└─────────┬──────────┘
          │
          ▼
┌────────────────────┐
│  Start Processing  │
│  (ThreadPool)      │
└─────────┬──────────┘
          │
          ▼
    ┌─────────┐
    │  Item 1 │◄─────────────┐
    └────┬────┘              │
         │                   │
         ▼                   │
┌────────────────────┐       │
│ Broadcast Progress │       │
│ via SSE            │       │
└─────────┬──────────┘       │
          │                  │
          ▼                  │
┌────────────────────┐       │
│ Frontend Updates   │       │
│ Progress Bar       │       │
└─────────┬──────────┘       │
          │                  │
          ▼                  │
┌────────────────────┐       │
│ Reset Watchdog     │       │
│ Timer              │       │
└─────────┬──────────┘       │
          │                  │
          ▼                  │
    ┌─────────┐              │
    │ Item 2  │              │
    └────┬────┘              │
         │                   │
         └───────────────────┘
              (repeat)
         
         ▼
┌────────────────────┐
│ All Items Done     │
└─────────┬──────────┘
          │
          ▼
┌────────────────────┐
│ Broadcast Complete │
│ (3 retries)        │
└─────────┬──────────┘
          │
          ▼
┌────────────────────┐
│ Frontend Shows     │
│ Completion         │
└─────────┬──────────┘
          │
          ▼
┌────────────────────┐
│ Refresh File List  │
└────────────────────┘
```

## SSE Disconnection Recovery

```
Processing Job
      │
      ▼
┌─────────────┐
│ SSE Active  │
└──────┬──────┘
       │
       ▼
   ❌ Connection Lost!
       │
       ▼
┌──────────────────┐
│ Browser Detects  │
│ Error            │
└──────┬───────────┘
       │
       ▼
┌──────────────────┐
│ Auto-Reconnect   │
│ (5 second delay) │
└──────┬───────────┘
       │
       ▼
┌──────────────────┐
│ onopen Fires     │
│ Detects Active   │
│ Job              │
└──────┬───────────┘
       │
       ▼
┌──────────────────┐
│ Poll Job Status  │
│ GET /api/jobs/ID │
└──────┬───────────┘
       │
       ▼
┌──────────────────┐
│ Update UI with   │
│ Current Progress │
└──────┬───────────┘
       │
       ▼
┌──────────────────┐
│ Resume SSE       │
│ Updates          │
└──────────────────┘
```

## Watchdog Timer Recovery

```
SSE Connected
      │
      ▼
┌──────────────────┐
│ Job Processing   │
│ (updates flowing)│
└──────┬───────────┘
       │
       ▼
┌──────────────────┐
│ Watchdog Timer   │
│ Started          │
│ (15s interval)   │
└──────┬───────────┘
       │
       ▼
  Normal Update?
       │
   Yes │        No
       │         │
       ▼         ▼
 ┌─────────┐  Time Since Last
 │ Reset   │  Update > 60s?
 │ Timer   │      │
 └────┬────┘  Yes │
      │           ▼
      │    ┌──────────────┐
      │    │ ⚠️ Watchdog  │
      │    │   Triggers!  │
      │    └──────┬───────┘
      │           │
      │           ▼
      │    ┌──────────────┐
      │    │ Poll Status  │
      │    │ GET /api/... │
      │    └──────┬───────┘
      │           │
      │           ▼
      │    ┌──────────────┐
      │    │ Update UI    │
      │    └──────┬───────┘
      │           │
      │           ▼
      │    ┌──────────────┐
      │    │ Reset Timer  │
      │    └──────┬───────┘
      │           │
      └───────────┴───────────┐
                              │
                              ▼
                        ┌──────────┐
                        │ Continue │
                        └──────────┘
```

## Three-Layer Defense Visualization

```
╔═══════════════════════════════════════════════════════╗
║                BATCH PROCESSING SYSTEM                 ║
╚═══════════════════════════════════════════════════════╝

Layer 1: SSE Real-Time Updates (Primary)
┌─────────────────────────────────────────────────────┐
│ ✓ Instant updates as files process                  │
│ ✓ Low overhead, efficient                           │
│ ✓ Main communication channel                        │
│ ⚠️ Can fail due to network issues                   │
└─────────────────────────────────────────────────────┘
           │
           │ IF SSE FAILS
           ▼
Layer 2: SSE Reconnection (Automatic Recovery)
┌─────────────────────────────────────────────────────┐
│ ✓ Detects when connection lost                      │
│ ✓ Auto-reconnects after 5 seconds                   │
│ ✓ Polls status to catch up on missed updates        │
│ ✓ Seamless to user                                  │
└─────────────────────────────────────────────────────┘
           │
           │ IF STILL STUCK
           ▼
Layer 3: Watchdog Timer (Last Resort)
┌─────────────────────────────────────────────────────┐
│ ✓ Monitors for 60s of no updates                    │
│ ✓ Checks every 15 seconds                           │
│ ✓ Polls status when triggered                       │
│ ✓ Ensures UI never permanently stuck                │
└─────────────────────────────────────────────────────┘

                    ▼
           ┌─────────────────┐
           │  User sees       │
           │  current status  │
           │  always!         │
           └─────────────────┘
```

## Error Recovery Matrix

| Scenario | SSE Layer | Reconnection | Watchdog | Result |
|----------|-----------|--------------|----------|--------|
| **Normal Operation** | ✅ Working | Dormant | Dormant | Instant updates |
| **Brief Network Glitch** | ❌ Failed | ✅ Reconnects in 5s | Dormant | 5s delay, then recovers |
| **Extended Disconnect** | ❌ Failed | ✅ Reconnects when possible | ⚠️ Polls after 60s | UI updates via polling until SSE returns |
| **SSE Completely Dead** | ❌ Failed | ❌ Can't reconnect | ✅ Polls every 60s | Slower updates but functional |
| **Backend Broadcast Fails** | ⚠️ No events sent | ❌ Nothing to reconnect to | ✅ Detects after 60s | Discovers issue, polls status |

## Timing Diagram

```
Time →

0s    ───●───────────────────────────────────────────────
      Job starts, SSE active

5s    ─────●─────────────────────────────────────────────
      Progress updates flowing normally

15s   ───────────●───────────────────────────────────────
      Watchdog check (no action needed, updates recent)

20s   ─────────────●─────────────────────────────────────
      ❌ SSE connection drops

25s   ───────────────●───────────────────────────────────
      Browser reconnects (5s delay)
      ✓ pollJobStatusOnce() called
      ✓ UI syncs with backend

30s   ─────────────────●─────────────────────────────────
      Watchdog check (updates recent from poll)

40s   ───────────────────────●───────────────────────────
      SSE updates resume normally

45s   ─────────────────────────●─────────────────────────
      Watchdog check (updates recent)

60s   ───────────────────────────────●───────────────────
      Job completes
      ✓ Completion broadcast (retry x3)
      ✓ Frontend shows completion
```

## Component Interaction

```
┌─────────────────────────────────────────────────────────┐
│                       FRONTEND                           │
├─────────────────────────────────────────────────────────┤
│                                                          │
│  ┌──────────────┐    ┌──────────────┐    ┌───────────┐ │
│  │   SSE        │    │  Watchdog    │    │  Poll     │ │
│  │   Handler    │◄───┤  Timer       │───►│  Function │ │
│  └──────┬───────┘    └──────────────┘    └─────┬─────┘ │
│         │                                       │       │
│         │ Events                         Status│       │
│         │                                       │       │
└─────────┼───────────────────────────────────────┼───────┘
          │                                       │
          │ SSE Stream                    REST API
          │                                       │
┌─────────┼───────────────────────────────────────┼───────┐
│         │                                       │       │
│         ▼                                       ▼       │
│  ┌──────────────┐                      ┌──────────────┐│
│  │  Event       │                      │   Job API    ││
│  │  Broadcaster │                      │   Endpoint   ││
│  └──────▲───────┘                      └──────▲───────┘│
│         │                                     │        │
│         │ broadcast()                  query │        │
│         │                                     │        │
│  ┌──────┴─────────────────────────────────┬──┘        │
│  │          Job Manager                   │           │
│  │  ┌──────────────────────────────────┐  │           │
│  │  │  Process Items with ThreadPool   │  │           │
│  │  │  • Broadcast each progress       │  │           │
│  │  │  • Store results in DB           │  │           │
│  │  │  • Retry completion broadcast    │  │           │
│  │  └──────────────────────────────────┘  │           │
│  └─────────────────────────────────────────┘           │
│                                                         │
│                    BACKEND                              │
└─────────────────────────────────────────────────────────┘
```

## State Machine

```
     START
       │
       ▼
 ┌───────────┐
 │  QUEUED   │
 └─────┬─────┘
       │ start_job()
       ▼
 ┌───────────────────┐
 │   PROCESSING      │◄────────┐
 │                   │         │
 │  • SSE Updates    │         │
 │  • Watchdog Active│         │
 │  • Progress Shown │         │
 └─────┬─────┬───────┘         │
       │     │                 │
       │     │ If SSE Fails    │
       │     └─────────────────┘
       │       (Reconnect & Poll)
       │
       │ All items done
       │
       ▼
 ┌───────────┐
 │ COMPLETED │
 │           │
 │ • Final   │
 │   Broadcast│
 │ • Clear   │
 │   Watchdog│
 └───────────┘
```

## Recovery Time Estimates

| Issue Type | Detection Time | Recovery Time | Total Downtime | User Impact |
|------------|----------------|---------------|----------------|-------------|
| **Brief SSE drop** | 0s (immediate) | 5s | 5s | Minimal - one missed update |
| **Extended disconnect** | 15-60s | 5s after detection | 20-65s | Some updates missed, then catches up |
| **SSE permanently dead** | 60s | N/A (watchdog takes over) | 60s initial | Switches to polling mode |
| **Backend broadcast fail** | 60s | Immediate | 60s | Discovers issue, syncs state |

## Configuration Options

```javascript
// Frontend Configuration (templates/index.html)

// Watchdog timeout - how long without updates before polling
const WATCHDOG_TIMEOUT = 60000; // milliseconds (60 seconds)

// Watchdog check interval - how often to check for inactivity
const WATCHDOG_CHECK_INTERVAL = 15000; // milliseconds (15 seconds)

// SSE reconnection delay
const EVENT_SOURCE_RECONNECT_DELAY = 5000; // milliseconds (5 seconds)
```

```python
# Backend Configuration (src/job_manager.py)

# Completion broadcast retry count
COMPLETION_BROADCAST_RETRIES = 3

# Delay between broadcast retries
BROADCAST_RETRY_DELAY = 0.5  # seconds

# SSE heartbeat interval (event_broadcaster.py)
SSE_HEARTBEAT_INTERVAL = 15  # seconds
SSE_TIMEOUT = 30  # seconds
```

## Key Takeaways

1. **Defense in Depth**: Three independent mechanisms ensure updates are delivered
2. **Fail-Safe Design**: If one layer fails, others compensate automatically  
3. **User-Transparent**: All recovery happens automatically without user action
4. **Diagnostics Built-In**: Enhanced logging makes debugging easier
5. **Network Resilient**: Handles various failure modes gracefully

---

This flow ensures that regardless of network conditions or SSE reliability, users always see current batch processing status.
