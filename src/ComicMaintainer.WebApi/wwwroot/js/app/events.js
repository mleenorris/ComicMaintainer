        // ComicMaintainer SPA — Server-Sent Events connection, server event handlers and watcher status.
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

        // Server-Sent Events connection for real-time updates.
        //
        // EventSource cannot send an Authorization header, so every connection
        // is opened with a stream token: a one-minute, single-use token from
        // POST /api/auth/stream-token that the server accepts on the event
        // stream only (never the session token, which proxies would log with
        // the URL). Because the token is single-use, the browser's own
        // reconnect, which reuses the URL, cannot succeed: every error closes
        // the stream and the app reconnects itself, with backoff and a fresh
        // token.
        //
        // Job progress and the library must never silently stop updating:
        //  - the server sends a heartbeat event every 30 seconds, and a stream
        //    that stays quiet for EVENT_SOURCE_STALE_AFTER (dropped by a proxy
        //    or the network without being closed) is replaced;
        //  - while the stream is down, the active job's status is polled;
        //  - after EVENT_SOURCE_MAX_RETRIES failures the user is told, but the
        //    app keeps retrying at the longest delay, and reconnects at once
        //    when the browser comes back online, the tab becomes visible or the
        //    page is restored from the back/forward cache;
        //  - a reconnected stream catches up on the job, the watcher status and
        //    the library, whose events were lost while it was down;
        //  - only a definite answer that the session has ended leaves the app;
        //    an outage is retried like any other failure (checkSessionState).
        let eventSource = null;
        let eventSourceReconnectTimer = null;
        let eventSourceWatchdogTimer = null;
        const EVENT_SOURCE_RECONNECT_DELAY = 5000; // Initial delay: 5 seconds
        const EVENT_SOURCE_MAX_RETRY_DELAY = 60000; // Maximum delay: 60 seconds
        const EVENT_SOURCE_MAX_RETRIES = 20; // Tell the user after 20 consecutive failures (and keep retrying)
        const EVENT_SOURCE_WATCHDOG_INTERVAL = 5000; // How often the watchdog checks the stream and polls a job
        const EVENT_SOURCE_STALE_AFTER = 75000; // Two and a half missed heartbeats (EventsController.HeartbeatInterval)
        const EVENT_SOURCE_UNAVAILABLE_MESSAGE = 'Real-time updates unavailable. Retrying in the background...';
        let eventSourceRetryCount = 0;
        let eventSourceRetryDelay = EVENT_SOURCE_RECONNECT_DELAY;
        // Milliseconds of watchdog ticks since the open stream last said anything.
        let eventSourceQuietTime = 0;
        // Bumped by cleanupEventSource(), so a connection attempt it interrupts does not open a stream afterwards.
        let eventSourceGeneration = 0;
        let eventSourceConnecting = false;
        let eventSourceHasConnected = false;
        let eventSourceUnavailableReported = false;
        // Set when the page is put into the back/forward cache with a stream, so it is reopened on return.
        let eventSourceSuspended = false;

        // What checkSessionState() found out about the user's session.
        const SESSION_ACTIVE = 'active';
        const SESSION_ENDED = 'ended';
        const SESSION_UNKNOWN = 'unknown'; // The server could not say: offline, restarting, or a proxy error

        // Whether the user is still signed in, with JWT or Authelia. Only a definite answer ends the
        // session: a network or server outage is SESSION_UNKNOWN and the caller keeps retrying, so a
        // wifi change or a container restart signs nobody out.
        async function checkSessionState() {
            try {
                return isAutheliaAuthenticated() ? await checkAutheliaSession() : checkJwtSession();
            } catch (error) {
                console.warn('[AUTH] Could not check the session:', error.message);
                return SESSION_UNKNOWN;
            }
        }

        async function checkAutheliaSession() {
            // The server knows whether the Authelia session is still valid. A proxy sends an expired
            // session to the Authelia portal with a redirect, and following that one fails just like
            // being offline does (the portal is another origin), so it is looked at and not followed.
            const response = await apiFetch('/api/auth/status', { auth: false, json: false, redirect: 'manual' });

            if (response.type === 'opaqueredirect' || response.status === 401 || response.status === 403) {
                console.warn('[AUTH] Authelia session expired or invalid');
                return SESSION_ENDED;
            }

            if (!response.ok) {
                console.warn(`[AUTH] Could not check the Authelia session - status: ${response.status}`);
                return SESSION_UNKNOWN;
            }

            const authStatus = await response.json();
            if (authStatus.autheliaEnabled && authStatus.isAuthenticated) {
                return SESSION_ACTIVE;
            }

            console.warn('[AUTH] Authelia session expired or invalid');
            return SESSION_ENDED;
        }

        // A JWT is judged here, without asking the server: it exists and has not expired.
        function checkJwtSession() {
            const token = localStorage.getItem('jwt_token');
            if (!token) {
                console.warn('[AUTH] No JWT token found');
                return SESSION_ENDED;
            }

            if (isTokenExpired(token)) {
                console.warn('[AUTH] JWT token expired');
                return SESSION_ENDED;
            }

            return SESSION_ACTIVE;
        }

        /**
         * A stream token for opening the event stream, or null when the session
         * has ended (the user is then sent to the login page). Throws when no
         * token can be had right now, so the caller retries later.
         */
        async function fetchStreamToken() {
            const isAutheliaAuth = isAutheliaAuthenticated();
            console.log('SSE: Authentication mode -', isAutheliaAuth ? 'Authelia' : 'JWT');
            const response = await apiFetch('/api/auth/stream-token', { method: 'POST', json: false, redirectOn401: false });
            if (!response.ok) {
                console.error('SSE: Failed to get a stream token - status:', response.status);
                if (response.status === 401 && !isAutheliaAuth) {
                    // The stored session token no longer stands for a session: it was signed
                    // out, or the password, role or account changed.
                    console.warn('SSE: The session has ended');
                    return null;
                }
                throw new Error(`No stream token (${response.status})`);
            }
            const data = await response.json();
            if (!data || !data.token) {
                throw new Error('No stream token in the response');
            }
            return data.token;
        }

        // Initialize SSE connection (also used to reconnect: it replaces any existing stream)
        async function initEventSource() {
            if (eventSourceConnecting) {
                return;
            }
            eventSourceConnecting = true;
            const generation = eventSourceGeneration;
            if (eventSourceReconnectTimer) {
                clearTimeout(eventSourceReconnectTimer);
                eventSourceReconnectTimer = null;
            }
            closeEventSource();
            startEventSourceWatchdog();

            try {
                // Check authentication status for both JWT and Authelia before connecting
                console.log('SSE: Checking authentication status...');
                const session = await checkSessionState();
                if (generation !== eventSourceGeneration) return;
                if (session === SESSION_ENDED) {
                    console.error('SSE: Authentication check failed - cannot establish SSE connection');
                    console.error('SSE: User will be redirected to login page');
                    endEventSourceForSignOut();
                    return;
                }
                if (session === SESSION_UNKNOWN) {
                    console.warn('SSE: Could not check the session, the server may be unreachable');
                    scheduleEventSourceReconnect();
                    return;
                }
                console.log('SSE: Authentication check passed');

                const token = await fetchStreamToken();
                if (generation !== eventSourceGeneration) return;
                if (token === null) {
                    endEventSourceForSignOut();
                    return;
                }

                // EventSource doesn't support custom headers, so the stream token goes in the URL
                console.log('SSE: Connecting to event stream with a stream token...');
                const source = new EventSource(apiUrl(`/api/events/stream?access_token=${encodeURIComponent(token)}`));
                eventSource = source;
                eventSourceQuietTime = 0;

                source.onopen = () => handleEventSourceOpen(source);
                source.onmessage = (event) => {
                    markEventSourceActivity(source);
                    try {
                        const data = JSON.parse(event.data);
                        handleServerEvent(data);
                    } catch (error) {
                        console.error('SSE: Error parsing event data:', error);
                    }
                };
                // Sent every 30 seconds: proof that the stream is still alive.
                source.addEventListener('heartbeat', () => markEventSourceActivity(source));
                source.onerror = () => handleEventSourceError(source);
            } catch (error) {
                if (generation !== eventSourceGeneration) return;
                console.error('SSE: Failed to initialize EventSource:', error);
                scheduleEventSourceReconnect();
            } finally {
                // An attempt cleanupEventSource() interrupted no longer owns the flag.
                if (generation === eventSourceGeneration) {
                    eventSourceConnecting = false;
                }
            }
        }

        function handleEventSourceOpen(source) {
            if (source !== eventSource) return;
            console.log('SSE: Connected to event stream');
            const reconnected = eventSourceHasConnected;
            eventSourceHasConnected = true;
            eventSourceQuietTime = 0;

            // Reset retry counters on successful connection
            eventSourceRetryCount = 0;
            eventSourceRetryDelay = EVENT_SOURCE_RECONNECT_DELAY;
            if (eventSourceUnavailableReported) {
                eventSourceUnavailableReported = false;
                showMessage('Real-time updates are back.', 'success');
            }

            // When SSE reconnects and we have an active job, poll for its current status
            // This ensures we don't miss updates that occurred while disconnected
            if (hasActiveJob && currentJobId) {
                console.log(`SSE: Reconnected with active job ${currentJobId}, fetching current status...`);
                pollJobStatusOnce(currentJobId);
            }

            if (reconnected) {
                // Watcher and library events sent while the stream was down are lost: catch up.
                console.log('SSE: Catching up on events missed while disconnected');
                updateWatcherStatus();
                handleFileListUpdatedEvent({});
            }
        }

        async function handleEventSourceError(source) {
            if (source !== eventSource) return;
            // The stream token was single-use, so the browser's own retry, which reuses the URL, cannot
            // succeed: close the stream and reconnect with a fresh token.
            console.warn(`SSE: Connection lost (readyState ${source.readyState})`);
            closeEventSource();
            const generation = eventSourceGeneration;

            // Check authentication status for both JWT and Authelia. Anything but a definite
            // "signed out" (an outage, say) is retried, and the active job is polled meanwhile.
            const session = await checkSessionState();
            if (generation !== eventSourceGeneration || eventSource || eventSourceConnecting) return;
            if (session === SESSION_ENDED) {
                console.warn('SSE: Authentication expired during connection');
                endEventSourceForSignOut();
                return;
            }
            scheduleEventSourceReconnect();
        }

        // Schedules the next attempt with exponential backoff. Retries never stop; after
        // EVENT_SOURCE_MAX_RETRIES consecutive failures the user is told once.
        function scheduleEventSourceReconnect() {
            eventSourceRetryCount++;
            const delay = eventSourceRetryDelay;
            console.warn(`SSE: Will retry in ${delay / 1000}s (attempt ${eventSourceRetryCount})`);
            if (eventSourceRetryCount >= EVENT_SOURCE_MAX_RETRIES && !eventSourceUnavailableReported) {
                eventSourceUnavailableReported = true;
                console.error(`SSE: ${eventSourceRetryCount} consecutive connection failures; still retrying every ${EVENT_SOURCE_MAX_RETRY_DELAY / 1000}s at most`);
                showMessage(EVENT_SOURCE_UNAVAILABLE_MESSAGE, 'error');
            }
            if (eventSourceReconnectTimer) {
                clearTimeout(eventSourceReconnectTimer);
            }
            eventSourceReconnectTimer = setTimeout(() => {
                eventSourceReconnectTimer = null;
                initEventSource();
            }, delay);
            eventSourceRetryDelay = Math.min(eventSourceRetryDelay * 1.5, EVENT_SOURCE_MAX_RETRY_DELAY);
        }

        function markEventSourceActivity(source) {
            if (source === eventSource) {
                eventSourceQuietTime = 0;
            }
        }

        function isEventSourceOpen() {
            return Boolean(eventSource) && eventSource.readyState === EventSource.OPEN;
        }

        function startEventSourceWatchdog() {
            if (!eventSourceWatchdogTimer) {
                eventSourceWatchdogTimer = setInterval(checkEventSource, EVENT_SOURCE_WATCHDOG_INTERVAL);
            }
        }

        // Every EVENT_SOURCE_WATCHDOG_INTERVAL: replace a stream that has gone quiet (open, or stuck
        // connecting), and while there is no open stream keep the active job's progress moving by polling it.
        function checkEventSource() {
            if (eventSource) {
                eventSourceQuietTime += EVENT_SOURCE_WATCHDOG_INTERVAL;
                if (eventSourceQuietTime >= EVENT_SOURCE_STALE_AFTER) {
                    console.warn(`SSE: No heartbeat for ${eventSourceQuietTime / 1000}s, replacing the stream`);
                    initEventSource();
                    return;
                }
            }
            if (!isEventSourceOpen() && hasActiveJob && currentJobId) {
                console.log(`SSE: Stream is down, polling job ${currentJobId}`);
                pollJobStatusOnce(currentJobId);
            }
        }

        // Reconnects at once (resetting the backoff) when something suggests the connection can work
        // again: only while the app wants a stream and has none open.
        function reconnectEventSourceNow(reason) {
            if (!eventSourceWatchdogTimer || eventSourceConnecting || isEventSourceOpen()) {
                return;
            }
            console.log(`SSE: ${reason}, reconnecting now`);
            eventSourceRetryDelay = EVENT_SOURCE_RECONNECT_DELAY;
            initEventSource();
        }

        function closeEventSource() {
            if (eventSource) {
                eventSource.close();
                eventSource = null;
            }
        }

        function endEventSourceForSignOut() {
            cleanupEventSource();
            redirectToLogin();
        }

        window.addEventListener('online', () => reconnectEventSourceNow('Back online'));
        document.addEventListener('visibilitychange', () => {
            if (document.visibilityState === 'visible') {
                reconnectEventSourceNow('Page visible again');
            }
        });
        // Close the stream when the page really goes away. Not in beforeunload: the user can still
        // choose to stay on the page there (jobs.js asks while a job runs).
        window.addEventListener('pagehide', (event) => {
            eventSourceSuspended = Boolean(event.persisted && eventSourceWatchdogTimer);
            cleanupEventSource();
        });
        window.addEventListener('pageshow', (event) => {
            if (event.persisted && eventSourceSuspended) {
                eventSourceSuspended = false;
                initEventSource();
            }
        });

        
        // Handle different types of server events
        function handleServerEvent(data) {
            const eventType = data.type;
            const eventData = data.data;
            
            console.log('SSE Event:', eventType, eventData);
            
            switch(eventType) {
                case 'watcher_status':
                    handleWatcherStatusEvent(eventData);
                    break;
                case 'file_processed':
                    handleFileProcessedEvent(eventData);
                    break;
                case 'job_updated':
                    // Real-time job progress updates via SSE (no polling needed!)
                    handleJobUpdatedEvent(eventData);
                    break;
                case 'file_list_updated':
                    handleFileListUpdatedEvent(eventData);
                    break;
                default:
                    console.log('SSE: Unknown event type:', eventType);
            }
        }
        
        // Handle watcher status events
        function handleWatcherStatusEvent(data) {
            console.log('SSE: Watcher status updated:', data);
            updateWatcherStatusDisplay(data.running, data.enabled);
        }
        
        // Handle file processed events
        function handleFileProcessedEvent(data) {
            console.log('SSE: File processed:', data.filename, 'Success:', data.success);
            
            // Add to progress details if modal is active
            if (hasActiveJob) {
                addProgressDetail(data.filename, data.success, data.error);
            }
            
            // When the user has a series detail view open, just refresh
            // that series's issue list in-place so a rename/normalize
            // updates the visible file names without rebuilding the
            // series library (which would lose scroll position and
            // could close the detail view if the series fell outside
            // the freshly-loaded first page).
            if (currentSeriesDetailId) {
                loadSeriesIssues(currentSeriesDetailId, true);
            }
            // When viewing the series list (no detail open) we intentionally
            // do NOT rebuild the whole library on each per-file event. A
            // background job (e.g. the metadata retag queued after a
            // series-name change) emits one file_processed event per file,
            // and reloading the list for every one of them makes the series
            // list visibly flash/reload after each completion. The set of
            // series shown doesn't change as individual files are retagged;
            // any genuine list change (renames/moves) is coalesced into a
            // single refresh by the debounced file_list_updated handler.
            scheduleLibraryHealthRefresh();
        }
        
        // Handle file list updated events
        function handleFileListUpdatedEvent(data) {
            console.log('SSE: File list updated');

            // Debounce: bursts of file_list_updated events (e.g. when combining folders
            // containing many files) would otherwise trigger a full library reload for
            // each event, hanging the browser. Coalesce into a single refresh.
            if (fileListRefreshTimer) {
                clearTimeout(fileListRefreshTimer);
            }
            fileListRefreshTimer = setTimeout(() => {
                fileListRefreshTimer = null;
                // When a series detail view is open, refresh only that
                // series's issues so renames/moves update the visible file
                // list without rebuilding the series library and kicking
                // the user back to the library screen.
                if (currentSeriesDetailId) {
                    loadSeriesIssues(currentSeriesDetailId, true);
                } else {
                    loadActiveLibraryView();
                }
            }, FILE_LIST_REFRESH_DEBOUNCE_DELAY);

            scheduleLibraryHealthRefresh();
        }
        
        // Handle job update events (real-time via SSE)
        function handleJobUpdatedEvent(data) {
            const jobId = data.job_id;
            const status = data.status;
            const progress = data.progress || {};
            
            console.log(`SSE: Job ${jobId} updated - status: ${status}, progress: ${progress.processed}/${progress.total}`);
            
            // Only handle updates for the current active job
            if (!hasActiveJob || currentJobId !== jobId) {
                console.log(`SSE: Ignoring job update for ${jobId} (not current job)`);
                return;
            }
            
            // Update progress UI in real-time
            const processed = progress.processed || 0;
            const total = progress.total || 0;
            const successCount = progress.success || 0;
            const errorCount = progress.errors || 0;
            
            updateProgress(processed, total, successCount, errorCount);
            
            // Handle job completion
            if (isTerminalJobStatus(status)) {
                console.log(`SSE: Job ${jobId} finished with status: ${status}`);
                scheduleLibraryHealthRefresh(250);
                
                // Allow a brief moment for final updates, then finalize. The
                // finalize (and the close it schedules) is dropped when another
                // job takes the progress modal over and does nothing once this
                // job is no longer the current one, so it never retitles,
                // deactivates or closes the next job's progress.
                jobProgress.scheduleJobFinalize(jobId, async () => {
                    if (status === 'completed') {
                        // Update modal title and call completeProgress to show close button
                        document.getElementById('progressTitle').textContent = `Completed! All ${total} items processed (${successCount} succeeded, ${errorCount} failed)`;
                        completeProgress();
                        hasActiveJob = false;
                        currentJobTitle = null;
                        // Clear selected files and refresh the file list. If
                        // the user is viewing a series detail, refresh only
                        // that series's issues in-place so a rename/normalize
                        // updates the visible file names without rebuilding
                        // the series library (which would lose scroll position
                        // and could close the detail view).
                        selectedFiles.clear();
                        resetSeriesSelectionState();
                        if (currentSeriesDetailId) {
                            await loadSeriesIssues(currentSeriesDetailId, true);
                            scheduleLibraryHealthRefresh();
                        } else {
                            await loadActiveLibraryView({ refresh: true });
                        }
                        // Close modal after refresh completes, unless another job
                        // was started meanwhile
                        jobProgress.scheduleProgressModalClose(jobId, 1000);
                    } else if (status === 'failed') {
                        document.getElementById('progressTitle').textContent = 'Failed - Job processing failed';
                        completeProgress();
                        hasActiveJob = false;
                        currentJobTitle = null;
                        jobProgress.scheduleProgressModalClose(jobId, 3000);
                    } else if (status === 'cancelled') {
                        document.getElementById('progressTitle').textContent = 'Cancelled - Job was cancelled';
                        completeProgress();
                        hasActiveJob = false;
                        currentJobTitle = null;
                        jobProgress.scheduleProgressModalClose(jobId, 2000);
                    } else if (status === 'interrupted') {
                        document.getElementById('progressTitle').textContent =
                            `Interrupted - the server restarted after ${processed} of ${total} items`;
                        completeProgress();
                        hasActiveJob = false;
                        currentJobTitle = null;
                        // Left open longer than the other outcomes: the job did not finish and
                        // the user may need to re-run it, so the message should not flash past.
                        jobProgress.scheduleProgressModalClose(jobId, 5000);
                    }
                }, 500);
            }
        }
        
        // Stops real-time updates: cancels a pending reconnect and the watchdog and closes the stream
        // (when the page goes away, before a service worker update reloads it, on sign-out).
        function cleanupEventSource() {
            eventSourceGeneration++;
            eventSourceConnecting = false;
            if (eventSourceReconnectTimer) {
                clearTimeout(eventSourceReconnectTimer);
                eventSourceReconnectTimer = null;
            }
            if (eventSourceWatchdogTimer) {
                clearInterval(eventSourceWatchdogTimer);
                eventSourceWatchdogTimer = null;
            }
            closeEventSource();
            // Reset retry counters
            eventSourceRetryCount = 0;
            eventSourceRetryDelay = EVENT_SOURCE_RECONNECT_DELAY;
            eventSourceQuietTime = 0;
        }

        
        // Watcher status management: fetched at page load and after the stream reconnects, SSE events in between
        async function updateWatcherStatus() {
            // Fetch the current status; SSE watcher_status events keep it up to date
            try {
                const data = await apiFetch('/api/watcher/status');
                updateWatcherStatusDisplay(data.running, data.enabled);
            } catch (error) {
                console.error('Error fetching initial watcher status:', error);
                updateWatcherStatusDisplay(null, null);
            }
        }
        
        function updateWatcherStatusDisplay(running, enabled) {
            const indicators = [
                document.getElementById('watcherStatus'),
                document.getElementById('watcherStatusMenu'),
                document.getElementById('watcherStatusSidebar')
            ].filter(Boolean);
            if (indicators.length === 0) return;

            // The icon is a CSS status dot coloured by the running / stopped
            // class, so only the text and tooltip change here.
            let text, title, statusClass;
            if (running === null || enabled === null) {
                // Unknown status
                text = 'Status Unknown';
                title = 'Unable to determine watcher status';
                statusClass = null;
            } else if (running) {
                text = 'Watcher Running';
                title = 'File watcher is running and monitoring for changes';
                statusClass = 'running';
            } else {
                text = 'Watcher Stopped';
                title = enabled
                    ? 'File watcher is enabled but not running'
                    : 'File watcher is disabled';
                statusClass = 'stopped';
            }

            indicators.forEach(statusIndicator => {
                statusIndicator.classList.remove('running', 'stopped');
                if (statusClass) statusIndicator.classList.add(statusClass);
                const textElement = statusIndicator.querySelector('.watcher-text');
                if (textElement) textElement.textContent = text;
                statusIndicator.title = title;
            });
        }
