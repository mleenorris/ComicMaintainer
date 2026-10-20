        // ComicMaintainer SPA — Server-Sent Events connection, server event handlers and watcher status.
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

        // Server-Sent Events connection for real-time updates
        let eventSource = null;
        let eventSourceReconnectTimer = null;
        const EVENT_SOURCE_RECONNECT_DELAY = 5000; // Initial delay: 5 seconds
        const EVENT_SOURCE_MAX_RETRY_DELAY = 60000; // Maximum delay: 60 seconds
        const EVENT_SOURCE_MAX_RETRIES = 20; // Stop trying after 20 consecutive failures
        let eventSourceRetryCount = 0;
        let eventSourceRetryDelay = EVENT_SOURCE_RECONNECT_DELAY;
        
        // Helper function to check if user is still authenticated (supports both JWT and Authelia)
        async function checkAuthenticationStatus() {
            try {
                // First check if using Authelia authentication
                const isAutheliaAuth = isAutheliaAuthenticated();
                
                if (isAutheliaAuth) {
                    // For Authelia, check server auth status to verify session is still valid
                    const response = await fetch(apiUrl('/api/auth/status'), {
                        credentials: 'include' // Important for Authelia cookies
                    });
                    
                    if (response.ok) {
                        const authStatus = await response.json();
                        if (authStatus.autheliaEnabled && authStatus.isAuthenticated) {
                            return true; // Authelia session is still valid
                        }
                    }
                    // If we get here, Authelia session is invalid
                    console.warn('[AUTH] Authelia session expired or invalid');
                    return false;
                } else {
                    // For JWT authentication, check if token exists and is not expired
                    const token = localStorage.getItem('jwt_token');
                    if (!token) {
                        console.warn('[AUTH] No JWT token found');
                        return false;
                    }
                    
                    if (isTokenExpired(token)) {
                        console.warn('[AUTH] JWT token expired');
                        return false;
                    }
                    
                    return true; // JWT token is valid
                }
            } catch (error) {
                console.error('[AUTH] Error checking authentication status:', error);
                return false;
            }
        }
        
        // Initialize SSE connection
        async function initEventSource() {
            // Close existing connection if any
            if (eventSource) {
                eventSource.close();
            }
            
            try {
                // Check if we've exceeded max retry attempts
                if (eventSourceRetryCount >= EVENT_SOURCE_MAX_RETRIES) {
                    console.error('SSE: Maximum retry attempts reached. Please refresh the page or check your connection.');
                    showMessage('Real-time updates unavailable. Please refresh the page.', 'error');
                    return;
                }
                
                // Check authentication status for both JWT and Authelia before connecting
                console.log('SSE: Checking authentication status...');
                const isAuthenticated = await checkAuthenticationStatus();
                if (!isAuthenticated) {
                    console.error('SSE: Authentication check failed - cannot establish SSE connection');
                    console.error('SSE: User will be redirected to login page');
                    redirectToLogin();
                    return;
                }
                console.log('SSE: Authentication check passed');
                
                // Get token for SSE connection
                // For JWT auth, use the stored token
                // For Authelia auth, request a JWT token specifically for SSE
                let token = localStorage.getItem('jwt_token');
                const isAutheliaAuth = isAutheliaAuthenticated();
                
                console.log('SSE: Authentication mode -', isAutheliaAuth ? 'Authelia' : 'JWT');
                console.log('SSE: Existing token -', token ? 'Present' : 'None');
                
                if (isAutheliaAuth && !token) {
                    // For Authelia users, get an SSE token from the backend
                    console.log('SSE: Fetching token for Authelia-authenticated user');
                    try {
                        const response = await fetch(apiUrl('/api/auth/sse-token'), {
                            credentials: 'include',
                            headers: getAuthHeaders()
                        });
                        if (response.ok) {
                            const data = await response.json();
                            token = data.token;
                            console.log('SSE: Received SSE token for Authelia user');
                        } else {
                            console.error('SSE: Failed to get SSE token - status:', response.status);
                            console.error('SSE: This may indicate an authentication issue with Authelia');
                            // Don't retry immediately if we get auth errors
                            if (response.status === 401 || response.status === 403) {
                                console.error('SSE: Authentication failed. SSE connection cannot be established.');
                                throw new Error(`Authentication failed (${response.status}). Please check Authelia configuration.`);
                            }
                        }
                    } catch (error) {
                        console.error('SSE: Error fetching SSE token:', error);
                        // Re-throw to be caught by outer try-catch for proper error handling
                        throw error;
                    }
                }
                
                // Ensure we have a token before attempting connection
                if (!token) {
                    console.error('SSE: No authentication token available. Cannot establish SSE connection.');
                    console.error('SSE: For JWT auth, check if jwt_token exists in localStorage');
                    console.error('SSE: For Authelia auth, check if /api/auth/sse-token endpoint is accessible');
                    throw new Error('No authentication token available for SSE connection');
                }
                
                // EventSource doesn't support custom headers, so we pass the token as a query parameter
                const streamUrl = apiUrl(`/api/events/stream?access_token=${encodeURIComponent(token)}`);
                
                console.log('SSE: Connecting to event stream with authentication token...');
                eventSource = new EventSource(streamUrl);
                
                eventSource.onopen = () => {
                    console.log('SSE: Connected to event stream');
                    
                    // Reset retry counters on successful connection
                    eventSourceRetryCount = 0;
                    eventSourceRetryDelay = EVENT_SOURCE_RECONNECT_DELAY;
                    
                    // When SSE reconnects and we have an active job, poll for its current status
                    // This ensures we don't miss updates that occurred while disconnected
                    if (hasActiveJob && currentJobId) {
                        console.log(`SSE: Reconnected with active job ${currentJobId}, fetching current status...`);
                        pollJobStatusOnce(currentJobId);
                    }
                };
                
                eventSource.onmessage = (event) => {
                    try {
                        const data = JSON.parse(event.data);
                        handleServerEvent(data);
                    } catch (error) {
                        console.error('SSE: Error parsing event data:', error);
                    }
                };
                
                eventSource.onerror = async (error) => {
                    // EventSource automatically attempts to reconnect, but readyState tells us the status
                    // readyState 0 = CONNECTING, 1 = OPEN, 2 = CLOSED
                    const state = eventSource.readyState;
                    
                    if (state === EventSource.CLOSED) {
                        eventSourceRetryCount++;
                        console.warn(`SSE: Connection closed (attempt ${eventSourceRetryCount}/${EVENT_SOURCE_MAX_RETRIES}), will retry in ${eventSourceRetryDelay/1000}s`);
                        
                        // Close the connection to stop automatic retry attempts
                        eventSource.close();
                        
                        // Check authentication status for both JWT and Authelia
                        const isAuthenticated = await checkAuthenticationStatus();
                        if (!isAuthenticated) {
                            console.warn('SSE: Authentication expired during connection');
                            redirectToLogin();
                            return;
                        }
                        
                        // Schedule reconnection with exponential backoff
                        if (eventSourceReconnectTimer) {
                            clearTimeout(eventSourceReconnectTimer);
                        }
                        eventSourceReconnectTimer = setTimeout(() => {
                            initEventSource();
                            // Increase delay for next retry (exponential backoff)
                            eventSourceRetryDelay = Math.min(eventSourceRetryDelay * 1.5, EVENT_SOURCE_MAX_RETRY_DELAY);
                        }, eventSourceRetryDelay);
                    } else if (state === EventSource.CONNECTING) {
                        // EventSource is attempting to reconnect automatically
                        console.log('SSE: Reconnecting...');
                    }
                };
            } catch (error) {
                console.error('SSE: Failed to initialize EventSource:', error);
                eventSourceRetryCount++;
                
                // Retry with exponential backoff
                if (eventSourceRetryCount < EVENT_SOURCE_MAX_RETRIES) {
                    console.log(`SSE: Will retry in ${eventSourceRetryDelay/1000}s (attempt ${eventSourceRetryCount}/${EVENT_SOURCE_MAX_RETRIES})`);
                    if (eventSourceReconnectTimer) {
                        clearTimeout(eventSourceReconnectTimer);
                    }
                    eventSourceReconnectTimer = setTimeout(() => {
                        initEventSource();
                        eventSourceRetryDelay = Math.min(eventSourceRetryDelay * 1.5, EVENT_SOURCE_MAX_RETRY_DELAY);
                    }, eventSourceRetryDelay);
                } else {
                    console.error('SSE: Maximum retry attempts reached');
                    showMessage('Real-time updates unavailable. Please refresh the page.', 'error');
                }
            }
        }
        
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
            
            // Invalidate just the affected folder cache so the next render/expand
            // shows fresh state. Aggregate summaries are refreshed via the
            // debounced file_list_updated handler.
            if (libraryViewMode !== 'series' && data && data.filename) {
                invalidateFolderForFile(data.filename);
            } else if (libraryViewMode === 'series') {
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
            }
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
                if (libraryViewMode === 'series' && currentSeriesDetailId) {
                    loadSeriesIssues(currentSeriesDetailId, true);
                } else {
                    loadActiveLibraryView(1, false);
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
                
                // Allow a brief moment for final updates, then finalize
                setTimeout(async () => {
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
                        if (libraryViewMode === 'series' && currentSeriesDetailId) {
                            await loadSeriesIssues(currentSeriesDetailId, true);
                            scheduleLibraryHealthRefresh();
                        } else {
                            await loadActiveLibraryView(1, true);
                        }
                        // Close modal after refresh completes
                        setTimeout(closeProgressModal, 1000);
                    } else if (status === 'failed') {
                        document.getElementById('progressTitle').textContent = 'Failed - Job processing failed';
                        completeProgress();
                        hasActiveJob = false;
                        currentJobTitle = null;
                        setTimeout(closeProgressModal, 3000);
                    } else if (status === 'cancelled') {
                        document.getElementById('progressTitle').textContent = 'Cancelled - Job was cancelled';
                        completeProgress();
                        hasActiveJob = false;
                        currentJobTitle = null;
                        setTimeout(closeProgressModal, 2000);
                    } else if (status === 'interrupted') {
                        document.getElementById('progressTitle').textContent =
                            `Interrupted - the server restarted after ${processed} of ${total} items`;
                        completeProgress();
                        hasActiveJob = false;
                        currentJobTitle = null;
                        // Left open longer than the other outcomes: the job did not finish and
                        // the user may need to re-run it, so the message should not flash past.
                        setTimeout(closeProgressModal, 5000);
                    }
                }, 500);
            }
        }
        
        // Clean up SSE connection on page unload
        function cleanupEventSource() {
            if (eventSourceReconnectTimer) {
                clearTimeout(eventSourceReconnectTimer);
                eventSourceReconnectTimer = null;
            }
            if (eventSource) {
                eventSource.close();
                eventSource = null;
            }
            // Reset retry counters
            eventSourceRetryCount = 0;
            eventSourceRetryDelay = EVENT_SOURCE_RECONNECT_DELAY;
        }
        
        // Watcher status management - no polling, using SSE events only
        async function updateWatcherStatus() {
            // Fetch initial status on page load only, then rely on SSE for updates
            try {
                const response = await fetch(apiUrl('/api/watcher/status'), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                const data = await response.json();
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
