        // BASE_PATH and apiUrl are defined in an inline script in the main HTML file
        // (before this external JS is loaded) to support Flask template variable injection.
        // They are available globally when this script executes.
        
        // Helper function to decode JWT token and extract expiration
        function decodeJwtToken(token) {
            try {
                // JWT format: header.payload.signature
                const parts = token.split('.');
                if (parts.length !== 3) {
                    return null;
                }
                
                // Decode the payload (second part)
                const payload = parts[1];
                // Add padding if needed for base64 decode
                const base64 = payload.replace(/-/g, '+').replace(/_/g, '/');
                const paddedBase64 = base64.padEnd(base64.length + (4 - base64.length % 4) % 4, '=');
                const jsonPayload = decodeURIComponent(atob(paddedBase64).split('').map(function(c) {
                    return '%' + ('00' + c.charCodeAt(0).toString(16)).slice(-2);
                }).join(''));
                
                return JSON.parse(jsonPayload);
            } catch (error) {
                console.error('Error decoding JWT token:', error);
                return null;
            }
        }
        
        // Helper function to check if JWT token is expired
        function isTokenExpired(token) {
            const decoded = decodeJwtToken(token);
            if (!decoded || !decoded.exp) {
                return true; // Treat invalid token as expired
            }
            
            // JWT exp claim is in seconds, Date.now() is in milliseconds
            const expirationTime = decoded.exp * 1000;
            const currentTime = Date.now();
            
            return currentTime >= expirationTime;
        }
        
        // Helper function to redirect to login and clear auth data
        function redirectToLogin() {
            localStorage.removeItem('jwt_token');
            localStorage.removeItem('username');
            localStorage.removeItem('authelia_authenticated');
            window.location.href = '/login.html';
        }
        
        // Authentication check - check server auth status first (supports Authelia)
        // Returns a promise that resolves when authentication is verified or rejects if auth fails
        async function checkAuth() {
            try {
                // First check server auth status (includes Authelia check)
                const response = await fetch(apiUrl('/api/auth/status'), {
                    credentials: 'include' // Important for Authelia cookies
                });
                
                if (response.ok) {
                    const authStatus = await response.json();
                    console.log('[AUTH] Server auth status:', authStatus);
                    
                    // If Authelia is enabled and user is authenticated, no need for JWT token
                    if (authStatus.autheliaEnabled && authStatus.isAuthenticated) {
                        console.log('[AUTH] Authenticated via Authelia as:', authStatus.username);
                        localStorage.setItem('authelia_authenticated', 'true');
                        localStorage.setItem('username', authStatus.username);
                        return true; // User is authenticated via Authelia
                    }
                    
                    // If Authelia is not enabled or user not authenticated, check JWT token
                    const token = localStorage.getItem('jwt_token');
                    if (!token) {
                        console.log('[AUTH] No JWT token and not authenticated via Authelia');
                        redirectToLogin();
                        return false;
                    }
                    
                    // Check if JWT token is expired
                    if (isTokenExpired(token)) {
                        console.log('[AUTH] JWT token has expired, redirecting to login');
                        redirectToLogin();
                        return false;
                    }
                    
                    console.log('[AUTH] Authenticated via JWT token');
                    return true;
                } else {
                    // Fallback to JWT token check if status endpoint fails
                    const token = localStorage.getItem('jwt_token');
                    if (!token) {
                        redirectToLogin();
                        return false;
                    }
                    
                    if (isTokenExpired(token)) {
                        console.log('[AUTH] JWT token has expired, redirecting to login');
                        redirectToLogin();
                        return false;
                    }
                    return true;
                }
            } catch (error) {
                console.error('[AUTH] Error checking auth status:', error);
                // Fallback to JWT token check on error
                const token = localStorage.getItem('jwt_token');
                if (!token) {
                    redirectToLogin();
                    return false;
                }
                
                if (isTokenExpired(token)) {
                    console.log('[AUTH] JWT token has expired, redirecting to login');
                    redirectToLogin();
                    return false;
                }
                return true;
            }
        }
        
        // Helper function to get auth headers
        function getAuthHeaders() {
            const isAutheliaAuth = localStorage.getItem('authelia_authenticated') === 'true';
            
            // If authenticated via Authelia, don't need Authorization header
            // The cookies will be sent automatically with credentials: 'include'
            if (isAutheliaAuth) {
                console.log('[AUTH] Using Authelia authentication (cookies)');
                return {
                    'Content-Type': 'application/json'
                };
            }
            
            const token = localStorage.getItem('jwt_token');
            
            // Proactively check token expiry before making API calls
            if (token && isTokenExpired(token)) {
                console.log('[AUTH] JWT token expired, redirecting to login');
                redirectToLogin();
                return {};
            }
            
            const headers = {
                'Content-Type': 'application/json'
            };
            if (token) {
                headers['Authorization'] = `Bearer ${token}`;
                console.log('[AUTH] Adding authorization header (token present)');
            } else {
                console.warn('[AUTH] No JWT token found in localStorage');
            }
            return headers;
        }
        
        // Helper function to handle auth errors
        function handleAuthError(response) {
            if (response.status === 401) {
                // Token expired or invalid, redirect to login
                console.log('Received 401 Unauthorized, redirecting to login');
                redirectToLogin();
                return true;
            }
            return false;
        }
        
        // Helper function to get fetch options with proper credentials
        function getFetchOptions(method = 'GET', body = null) {
            const options = {
                method: method,
                headers: getAuthHeaders(),
                credentials: 'include' // Always include credentials for Authelia support
            };
            
            if (body) {
                options.body = typeof body === 'string' ? body : JSON.stringify(body);
            }
            
            return options;
        }
        
        // Helper function to encode filepath for RESTful URL
        function encodeFilePathForUrl(filePath) {
            // Convert to base64 URL-safe encoding
            // Use TextEncoder for proper UTF-8 encoding
            const encoder = new TextEncoder();
            const data = encoder.encode(filePath);
            // Convert Uint8Array to regular array and then to base64
            const base64 = btoa(String.fromCharCode(...data));
            return base64.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
        }
        
        // Add logout function
        function logout() {
            if (confirm('Are you sure you want to logout?')) {
                redirectToLogin();
            }
        }
        
        // Constants
        const DEFAULT_PER_PAGE = 100; // Default number of items per page
        const LIBRARY_HEALTH_REFRESH_DELAY = 750;
        const FILE_LIST_REFRESH_DEBOUNCE_DELAY = 500;
        const MAX_PROGRESS_RESULTS = 200;
        
        // Filename truncation constants
        const FILENAME_TRUNCATION_START_LENGTH = 30; // Characters to show at the start
        const FILENAME_TRUNCATION_END_LENGTH = 15; // Characters to show at the end (includes extension)
        const MAX_EXTENSION_LENGTH = 10; // Maximum length to consider as a valid extension
        const TRUNCATION_ELLIPSIS = '...'; // String to indicate truncation
        
        let files = [];
        let selectedFiles = new Set();
        let currentEditFile = null;
        let collapsedDirectories = new Set();
        let searchQuery = '';
        let allFoldersExpanded = false;
        let currentPage = 1;
        let totalPages = 1;
        let totalFiles = 0;
        let unmarkedCount = 0;
        let perPage = DEFAULT_PER_PAGE; // Will be loaded from server preferences
        let filterMode = 'all'; // 'all', 'marked', 'unmarked', 'duplicates'
        let libraryViewMode = 'series';
        // Series layout: null = auto (list on mobile portrait, compact otherwise),
        // or one of 'list', 'grid', 'compact' when explicitly chosen by the user.
        let seriesLayoutPreference = null;
        try {
            const stored = localStorage.getItem('seriesLayout');
            if (stored === 'list' || stored === 'grid' || stored === 'compact') {
                seriesLayoutPreference = stored;
            }
        } catch (_e) { /* localStorage may be unavailable */ }
        let seriesLibrary = [];
        // Incremental library view state
        const FOLDER_PAGE_SIZE = 100;
        const SERIES_PAGE_SIZE = 60;
        let folderList = [];
        let folderTotal = 0;
        let folderOffset = 0;
        let folderLoading = false;
        const folderFiles = new Map(); // path -> { status: 'loading'|'loaded'|'error', files: [] }
        let seriesOffset = 0;
        let seriesTotal = 0;
        let seriesLoading = false;
        let scrollObserver = null;
        let currentSeriesDetailId = null;
        // Title-based identity for the open series detail. The series id is a
        // union-find representative key whose value can change when an external
        // metadata refresh introduces new aliases — when that happens we use
        // these title keys to find the same series under its new id so the user
        // stays inside the detail view instead of getting kicked back to the
        // library list.
        let currentSeriesDetailTitleKeys = null;
        // Snapshot of the open series' lightweight metadata (id, title, aliases,
        // cover, issue_count, total_size, metadata_source). Updated whenever
        // the series is found in the library list or refreshed via the issues
        // endpoint. Used as a fallback when the active filter excludes the
        // series from the library list so the user is not kicked back to the
        // series list while drilled into a series.
        let currentSeriesDetailSeries = null;
        const seriesIssuesCache = new Map();        // seriesId -> { issues: [], total: n }
        const metadataRefreshJobs = new Map();     // jobId -> { seriesIds, label }
        let providerHealthRefreshTimer = null;
        let searchDebounceTimer = null;
        let historyCurrentPage = 1;
        let historyPerPage = 50;
        let historyTotal = 0;
        let libraryHealthRefreshTimer = null;
        let libraryHealthRequestInFlight = false;
        let libraryHealthRefreshPending = false;
        let fileListRefreshTimer = null;
        const MOBILE_LIBRARY_VIEW_BREAKPOINT = 768; // Matches the existing mobile CSS breakpoint.
        const DEFAULT_MOBILE_LIBRARY_VIEW = 'files';
        let currentMobileLibraryView = DEFAULT_MOBILE_LIBRARY_VIEW;
        let progressResults = [];
        let progressResultLookup = new Set();
        let progressResultElements = new Map();
        let duplicateReviewFiles = [];
        let duplicateReviewIndex = 0;
        let combineFolderGroups = [];
        let combineFolderIndex = 0;
        let combineFolderSelectedDestination = null;
        let combineFolderActionInFlight = false;
        let protectedImageUrls = new Map();
        const MAX_PROTECTED_IMAGE_CACHE_ENTRIES = 150;

        // Stable sentinel filepath used to drive the existing toggleDropdown /
        // getDropdownId helpers for the single per-series actions dropdown
        // rendered in the series detail view.
        const SERIES_ACTIONS_DROPDOWN_KEY = '__series_actions__';
        
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
                const isAutheliaAuth = localStorage.getItem('authelia_authenticated') === 'true';
                
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
                const isAutheliaAuth = localStorage.getItem('authelia_authenticated') === 'true';
                
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
                } else {
                    loadActiveLibraryView(1, false);
                }
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
            if (status === 'completed' || status === 'failed' || status === 'cancelled') {
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
                    }
                }, 500);
            }
        }
        
        // Update watcher status display
        function updateWatcherStatusDisplay(running, enabled) {
            const statusIndicator = document.getElementById('watcherStatus');
            if (!statusIndicator) return;
            
            const iconElement = statusIndicator.querySelector('.watcher-icon');
            const textElement = statusIndicator.querySelector('.watcher-text');
            
            // Remove previous status classes
            statusIndicator.classList.remove('running', 'stopped');
            
            if (running) {
                statusIndicator.classList.add('running');
                iconElement.textContent = '✅';
                textElement.textContent = 'Watcher Running';
                statusIndicator.title = 'File watcher is running and monitoring for changes';
            } else {
                statusIndicator.classList.add('stopped');
                iconElement.textContent = '⛔';
                textElement.textContent = 'Watcher Stopped';
                if (enabled) {
                    statusIndicator.title = 'File watcher is enabled but not running';
                } else {
                    statusIndicator.title = 'File watcher is disabled';
                }
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
        
        // API helper functions for server-side preferences
        async function getPreferences() {
            try {
                const response = await fetch(apiUrl('/api/preferences'), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(response)) return {};
                if (!response.ok) {
                    console.error('Failed to get preferences:', response.status);
                    return {};
                }
                return await response.json();
            } catch (error) {
                console.error('Error getting preferences:', error);
                return {};
            }
        }
        
        async function setPreferences(prefs) {
            try {
                const response = await fetch(apiUrl('/api/preferences'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify(prefs)
                });
                if (!response.ok) {
                    console.error('Failed to set preferences:', response.status);
                }
            } catch (error) {
                console.error('Error setting preferences:', error);
            }
        }

        async function loadLibraryHealth() {
            const summary = document.getElementById('libraryHealthSummary');
            const emptyState = document.getElementById('libraryHealthEmpty');
            const grid = document.querySelector('#libraryHealthDashboard .library-health-grid');

            if (libraryHealthRequestInFlight) {
                libraryHealthRefreshPending = true;
                return;
            }

            libraryHealthRequestInFlight = true;

            try {
                const response = await fetch(apiUrl('/api/files/counts'), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }

                const stats = await response.json();
                const total = stats.total || 0;
                const processed = stats.processed || 0;
                const unprocessed = stats.unprocessed || 0;
                const duplicates = stats.duplicates || 0;
                const combinableFolders = stats.combinableFolders || 0;
                document.getElementById('libraryHealthProcessed').textContent = processed.toLocaleString();
                document.getElementById('libraryHealthUnprocessed').textContent = unprocessed.toLocaleString();
                document.getElementById('libraryHealthDuplicates').textContent = duplicates.toLocaleString();
                document.getElementById('libraryHealthCombinableFolders').textContent = combinableFolders.toLocaleString();

                // Toggle empty-state guidance vs the stat grid based on whether
                // the library has any files indexed yet.
                if (emptyState && grid) {
                    if (total > 0) {
                        emptyState.hidden = true;
                        grid.style.display = '';
                    } else {
                        emptyState.hidden = false;
                        grid.style.display = 'none';
                    }
                }

                // Clear the prose summary on success — the stat cards are the
                // canonical source of truth, and the summary just repeated them.
                // (The element is kept for error/status messaging below.)
                if (summary) {
                    summary.textContent = '';
                    summary.classList.remove('library-health-summary--status');
                }

                // Hide (not just disable) zero-count CTAs so the dashboard
                // doesn't show greyed-out "Combine Folders" buttons that don't
                // do anything when tapped.
                const reviewDuplicatesBtn = document.getElementById('reviewDuplicatesBtn');
                if (reviewDuplicatesBtn) {
                    reviewDuplicatesBtn.hidden = duplicates === 0;
                    reviewDuplicatesBtn.disabled = duplicates === 0;
                }
                const combineFoldersBtn = document.getElementById('combineFoldersBtn');
                if (combineFoldersBtn) {
                    combineFoldersBtn.hidden = combinableFolders === 0;
                    combineFoldersBtn.disabled = combinableFolders === 0;
                }
            } catch (error) {
                console.error('Failed to load library health:', error);
                if (summary) {
                    summary.textContent = 'Unable to load library health right now.';
                    summary.classList.add('library-health-summary--status');
                }
            } finally {
                libraryHealthRequestInFlight = false;
                if (libraryHealthRefreshPending) {
                    libraryHealthRefreshPending = false;
                    loadLibraryHealth();
                }
            }
        }

        function refreshLibraryHealth() {
            loadLibraryHealth();
        }

        function isMobileLibraryViewport() {
            return window.matchMedia(`(max-width: ${MOBILE_LIBRARY_VIEW_BREAKPOINT}px)`).matches;
        }

        function initializeMobileLibraryViewToggle() {
            const filesButton = document.getElementById('mobileFilesViewBtn');
            const seriesButton = document.getElementById('mobileSeriesViewBtn');

            if (!filesButton) {
                return;
            }

            if (!filesButton.dataset.bound) {
                filesButton.addEventListener('click', () => setMobileLibraryView('files'));
                filesButton.dataset.bound = 'true';
            }

            if (seriesButton && !seriesButton.dataset.bound) {
                seriesButton.addEventListener('click', () => setMobileLibraryView('series'));
                seriesButton.dataset.bound = 'true';
            }
        }

        function applyMobileLibraryView() {
            const toggle = document.getElementById('mobileLibraryViewToggle');
            const filesView = document.getElementById('libraryFilesView');
            const filesButton = document.getElementById('mobileFilesViewBtn');
            const seriesButton = document.getElementById('mobileSeriesViewBtn');

            if (!toggle || !filesView || !filesButton) {
                // The mobile library view toggle was removed when the dedicated
                // Files view was retired; the underlying library list is still
                // rendered, so there is nothing more to do here.
                return;
            }

            // 'overview' is no longer a separate view; the overview dashboard
            // is now an expand/collapse panel rendered above the file list and
            // controlled independently via toggleLibraryHealthDashboard().
            if (currentMobileLibraryView === 'overview') {
                currentMobileLibraryView = 'files';
            }

            const isMobile = isMobileLibraryViewport();
            const showingFiles = currentMobileLibraryView === 'files';
            const showingSeries = currentMobileLibraryView === 'series';

            toggle.hidden = !isMobile;
            // The file/series list is always rendered now; the overview lives
            // in its own collapsible section above it.
            filesView.hidden = false;

            filesButton.classList.toggle('active', showingFiles);
            filesButton.setAttribute('aria-pressed', showingFiles ? 'true' : 'false');

            if (seriesButton) {
                seriesButton.classList.toggle('active', showingSeries);
                seriesButton.setAttribute('aria-pressed', showingSeries ? 'true' : 'false');
            }

            // Expose the active mobile view on <body> so CSS can react.
            if (document.body) {
                document.body.dataset.mobileView = isMobile ? currentMobileLibraryView : '';
            }
        }

        function setMobileLibraryView(view) {
            if (view !== 'files' && view !== 'series') {
                return;
            }

            const previous = currentMobileLibraryView;
            currentMobileLibraryView = view;
            applyMobileLibraryView();

            // When the user picks Files or Series on mobile, also switch the
            // underlying library view mode so the list actually reflects the
            // choice. Avoid triggering a redundant reload if nothing changed.
            if (view === 'files' && libraryViewMode !== 'files') {
                setLibraryViewMode('files');
            } else if (view === 'series' && libraryViewMode !== 'series') {
                setLibraryViewMode('series');
            }

            const status = document.getElementById('mobileLibraryViewStatus');
            if (status) {
                status.textContent = view === 'series'
                    ? 'Series view selected.'
                    : 'Files view selected.';
            }

            // Restore scroll position when navigating between views so users
            // don't lose their place. Save the outgoing view's scroll first.
            if (previous && previous !== view) {
                saveMobileViewScroll(previous);
                restoreMobileViewScroll(view);
            }
        }

        // Expand/collapse the Library Health overview panel. Replaces the old
        // 3-way Overview/Files/Series mobile tab — the overview is no longer a
        // separate "page" but an inline section that can be hidden when not
        // wanted. Persisted to localStorage so the choice survives reloads.
        const LIBRARY_HEALTH_COLLAPSED_KEY = 'cm.libraryHealth.collapsed';
        function setLibraryHealthCollapsed(collapsed) {
            const dashboard = document.getElementById('libraryHealthDashboard');
            const toggleBtn = document.getElementById('libraryHealthToggle');
            const chevron = toggleBtn ? toggleBtn.querySelector('.library-health-toggle-chevron') : null;
            if (!dashboard) return;
            dashboard.classList.toggle('library-health-dashboard--collapsed', collapsed);
            if (toggleBtn) {
                toggleBtn.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
            }
            if (chevron) {
                chevron.textContent = collapsed ? '▸' : '▾';
            }
            try {
                localStorage.setItem(LIBRARY_HEALTH_COLLAPSED_KEY, collapsed ? '1' : '0');
            } catch (e) { /* localStorage unavailable */ }
        }
        function toggleLibraryHealthDashboard() {
            const dashboard = document.getElementById('libraryHealthDashboard');
            if (!dashboard) return;
            const collapsed = dashboard.classList.contains('library-health-dashboard--collapsed');
            setLibraryHealthCollapsed(!collapsed);
        }
        function initializeLibraryHealthToggle() {
            // Default: collapsed on mobile (small screen), expanded otherwise.
            let stored = null;
            try { stored = localStorage.getItem(LIBRARY_HEALTH_COLLAPSED_KEY); } catch (e) { /* ignore */ }
            const collapsed = stored !== null
                ? stored === '1'
                : isMobileLibraryViewport();
            setLibraryHealthCollapsed(collapsed);
        }

        // Per-view scroll memory so the back-trip from Files → Overview → Files
        // (or Series → Overview → Series) preserves the user's place.
        const mobileViewScrollPositions = {};
        function saveMobileViewScroll(view) {
            mobileViewScrollPositions[view] = window.scrollY || window.pageYOffset || 0;
        }
        function restoreMobileViewScroll(view) {
            const y = mobileViewScrollPositions[view];
            if (typeof y === 'number') {
                // Defer to next frame so the layout swap completes first.
                requestAnimationFrame(() => window.scrollTo(0, y));
            }
        }

        window.addEventListener('resize', applyMobileLibraryView);

        // Debounce library health refreshes so bursts of file events trigger only one refresh.
        // The delay can be overridden for cases like job completion where a faster refresh is useful.
        function scheduleLibraryHealthRefresh(delay = LIBRARY_HEALTH_REFRESH_DELAY) {
            if (libraryHealthRefreshTimer) {
                clearTimeout(libraryHealthRefreshTimer);
            }

            libraryHealthRefreshTimer = setTimeout(() => {
                loadLibraryHealth();
                libraryHealthRefreshTimer = null;
            }, delay);
        }
        
        async function getActiveJobFromServer() {
            try {
                const response = await fetch(apiUrl('/api/active-job'), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(response)) return null;
                if (!response.ok) {
                    console.error('Failed to get active job:', response.status);
                    return null;
                }
                const data = await response.json();
                return data.job_id ? data : null;
            } catch (error) {
                console.error('Error getting active job:', error);
                return null;
            }
        }
        
        // Note: setActiveJobOnServer and clearActiveJobOnServer are no longer needed
        // The server automatically tracks active jobs via GetActiveJob() which finds
        // jobs with status Running or Queued in the in-memory job dictionary
        
        // Debounce function for search input
        function debouncedFilterFiles() {
            // Clear existing timer
            if (searchDebounceTimer) {
                clearTimeout(searchDebounceTimer);
            }
            
            // Set new timer to trigger after 300ms of inactivity
            searchDebounceTimer = setTimeout(() => {
                filterFiles();
            }, 300);
        }
        
        // Theme management
        async function initTheme() {
            // Get saved preference from server
            const prefs = await getPreferences();
            const savedTheme = prefs.theme;
            
            if (savedTheme) {
                // Use saved preference
                setTheme(savedTheme);
            } else {
                // Use system preference
                const prefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
                setTheme(prefersDark ? 'dark' : 'light');
            }
            
            // Listen for system theme changes
            window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', async (e) => {
                // Only auto-switch if user hasn't set a preference
                const prefs = await getPreferences();
                if (!prefs.theme) {
                    setTheme(e.matches ? 'dark' : 'light');
                }
            });
        }
        
        function setTheme(theme) {
            document.documentElement.setAttribute('data-theme', theme);
        }
        
        async function toggleTheme() {
            const currentTheme = document.documentElement.getAttribute('data-theme') || 'light';
            const newTheme = currentTheme === 'light' ? 'dark' : 'light';
            setTheme(newTheme);
            await setPreferences({ theme: newTheme });
        }
        
        // Update theme from settings modal
        async function updateThemeFromSettings() {
            const selectedTheme = document.getElementById('themeSelect').value;
            setTheme(selectedTheme);
            await setPreferences({ theme: selectedTheme });
        }

        // Update default library view from settings modal
        async function updateDefaultLibraryViewFromSettings() {
            const select = document.getElementById('defaultLibraryViewSelect');
            if (!select) return;
            const selectedView = select.value === 'series' ? 'series' : 'files';
            try {
                const response = await fetch(apiUrl('/api/settings/default-library-view'), {
                    method: 'PUT',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ view: selectedView })
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                showMessage(`Default library view set to ${selectedView === 'series' ? 'Series' : 'Files (folder view)'}`, 'success');
            } catch (error) {
                console.error('Failed to update default library view:', error);
                showMessage('Failed to update default library view: ' + error.message, 'error');
            }
        }

        // Update the global default preferred series-name language. Posting
        // an empty string clears the default and reverts to the canonical
        // title for any series that doesn't have its own override.
        async function updateDefaultPreferredLanguageFromSettings() {
            const select = document.getElementById('defaultPreferredLanguageSelect');
            if (!select) return;
            const value = select.value || null;
            try {
                const response = await fetch(apiUrl('/api/settings/default-preferred-language'), {
                    method: 'PUT',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ language: value })
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    let msg = `HTTP error! status: ${response.status}`;
                    try { const j = await response.json(); if (j && j.error) msg = j.error; } catch {}
                    throw new Error(msg);
                }
                showMessage('Default preferred language updated', 'success');
                if (typeof loadSeriesLibrary === 'function') {
                    loadSeriesLibrary(1, true);
                }
            } catch (error) {
                console.error('Failed to update default preferred language:', error);
                showMessage('Failed to update default preferred language: ' + error.message, 'error');
            }
        }
        
        // Note: Watcher is now automatically enabled/disabled based on rename and normalize settings
        // No need for explicit watcher toggle
        
        async function updateWatcherEnableRename() {
            const enabled = document.getElementById('watcherEnableRenameCheckbox').checked;
            
            try {
                const response = await fetch(apiUrl('/api/settings/watcher-enable-rename'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ enabled: enabled })
                });
                
                if (handleAuthError(response)) {
                    document.getElementById('watcherEnableRenameCheckbox').checked = !enabled;
                    return;
                }
                
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                const statusText = enabled ? 'enabled' : 'disabled';
                showMessage(`File rename ${statusText} successfully!`, 'success');
            } catch (error) {
                showMessage('Failed to update rename setting: ' + error.message, 'error');
                document.getElementById('watcherEnableRenameCheckbox').checked = !enabled;
            }
        }
        
        async function updateWatcherEnableNormalize() {
            const enabled = document.getElementById('watcherEnableNormalizeCheckbox').checked;
            
            try {
                const response = await fetch(apiUrl('/api/settings/watcher-enable-normalize'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ enabled: enabled })
                });
                
                if (handleAuthError(response)) {
                    document.getElementById('watcherEnableNormalizeCheckbox').checked = !enabled;
                    return;
                }
                
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                const statusText = enabled ? 'enabled' : 'disabled';
                showMessage(`Metadata normalize ${statusText} successfully!`, 'success');
            } catch (error) {
                showMessage('Failed to update normalize setting: ' + error.message, 'error');
                document.getElementById('watcherEnableNormalizeCheckbox').checked = !enabled;
            }
        }
        
        // Fetch and display version
        async function loadVersion() {
            try {
                const response = await fetch(apiUrl('/api/version'), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                const data = await response.json();
                const versionElement = document.getElementById('appVersion');
                if (versionElement && data.version) {
                    versionElement.textContent = `v${data.version}`;
                }
            } catch (error) {
                console.error('Error loading version:', error);
            }
        }
        
        // Load files on page load
        // PWA Installation support
        let deferredPrompt = null;
        
        // Register service worker for offline support
        if ('serviceWorker' in navigator) {
            window.addEventListener('load', () => {
                navigator.serviceWorker.register(apiUrl('/sw.js'), {
                    scope: apiUrl('/')
                })
                    .then((registration) => {
                        console.log('PWA: Service Worker registered successfully:', registration.scope);

                        // Check for updates periodically
                        setInterval(() => {
                            registration.update();
                        }, 60000); // Check every minute

                        // When a new service worker has fully installed, tell it to
                        // skip waiting so it activates immediately. The actual reload
                        // is triggered by the 'controllerchange' event below, which
                        // is the documented signal that the new SW is now in control.
                        // Reloading earlier (e.g. on 'statechange' => 'installed') is
                        // racy: the reload would be served by the OLD service worker
                        // and the user would still see stale assets, forcing a hard refresh.
                        registration.addEventListener('updatefound', () => {
                            const newWorker = registration.installing;
                            console.log('PWA: New service worker installing...');

                            if (!newWorker) {
                                return;
                            }

                            newWorker.addEventListener('statechange', () => {
                                if (newWorker.state === 'installed' && navigator.serviceWorker.controller) {
                                    console.log('PWA: New version installed, asking it to skipWaiting...');
                                    newWorker.postMessage({ type: 'SKIP_WAITING' });
                                }
                            });
                        });
                    })
                    .catch((error) => {
                        console.log('PWA: Service Worker registration failed:', error);
                    });

                // Reload exactly once the new service worker has taken control,
                // so the reloaded page is served by the NEW worker (and thus the
                // new cached assets). Guarded to avoid reload loops.
                let reloading = false;
                navigator.serviceWorker.addEventListener('controllerchange', () => {
                    if (reloading) {
                        return;
                    }
                    reloading = true;
                    console.log('PWA: New service worker took control, reloading page...');
                    window.location.reload();
                });
            });
        }
        
        // Listen for the beforeinstallprompt event
        window.addEventListener('beforeinstallprompt', (e) => {
            console.log('PWA: beforeinstallprompt event fired');
            // Prevent the default mini-infobar from appearing on mobile
            e.preventDefault();
            // Stash the event so we can trigger it later via our custom install button
            deferredPrompt = e;
            // Show the custom install button
            const installButton = document.getElementById('installAppButton');
            if (installButton) {
                installButton.style.display = 'block';
            }
        });
        
        // Handle successful installation
        window.addEventListener('appinstalled', (evt) => {
            console.log('PWA: App successfully installed');
            // Hide the install button after installation
            const installButton = document.getElementById('installAppButton');
            if (installButton) {
                installButton.style.display = 'none';
            }
            deferredPrompt = null;
        });
        
        // Function to trigger installation
        function installApp() {
            if (!deferredPrompt) {
                showMessage('App is already installed or installation is not available.', 'info');
                return;
            }
            
            // Show the install prompt
            deferredPrompt.prompt();
            
            // Wait for the user to respond to the prompt
            deferredPrompt.userChoice.then((choiceResult) => {
                if (choiceResult.outcome === 'accepted') {
                    console.log('PWA: User accepted the install prompt');
                } else {
                    console.log('PWA: User dismissed the install prompt');
                }
                deferredPrompt = null;
            });
        }
        
        // Initialization function that runs after DOM is ready
        async function initializeApp() {
            // Initialize non-async operations immediately
            initTheme();
            initializeMobileLibraryViewToggle();
            applyMobileLibraryView();
            initializeLibraryHealthToggle();
            
            // Check authentication FIRST before doing anything else
            // This prevents race condition where SSE and API calls start before auth is verified
            const isAuthenticated = await checkAuth();
            if (!isAuthenticated) {
                // Authentication failed, user will be redirected to login
                // Don't initialize anything else
                return;
            }
            
            // Display logged in username
            const username = localStorage.getItem('username');
            if (username) {
                const userInfo = document.getElementById('userInfo');
                const usernameDisplay = document.getElementById('usernameDisplay');
                if (userInfo && usernameDisplay) {
                    usernameDisplay.textContent = username;
                    userInfo.style.display = 'block';
                }
            }
            
            loadVersion();
            
            // Initialize SSE connection for real-time updates
            initEventSource();
            
            // Start all async operations in parallel for faster initial load
            // This prevents sequential API calls from blocking the file list display
            const prefsPromise = getPreferences();
            const jobCheckPromise = checkAndResumeActiveJob();
            const libraryHealthPromise = loadLibraryHealth();
            
            // Start loading files immediately without waiting for preferences or job check
            // The file list will use default values (perPage=DEFAULT_PER_DEFAULT) and update when preferences arrive
            loadActiveLibraryView();
            
            // Fetch initial watcher status in parallel
            updateWatcherStatus();
            
            // Apply preferences when they arrive (don't block file loading)
            prefsPromise.then(prefs => {
                const oldPerPage = perPage;
                const oldLibraryViewMode = libraryViewMode;
                perPage = prefs.perPage || DEFAULT_PER_PAGE;
                
                // Set the per-page selector to the saved value
                const perPageSelect = document.getElementById('perPageSelect');
                if (perPageSelect) {
                    perPageSelect.value = perPage;
                }
                
                // Restore filter mode from preferences
                if (prefs.filterMode) {
                    filterMode = prefs.filterMode;
                    
                    // Update button label
                    const filterLabels = {
                        'all': '📚 All',
                        'unmarked': '⚠️ Unmarked',
                        'marked': '✅ Marked',
                        'duplicates': '🔁 Duplicates',
                        'matched': '🔗 Matched',
                        'unmatched': '❓ Not Matched'
                    };
                    document.getElementById('headerFilterLabel').textContent = filterLabels[filterMode];
                    
                    // Update active class on dropdown items
                    document.querySelectorAll('#headerFilterMenu .header-dropdown-item').forEach(item => {
                        if (item.dataset.filter === filterMode) {
                            item.classList.add('active');
                        } else {
                            item.classList.remove('active');
                        }
                    });
                }

                // The Files library view has been removed; always force series
                // mode regardless of what the server has cached for this user.
                libraryViewMode = 'series';

                updateLibraryViewButtons();
                updateLibraryViewLayout();
                
                // Reload files if perPage changed from default
                if (libraryViewMode !== oldLibraryViewMode || (perPage !== oldPerPage && perPage !== DEFAULT_PER_PAGE)) {
                    loadActiveLibraryView(1);
                }
            });
            
            // These run in the background so the file list can render immediately.
            // Errors are handled here to avoid unhandled promise rejections.
            jobCheckPromise.catch(error => console.error('Failed to check active job:', error));
            libraryHealthPromise.catch(error => console.error('Failed to load library health:', error));

            // Restore "where we were" if the user is coming back from the
            // reader. Runs after the initial library view has had a chance
            // to render so the target scroll position exists in the DOM.
            tryRestoreReaderReturnState();
        }

        // Two-hour TTL: stale snapshots are silently ignored so we don't
        // teleport users on a much later visit.
        const READER_RETURN_STATE_TTL_MS = 2 * 60 * 60 * 1000;

        function consumeReaderReturnState() {
            try {
                const raw = sessionStorage.getItem('comicReaderReturnState');
                if (!raw) return null;
                sessionStorage.removeItem('comicReaderReturnState');
                const snap = JSON.parse(raw);
                if (!snap || typeof snap !== 'object') return null;
                if (!snap.timestamp || (Date.now() - snap.timestamp) > READER_RETURN_STATE_TTL_MS) return null;
                return snap;
            } catch (_e) {
                return null;
            }
        }

        async function tryRestoreReaderReturnState() {
            const snap = consumeReaderReturnState();
            if (!snap) return;
            try {
                // Apply search box value (no reload needed beyond the one that
                // loadActiveLibraryView already triggered with the saved filter
                // from preferences).
                if (snap.searchQuery) {
                    const input = document.getElementById('headerSearchInput');
                    if (input) input.value = snap.searchQuery;
                    searchQuery = snap.searchQuery;
                    // Trigger a fresh load with the search applied. Tests for
                    // emptiness avoid an unnecessary reload when nothing
                    // changed.
                    loadActiveLibraryView(1, false);
                }
                // Reopen the series detail panel if one was open.
                if (snap.currentSeriesDetailId
                    && typeof openSeriesDetail === 'function'
                    && libraryViewMode === 'series') {
                    // Wait briefly for the series list to populate before
                    // reopening the detail panel.
                    await waitForSeriesAvailable(snap.currentSeriesDetailId, 2500);
                    if (currentSeriesDetailId !== snap.currentSeriesDetailId
                        && seriesLibrary.some(s => s && s.id === snap.currentSeriesDetailId)) {
                        openSeriesDetail(snap.currentSeriesDetailId);
                    }
                }
                // Finally, restore scroll position. Wait for layout to settle
                // — folder/series rendering is async — by polling for a few
                // animation frames.
                await restoreScrollPosition(snap.scrollY || 0);
            } catch (e) {
                console.error('Failed to restore reader return state:', e);
            }
        }

        function waitForSeriesAvailable(seriesId, timeoutMs) {
            return new Promise(resolve => {
                const started = Date.now();
                const check = () => {
                    if (seriesLibrary.some(s => s && s.id === seriesId)) { resolve(true); return; }
                    if (Date.now() - started > timeoutMs) { resolve(false); return; }
                    setTimeout(check, 80);
                };
                check();
            });
        }

        function restoreScrollPosition(targetY) {
            return new Promise(resolve => {
                if (!targetY || targetY <= 0) { resolve(); return; }
                let attempts = 0;
                const tick = () => {
                    attempts++;
                    // window.scroll(0, y) will be clamped to documentElement.scrollHeight,
                    // so we keep trying until either the page has grown enough or
                    // we exhaust retries (~1.5 s worst case).
                    window.scrollTo(0, targetY);
                    const actual = window.scrollY || document.documentElement.scrollTop || 0;
                    if (Math.abs(actual - targetY) < 4 || attempts > 30) { resolve(); return; }
                    setTimeout(tick, 50);
                };
                requestAnimationFrame(tick);
            });
        }
        
        // Check if DOM is already loaded (script loaded after DOMContentLoaded fired)
        if (document.readyState === 'loading') {
            // DOM is still loading, wait for DOMContentLoaded
            document.addEventListener('DOMContentLoaded', initializeApp);
        } else {
            // DOM is already loaded, initialize immediately
            initializeApp();
        }
        
        // Warn user before leaving page if there's an active batch job
        // Note: We can't use async in beforeunload, so we track the active job in a variable
        let hasActiveJob = false;
        let currentJobId = null;  // Track current job ID for cancellation
        let currentJobTitle = null;  // Track current job title for progress updates
        window.addEventListener('beforeunload', function(event) {
            // Clean up SSE connection
            cleanupEventSource();
            
            if (hasActiveJob) {
                // Show warning to prevent accidental navigation during batch processing
                const message = 'A batch processing job is still running. If you leave, you can resume it when you return, but progress tracking will be interrupted.';
                event.preventDefault();
                event.returnValue = message; // For older browsers
                return message;
            }
        });
        
        async function loadActiveLibraryView(_page = 1, refresh = false) {
            if (libraryViewMode === 'series') {
                return loadSeriesLibrary({ refresh });
            }

            return loadFolders({ refresh });
        }

        function updateLibraryViewButtons() {
            document.getElementById('filesViewModeBtn')?.classList.toggle('active', libraryViewMode === 'files');
            document.getElementById('seriesViewModeBtn')?.classList.toggle('active', libraryViewMode === 'series');
            updateSeriesLayoutButtons();
        }

        // ── Series layout (List / Grid / Compact) ─────────────────────────────
        // The layout toggle is only meaningful when viewing the series library;
        // hide it entirely when in Files mode to avoid cluttering the header.
        function isMobilePortrait() {
            try {
                return window.matchMedia('(max-width: 600px) and (orientation: portrait)').matches;
            } catch (_e) {
                return false;
            }
        }

        function getEffectiveSeriesLayout() {
            if (seriesLayoutPreference) return seriesLayoutPreference;
            // Auto: list on phone-portrait so titles are always readable,
            // overlay-grid ("compact") everywhere else to preserve desktop behaviour.
            return isMobilePortrait() ? 'list' : 'compact';
        }

        function updateSeriesLayoutButtons() {
            const toggle = document.getElementById('seriesLayoutToggle');
            const mobileToolbar = document.getElementById('seriesLayoutToolbarMobile');
            const inSeriesMode = libraryViewMode === 'series';
            if (toggle) toggle.hidden = !inSeriesMode;
            if (mobileToolbar) mobileToolbar.hidden = !inSeriesMode;
            if (!inSeriesMode) return;
            const effective = getEffectiveSeriesLayout();
            document.getElementById('seriesLayoutListBtn')?.classList.toggle('active', effective === 'list');
            document.getElementById('seriesLayoutGridBtn')?.classList.toggle('active', effective === 'grid');
            document.getElementById('seriesLayoutCompactBtn')?.classList.toggle('active', effective === 'compact');
            document.getElementById('seriesLayoutListBtnMobile')?.classList.toggle('active', effective === 'list');
            document.getElementById('seriesLayoutGridBtnMobile')?.classList.toggle('active', effective === 'grid');
            document.getElementById('seriesLayoutCompactBtnMobile')?.classList.toggle('active', effective === 'compact');
        }

        function setSeriesLayout(layout) {
            if (layout !== 'list' && layout !== 'grid' && layout !== 'compact') return;
            seriesLayoutPreference = layout;
            try {
                localStorage.setItem('seriesLayout', layout);
            } catch (_e) { /* localStorage may be unavailable */ }
            updateSeriesLayoutButtons();
            if (libraryViewMode === 'series' && !currentSeriesDetailId) {
                renderSeriesLibrary();
            }
        }

        // Re-evaluate auto layout when the viewport rotates between portrait/landscape.
        try {
            const portraitMql = window.matchMedia('(max-width: 600px) and (orientation: portrait)');
            const onChange = () => {
                if (seriesLayoutPreference) return; // user has an explicit choice; don't override
                updateSeriesLayoutButtons();
                if (libraryViewMode === 'series' && !currentSeriesDetailId) {
                    renderSeriesLibrary();
                }
            };
            if (typeof portraitMql.addEventListener === 'function') {
                portraitMql.addEventListener('change', onChange);
            } else if (typeof portraitMql.addListener === 'function') {
                portraitMql.addListener(onChange); // Safari < 14
            }
        } catch (_e) { /* matchMedia may be unavailable */ }

        function updateLibraryViewLayout() {
            const controlsWrapper = document.querySelector('#libraryFilesView .controls-wrapper');
            const pagination = document.getElementById('pagination');
            const showBulkActions = libraryViewMode === 'files' || (libraryViewMode === 'series' && !!currentSeriesDetailId);

            if (controlsWrapper) {
                controlsWrapper.style.display = showBulkActions ? '' : 'none';
            }

            if (libraryViewMode === 'series' && currentSeriesDetailId && pagination) {
                pagination.style.display = 'none';
            }
        }

        async function setLibraryViewMode(mode) {
            // The Files view has been removed; the library is always shown in
            // Series mode. Any incoming request is coerced to 'series' so legacy
            // call sites (preferences with `libraryViewMode === 'files'`, etc.)
            // still resolve to a valid state.
            mode = 'series';
            if (isMobileLibraryViewport()) {
                if (currentMobileLibraryView !== 'series') {
                    currentMobileLibraryView = 'series';
                    applyMobileLibraryView();
                }
            }

            if (libraryViewMode === mode && !(mode === 'series' && currentSeriesDetailId)) {
                return;
            }

            libraryViewMode = mode;
            currentSeriesDetailId = null;
            currentSeriesDetailTitleKeys = null;
            updateLibraryViewButtons();
            updateLibraryViewLayout();
            await setPreferences({ libraryViewMode: mode });
            await loadActiveLibraryView(1, true);
        }

        function promptForForceReprocess(actionDescription, statusDescription) {
            return confirm(`${actionDescription}\n\nClick OK to include files already marked as ${statusDescription}, or Cancel to skip them.`);
        }

        async function loadSeriesLibrary(opts, legacyRefresh) {
            // Backwards-compat: callers used to pass (page, refresh) numerically.
            // The page argument is ignored — pagination is now offset-based and
            // a refresh always resets to offset 0 — but the refresh flag still
            // controls whether we reset state, add the cache-busting
            // ?refresh=true query, and re-trigger library-health stats.
            let append = false;
            let refresh = false;
            if (typeof opts === 'object' && opts !== null) {
                append = !!opts.append;
                refresh = !!opts.refresh;
            } else if (typeof opts === 'boolean') {
                refresh = opts;
            } else if (typeof opts === 'number') {
                refresh = !!legacyRefresh;
            }

            if (seriesLoading) return;

            if (refresh || !append) {
                seriesLibrary = [];
                seriesOffset = 0;
                seriesTotal = 0;
            }

            // Render an immediate loading state into the file list so that
            // switching to the series view always gives the user feedback,
            // even if the underlying API call is slow on large libraries.
            const fileListEl = document.getElementById('fileList');
            if (!append && fileListEl && !currentSeriesDetailId) {
                fileListEl.innerHTML = `
                    <div class="loading">
                        <div class="spinner"></div>
                        <p>Loading series...</p>
                    </div>
                `;
            }

            seriesLoading = true;
            try {
                let url = apiUrl(`/api/files/series?offset=${seriesOffset}&limit=${SERIES_PAGE_SIZE}`);
                if (refresh) {
                    url += '&refresh=true';
                }
                if (searchQuery) {
                    url += `&search=${encodeURIComponent(searchQuery)}`;
                }
                if (filterMode !== 'all') {
                    url += `&filter=${encodeURIComponent(filterMode)}`;
                }
                if (sortMode !== 'name') {
                    url += `&sort=${encodeURIComponent(sortMode)}`;
                }
                if (sortDirection !== 'asc') {
                    url += `&direction=${encodeURIComponent(sortDirection)}`;
                }

                const response = await fetch(url, {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }

                const data = await response.json();
                const newSeries = data.series || [];
                seriesLibrary = seriesLibrary.concat(newSeries);
                seriesOffset += newSeries.length;
                seriesTotal = data.total_series || 0;
                totalFiles = seriesTotal;
                unmarkedCount = data.unmarked_count || 0;

                if (currentSeriesDetailId) {
                    renderSeriesDetail(currentSeriesDetailId);
                } else {
                    renderSeriesLibrary();
                }

                updateButtonVisibility();
                updateLibraryViewLayout();
                ensureScrollObserver();

                if (refresh) {
                    loadLibraryHealth();
                }
            } catch (error) {
                showMessage('Failed to load series: ' + error.message, 'error');
            } finally {
                seriesLoading = false;
            }
        }

        async function loadFolders(opts) {
            let append = false;
            let refresh = false;
            if (typeof opts === 'object' && opts !== null) {
                append = !!opts.append;
                refresh = !!opts.refresh;
            } else if (typeof opts === 'boolean') {
                refresh = opts;
            }

            if (folderLoading) return;

            // For non-append calls, reset the folder summary list (we re-fetch
            // from offset 0). When the call is an explicit refresh, also clear
            // per-folder file caches and collapsed state so the user sees fresh
            // data. Otherwise, preserve cached folder file lists / expanded
            // state so per-file SSE events don't wipe the user's session.
            if (!append) {
                folderList = [];
                folderOffset = 0;
                folderTotal = 0;
                if (refresh) {
                    folderFiles.clear();
                    collapsedDirectories.clear();
                    allFoldersExpanded = false;
                }
            }

            folderLoading = true;
            try {
                let url = apiUrl(`/api/files/folders?offset=${folderOffset}&limit=${FOLDER_PAGE_SIZE}`);
                if (refresh) {
                    url += '&refresh=true';
                }
                if (searchQuery) {
                    url += `&search=${encodeURIComponent(searchQuery)}`;
                }
                if (filterMode !== 'all') {
                    url += `&filter=${encodeURIComponent(filterMode)}`;
                }
                if (sortMode !== 'name') {
                    url += `&sort=${encodeURIComponent(sortMode)}`;
                }
                if (sortDirection !== 'asc') {
                    url += `&direction=${encodeURIComponent(sortDirection)}`;
                }

                const response = await fetch(url, {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                const data = await response.json();

                const newFolders = data.folders || [];
                folderList = folderList.concat(newFolders);
                folderOffset += newFolders.length;
                folderTotal = data.total_folders || 0;
                totalFiles = folderTotal;
                unmarkedCount = data.unmarked_count || 0;

                // Newly arrived folders should be collapsed by default, but
                // preserve the user's explicit state for folders we've already
                // expanded in this session.
                for (const f of newFolders) {
                    if (f && typeof f.path === 'string') {
                        if (!folderFiles.has(f.path)) {
                            collapsedDirectories.add(f.path);
                        }
                    }
                }

                renderFileList();
                updateButtonVisibility();
                ensureScrollObserver();
                if (refresh) {
                    loadLibraryHealth();
                }
            } catch (error) {
                showMessage('Failed to load files: ' + error.message, 'error');
            } finally {
                folderLoading = false;
            }
        }

        // Backward-compatible shim so legacy callers still work.
        async function loadFiles(_page = 1, refresh = false) {
            return loadFolders({ refresh });
        }

        function updatePagination() {
            // Pagination has been replaced with infinite scroll; this is a no-op
            // kept so any leftover callers (or browser extensions) don't error.
        }
        
        function updateButtonVisibility() {
            // Get all unmarked-related buttons
            const processUnmarkedBtn = document.querySelector('button[onclick="processUnmarkedFiles()"]');
            const renameUnmarkedBtn = document.querySelector('button[onclick="renameUnmarkedFiles()"]');
            const normalizeUnmarkedBtn = document.querySelector('button[onclick="normalizeUnmarkedFiles()"]');
            const filterUnmarkedBtn = document.getElementById('filterUnmarked');
            const controlsWrapper = document.querySelector('#libraryFilesView .controls-wrapper');
            const showBulkActions = libraryViewMode === 'files' || (libraryViewMode === 'series' && !!currentSeriesDetailId);
            
            // Show or hide buttons based on whether there are unmarked files
            const hasUnmarkedFiles = unmarkedCount > 0;
            const displayStyle = hasUnmarkedFiles ? '' : 'none';
            
            if (processUnmarkedBtn) processUnmarkedBtn.style.display = displayStyle;
            if (renameUnmarkedBtn) renameUnmarkedBtn.style.display = displayStyle;
            if (normalizeUnmarkedBtn) normalizeUnmarkedBtn.style.display = displayStyle;
            if (filterUnmarkedBtn) filterUnmarkedBtn.style.display = displayStyle;
            if (controlsWrapper) controlsWrapper.style.display = showBulkActions ? '' : 'none';
        }
        
        async function changePerPage() {
            // Per-page selector has been removed in favor of infinite scroll.
        }
        
        function nextPage() { /* deprecated: infinite scroll */ }
        function previousPage() { /* deprecated: infinite scroll */ }
        
        async function filterFiles() {
            searchQuery = document.getElementById('headerSearchInput').value;
            // If a series detail is open, keep the user inside the series and
            // re-fetch its issues with the new search query. The series
            // library is refreshed in the background so that pressing Back
            // shows an up-to-date list, but we deliberately do NOT await it
            // — awaiting causes the open series id to potentially drift
            // (union-find representative changes when the filtered file-set
            // changes) and leaves the user staring at a loading spinner
            // while the library reload happens, which is what makes filter
            // toggles feel like they "kick the user out" of the view.
            if (currentSeriesDetailId) {
                const detailId = currentSeriesDetailId;
                seriesIssuesCache.delete(detailId);
                renderSeriesDetail(detailId); // re-render to show loading state
                loadSeriesIssues(detailId, true);
                loadActiveLibraryView(1, true);
                return;
            }
            // Reload from page 1 with new search query
            currentSeriesDetailTitleKeys = null;
            loadActiveLibraryView(1);
        }
        
        let sortMode = 'name'; // 'name', 'date', 'size'
        let sortDirection = 'asc'; // 'asc', 'desc'
        
        async function setHeaderFilter(mode) {
            filterMode = mode;
            
            // Update dropdown label and active state
            const filterLabels = {
                'all': '📚 All',
                'unmarked': '⚠️ Unmarked',
                'marked': '✅ Marked',
                'duplicates': '🔁 Duplicates',
                'renamed': '📝 Renamed',
                'normalized': '📋 Normalized',
                'read': '👁️ Read',
                'unread': '📚 Unread',
                'matched': '🔗 Matched',
                'unmatched': '❓ Not Matched'
            };
            
            document.getElementById('headerFilterLabel').textContent = filterLabels[mode];
            
            // Update active class on dropdown items
            document.querySelectorAll('#headerFilterMenu .header-dropdown-item').forEach(item => {
                if (item.dataset.filter === mode) {
                    item.classList.add('active');
                } else {
                    item.classList.remove('active');
                }
            });
            
            // Close the dropdown
            document.getElementById('headerFilterMenu').classList.remove('show');
            
            // Save filter mode to preferences
            await setPreferences({ filterMode: mode });

            // Dashboard cards also use setHeaderFilter(), so switch back to the files view on mobile
            // to immediately show the filtered list after a user taps a library health card.
            if (isMobileLibraryViewport()) {
                setMobileLibraryView('files');
            }

            // If a series detail is open, stay inside the series and re-fetch
            // its issues with the new filter (the filter applies to the issue
            // list too via the /api/files/series/{id}/issues endpoint). The
            // backend tolerates id-drift (union-find representative changes
            // between filtered file-sets) by resolving the series via the
            // unfiltered groups, so we can safely fetch issues using the
            // current id without first waiting for the series library to
            // reload. Refresh the library in the background so going Back
            // shows fresh data — but do NOT await it, otherwise the user is
            // stuck on a loading spinner during what should be a quick
            // in-place refresh of the issues grid.
            if (currentSeriesDetailId) {
                const detailId = currentSeriesDetailId;
                seriesIssuesCache.delete(detailId);
                renderSeriesDetail(detailId);
                loadSeriesIssues(detailId, true);
                loadActiveLibraryView(1, true);
                return;
            }

            // Reload from page 1 with new filter
            currentSeriesDetailTitleKeys = null;
            loadActiveLibraryView(1);
        }
        
        function setSort(mode) {
            // If same mode is selected, toggle direction; otherwise reset to asc
            if (sortMode === mode) {
                sortDirection = sortDirection === 'asc' ? 'desc' : 'asc';
            } else {
                sortMode = mode;
                sortDirection = 'asc';
            }
            
            // Update dropdown label and active state
            const sortLabels = {
                'name': '🔤 Name',
                'date': '📅 Date',
                'size': '💾 Size'
            };
            
            const arrow = sortDirection === 'asc' ? '↑' : '↓';
            document.getElementById('headerSortLabel').textContent = sortLabels[mode] + ' ' + arrow;
            
            // Update active class on dropdown items
            document.querySelectorAll('#headerSortMenu .header-dropdown-item').forEach(item => {
                if (item.dataset.sort === mode) {
                    item.classList.add('active');
                } else {
                    item.classList.remove('active');
                }
            });
            
            // Close the dropdown
            document.getElementById('headerSortMenu').classList.remove('show');

            // If a series detail is open, keep the user inside it. Sorting
            // does not apply to issues within a series (issues are ordered by
            // their number), so we just refresh the underlying series library
            // in the background and stay on the current series.
            if (currentSeriesDetailId) {
                loadActiveLibraryView(1, true);
                return;
            }

            // Reload from page 1 with new sort order
            currentSeriesDetailTitleKeys = null;
            loadActiveLibraryView(1);
        }
        
        function toggleHeaderFilterDropdown(event) {
            event.stopPropagation();
            const menu = document.getElementById('headerFilterMenu');
            const sortMenu = document.getElementById('headerSortMenu');
            sortMenu.classList.remove('show');
            menu.classList.toggle('show');
        }
        
        function toggleHeaderSortDropdown(event) {
            event.stopPropagation();
            const menu = document.getElementById('headerSortMenu');
            const filterMenu = document.getElementById('headerFilterMenu');
            filterMenu.classList.remove('show');
            menu.classList.toggle('show');
        }
        
        function setFilter(mode) {
            // Redirect to header filter function
            setHeaderFilter(mode);
        }
        
        function toggleFilterDropdown(event) {
            // Redirect to header filter toggle
            toggleHeaderFilterDropdown(event);
        }
        
        function toggleSettingsMenu(event) {
            event.stopPropagation();
            const menu = document.getElementById('settingsDropdownMenu');
            menu.classList.toggle('show');
        }
        
        function closeSettingsMenu() {
            const menu = document.getElementById('settingsDropdownMenu');
            menu.classList.remove('show');
        }
        
        async function scanUnmarkedFiles() {
            try {
                showMessage('Scanning for unmarked files...', 'info');
                const response = await fetch(apiUrl('/api/scan-unmarked'), {
                    method: 'POST',
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) return;
                
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                const data = await response.json();
                
                showMessage(`Found ${data.unmarked_count} unmarked file(s) and ${data.marked_count} marked file(s) out of ${data.total_count} total files.`, 'success');
            } catch (error) {
                showMessage('Failed to scan files: ' + error.message, 'error');
            }
        }

        function getSeriesCoverUrl(filePath) {
            // Persistent server-side cache endpoint. Falls back automatically
            // (via the protected-image fallback machinery) to the legacy page
            // extraction endpoint if the cache is unavailable.
            return apiUrl(`/api/comicreader/cover?filePath=${encodeURIComponent(filePath)}`);
        }

        function getSeriesCoverFallbackUrl(filePath) {
            return apiUrl(`/api/comicreader/page?filePath=${encodeURIComponent(filePath)}&page=1`);
        }

        /**
         * Resolve a `data-protected-image` token to a fetchable URL. Tokens
         * that look like API paths (start with "/api/") are passed through to
         * apiUrl(); anything else is treated as a comic file path and routed
         * through the comicreader page endpoint.
         */
        function resolveProtectedImageUrl(token) {
            if (!token) return null;
            if (token.startsWith('/api/')) {
                return apiUrl(token);
            }
            return getSeriesCoverUrl(token);
        }

        // Lazy per-image hydration. Previously this function fetched every
        // matching image immediately in batches of 8 — fine for small
        // libraries, but with 100+ issue series it fired dozens of cover
        // requests for off-screen cards. We now use an IntersectionObserver
        // so each image is only fetched when it (or its container) is close
        // to entering the viewport. The original eager-batch behaviour is
        // kept as a fallback for environments without IntersectionObserver.
        const PROTECTED_IMAGE_OBSERVER = (typeof IntersectionObserver !== 'undefined')
            ? new IntersectionObserver(entries => {
                for (const entry of entries) {
                    if (!entry.isIntersecting) continue;
                    const el = entry.target;
                    PROTECTED_IMAGE_OBSERVER.unobserve(el);
                    hydrateOneProtectedImage(el);
                }
            }, { rootMargin: '400px 0px' })
            : null;

        async function hydrateOneProtectedImage(image) {
            const primary = image.dataset.protectedImage;
            const fallback = image.dataset.protectedImageFallback;
            if (!primary) return;
            if (image.dataset.protectedImageLoaded === '1') return;
            image.dataset.protectedImageLoaded = '1';

            const sleep = (ms) => new Promise(resolve => setTimeout(resolve, ms));
            const fetchOnce = async (url) => {
                try {
                    const response = await fetch(url, { headers: getAuthHeaders() });
                    return { response, errored: false };
                } catch (error) {
                    return { response: null, errored: true, error };
                }
            };
            const tryLoad = async (token) => {
                if (!token) return 'missing';
                if (protectedImageUrls.has(token)) {
                    image.src = protectedImageUrls.get(token);
                    return 'ok';
                }
                const url = resolveProtectedImageUrl(token);
                if (!url) return 'missing';

                let attempt = await fetchOnce(url);
                let response = attempt.response;
                const isTransient = attempt.errored
                    || (response && response.status >= 500);
                if (isTransient) {
                    await sleep(250);
                    attempt = await fetchOnce(url);
                    response = attempt.response;
                }

                if (attempt.errored || !response) {
                    console.warn('Protected image fetch failed', attempt.error);
                    return 'failed';
                }
                if (response.status === 404) {
                    return 'missing';
                }
                if (!response.ok) {
                    return 'failed';
                }

                try {
                    const blob = await response.blob();
                    const objectUrl = URL.createObjectURL(blob);
                    if (protectedImageUrls.size >= MAX_PROTECTED_IMAGE_CACHE_ENTRIES) {
                        const oldestKey = protectedImageUrls.keys().next().value;
                        if (oldestKey) {
                            URL.revokeObjectURL(protectedImageUrls.get(oldestKey));
                            protectedImageUrls.delete(oldestKey);
                        }
                    }
                    protectedImageUrls.set(token, objectUrl);
                    image.src = objectUrl;
                    return 'ok';
                } catch (error) {
                    console.error('Failed to decode protected image', error);
                    return 'failed';
                }
            };

            const primaryResult = await tryLoad(primary);
            if (primaryResult === 'missing' && fallback && fallback !== primary) {
                await tryLoad(fallback);
            } else if (primaryResult === 'failed') {
                // Allow a future hydration attempt (e.g. when the element
                // scrolls back into view after a transient network failure).
                delete image.dataset.protectedImageLoaded;
            }
        }

        async function hydrateProtectedImages(container = document) {
            const images = container.querySelectorAll('[data-protected-image]');
            // Fast path: if the cover token is already cached, set it
            // immediately so the user sees instant re-renders for issues
            // they've already scrolled past. Uncached covers go through the
            // observer (lazy) when available, or fall back to eager batched
            // loading.
            const uncached = [];
            for (const image of images) {
                const primary = image.dataset.protectedImage;
                if (primary && protectedImageUrls.has(primary)) {
                    image.src = protectedImageUrls.get(primary);
                    image.dataset.protectedImageLoaded = '1';
                    continue;
                }
                uncached.push(image);
            }

            if (PROTECTED_IMAGE_OBSERVER) {
                for (const image of uncached) {
                    PROTECTED_IMAGE_OBSERVER.observe(image);
                }
                return;
            }

            // Fallback (no IntersectionObserver support): batched eager load.
            const batchSize = 8;
            for (let index = 0; index < uncached.length; index += batchSize) {
                const batch = uncached.slice(index, index + batchSize);
                await Promise.all(batch.map(hydrateOneProtectedImage));
            }
        }

        function renderSeriesLibrary() {
            const fileList = document.getElementById('fileList');

            if (!seriesLibrary.length) {
                fileList.innerHTML = `
                    <div class="empty-state">
                        <div class="empty-state-icon">🖼️</div>
                        <h2>No series found</h2>
                        <p>${searchQuery || filterMode !== 'all' ? 'Try a different search term or filter' : 'Process and normalize comics to build your series library view.'}</p>
                    </div>
                `;
                return;
            }

            const layout = getEffectiveSeriesLayout();
            const body = (layout === 'list')
                ? renderSeriesLibraryList()
                : (layout === 'grid')
                    ? renderSeriesLibraryGrid()
                    : renderSeriesLibraryCompact();

            fileList.innerHTML = `
                ${body}
            `;

            hydrateProtectedImages(fileList);
        }

        // Cover-only cards with title overlaid on the cover (original behaviour).
        function renderSeriesLibraryCompact() {
            return `
                <div class="series-grid series-grid--compact">
                    ${seriesLibrary.map(series => `
                        <button class="series-card" type="button" aria-expanded="${currentSeriesDetailId === series.id ? 'true' : 'false'}" aria-controls="seriesDetailPanel" aria-label="Open series ${escapeHtml(series.title)}" onclick="openSeriesDetail('${escapeJs(series.id)}')">
                            <div class="series-cover-wrapper">
                                <img class="series-cover" data-protected-image="${escapeHtml(series.has_external_image && series.external_image_url ? series.external_image_url : series.cover_file_path)}" data-protected-image-fallback="${escapeHtml(series.has_external_image && series.external_image_url ? series.cover_file_path : '')}" alt="${escapeHtml(series.title)} cover" loading="lazy">
                                <div class="series-cover-overlay"></div>
                                <span class="series-count-badge">${series.issue_count}</span>
                                ${renderLookupStatusBadge(series)}
                                <div class="series-card-body">
                                    <h3 class="series-title" title="${escapeHtml(series.title)}">${escapeHtml(series.title)}</h3>
                                    <div class="series-meta">${formatFileSize(series.total_size)}</div>
                                </div>
                            </div>
                        </button>
                    `).join('')}
                </div>
            `;
        }

        // Grid of cards with the title rendered *below* the cover (not overlaid),
        // allowing it to wrap onto multiple lines so long names remain readable.
        function renderSeriesLibraryGrid() {
            return `
                <div class="series-grid series-grid--titled">
                    ${seriesLibrary.map(series => `
                        <button class="series-card series-card--titled" type="button" aria-expanded="${currentSeriesDetailId === series.id ? 'true' : 'false'}" aria-controls="seriesDetailPanel" aria-label="Open series ${escapeHtml(series.title)}" onclick="openSeriesDetail('${escapeJs(series.id)}')">
                            <div class="series-cover-wrapper">
                                <img class="series-cover" data-protected-image="${escapeHtml(series.has_external_image && series.external_image_url ? series.external_image_url : series.cover_file_path)}" data-protected-image-fallback="${escapeHtml(series.has_external_image && series.external_image_url ? series.cover_file_path : '')}" alt="${escapeHtml(series.title)} cover" loading="lazy">
                                <span class="series-count-badge">${series.issue_count}</span>
                                ${renderLookupStatusBadge(series)}
                            </div>
                            <div class="series-card-body series-card-body--below">
                                <h3 class="series-title series-title--below" title="${escapeHtml(series.title)}">${escapeHtml(series.title)}</h3>
                                <div class="series-meta series-meta--below">${formatFileSize(series.total_size)}</div>
                            </div>
                        </button>
                    `).join('')}
                </div>
            `;
        }

        // Single-column list rows optimised for phones in portrait: small thumbnail
        // on the left, full-width title (wraps to 2 lines) and meta on the right,
        // issue count on the far right.
        function renderSeriesLibraryList() {
            return `
                <div class="series-list" role="list">
                    ${seriesLibrary.map(series => `
                        <button class="series-list-row" type="button" role="listitem" aria-expanded="${currentSeriesDetailId === series.id ? 'true' : 'false'}" aria-controls="seriesDetailPanel" aria-label="Open series ${escapeHtml(series.title)}" onclick="openSeriesDetail('${escapeJs(series.id)}')">
                            <div class="series-list-thumb-wrapper">
                                <img class="series-list-thumb" data-protected-image="${escapeHtml(series.has_external_image && series.external_image_url ? series.external_image_url : series.cover_file_path)}" data-protected-image-fallback="${escapeHtml(series.has_external_image && series.external_image_url ? series.cover_file_path : '')}" alt="${escapeHtml(series.title)} cover" loading="lazy">
                            </div>
                            <div class="series-list-info">
                                <h3 class="series-list-title" title="${escapeHtml(series.title)}">${escapeHtml(series.title)}</h3>
                                <div class="series-list-meta">${formatFileSize(series.total_size)}</div>
                            </div>
                            <div class="series-list-aside">
                                ${renderLookupStatusBadge(series)}
                                <span class="series-list-count" aria-label="${series.issue_count} issues">${series.issue_count}</span>
                                <span class="series-list-chevron" aria-hidden="true">›</span>
                            </div>
                        </button>
                    `).join('')}
                </div>
            `;
        }

        function renderLookupStatusBadge(series) {
            // Visual indicator for the most recent external metadata lookup.
            // Helps the user see at a glance whether the series has been
            // matched against an external provider (so the next bulk
            // refresh is meaningful) or is still unmatched.
            const status = (series.lookup_status || '').toLowerCase();
            const map = {
                success:      { cls: 'success',   symbol: '✓', label: 'Provider matched' },
                manual_match: { cls: 'success',   symbol: '✓', label: 'Manually matched' },
                manual:       { cls: 'success',   symbol: '✓', label: 'Manually matched' },
                not_found:    { cls: 'warn',      symbol: '?', label: 'No external match found' },
                error:        { cls: 'error',     symbol: '!', label: 'Last lookup failed' }
            };
            const info = status
                ? (map[status] || { cls: 'warn', symbol: '?', label: status })
                : { cls: 'unmatched', symbol: '✕', label: 'Not yet matched to a provider' };
            const sourceText = series.metadata_source ? ` · ${series.metadata_source}` : '';
            const lookupText = series.last_lookup_utc ? ` · ${new Date(series.last_lookup_utc).toLocaleString()}` : '';
            return `<span class="series-lookup-badge series-lookup-badge--${info.cls}" title="${escapeHtml(info.label + sourceText + lookupText)}" aria-label="${escapeHtml(info.label)}">${info.symbol}</span>`;
        }

        function renderProviderHealthWidget() {
            // Populated asynchronously by loadProviderHealth(); render an empty
            // placeholder so the layout doesn't shift when results arrive.
            return '<div id="providerHealthWidget" class="provider-health-widget" data-loaded="false"></div>';
        }

        function scheduleProviderHealthLoad() {
            if (providerHealthRefreshTimer) {
                clearTimeout(providerHealthRefreshTimer);
            }
            providerHealthRefreshTimer = setTimeout(() => loadProviderHealth(), 50);
        }

        // Show the External Providers Status modal. The widget itself lives in
        // the modal body now (previously it was always-visible above the series
        // grid) so the main view stays focused on the library.
        function openExternalProvidersModal() {
            const modal = document.getElementById('externalProvidersModal');
            const host = document.getElementById('externalProvidersModalContent');
            if (!modal || !host) return;
            host.innerHTML = renderProviderHealthWidget();
            modal.classList.add('show');
            modal.style.display = 'flex';
            loadProviderHealth();
        }

        function closeExternalProvidersModal() {
            const modal = document.getElementById('externalProvidersModal');
            if (!modal) return;
            modal.classList.remove('show');
            modal.style.display = 'none';
        }

        async function loadProviderHealth() {
            const container = document.getElementById('providerHealthWidget');
            if (!container) return;
            try {
                const response = await fetch(apiUrl('/api/metadata/providers'), {
                    headers: getAuthHeaders ? getAuthHeaders() : undefined,
                    credentials: 'same-origin'
                });
                if (!response.ok) {
                    container.innerHTML = '<div style="color: var(--text-muted); font-size: 13px;">Unable to fetch provider status.</div>';
                    return;
                }
                const data = await response.json();
                const providers = data.providers || [];
                if (!providers.length) {
                    container.innerHTML = '<div style="color: var(--text-muted); font-size: 13px;">No external providers are configured.</div>';
                    return;
                }
                container.innerHTML = `
                    <div class="provider-health-row">
                        <span class="provider-health-label">External providers:</span>
                        ${providers.map(p => {
                            const cls = providerHealthClass(p);
                            const tooltipParts = [
                                p.enabled ? 'Enabled' : 'Disabled',
                                p.configured ? 'Configured' : 'Not configured',
                                p.status_message || '',
                                p.last_error ? `Last error: ${p.last_error}` : '',
                                p.last_success_utc ? `Last success: ${new Date(p.last_success_utc).toLocaleString()}` : 'No successful lookups yet',
                                `Successes: ${p.success_count || 0}, Failures: ${p.failure_count || 0}`
                            ].filter(Boolean).join('\n');
                            return `<span class="provider-health-pill provider-health-pill--${cls}" title="${escapeHtml(tooltipParts)}">
                                <span class="provider-health-dot"></span>${escapeHtml(p.name)}
                            </span>`;
                        }).join('')}
                    </div>
                `;
                container.dataset.loaded = 'true';
            } catch (err) {
                console.warn('Provider health fetch failed', err);
                container.innerHTML = '<div style="color: var(--text-muted); font-size: 13px;">Unable to fetch provider status.</div>';
            }
        }

        function providerHealthClass(p) {
            if (!p.enabled) return 'disabled';
            if (!p.configured) return 'disabled';
            if (p.reachable === false) return 'error';
            if (p.reachable === true && (p.failure_count || 0) === 0) return 'ok';
            if (p.reachable === true) return 'warn';
            // Reachable unknown but configured
            return 'warn';
        }

        function openSeriesDetail(seriesId) {
            currentSeriesDetailId = seriesId;
            const found = seriesLibrary.find(item => item && item.id === seriesId);
            captureSeriesDetailIdentity(found);
            if (found) currentSeriesDetailSeries = found;
            renderSeriesDetail(seriesId);
            // Kick off the issues fetch right away so the detail content
            // appears as soon as it's available.
            loadSeriesIssues(seriesId);
        }

        function closeSeriesDetail() {
            disconnectSeriesIssuesObserver();
            currentSeriesDetailId = null;
            currentSeriesDetailTitleKeys = null;
            currentSeriesDetailSeries = null;
            renderSeriesLibrary();
            updatePagination();
            updateLibraryViewLayout();
        }

        // Normalize a title-like value for case-insensitive comparison.
        function normalizeSeriesTitleKey(value) {
            return typeof value === 'string' ? value.trim().toLowerCase() : '';
        }

        // Record the set of titles/aliases for the currently open series so we
        // can remap the detail id if an external metadata refresh causes the
        // backend's series id (union-find representative) to change.
        function captureSeriesDetailIdentity(series) {
            if (!series) {
                currentSeriesDetailTitleKeys = null;
                return;
            }
            const keys = new Set();
            const add = (v) => {
                const k = normalizeSeriesTitleKey(v);
                if (k) keys.add(k);
            };
            add(series.title);
            add(series.canonical_title);
            if (Array.isArray(series.aliases)) {
                series.aliases.forEach(add);
            }
            currentSeriesDetailTitleKeys = keys.size ? keys : null;
        }

        // Find a series in the current library whose title/canonical/aliases
        // overlap with the given set of normalized keys.
        function findSeriesByTitleKeys(keys) {
            if (!keys || !keys.size) return null;
            for (const series of seriesLibrary) {
                if (!series) continue;
                const titleKey = normalizeSeriesTitleKey(series.title);
                if (titleKey && keys.has(titleKey)) return series;
                const canonicalKey = normalizeSeriesTitleKey(series.canonical_title);
                if (canonicalKey && keys.has(canonicalKey)) return series;
                if (Array.isArray(series.aliases)) {
                    for (const alias of series.aliases) {
                        const aliasKey = normalizeSeriesTitleKey(alias);
                        if (aliasKey && keys.has(aliasKey)) return series;
                    }
                }
            }
            return null;
        }

        // When the open series can't be found by its id (typically because a
        // metadata refresh changed the union-find representative), try to
        // remap currentSeriesDetailId by matching the previously-captured
        // title/alias keys against the freshly-loaded library. Returns the
        // remapped series object, or null when no match is found.
        function remapCurrentSeriesDetailId() {
            if (!currentSeriesDetailId) return null;
            const remapped = findSeriesByTitleKeys(currentSeriesDetailTitleKeys);
            if (!remapped || remapped.id === currentSeriesDetailId) return null;
            // Migrate cached issues to the new id so we don't refetch when
            // the underlying content hasn't changed. Drop the old key. Never
            // migrate error entries — a 404/failure cached under the stale id
            // (e.g. from a race during a filter change) must not poison the
            // remapped id; leaving the new key empty lets renderSeriesDetail
            // trigger a fresh fetch.
            const oldCached = seriesIssuesCache.get(currentSeriesDetailId);
            if (oldCached && !oldCached.error && !seriesIssuesCache.has(remapped.id)) {
                seriesIssuesCache.set(remapped.id, oldCached);
            }
            seriesIssuesCache.delete(currentSeriesDetailId);
            currentSeriesDetailId = remapped.id;
            return remapped;
        }

        // Page size used when incrementally loading a series's issue list.
        // Tuned so the first paint of a large (100+ issue) series renders
        // quickly while still amortising request overhead across batches.
        const SERIES_ISSUES_PAGE_SIZE = 100;

        async function loadSeriesIssues(seriesId, force = false, page = 1) {
            if (!seriesId) return;
            if (force) {
                seriesIssuesCache.delete(seriesId);
            }
            const existing = seriesIssuesCache.get(seriesId);
            // First page already loaded and not forced — render and exit.
            if (!force && page === 1 && existing && !existing.error && existing.issues.length > 0) {
                renderSeriesDetail(seriesId);
                return;
            }
            // Subsequent page already loaded — nothing to do.
            if (existing && !existing.error && existing.loadedPages && existing.loadedPages.has(page)) {
                return;
            }
            // Another request is already loading this exact page.
            if (existing && existing.loadingPages && existing.loadingPages.has(page)) {
                return;
            }

            // Prime the cache entry so concurrent callers don't fan out.
            let entry = existing;
            if (!entry || entry.error) {
                entry = {
                    issues: [],
                    total: 0,
                    totalPages: 1,
                    perPage: SERIES_ISSUES_PAGE_SIZE,
                    loadedPages: new Set(),
                    loadingPages: new Set(),
                    allLoaded: false,
                    error: false
                };
                seriesIssuesCache.set(seriesId, entry);
            }
            entry.loadingPages.add(page);

            try {
                let url = apiUrl(`/api/files/series/${encodeURIComponent(seriesId)}/issues?per_page=${SERIES_ISSUES_PAGE_SIZE}&page=${page}`);
                if (filterMode !== 'all') {
                    url += `&filter=${encodeURIComponent(filterMode)}`;
                }
                const response = await fetch(url, {
                    headers: getAuthHeaders ? getAuthHeaders() : undefined,
                    credentials: 'same-origin'
                });
                if (!response.ok) {
                    entry.error = true;
                } else {
                    const data = await response.json();
                    const pageIssues = Array.isArray(data.issues) ? data.issues : [];
                    if (page === 1) {
                        entry.issues = pageIssues.slice();
                    } else {
                        // De-dupe by file_path in case the same file appears
                        // across pages (e.g. concurrent file-store mutations).
                        const seen = new Set(entry.issues.map(i => i.file_path));
                        for (const issue of pageIssues) {
                            if (!seen.has(issue.file_path)) {
                                entry.issues.push(issue);
                                seen.add(issue.file_path);
                            }
                        }
                    }
                    entry.total = typeof data.issue_count === 'number'
                        ? data.issue_count
                        : entry.issues.length;
                    entry.totalPages = typeof data.total_pages === 'number' && data.total_pages > 0
                        ? data.total_pages
                        : 1;
                    entry.perPage = typeof data.per_page === 'number' && data.per_page > 0
                        ? data.per_page
                        : SERIES_ISSUES_PAGE_SIZE;
                    entry.loadedPages.add(page);
                    entry.allLoaded = entry.loadedPages.size >= entry.totalPages
                        || entry.issues.length >= entry.total;
                    entry.error = false;
                    // Refresh the cached series metadata snapshot from the
                    // response so the detail view can render correctly even
                    // when the active filter excludes this series from the
                    // library list.
                    if (currentSeriesDetailId === seriesId) {
                        currentSeriesDetailSeries = Object.assign(
                            {},
                            currentSeriesDetailSeries || {},
                            {
                                id: data.id || seriesId,
                                title: data.title,
                                canonical_title: data.canonical_title,
                                aliases: data.aliases,
                                metadata_source: data.metadata_source,
                                cover_file_path: data.cover_file_path,
                                issue_count: data.issue_count,
                                total_size: data.total_size
                            }
                        );
                    }
                }
            } catch (err) {
                console.error('loadSeriesIssues failed', err);
                entry.error = true;
            } finally {
                entry.loadingPages.delete(page);
            }

            // Re-render only if the user is still on this series.
            if (currentSeriesDetailId === seriesId) {
                if (page === 1 || entry.error) {
                    renderSeriesDetail(seriesId);
                } else {
                    appendSeriesIssuePage(seriesId, page);
                }
            }
        }

        // Re-renders the issue grid contents from the cache after a new page
        // has been appended to it. Re-rendering the full grid (rather than
        // appending only the new page's items) preserves correct ordering
        // and missing-issue placeholders without re-sorting the DOM in place.
        // The surrounding panel and header are left untouched. Cover images
        // already hydrated stay cached in `protectedImageUrls` and rebind
        // instantly; only the freshly-added covers go through a fetch.
        function appendSeriesIssuePage(seriesId, page) {
            if (currentSeriesDetailId !== seriesId) return;
            const panel = document.getElementById('seriesDetailPanel');
            const grid = panel && panel.querySelector('.series-issues-grid');
            const cached = seriesIssuesCache.get(seriesId);
            if (!panel || !grid || !cached) {
                renderSeriesDetail(seriesId);
                return;
            }
            const gridItems = buildSeriesIssuesGridItems(cached.issues);
            const allLoaded = cached.allLoaded;
            grid.innerHTML = gridItems.map(renderSeriesIssueGridItemHtml).join('')
                + (!allLoaded ? `<div id="seriesIssuesSentinel" class="series-issues-sentinel" aria-hidden="true"><div class="spinner spinner-small"></div></div>` : '');
            // Update the "showing N of M" counter in the selection bar.
            const metaSpan = panel.querySelector('.series-detail-selection-meta');
            if (metaSpan) {
                const total = typeof cached.total === 'number' && cached.total > 0
                    ? cached.total
                    : cached.issues.length;
                metaSpan.textContent = `${total} issue${total === 1 ? '' : 's'} in this series${!allLoaded ? ` · showing ${cached.issues.length}` : ''}`;
            }
            hydrateProtectedImages(grid);
            updateSelectInfo();
            updateSelectAllCheckbox();
            setupSeriesIssuesSentinel(seriesId);
        }

        // IntersectionObserver wiring for infinite scroll. When the sentinel
        // at the bottom of the issues grid becomes visible (or near visible
        // via the rootMargin), kick off the next page's fetch. Disconnects
        // automatically when no more pages remain.
        let seriesIssuesObserver = null;
        function disconnectSeriesIssuesObserver() {
            if (seriesIssuesObserver) {
                try { seriesIssuesObserver.disconnect(); } catch (_) {}
                seriesIssuesObserver = null;
            }
        }
        function setupSeriesIssuesSentinel(seriesId) {
            disconnectSeriesIssuesObserver();
            const sentinel = document.getElementById('seriesIssuesSentinel');
            if (!sentinel) return;
            const cached = seriesIssuesCache.get(seriesId);
            if (!cached || cached.allLoaded) return;
            if (typeof IntersectionObserver === 'undefined') {
                // Fallback: eagerly fetch all remaining pages on environments
                // without IntersectionObserver support.
                (async () => {
                    while (true) {
                        const entry = seriesIssuesCache.get(seriesId);
                        if (!entry || entry.allLoaded || entry.error) break;
                        if (currentSeriesDetailId !== seriesId) break;
                        const nextPage = entry.loadedPages.size + 1;
                        await loadSeriesIssues(seriesId, false, nextPage);
                    }
                })();
                return;
            }
            seriesIssuesObserver = new IntersectionObserver(entries => {
                for (const entry of entries) {
                    if (!entry.isIntersecting) continue;
                    const current = seriesIssuesCache.get(seriesId);
                    if (!current || current.allLoaded || current.error) {
                        disconnectSeriesIssuesObserver();
                        return;
                    }
                    if (currentSeriesDetailId !== seriesId) {
                        disconnectSeriesIssuesObserver();
                        return;
                    }
                    const nextPage = current.loadedPages.size + 1;
                    if (current.loadingPages && current.loadingPages.has(nextPage)) {
                        return;
                    }
                    loadSeriesIssues(seriesId, false, nextPage);
                }
            }, { rootMargin: '400px 0px' });
            seriesIssuesObserver.observe(sentinel);
        }

        // Parse a free-form issue identifier into a positive integer when it
        // represents an integer issue number (e.g. "1", "001", "12"). Returns
        // null for non-integer values like "1.5", "Annual", or empty strings.
        function parseIntegerIssueNumber(value) {
            if (value === null || value === undefined) return null;
            const text = String(value).trim();
            if (!text) return null;
            // Allow optional leading zeros, but require the whole token to be
            // a non-negative integer to avoid mistaking "1.5" or "1a" for #1.
            if (!/^\d+$/.test(text)) return null;
            const n = parseInt(text, 10);
            if (!Number.isFinite(n) || n < 0) return null;
            return n;
        }

        // Given the issues array returned by the API, identify integer issue
        // numbers that are missing between 1 and the highest known integer
        // issue number. Returns a sorted array of missing integers.
        function computeMissingIssueNumbers(issues) {
            if (!Array.isArray(issues) || !issues.length) return [];
            const present = new Set();
            let maxIssue = 0;
            for (const issue of issues) {
                const n = parseIntegerIssueNumber(issue && issue.issue);
                if (n === null) continue;
                present.add(n);
                if (n > maxIssue) maxIssue = n;
            }
            if (maxIssue <= 0) return [];
            const missing = [];
            for (let n = 1; n <= maxIssue; n++) {
                if (!present.has(n)) missing.push(n);
            }
            return missing;
        }

        // Build the grid items array for the series-issues-grid, interleaving
        // missing-issue placeholders in their proper position (sorted by
        // integer issue number) so the user can visually see which issues are
        // missing alongside the issues they own. Issues without an integer
        // issue number are appended at the end in their original order.
        function buildSeriesIssuesGridItems(issues) {
            const items = [];
            const integerIssues = [];
            const nonIntegerIssues = [];
            for (const issue of issues || []) {
                const n = parseIntegerIssueNumber(issue && issue.issue);
                if (n === null) {
                    nonIntegerIssues.push(issue);
                } else {
                    integerIssues.push({ number: n, issue });
                }
            }
            integerIssues.sort((a, b) => a.number - b.number);
            const missing = computeMissingIssueNumbers(issues);
            const missingSet = new Set(missing);
            const presentSet = new Set(integerIssues.map(i => i.number));
            const maxIssue = integerIssues.length ? integerIssues[integerIssues.length - 1].number : 0;
            for (let n = 1; n <= maxIssue; n++) {
                if (presentSet.has(n)) {
                    const match = integerIssues.find(i => i.number === n);
                    if (match) items.push({ kind: 'issue', issue: match.issue });
                } else if (missingSet.has(n)) {
                    items.push({ kind: 'missing', number: n });
                }
            }
            // Append issues whose number is not a plain integer (e.g. "1.5",
            // "Annual") after the numbered range so they remain visible.
            for (const issue of nonIntegerIssues) {
                items.push({ kind: 'issue', issue });
            }
            return items;
        }

        // Render a small banner above the issues grid summarizing any missing
        // integer issue numbers between #1 and the highest known issue number.
        function renderMissingIssuesBanner(issues) {
            const missing = computeMissingIssueNumbers(issues);
            if (!missing.length) return '';
            // Compress runs of consecutive numbers into ranges (e.g. 2,3,4 -> "2-4").
            const ranges = [];
            let start = missing[0];
            let prev = missing[0];
            for (let i = 1; i < missing.length; i++) {
                if (missing[i] === prev + 1) {
                    prev = missing[i];
                    continue;
                }
                ranges.push(start === prev ? `#${start}` : `#${start}-${prev}`);
                start = missing[i];
                prev = missing[i];
            }
            ranges.push(start === prev ? `#${start}` : `#${start}-${prev}`);
            const label = ranges.join(', ');
            const count = missing.length;
            return `
                <div class="series-detail-missing-banner" role="status">
                    <span class="series-detail-missing-banner-icon" aria-hidden="true">⚠️</span>
                    <span class="series-detail-missing-banner-text">
                        Missing ${count} issue${count === 1 ? '' : 's'}: ${escapeHtml(label)}
                    </span>
                </div>
            `;
        }

        // Renders one grid item (issue card or missing-issue placeholder)
        // to HTML. Extracted from renderSeriesDetail so the same template is
        // used for the initial render and for incrementally appending later
        // pages without rebuilding the surrounding panel.
        function renderSeriesIssueGridItemHtml(item) {
            if (item.kind === 'missing') {
                return `
                                <div class="series-issue-card series-issue-card--missing" aria-label="Missing issue #${item.number}" title="Missing issue #${item.number}">
                                    <div class="series-issue-cover-button series-issue-cover-button--missing">
                                        <div class="series-issue-cover series-issue-cover--missing">
                                            <span class="series-issue-missing-icon">❔</span>
                                        </div>
                                        <span class="series-issue-badge">#${item.number}</span>
                                    </div>
                                    <div class="series-issue-body">
                                        <h3 class="series-issue-title">Missing</h3>
                                        <p class="series-issue-subtitle">Issue #${item.number} is not in your collection</p>
                                    </div>
                                </div>`;
            }
            const issue = item.issue;
            return `
                                <div class="series-issue-card ${selectedFiles.has(issue.file_path) ? 'series-issue-card--selected' : ''} ${issue.duplicate ? 'series-issue-card--duplicate' : ''}" data-file-path="${escapeHtml(issue.file_path)}">
                                    <label class="series-issue-select" aria-label="Select ${escapeHtml(issue.title || issue.file_name)}" onclick="event.stopPropagation()">
                                        <input type="checkbox"
                                               ${selectedFiles.has(issue.file_path) ? 'checked' : ''}
                                               onchange="toggleFileSelection('${escapeJs(issue.file_path)}', this.checked)">
                                    </label>
                                    ${issue.duplicate ? `<span class="series-issue-duplicate-badge" title="Duplicate">🔁 Duplicate</span>` : ''}
                                    <button type="button" class="series-issue-cover-button" aria-label="Read ${escapeHtml(issue.title || issue.file_name)}" onclick="readComic('${escapeJs(issue.file_path)}')">
                                        <img class="series-issue-cover" data-protected-image="${escapeHtml(issue.file_path)}" alt="${escapeHtml(issue.file_name)} cover" loading="lazy">
                                        <div class="series-issue-cover-overlay"></div>
                                        ${issue.issue ? `<span class="series-issue-badge">#${escapeHtml(issue.issue)}</span>` : ''}
                                    </button>
                                    <div class="series-issue-body">
                                        <h3 class="series-issue-title" title="${escapeHtml(issue.title || issue.file_name)}">${escapeHtml(issue.title || issue.file_name)}</h3>
                                        <p class="series-issue-subtitle">${issue.year ? `${issue.year}` : ''}${issue.volume ? `${issue.year ? ' · ' : ''}Vol. ${escapeHtml(issue.volume)}` : ''}</p>
                                        <div class="series-detail-meta">${formatFileSize(issue.size)}</div>
                                    </div>
                                    <div class="series-issue-actions file-actions-dropdown" onclick="event.stopPropagation()">
                                        <button type="button" class="dropdown-toggle series-issue-actions-toggle" aria-label="Issue actions" onclick="toggleDropdown(event, '${escapeJs(issue.file_path)}')">
                                            <span aria-hidden="true">⋮</span>
                                        </button>
                                        <div class="dropdown-menu" id="${getDropdownId(issue.file_path)}">
                                            <button class="dropdown-item" onclick="showFileInfo('${escapeJs(issue.file_path)}', '${escapeJs(issue.file_name)}'); closeAllDropdowns();">
                                                ℹ️ Info
                                            </button>
                                            <button class="dropdown-item" onclick="viewTags('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                👁️ View/Edit
                                            </button>
                                            <button class="dropdown-item" onclick="readComic('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                📖 Read Comic
                                            </button>
                                            ${issue.duplicate
                                                ? `<button class="dropdown-item" onclick="openDuplicateReviewModal('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                    🔁 Review Duplicate
                                                </button>`
                                                : ''
                                            }
                                            <div class="dropdown-divider"></div>
                                            ${issue.read
                                                ? `<button class="dropdown-item" onclick="markFileUnread('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                    📚 Mark Unread
                                                </button>`
                                                : `<button class="dropdown-item" onclick="markFileRead('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                    ✅ Mark Read
                                                </button>`
                                            }
                                            <div class="dropdown-divider"></div>
                                            <button class="dropdown-item" onclick="processSingleFile('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                🚀 Process
                                            </button>
                                            <button class="dropdown-item" onclick="renameSingleFile('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                📝 Rename
                                            </button>
                                            <button class="dropdown-item" onclick="normalizeSingleFile('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                ✨ Normalize
                                            </button>
                                            <button class="dropdown-item" onclick="removeFileMetadata('${escapeJs(issue.file_path)}'); closeAllDropdowns();" title="Open the archive, delete the embedded ComicInfo.xml, and mark the file as unprocessed so it can be re-processed.">
                                                🧹 Remove Metadata
                                            </button>
                                            <div class="dropdown-divider"></div>
                                            <button class="dropdown-item" onclick="deleteSingleFile('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                🗑️ Delete
                                            </button>
                                        </div>
                                    </div>
                                </div>`;
        }

        function renderSeriesDetail(seriesId) {
            const fileList = document.getElementById('fileList');
            let series = seriesLibrary.find(item => item.id === seriesId);
            if (!series && seriesId === currentSeriesDetailId) {
                // A metadata refresh may have changed the union-find
                // representative used as the series id. Try to remap to the
                // same series under its new id (matching by title / canonical
                // title / aliases) instead of closing the detail view.
                const remapped = remapCurrentSeriesDetailId();
                if (remapped) {
                    series = remapped;
                    seriesId = remapped.id;
                    if (!seriesIssuesCache.has(seriesId)) {
                        loadSeriesIssues(seriesId);
                    }
                }
            }
            // Fall back to the cached snapshot of the open series when the
            // current filter / search excludes it from the library list. This
            // keeps the user inside the series detail when they change filter,
            // instead of being kicked back to the series grid.
            //
            // We intentionally do NOT require `currentSeriesDetailSeries.id`
            // to equal `seriesId` here. The backend series id is the
            // union-find representative of the files visible under the
            // current filter, so switching filters (e.g. duplicates → all)
            // can cause the representative key — and therefore the series
            // id — to drift even though it still refers to the same series
            // the user is viewing. The snapshot is set when the series is
            // opened and refreshed by every successful `loadSeriesIssues`
            // response, so it always represents the open series; trust it
            // as a fallback regardless of id drift.
            if (!series && seriesId === currentSeriesDetailId && currentSeriesDetailSeries) {
                series = currentSeriesDetailSeries;
            }
            if (!series) {
                closeSeriesDetail();
                return;
            }
            // Keep the cached snapshot fresh whenever we successfully resolve
            // the series object.
            currentSeriesDetailSeries = series;
            // Keep title-based identity fresh in case aliases changed.
            captureSeriesDetailIdentity(series);

            const cached = seriesIssuesCache.get(seriesId);
            const issues = cached ? cached.issues : [];
            // Use the backend-reported total (issue_count / cached.total) so
            // counts reflect the full series even before all pages have
            // streamed in via the infinite-scroll loader.
            const issueCount = cached && typeof cached.total === 'number' && cached.total > 0
                ? cached.total
                : (series.issue_count || issues.length || 0);
            const issuesLoading = !cached || (issues.length === 0 && !cached.error && (!cached.loadedPages || cached.loadedPages.size === 0));
            const issuesFailed = cached && cached.error && issues.length === 0;
            const allLoaded = cached ? cached.allLoaded : false;
            const selectableIssuePaths = issues
                .map(issue => issue.file_path)
                .filter(path => typeof path === 'string' && path.length > 0);
            const allIssuesSelected = selectableIssuePaths.length > 0 && selectableIssuePaths.every(path => selectedFiles.has(path));
            const someIssuesSelected = selectableIssuePaths.some(path => selectedFiles.has(path));
            const gridItems = buildSeriesIssuesGridItems(issues);

            fileList.innerHTML = `
                <div class="series-detail" id="seriesDetailPanel">
                    <div class="series-detail-header">
                        <button type="button" class="btn btn-small series-detail-back" onclick="closeSeriesDetail()">← Back to Series</button>
                        <div class="series-detail-summary">
                            <img class="series-detail-cover" data-protected-image="${escapeHtml(series.has_external_image && series.external_image_url ? series.external_image_url : series.cover_file_path)}" data-protected-image-fallback="${escapeHtml(series.has_external_image && series.external_image_url ? series.cover_file_path : '')}" alt="${escapeHtml(series.title)} cover" loading="lazy">
                            <div class="series-detail-summary-body">
                                <h2>${escapeHtml(series.title)} ${renderLookupStatusBadge(series)}</h2>
                                <div class="series-detail-meta">${issueCount} issue${issueCount === 1 ? '' : 's'} · ${formatFileSize(series.total_size)}</div>
                                ${series.aliases?.length ? `<div class="series-detail-meta">Also known as: ${escapeHtml(series.aliases.join(', '))}</div>` : ''}
                                ${series.metadata_source ? `<div class="series-detail-meta">Source: ${escapeHtml(series.metadata_source)}${series.last_lookup_utc ? ` · ${new Date(series.last_lookup_utc).toLocaleString()}` : ''}</div>` : ''}
                                <div class="series-detail-actions">
                                    ${issues.length ? `<button type="button" class="btn btn-small" onclick="readComic('${escapeJs(issues[0].file_path)}')">📖 Read First Issue</button>` : ''}
                                    <div class="file-actions-dropdown series-actions-dropdown">
                                        <button type="button" class="dropdown-toggle" onclick="toggleDropdown(event, '${escapeJs(SERIES_ACTIONS_DROPDOWN_KEY)}')">
                                            ⚙️ Actions
                                        </button>
                                        <div class="dropdown-menu" id="${getDropdownId(SERIES_ACTIONS_DROPDOWN_KEY)}">
                                            <button class="dropdown-item" onclick="openManageSeriesNamesModal('${escapeJs(series.title)}'); closeAllDropdowns();">
                                                🏷️ Manage Names
                                            </button>
                                            <button class="dropdown-item" onclick="openSeriesFoldersModal('${escapeJs(series.id)}','${escapeJs(series.title)}'); closeAllDropdowns();" title="See the on-disk folders contributing to this series and merge them into one">
                                                📁 Manage Folders
                                            </button>
                                            <div class="dropdown-divider"></div>
                                            <button class="dropdown-item" onclick="refreshSeriesMetadataDirect('${escapeJs(series.title)}'); closeAllDropdowns();">
                                                🌐 Refresh Metadata
                                            </button>
                                            <button class="dropdown-item" onclick="refreshSeriesFolder('${escapeJs(series.id)}','${escapeJs(series.title)}'); closeAllDropdowns();" title="Refresh metadata for every folder/alias that groups under this series">
                                                📁 Refresh Folder
                                            </button>
                                            <div class="dropdown-divider"></div>
                                            <button class="dropdown-item" onclick="resetSeriesProcessedStatus('${escapeJs(series.id)}','${escapeJs(series.title)}'); closeAllDropdowns();" title="Clear the renamed/normalized flags on every file in this series so they will be re-processed on the next Process / Rename / Normalize run. Use this if a metadata or filename change is not being applied.">
                                                ♻️ Reset Processed Status
                                            </button>
                                        </div>
                                    </div>
                                </div>
                            </div>
                        </div>
                    </div>
                    ${!issuesLoading && !issuesFailed && issues.length ? `
                        <div class="series-detail-selection-bar">
                            <label class="series-detail-select-all" for="selectAll">
                                <input type="checkbox"
                                       id="selectAll"
                                       onchange="toggleSelectAll(this.checked)"
                                       ${allIssuesSelected ? 'checked' : ''}>
                                <span>Select all issues</span>
                            </label>
                            <span class="series-detail-selection-meta">${issueCount} issue${issueCount === 1 ? '' : 's'} in this series${!allLoaded ? ` · showing ${issues.length}` : ''}</span>
                        </div>
                        ${renderMissingIssuesBanner(issues)}
                    ` : ''}
                    ${issuesLoading ? `
                        <div class="loading">
                            <div class="spinner"></div>
                            <p>Loading issues...</p>
                        </div>
                    ` : issuesFailed ? `
                        <div class="empty-state"><p>Failed to load issues. <button type="button" class="btn btn-small" onclick="loadSeriesIssues('${escapeJs(seriesId)}', true)">Retry</button></p></div>
                    ` : issues.length === 0 ? `
                        <div class="empty-state">
                            <p>${filterMode !== 'all' || searchQuery ? 'No issues in this series match the current filter.' : 'No issues in this series.'}</p>
                            ${filterMode !== 'all' ? `<button type="button" class="btn btn-small" onclick="setHeaderFilter('all')">Clear filter</button>` : ''}
                        </div>
                    ` : `
                        <div class="series-issues-grid">
                            ${gridItems.map(renderSeriesIssueGridItemHtml).join('')}
                            ${!allLoaded ? `<div id="seriesIssuesSentinel" class="series-issues-sentinel" aria-hidden="true"><div class="spinner spinner-small"></div></div>` : ''}
                        </div>
                    `}
                </div>
            `;

            const selectAllCheckbox = document.getElementById('selectAll');
            if (selectAllCheckbox) {
                selectAllCheckbox.indeterminate = someIssuesSelected && !allIssuesSelected;
            }
            updateSelectInfo();
            updateSelectAllCheckbox();
            updateLibraryViewLayout();
            hydrateProtectedImages(fileList);
            setupSeriesIssuesSentinel(seriesId);
        }
        
        function renderFileRow(file, dir) {
            const isSelected = selectedFiles.has(file.relative_path);
            const fileSize = formatFileSize(file.size);
            const modifiedDate = formatModifiedDate(file.modified);

            let readIcon = '';
            let readTitle = '';
            if (file.read) {
                readIcon = '👁️';
                readTitle = 'Read';
            }

            let statusIcon = '';
            let statusTitle = '';
            let statusClass = '';

            if (file.duplicate) {
                statusIcon = '🔁';
                statusTitle = 'Duplicate';
                statusClass = 'status-duplicate';
            } else if (file.renamed && file.normalized) {
                statusIcon = '✅';
                statusTitle = 'Processed (Renamed & Normalized)';
                statusClass = 'status-marked';
            } else if (file.renamed && !file.normalized) {
                statusIcon = '🔵';
                statusTitle = 'Renamed';
                statusClass = 'status-renamed';
            } else if (file.normalized && !file.renamed) {
                statusIcon = '🔴';
                statusTitle = 'Normalized';
                statusClass = 'status-normalized';
            } else {
                statusIcon = '⚠️';
                statusTitle = 'Unmarked';
                statusClass = 'status-unmarked';
            }

            const filenameParts = truncateFilenameMiddle(file.name);
            const filenameHtml = filenameParts.end
                ? `<span class="file-name-start">${escapeHtml(filenameParts.start)}</span><span class="file-name-end">${escapeHtml(filenameParts.end)}</span>`
                : `<span class="file-name-content">${escapeHtml(filenameParts.start)}</span>`;

            return `
                <div class="file-item ${statusClass}">
                    <input type="checkbox"
                           ${isSelected ? 'checked' : ''}
                           onchange="toggleFileSelection('${escapeJs(file.relative_path)}', this.checked)">
                    <div class="status-badge" title="${statusTitle}">
                        <span>${statusIcon}</span>
                    </div>
                    <div>
                        <div class="file-name" title="${escapeHtml(file.name)}">
                            ${readIcon ? `<span class="read-indicator" title="${readTitle}">${readIcon}</span> ` : ''}${filenameHtml}
                        </div>
                        ${!dir ? `<div class="file-path">${escapeHtml(file.relative_path)}</div>` : ''}
                    </div>
                    <div>${fileSize}</div>
                    <div style="color: var(--text-muted); font-size: 13px;">${modifiedDate}</div>
                    <div class="file-actions">
                        <div class="file-actions-dropdown">
                            <button class="dropdown-toggle" onclick="toggleDropdown(event, '${escapeJs(file.relative_path)}')">
                                Actions
                            </button>
                            <div class="dropdown-menu" id="${getDropdownId(file.relative_path)}">
                                <button class="dropdown-item" onclick="showFileInfo('${escapeJs(file.relative_path)}', '${escapeJs(file.name)}'); closeAllDropdowns();">
                                    ℹ️ Info
                                </button>
                                <button class="dropdown-item" onclick="viewTags('${escapeJs(file.relative_path)}'); closeAllDropdowns();">
                                    👁️ View/Edit
                                </button>
                                <button class="dropdown-item" onclick="readComic('${escapeJs(file.relative_path)}'); closeAllDropdowns();">
                                    📖 Read Comic
                                </button>
                                ${file.duplicate
                                    ? `<button class="dropdown-item" onclick="openDuplicateReviewModal('${escapeJs(file.relative_path)}'); closeAllDropdowns();">
                                        🔁 Review Duplicate
                                    </button>`
                                    : ''
                                }
                                <div class="dropdown-divider"></div>
                                ${file.read
                                    ? `<button class="dropdown-item" onclick="markFileUnread('${escapeJs(file.relative_path)}'); closeAllDropdowns();">
                                        📚 Mark Unread
                                    </button>`
                                    : `<button class="dropdown-item" onclick="markFileRead('${escapeJs(file.relative_path)}'); closeAllDropdowns();">
                                        ✅ Mark Read
                                    </button>`
                                }
                                <div class="dropdown-divider"></div>
                                <button class="dropdown-item" onclick="processSingleFile('${escapeJs(file.relative_path)}'); closeAllDropdowns();">
                                    🚀 Process
                                </button>
                                <button class="dropdown-item" onclick="renameSingleFile('${escapeJs(file.relative_path)}'); closeAllDropdowns();">
                                    📝 Rename
                                </button>
                                <button class="dropdown-item" onclick="normalizeSingleFile('${escapeJs(file.relative_path)}'); closeAllDropdowns();">
                                    ✨ Normalize
                                </button>
                                <button class="dropdown-item" onclick="removeFileMetadata('${escapeJs(file.relative_path)}'); closeAllDropdowns();" title="Open the archive, delete the embedded ComicInfo.xml, and mark the file as unprocessed so it can be re-processed.">
                                    🧹 Remove Metadata
                                </button>
                                <div class="dropdown-divider"></div>
                                <button class="dropdown-item" onclick="deleteSingleFile('${escapeJs(file.relative_path)}'); closeAllDropdowns();">
                                    🗑️ Delete
                                </button>
                            </div>
                        </div>
                    </div>
                </div>
            `;
        }

        function renderFileList() {
            const fileList = document.getElementById('fileList');

            // Prune selections for files in *loaded* folders; leave selections in
            // unloaded folders alone so the user doesn't lose them.
            const loadedFiles = getAllLoadedFiles();
            const loadedFolderPaths = new Set();
            for (const [k, v] of folderFiles) {
                if (v && v.status === 'loaded') loadedFolderPaths.add(k);
            }
            const currentFilePaths = new Set(loadedFiles.map(f => f.relative_path));
            for (const filepath of Array.from(selectedFiles)) {
                const dir = getFolderForRelativePath(filepath);
                if (loadedFolderPaths.has(dir) && !currentFilePaths.has(filepath)) {
                    selectedFiles.delete(filepath);
                }
            }

            if (folderList.length === 0) {
                if (folderLoading) {
                    fileList.innerHTML = `
                        <div class="loading">
                            <div class="spinner"></div>
                            <p>Loading files...</p>
                        </div>
                    `;
                } else if (searchQuery || filterMode !== 'all') {
                    fileList.innerHTML = `
                        <div class="empty-state">
                            <div class="empty-state-icon">🔍</div>
                            <h2>No matching files found</h2>
                            <p>Try a different search term or filter</p>
                        </div>
                    `;
                } else {
                    fileList.innerHTML = `
                        <div class="empty-state">
                            <div class="empty-state-icon">📁</div>
                            <h2>No comic files found</h2>
                            <p>Add some .cbz or .cbr files to your watched directory</p>
                        </div>
                    `;
                }
                updateSelectInfo();
                updateSelectAllCheckbox();
                ensureScrollObserver();
                return;
            }

            let html = `
                <div class="file-list-header">
                    <input type="checkbox" id="selectAll" onchange="toggleSelectAll(this.checked)" title="Select all loaded files">
                    <button class="toggle-all-btn" onclick="toggleAllFolders()" id="toggleAllBtn" title="Expand/Collapse All">
                        ${allFoldersExpanded ? '▼' : '▶'}
                    </button>
                    <div>File</div>
                    <div>Size</div>
                    <div>Modified</div>
                    <div>Actions</div>
                </div>
            `;

            folderList.forEach(folder => {
                const dir = folder.path || '';
                const isCollapsed = collapsedDirectories.has(dir);
                const entry = folderFiles.get(dir);
                const loadedFiles = (entry && entry.status === 'loaded') ? entry.files : [];

                if (dir) {
                    const allSelected = loadedFiles.length > 0 && loadedFiles.every(f => selectedFiles.has(f.relative_path));
                    const someSelected = loadedFiles.some(f => selectedFiles.has(f.relative_path));
                    html += `
                        <div class="directory-header">
                            <input type="checkbox"
                                   class="directory-checkbox"
                                   ${allSelected ? 'checked' : ''}
                                   ${someSelected && !allSelected ? 'style="opacity: 0.5"' : ''}
                                   onchange="toggleDirectorySelection('${escapeJs(dir)}', this.checked)"
                                   onclick="event.stopPropagation()">
                            <button class="directory-toggle-btn" onclick="toggleDirectory('${escapeJs(dir)}')">
                                <span class="directory-toggle ${isCollapsed ? 'collapsed' : ''}">▼</span>
                            </button>
                            <div class="directory-name-section" onclick="toggleDirectory('${escapeJs(dir)}')">
                                <span class="directory-icon">📁</span>
                                <span class="directory-path">${escapeHtml(dir)}</span>
                            </div>
                            <span class="directory-file-count">${folder.file_count} file${folder.file_count !== 1 ? 's' : ''}</span>
                            <span></span>
                            <span></span>
                        </div>
                    `;
                }

                html += `<div class="directory-content ${isCollapsed ? 'collapsed' : ''}" data-dir="${escapeHtml(dir)}">`;

                if (!isCollapsed) {
                    if (entry && entry.status === 'loaded') {
                        loadedFiles.forEach(file => {
                            html += renderFileRow(file, dir);
                        });
                    } else if (entry && entry.status === 'loading') {
                        html += `
                            <div class="folder-loading">
                                <div class="spinner"></div>
                                <span>Loading files...</span>
                            </div>
                        `;
                    } else if (entry && entry.status === 'error') {
                        html += `
                            <div class="folder-error">
                                Failed to load files in this folder.
                                <button class="btn btn-small" onclick="ensureFolderLoaded('${escapeJs(dir)}', true)">Retry</button>
                            </div>
                        `;
                    }
                }

                html += `</div>`;
            });

            fileList.innerHTML = html;

            updateSelectInfo();
            updateSelectAllCheckbox();
            updateToggleAllButton();
            ensureScrollObserver();
        }
        
        function formatFileSize(bytes) {
            if (bytes < 1024) return bytes + ' B';
            if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
            return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
        }
        
        function formatModifiedDate(timestamp) {
            const date = new Date(timestamp * 1000); // Convert Unix timestamp to milliseconds
            const now = new Date();
            const diffMs = now - date;
            const diffDays = Math.floor(diffMs / (1000 * 60 * 60 * 24));
            
            // Show relative time for recent files
            if (diffDays === 0) {
                const diffHours = Math.floor(diffMs / (1000 * 60 * 60));
                if (diffHours === 0) {
                    const diffMinutes = Math.floor(diffMs / (1000 * 60));
                    if (diffMinutes < 1) return 'Just now';
                    return `${diffMinutes} min ago`;
                }
                return `${diffHours}h ago`;
            } else if (diffDays === 1) {
                return 'Yesterday';
            } else if (diffDays < 7) {
                return `${diffDays} days ago`;
            }
            
            // Show date for older files
            const year = date.getFullYear();
            const month = String(date.getMonth() + 1).padStart(2, '0');
            const day = String(date.getDate()).padStart(2, '0');
            
            // Show year only if different from current year
            if (year !== now.getFullYear()) {
                return `${year}-${month}-${day}`;
            }
            return `${month}-${day}`;
        }
        
        function escapeHtml(text) {
            const div = document.createElement('div');
            div.textContent = text;
            return div.innerHTML;
        }
        
        function escapeJs(text) {
            // Produce a value safe for use inside a single-quoted JavaScript string
            // that is itself embedded in a double-quoted HTML attribute (e.g.
            // onclick="doStuff('${escapeJs(value)}')"). Backslashes, single quotes,
            // and control characters are JS-escaped. The double-quote character must
            // be HTML-entity-encoded — not JS-escaped — because the browser parses
            // the attribute value before handing it to the JS parser, so a literal
            // " would otherwise terminate the attribute and break the markup
            // (e.g. for series titles like: Hazure Zokusei "Hikari Mahou" ga ...).
            // After HTML decoding the JS sees a plain " inside a single-quoted
            // string, which is valid.
            return text.replace(/\\/g, '\\\\')
                       .replace(/'/g, "\\'")
                       .replace(/"/g, '&quot;')
                       .replace(/\n/g, '\\n')
                       .replace(/\r/g, '\\r')
                       .replace(/\t/g, '\\t');
        }

        // Encode a folder path for use in URLs (URL-safe base64, matching the
        // server-side DecodeBase64UrlSafe helper).
        function encodeFolderPath(p) {
            try {
                const b64 = btoa(unescape(encodeURIComponent(p || '')));
                return b64.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
            } catch (e) {
                return '';
            }
        }

        function getAllLoadedFiles() {
            const out = [];
            for (const [, v] of folderFiles) {
                if (v && v.status === 'loaded' && Array.isArray(v.files)) {
                    out.push(...v.files);
                }
            }
            return out;
        }

        function getCurrentSelectableFilePaths() {
            if (libraryViewMode === 'series' && currentSeriesDetailId) {
                const cached = seriesIssuesCache.get(currentSeriesDetailId);
                if (!cached || !Array.isArray(cached.issues)) {
                    return [];
                }

                return cached.issues
                    .map(issue => issue.file_path)
                    .filter(path => typeof path === 'string' && path.length > 0);
            }

            return getAllLoadedFiles()
                .map(file => file.relative_path)
                .filter(path => typeof path === 'string' && path.length > 0);
        }

        function updateSeriesIssueSelectionState(filepath, checked) {
            document.querySelectorAll('.series-issue-card[data-file-path]').forEach(card => {
                if (card.dataset.filePath === filepath) {
                    card.classList.toggle('series-issue-card--selected', checked);
                }
            });
        }

        function getFolderForRelativePath(relativePath) {
            if (!relativePath) return '';
            const idx = Math.max(relativePath.lastIndexOf('/'), relativePath.lastIndexOf('\\'));
            return idx >= 0 ? relativePath.substring(0, idx) : '';
        }

        function invalidateFolderForFile(relativePath) {
            const dir = getFolderForRelativePath(relativePath);
            const entry = folderFiles.get(dir);
            if (!entry) return;
            const wasExpanded = !collapsedDirectories.has(dir) && entry.status === 'loaded';
            folderFiles.delete(dir);
            if (wasExpanded) {
                ensureFolderLoaded(dir, true).catch(() => {});
            }
        }

        async function ensureFolderLoaded(dir, force = false) {
            const existing = folderFiles.get(dir);
            if (!force && existing && existing.status === 'loaded') return;
            if (existing && existing.status === 'loading') return existing.promise;

            const encoded = encodeFolderPath(dir);
            let url = apiUrl(`/api/files/folders/${encoded}/files`);
            const params = [];
            if (searchQuery) params.push(`search=${encodeURIComponent(searchQuery)}`);
            if (filterMode !== 'all') params.push(`filter=${encodeURIComponent(filterMode)}`);
            if (sortMode !== 'name') params.push(`sort=${encodeURIComponent(sortMode)}`);
            if (sortDirection !== 'asc') params.push(`direction=${encodeURIComponent(sortDirection)}`);
            if (params.length) url += '?' + params.join('&');

            const fetchPromise = (async () => {
                try {
                    const response = await fetch(url, { headers: getAuthHeaders() });
                    if (handleAuthError(response)) {
                        folderFiles.set(dir, { status: 'error', files: [] });
                        return;
                    }
                    if (!response.ok) {
                        throw new Error(`HTTP error! status: ${response.status}`);
                    }
                    const data = await response.json();
                    folderFiles.set(dir, { status: 'loaded', files: data.files || [] });
                } catch (err) {
                    folderFiles.set(dir, { status: 'error', files: [] });
                } finally {
                    renderFileList();
                }
            })();

            folderFiles.set(dir, { status: 'loading', files: [], promise: fetchPromise });
            renderFileList();
            return fetchPromise;
        }

        function ensureScrollObserver() {
            if (scrollObserver) {
                try { scrollObserver.disconnect(); } catch (e) { /* ignore */ }
            }
            const fileList = document.getElementById('fileList');
            if (!fileList) return;
            // The sentinel must live INSIDE the scrolling container (`.file-list`)
            // and be observed against that container as the IO root. Otherwise the
            // sentinel — sitting at the bottom of the viewport because `.file-list`
            // has `flex: 1` — would be perpetually within the 400 px rootMargin and
            // trigger an infinite "load more → re-render → re-observe" loop. That
            // loop is what made the list flicker and items unclickable on desktop
            // and could leave the list appearing cut off or never displayed at all
            // on smaller viewports.
            let sentinel = document.getElementById('libraryScrollSentinel');
            if (!sentinel) {
                sentinel = document.createElement('div');
                sentinel.id = 'libraryScrollSentinel';
            }
            if (sentinel.parentElement !== fileList) {
                fileList.appendChild(sentinel);
            } else if (sentinel !== fileList.lastElementChild) {
                fileList.appendChild(sentinel);
            }
            scrollObserver = new IntersectionObserver((entries) => {
                for (const e of entries) {
                    if (!e.isIntersecting) continue;
                    if (libraryViewMode === 'series') {
                        if (!seriesLoading && seriesOffset < seriesTotal) {
                            loadSeriesLibrary({ append: true });
                        }
                    } else {
                        if (!folderLoading && folderOffset < folderTotal) {
                            loadFolders({ append: true });
                        }
                    }
                }
            }, { root: fileList, rootMargin: '400px' });
            scrollObserver.observe(sentinel);
        }


        // Extract just the filename portion from a relative or absolute path.
        function extractDisplayName(filepath) {
            if (!filepath) return '';
            const parts = filepath.split(/[/\\]/);
            return parts[parts.length - 1] || filepath;
        }
        
        function truncateFilenameMiddle(filename) {
            // Split filename into start and end parts for middle truncation
            // This ensures the file extension is always visible
            if (filename.length <= FILENAME_TRUNCATION_START_LENGTH + FILENAME_TRUNCATION_END_LENGTH) {
                // Filename is short enough, no truncation needed
                return {
                    start: filename,
                    end: ''
                };
            }
            
            // Find the last dot for extension
            const lastDotIndex = filename.lastIndexOf('.');
            const hasExtension = lastDotIndex > 0 && lastDotIndex > filename.length - MAX_EXTENSION_LENGTH;
            
            if (hasExtension) {
                // Preserve extension
                const extension = filename.substring(lastDotIndex);
                const nameWithoutExt = filename.substring(0, lastDotIndex);
                
                // Calculate how much of the name we can show
                const remainingForName = FILENAME_TRUNCATION_END_LENGTH - extension.length;
                
                if (nameWithoutExt.length <= FILENAME_TRUNCATION_START_LENGTH + remainingForName) {
                    // Can show the whole name
                    return {
                        start: nameWithoutExt,
                        end: extension
                    };
                }
                
                // Need to truncate
                const start = nameWithoutExt.substring(0, FILENAME_TRUNCATION_START_LENGTH);
                const end = nameWithoutExt.substring(nameWithoutExt.length - remainingForName) + extension;
                
                return {
                    start: start + TRUNCATION_ELLIPSIS,
                    end: end
                };
            } else {
                // No clear extension, just split at character count
                const start = filename.substring(0, FILENAME_TRUNCATION_START_LENGTH);
                const end = filename.substring(filename.length - FILENAME_TRUNCATION_END_LENGTH);
                
                return {
                    start: start + TRUNCATION_ELLIPSIS,
                    end: end
                };
            }
        }
        
        function getDropdownId(filepath) {
            // Generate a consistent dropdown ID from filepath
            // This must match the ID used in the HTML generation
            return 'dropdown-' + filepath.replace(/[^a-zA-Z0-9]/g, '_');
        }
        
        function toggleSelectAll(checked) {
            const visibleFilePaths = getCurrentSelectableFilePaths();
            if (checked) {
                visibleFilePaths.forEach(filepath => selectedFiles.add(filepath));
            } else {
                visibleFilePaths.forEach(filepath => selectedFiles.delete(filepath));
            }
            if (libraryViewMode === 'series' && currentSeriesDetailId) {
                renderSeriesDetail(currentSeriesDetailId);
            } else {
                renderFileList();
            }
        }
        
        function toggleFileSelection(filepath, checked) {
            if (checked) {
                selectedFiles.add(filepath);
            } else {
                selectedFiles.delete(filepath);
            }
            updateSeriesIssueSelectionState(filepath, checked);
            updateSelectInfo();
            updateSelectAllCheckbox();
        }
        
        function updateSelectInfo() {
            const count = selectedFiles.size;
            const info = document.getElementById('selectInfo');
            const batchBtn = document.getElementById('batchUpdateBtn');
            const deleteSelectedBtn = document.getElementById('deleteSelectedBtn');
            const processSelectedItem = document.getElementById('processSelectedItem');
            const renameSelectedItem = document.getElementById('renameSelectedItem');
            const normalizeSelectedItem = document.getElementById('normalizeSelectedItem');
            const markSelectedReadItem = document.getElementById('markSelectedReadItem');
            const markSelectedUnreadItem = document.getElementById('markSelectedUnreadItem');
            const clearSelectedStatusItem = document.getElementById('clearSelectedStatusItem');
            const removeMetadataSelectedItem = document.getElementById('removeMetadataSelectedItem');
            
            if (count === 0) {
                info.textContent = 'No files selected';
                batchBtn.disabled = true;
                if (deleteSelectedBtn) deleteSelectedBtn.disabled = true;
                if (processSelectedItem) processSelectedItem.disabled = true;
                if (renameSelectedItem) renameSelectedItem.disabled = true;
                if (normalizeSelectedItem) normalizeSelectedItem.disabled = true;
                if (markSelectedReadItem) markSelectedReadItem.disabled = true;
                if (markSelectedUnreadItem) markSelectedUnreadItem.disabled = true;
                if (clearSelectedStatusItem) clearSelectedStatusItem.disabled = true;
                if (removeMetadataSelectedItem) removeMetadataSelectedItem.disabled = true;
            } else {
                info.textContent = `${count} file${count > 1 ? 's' : ''} selected`;
                batchBtn.disabled = false;
                if (deleteSelectedBtn) deleteSelectedBtn.disabled = false;
                if (processSelectedItem) processSelectedItem.disabled = false;
                if (renameSelectedItem) renameSelectedItem.disabled = false;
                if (normalizeSelectedItem) normalizeSelectedItem.disabled = false;
                if (markSelectedReadItem) markSelectedReadItem.disabled = false;
                if (markSelectedUnreadItem) markSelectedUnreadItem.disabled = false;
                if (clearSelectedStatusItem) clearSelectedStatusItem.disabled = false;
                if (removeMetadataSelectedItem) removeMetadataSelectedItem.disabled = false;
            }
        }
        
        function updateSelectAllCheckbox() {
            const selectAllCheckbox = document.getElementById('selectAll');
            if (!selectAllCheckbox) return;

            const visibleFilePaths = getCurrentSelectableFilePaths();
            if (visibleFilePaths.length === 0) {
                selectAllCheckbox.checked = false;
                selectAllCheckbox.indeterminate = false;
            } else {
                const allSelected = visibleFilePaths.every(filepath => selectedFiles.has(filepath));
                const someSelected = visibleFilePaths.some(filepath => selectedFiles.has(filepath));

                selectAllCheckbox.checked = allSelected;
                selectAllCheckbox.indeterminate = someSelected && !allSelected;
            }
        }

        async function toggleDirectory(dir) {
            const wasCollapsed = collapsedDirectories.has(dir);
            if (wasCollapsed) {
                collapsedDirectories.delete(dir);
                renderFileList();
                await ensureFolderLoaded(dir);
            } else {
                collapsedDirectories.add(dir);
                renderFileList();
            }
            updateToggleAllButton();
        }

        async function toggleAllFolders() {
            if (allFoldersExpanded) {
                collapseAllFolders();
            } else {
                await expandAllFolders();
            }
        }

        async function expandAllFolders() {
            collapsedDirectories.clear();
            allFoldersExpanded = true;
            renderFileList();

            // Concurrency-limited loader for unloaded folders.
            const concurrency = 4;
            const queue = folderList
                .map(f => f.path || '')
                .filter(dir => {
                    const e = folderFiles.get(dir);
                    return !e || e.status === 'error';
                });
            let active = 0;
            let index = 0;
            await new Promise(resolve => {
                const launch = () => {
                    while (active < concurrency && index < queue.length) {
                        const dir = queue[index++];
                        active++;
                        ensureFolderLoaded(dir).catch(() => {}).finally(() => {
                            active--;
                            if (index >= queue.length && active === 0) {
                                resolve();
                            } else {
                                launch();
                            }
                        });
                    }
                    if (queue.length === 0) resolve();
                };
                launch();
            });
            updateToggleAllButton();
        }

        function collapseAllFolders() {
            collapsedDirectories = new Set(folderList.map(f => f.path || ''));
            allFoldersExpanded = false;
            renderFileList();
        }

        function updateToggleAllButton() {
            const totalDirs = folderList.length;
            if (totalDirs > 0 && collapsedDirectories.size >= totalDirs) {
                allFoldersExpanded = false;
            } else {
                allFoldersExpanded = true;
            }
        }

        async function toggleDirectorySelection(dir, checked) {
            const entry = folderFiles.get(dir);
            if (!entry || entry.status !== 'loaded') {
                await ensureFolderLoaded(dir);
            }
            const refreshed = folderFiles.get(dir);
            const dirFiles = (refreshed && refreshed.status === 'loaded') ? refreshed.files : [];
            dirFiles.forEach(file => {
                if (checked) {
                    selectedFiles.add(file.relative_path);
                } else {
                    selectedFiles.delete(file.relative_path);
                }
            });
            renderFileList();
        }
        
        function showFileInfo(filepath, filename) {
            // Show file information in a modal
            document.getElementById('fileInfoPath').textContent = filepath;
            document.getElementById('fileInfoName').textContent = filename;
            
            // Find the file in the loaded files to get size info
            const file = getAllLoadedFiles().find(f => f.relative_path === filepath);
            const duplicateReview = document.getElementById('fileInfoDuplicateReview');
            const duplicateReviewBtn = document.getElementById('fileInfoDuplicateReviewBtn');
            if (file) {
                document.getElementById('fileInfoSize').textContent = formatFileSize(file.size);
                document.getElementById('fileInfoProcessed').textContent = file.processed ? '✅ Yes (Renamed & Normalized)' : '⚠️ No';
                document.getElementById('fileInfoRenamed').textContent = file.renamed ? '🔵 Yes' : 'No';
                document.getElementById('fileInfoNormalized').textContent = file.normalized ? '🔴 Yes' : 'No';
                document.getElementById('fileInfoDuplicate').textContent = file.duplicate ? '🔁 Yes' : 'No';
                if (duplicateReview && duplicateReviewBtn) {
                    duplicateReview.style.display = file.duplicate ? 'block' : 'none';
                    if (file.duplicate) {
                        duplicateReviewBtn.dataset.filepath = filepath;
                    } else {
                        delete duplicateReviewBtn.dataset.filepath;
                    }
                }
            } else if (duplicateReview && duplicateReviewBtn) {
                duplicateReview.style.display = 'none';
                delete duplicateReviewBtn.dataset.filepath;
            }
            
            document.getElementById('fileInfoModal').classList.add('active');
        }
        
        function closeFileInfoModal() {
            document.getElementById('fileInfoModal').classList.remove('active');
        }

        function openDuplicateReviewFromFileInfo() {
            const duplicateReviewBtn = document.getElementById('fileInfoDuplicateReviewBtn');
            const filepath = duplicateReviewBtn?.dataset.filepath;
            if (!filepath) {
                return;
            }

            closeFileInfoModal();
            openDuplicateReviewModal(filepath);
        }

        // Open the duplicate review modal and optionally focus a specific duplicate file when provided.
        async function openDuplicateReviewModal(initialFilepath = null) {
            const modal = document.getElementById('duplicateReviewModal');
            const emptyState = document.getElementById('duplicateReviewEmptyState');
            const content = document.getElementById('duplicateReviewContent');

            modal.classList.add('active');
            emptyState.style.display = 'none';
            emptyState.textContent = 'No duplicate files are waiting for review.';
            content.style.display = 'block';
            document.getElementById('duplicateReviewName').textContent = 'Loading duplicates...';
            document.getElementById('duplicateReviewPath').textContent = '';

            try {
                const response = await fetch(apiUrl('/api/files?filter=duplicates&per_page=-1&sort=name'), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }

                const data = await response.json();
                duplicateReviewFiles = Array.isArray(data.files) ? data.files : [];

                if (duplicateReviewFiles.length === 0) {
                    emptyState.style.display = 'block';
                    content.style.display = 'none';
                    updateDuplicateReviewNavigation();
                    return;
                }

                const matchedIndex = initialFilepath
                    ? duplicateReviewFiles.findIndex(file => file.relative_path === initialFilepath)
                    : -1;
                duplicateReviewIndex = matchedIndex >= 0 ? matchedIndex : 0;
                renderDuplicateReviewItem();
            } catch (error) {
                console.error('Failed to load duplicates for review:', error);
                emptyState.style.display = 'block';
                emptyState.textContent = `Failed to load duplicates: ${error.message}`;
                content.style.display = 'none';
                updateDuplicateReviewNavigation();
            }
        }

        function closeDuplicateReviewModal() {
            document.getElementById('duplicateReviewModal').classList.remove('active');
        }

        function updateDuplicateReviewNavigation() {
            const total = duplicateReviewFiles.length;
            const counter = document.getElementById('duplicateReviewCounter');
            const prevBtn = document.getElementById('duplicateReviewPrevBtn');
            const nextBtn = document.getElementById('duplicateReviewNextBtn');

            counter.textContent = total > 0 ? `${duplicateReviewIndex + 1} of ${total}` : '0 of 0';
            prevBtn.disabled = total === 0 || duplicateReviewIndex <= 0;
            nextBtn.disabled = total === 0 || duplicateReviewIndex >= total - 1;
        }

        function renderDuplicateReviewItem() {
            const emptyState = document.getElementById('duplicateReviewEmptyState');
            const content = document.getElementById('duplicateReviewContent');
            const file = duplicateReviewFiles[duplicateReviewIndex];

            if (!file) {
                emptyState.style.display = 'block';
                content.style.display = 'none';
                updateDuplicateReviewNavigation();
                return;
            }

            emptyState.style.display = 'none';
            content.style.display = 'block';

            document.getElementById('duplicateReviewName').textContent = file.name;
            document.getElementById('duplicateReviewPath').textContent = file.relative_path;
            document.getElementById('duplicateReviewSize').textContent = formatFileSize(file.size);
            document.getElementById('duplicateReviewModified').textContent = formatModifiedDate(file.modified);
            document.getElementById('duplicateReviewReadStatus').textContent = file.read ? '👁️ Read' : '📚 Unread';
            document.getElementById('duplicateReviewReadToggleBtn').textContent = file.read ? '📚 Mark Unread' : '✅ Mark Read';

            updateDuplicateReviewNavigation();
        }

        function changeDuplicateReviewItem(direction) {
            const nextIndex = duplicateReviewIndex + direction;
            if (nextIndex < 0 || nextIndex >= duplicateReviewFiles.length) {
                return;
            }

            duplicateReviewIndex = nextIndex;
            renderDuplicateReviewItem();
        }

        function getCurrentDuplicateReviewFile() {
            return duplicateReviewFiles[duplicateReviewIndex] || null;
        }

        function openDuplicateInfo() {
            const file = getCurrentDuplicateReviewFile();
            if (!file) return;

            closeDuplicateReviewModal();
            showFileInfo(file.relative_path, file.name);
        }

        function openDuplicateTags() {
            const file = getCurrentDuplicateReviewFile();
            if (!file) return;

            closeDuplicateReviewModal();
            viewTags(file.relative_path);
        }

        function openDuplicateReader() {
            const file = getCurrentDuplicateReviewFile();
            if (!file) return;

            closeDuplicateReviewModal();
            readComic(file.relative_path);
        }

        async function toggleDuplicateReadStatus() {
            const file = getCurrentDuplicateReviewFile();
            if (!file) return;

            if (file.read) {
                await markFileUnread(file.relative_path);
            } else {
                await markFileRead(file.relative_path);
            }

            await openDuplicateReviewModal(file.relative_path);
        }

        async function deleteCurrentDuplicate() {
            const file = getCurrentDuplicateReviewFile();
            if (!file) return;

            await deleteSingleFile(file.relative_path);
            await openDuplicateReviewModal();
        }

        // Per-series folder list state. Holds the latest response from
        // /api/files/series/{id}/folders so the "Merge Folders" button can
        // hand off to the existing combine-folders modal without re-fetching.
        let seriesFoldersCurrent = null;

        async function openSeriesFoldersModal(seriesId, seriesTitle) {
            if (!seriesId) return;
            const modal = document.getElementById('seriesFoldersModal');
            const list = document.getElementById('seriesFoldersList');
            const emptyState = document.getElementById('seriesFoldersEmptyState');
            const mergeBtn = document.getElementById('seriesFoldersMergeBtn');
            const mergeNote = document.getElementById('seriesFoldersMergeNote');
            const titleEl = document.getElementById('seriesFoldersModalTitle');

            titleEl.textContent = seriesTitle ? `Folders for "${seriesTitle}"` : 'Series Folders';
            list.innerHTML = '<div class="loading"><div class="spinner"></div><p>Loading folders...</p></div>';
            emptyState.style.display = 'none';
            mergeBtn.disabled = true;
            mergeNote.style.display = 'none';
            mergeNote.textContent = '';
            seriesFoldersCurrent = null;
            modal.classList.add('active');

            try {
                const response = await fetch(apiUrl(`/api/files/series/${encodeURIComponent(seriesId)}/folders`), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                const data = await response.json();
                seriesFoldersCurrent = data;
                const folders = Array.isArray(data.folders) ? data.folders : [];

                if (folders.length === 0) {
                    list.innerHTML = '';
                    emptyState.style.display = 'block';
                    return;
                }

                list.innerHTML = folders.map(folder => `
                    <div class="combine-folder-row" style="padding: 8px 0; border-bottom: 1px solid var(--border, #ddd);">
                        <div style="font-weight: 500; word-break: break-all;">${escapeHtml(folder.directory)}</div>
                        <div class="combine-folders-meta">${folder.file_count} file${folder.file_count === 1 ? '' : 's'} · ${formatFileSize(folder.total_size)}</div>
                    </div>
                `).join('');

                if (folders.length < 2) {
                    mergeBtn.disabled = true;
                    mergeNote.style.display = 'block';
                    mergeNote.textContent = 'This series lives in a single folder — nothing to merge.';
                } else if (data.combine_group_key) {
                    mergeBtn.disabled = false;
                    mergeNote.style.display = 'block';
                    mergeNote.textContent = 'Click "Merge Folders" to combine these folders into one on disk.';
                } else {
                    // The API didn't return a combine group key for some
                    // reason (older server, unexpected error). Fall back to
                    // the global combine flow rather than blocking the user.
                    mergeBtn.disabled = true;
                    mergeNote.style.display = 'block';
                    mergeNote.textContent = 'Use the global "Combine Folders" tool from the toolbar to merge these folders.';
                }
            } catch (error) {
                console.error('Failed to load series folders:', error);
                list.innerHTML = '';
                emptyState.style.display = 'block';
                emptyState.textContent = `Failed to load folders: ${error.message}`;
            }
        }

        function closeSeriesFoldersModal() {
            document.getElementById('seriesFoldersModal').classList.remove('active');
            seriesFoldersCurrent = null;
        }

        async function openMergeForSeriesFolders() {
            if (!seriesFoldersCurrent || !seriesFoldersCurrent.combine_group_key) {
                return;
            }
            const targetGroupKey = seriesFoldersCurrent.combine_group_key;
            const suggestedDestination = seriesFoldersCurrent.suggested_destination_directory || null;
            // Hand off to the existing combine-folders modal, then jump to the
            // group that corresponds to this series so the user can review and
            // confirm using the same UI as the global combine flow.
            closeSeriesFoldersModal();
            await openCombineFoldersModal();
            const matchIndex = combineFolderGroups.findIndex(g => g && g.groupKey === targetGroupKey);
            if (matchIndex >= 0) {
                combineFolderIndex = matchIndex;
                combineFolderSelectedDestination = suggestedDestination;
                renderCombineFolderGroup();
                updateCombineFoldersNavigation();
            }
        }

        async function openCombineFoldersModal() {
            const modal = document.getElementById('combineFoldersModal');
            const emptyState = document.getElementById('combineFoldersEmptyState');
            const content = document.getElementById('combineFoldersContent');

            modal.classList.add('active');
            emptyState.style.display = 'none';
            emptyState.textContent = 'No folders look combinable right now.';
            content.style.display = 'block';
            document.getElementById('combineFoldersGroupTitle').textContent = 'Loading combinable folders...';
            document.getElementById('combineFoldersGroupMeta').textContent = '';
            document.getElementById('combineFoldersSuggestion').textContent = '';
            document.getElementById('combineFoldersList').innerHTML = '';
            document.getElementById('combineFoldersPreview').style.display = 'none';
            document.getElementById('combineFoldersPreview').innerHTML = '';
            document.getElementById('combineFoldersPreviewBtn').disabled = true;
            document.getElementById('combineFoldersConfirmBtn').disabled = true;

            try {
                const response = await fetch(apiUrl('/api/files/combinable-folders'), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                const data = await response.json();
                combineFolderGroups = Array.isArray(data.groups) ? data.groups : [];
                combineFolderIndex = 0;
                combineFolderSelectedDestination = null;

                if (combineFolderGroups.length === 0) {
                    emptyState.style.display = 'block';
                    content.style.display = 'none';
                    updateCombineFoldersNavigation();
                    return;
                }

                renderCombineFolderGroup();
            } catch (error) {
                console.error('Failed to load combinable folders:', error);
                emptyState.style.display = 'block';
                emptyState.textContent = `Failed to load combinable folders: ${error.message}`;
                content.style.display = 'none';
                updateCombineFoldersNavigation();
            }
        }

        function closeCombineFoldersModal() {
            document.getElementById('combineFoldersModal').classList.remove('active');
        }

        function updateCombineFoldersNavigation() {
            const total = combineFolderGroups.length;
            const counter = document.getElementById('combineFoldersCounter');
            const prevBtn = document.getElementById('combineFoldersPrevBtn');
            const nextBtn = document.getElementById('combineFoldersNextBtn');

            counter.textContent = total > 0 ? `${combineFolderIndex + 1} of ${total}` : '0 of 0';
            prevBtn.disabled = total === 0 || combineFolderIndex <= 0;
            nextBtn.disabled = total === 0 || combineFolderIndex >= total - 1;
        }

        function changeCombineFolderGroup(direction) {
            const next = combineFolderIndex + direction;
            if (next < 0 || next >= combineFolderGroups.length) {
                return;
            }
            combineFolderIndex = next;
            combineFolderSelectedDestination = null;
            renderCombineFolderGroup();
        }

        function getCurrentCombineFolderGroup() {
            return combineFolderGroups[combineFolderIndex] || null;
        }

        function formatCombineFolderDate(value) {
            if (!value) return '—';
            try {
                const d = new Date(value);
                if (isNaN(d.getTime())) return '—';
                return d.toLocaleString();
            } catch (e) {
                return '—';
            }
        }

        function renderCombineFolderGroup() {
            const group = getCurrentCombineFolderGroup();
            const emptyState = document.getElementById('combineFoldersEmptyState');
            const content = document.getElementById('combineFoldersContent');
            const previewPanel = document.getElementById('combineFoldersPreview');

            previewPanel.style.display = 'none';
            previewPanel.innerHTML = '';

            if (!group) {
                emptyState.style.display = 'block';
                content.style.display = 'none';
                document.getElementById('combineFoldersPreviewBtn').disabled = true;
                document.getElementById('combineFoldersConfirmBtn').disabled = true;
                updateCombineFoldersNavigation();
                return;
            }

            emptyState.style.display = 'none';
            content.style.display = 'block';

            const title = group.volume
                ? `${group.seriesName} · Vol. ${group.volume}`
                : group.seriesName;
            document.getElementById('combineFoldersGroupTitle').textContent = title || 'Combinable folders';
            document.getElementById('combineFoldersGroupMeta').textContent =
                `${group.folders.length} folder${group.folders.length === 1 ? '' : 's'} · ${group.totalFileCount} file${group.totalFileCount === 1 ? '' : 's'}`;
            document.getElementById('combineFoldersSuggestion').textContent = group.suggestionReason || '';

            if (!combineFolderSelectedDestination) {
                combineFolderSelectedDestination = group.suggestedDestinationDirectory;
            }
            // Ensure selection is part of the group; otherwise reset to suggested.
            if (!group.folders.some(f => f.directory === combineFolderSelectedDestination)) {
                combineFolderSelectedDestination = group.suggestedDestinationDirectory;
            }

            const list = document.getElementById('combineFoldersList');
            list.innerHTML = '';
            group.folders.forEach((folder, idx) => {
                const isSelected = folder.directory === combineFolderSelectedDestination;
                const isSuggested = folder.directory === group.suggestedDestinationDirectory;
                const card = document.createElement('div');
                card.className = 'combine-folder-card'
                    + (isSelected ? ' selected' : '')
                    + (isSuggested ? ' suggested' : '');
                card.setAttribute('role', 'button');
                card.tabIndex = 0;

                const radioId = `combineFolderRadio_${combineFolderIndex}_${idx}`;
                const sample = (folder.sampleFileNames || []).slice(0, 5);
                const sampleHtml = sample.length
                    ? `<div class="combine-folder-files">Sample files:<ul>${sample.map(n => `<li>${escapeHtml(n)}</li>`).join('')}</ul></div>`
                    : '';

                card.innerHTML = `
                    <div class="combine-folder-card-header">
                        <input type="radio" name="combineFolderDestination" id="${radioId}" ${isSelected ? 'checked' : ''}>
                        <label for="${radioId}" class="combine-folder-path">${escapeHtml(folder.directory)}</label>
                    </div>
                    <div class="combine-folder-stats">
                        <div>Files <strong>${folder.fileCount.toLocaleString()}</strong></div>
                        <div>Total size <strong>${formatFileSize(folder.totalSize)}</strong></div>
                        <div>Newest file <strong>${formatCombineFolderDate(folder.newestFileAddedAt)}</strong></div>
                        <div>Oldest file <strong>${formatCombineFolderDate(folder.oldestFileAddedAt)}</strong></div>
                    </div>
                    ${sampleHtml}
                `;

                const selectThis = () => {
                    combineFolderSelectedDestination = folder.directory;
                    renderCombineFolderGroup();
                };
                card.addEventListener('click', (ev) => {
                    if (ev.target && ev.target.tagName === 'A') return;
                    selectThis();
                });
                card.addEventListener('keydown', (ev) => {
                    if (ev.key === 'Enter' || ev.key === ' ') {
                        ev.preventDefault();
                        selectThis();
                    }
                });
                list.appendChild(card);
            });

            const canCombine = group.folders.length >= 2 && !!combineFolderSelectedDestination;
            document.getElementById('combineFoldersPreviewBtn').disabled = !canCombine || combineFolderActionInFlight;
            document.getElementById('combineFoldersConfirmBtn').disabled = !canCombine || combineFolderActionInFlight;
            updateCombineFoldersNavigation();
        }

        function buildCombineFoldersRequestBody() {
            const group = getCurrentCombineFolderGroup();
            if (!group || !combineFolderSelectedDestination) {
                return null;
            }
            const sources = group.folders
                .map(f => f.directory)
                .filter(d => d !== combineFolderSelectedDestination);
            return {
                groupKey: group.groupKey,
                destinationDirectory: combineFolderSelectedDestination,
                sourceDirectories: sources
            };
        }

        async function previewCombineFolders() {
            const body = buildCombineFoldersRequestBody();
            if (!body) return;
            const previewPanel = document.getElementById('combineFoldersPreview');
            previewPanel.style.display = 'block';
            previewPanel.innerHTML = '<em>Building preview...</em>';
            combineFolderActionInFlight = true;
            document.getElementById('combineFoldersPreviewBtn').disabled = true;
            document.getElementById('combineFoldersConfirmBtn').disabled = true;
            try {
                const response = await fetch(apiUrl('/api/files/combine-folders/preview'), {
                    method: 'POST',
                    headers: {
                        ...getAuthHeaders(),
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify(body)
                });
                if (handleAuthError(response)) return;
                const data = await response.json();
                if (!response.ok) {
                    throw new Error(data.error || `HTTP error! status: ${response.status}`);
                }
                const moves = Array.isArray(data.moves) ? data.moves : [];
                if (moves.length === 0) {
                    previewPanel.innerHTML = '<em>No files will be moved.</em>';
                } else {
                    const conflicts = moves.filter(m => m.conflict).length;
                    const skipped = moves.filter(m => m.skipped).length;
                    const list = moves.map(m => {
                        const cls = m.skipped ? 'skipped' : (m.conflict ? 'conflict' : '');
                        const note = m.skipped ? ' (skipped)' : (m.conflict ? ' (renamed to avoid conflict)' : '');
                        return `<li class="${cls}">${escapeHtml(m.sourcePath)} → ${escapeHtml(m.destinationPath)}${note}</li>`;
                    }).join('');
                    previewPanel.innerHTML = `
                        <h4>Move plan (${moves.length} file${moves.length === 1 ? '' : 's'}${conflicts ? `, ${conflicts} renamed` : ''}${skipped ? `, ${skipped} skipped` : ''})</h4>
                        <ul>${list}</ul>
                    `;
                }
            } catch (error) {
                console.error('Failed to build combine preview:', error);
                previewPanel.innerHTML = `<span class="conflict">Failed to build preview: ${escapeHtml(error.message)}</span>`;
            } finally {
                combineFolderActionInFlight = false;
                renderCombineFolderGroup();
            }
        }

        async function confirmCombineFolders() {
            const body = buildCombineFoldersRequestBody();
            if (!body) return;
            const group = getCurrentCombineFolderGroup();
            const sourceCount = group ? group.folders.length - 1 : 0;
            const message = `Combine ${sourceCount} folder${sourceCount === 1 ? '' : 's'} into:\n${combineFolderSelectedDestination}\n\nFiles will be moved on disk. Continue?`;
            if (!confirm(message)) {
                return;
            }
            combineFolderActionInFlight = true;
            document.getElementById('combineFoldersPreviewBtn').disabled = true;
            document.getElementById('combineFoldersConfirmBtn').disabled = true;
            try {
                const response = await fetch(apiUrl('/api/files/combine-folders'), {
                    method: 'POST',
                    headers: {
                        ...getAuthHeaders(),
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify(body)
                });
                if (handleAuthError(response)) return;
                const data = await response.json();
                if (!response.ok) {
                    throw new Error(data.error || `HTTP error! status: ${response.status}`);
                }

                // The server now enqueues the moves as a background job and
                // returns 202 immediately with { jobId, totalItems, ... }.
                // Track via the standard SSE-driven progress modal so the user
                // sees per-move progress, then refresh the modal once the job
                // finishes.
                const jobId = data.jobId || data.job_id;
                const totalItems = data.totalItems || data.total_items || 0;
                const skipped = data.skipped || 0;

                if (!jobId || totalItems === 0) {
                    // Nothing to move (everything was skipped). Just refresh.
                    showMessage(`Combine complete: 0 moved, ${skipped} skipped, 0 failed.`, 'info');
                    if (typeof loadLibraryHealth === 'function') loadLibraryHealth();
                    if (typeof refreshFiles === 'function') refreshFiles();
                    await openCombineFoldersModal();
                    return;
                }

                showMessage(`Combining ${totalItems} file(s) in background${skipped > 0 ? ` (${skipped} skipped)` : ''}`, 'info');
                showProgressModal(`Combining ${totalItems} file(s)...`);
                await trackJobStatus(jobId, `Combining ${totalItems} file(s)...`);

                // The progress-modal completion handler already refreshes the
                // library view; refresh the combine-folders modal once the job
                // settles so the just-combined group disappears.
                if (typeof loadLibraryHealth === 'function') loadLibraryHealth();
                if (typeof refreshFiles === 'function') refreshFiles();
                await openCombineFoldersModal();
            } catch (error) {
                console.error('Failed to combine folders:', error);
                showMessage(`Failed to combine folders: ${error.message}`, 'error');
            } finally {
                combineFolderActionInFlight = false;
                renderCombineFolderGroup();
            }
        }
        
        async function viewTags(filepath) {
            try {
                const encodedPath = encodeFilePathForUrl(filepath);
                const response = await fetch(apiUrl(`/api/files/${encodedPath}/tags`), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                const tags = await response.json();
                
                if (tags.error) {
                    showMessage(tags.error, 'error');
                    return;
                }
                
                currentEditFile = filepath;
                
                // Populate form
                Object.keys(tags).forEach(key => {
                    const input = document.getElementById(key);
                    if (input) {
                        input.value = tags[key] || '';
                    }
                });
                
                document.getElementById('modalTitle').textContent = `Edit Tags - ${filepath}`;
                document.getElementById('tagModal').classList.add('active');
            } catch (error) {
                showMessage('Failed to load tags: ' + error.message, 'error');
            }
        }
        
        function readComic(filepath) {
            // Snapshot the current page state so the reader can return us to
            // exactly where we came from when the user exits. Stored in
            // sessionStorage (tab-scoped, auto-cleared when the tab closes).
            try {
                const snapshot = {
                    originUrl: window.location.href,
                    scrollY: window.scrollY || document.documentElement.scrollTop || 0,
                    searchQuery: typeof searchQuery === 'string' ? searchQuery : '',
                    filterMode: typeof filterMode === 'string' ? filterMode : 'all',
                    libraryViewMode: typeof libraryViewMode === 'string' ? libraryViewMode : 'files',
                    currentSeriesDetailId: typeof currentSeriesDetailId !== 'undefined' ? currentSeriesDetailId : null,
                    timestamp: Date.now()
                };
                sessionStorage.setItem('comicReaderReturnState', JSON.stringify(snapshot));
            } catch (_e) {
                // sessionStorage can throw in private modes — ignore.
            }
            // Open comic reader in the same window
            window.location.href = `/reader.html?file=${encodeURIComponent(filepath)}`;
        }
        
        function closeModal() {
            document.getElementById('tagModal').classList.remove('active');
            currentEditFile = null;
        }
        
        async function saveTags() {
            if (!currentEditFile) return;

            // Capture the target file path before calling closeModal(), which
            // resets currentEditFile to null. Without this snapshot, the
            // request body below would serialize Files:[null] and the server
            // would silently fail to update any file's metadata.
            const targetFile = currentEditFile;

            const form = document.getElementById('tagForm');
            const formData = new FormData(form);
            const metadata = {};
            
            // Only include non-empty fields. Empty inputs (e.g. an empty Year)
            // would fail JSON deserialization on the server because
            // ComicMetadata.Year is int? — sending "" would cause a 400 and
            // the file's tags would never get written.
            for (let [key, value] of formData.entries()) {
                if (typeof value !== 'string' || !value.trim()) continue;
                // Capitalize first letter to match ComicMetadata property names
                const propertyName = key.charAt(0).toUpperCase() + key.slice(1);
                if (propertyName === 'Year') {
                    const parsedYear = parseInt(value, 10);
                    if (!isNaN(parsedYear)) {
                        metadata[propertyName] = parsedYear;
                    }
                    continue;
                }
                metadata[propertyName] = value;
            }
            
            closeModal();
            showProgressModal('Updating metadata...');
            
            try {
                console.log('[SINGLE FILE] Starting update metadata file request...');
                // Start the update metadata job using job-based pattern
                const response = await fetch(apiUrl('/api/jobs/update-metadata-selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ Files: [targetFile], Metadata: metadata })
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[SINGLE FILE] Failed to start updating metadata for file (HTTP ${response.status})`);
                    throw new Error('Failed to start update metadata job');
                }
                
                const result = await response.json();
                const jobId = result.job_id;
                
                if (!jobId) {
                    throw new Error('No job ID returned');
                }
                
                console.log(`[SINGLE FILE] Created job ${jobId} for file: ${targetFile}`);
                showMessage(`Started updating metadata for file in background`, 'info');
                
                // Track job status
                await trackJobStatus(jobId, 'Updating Metadata...');
                
            } catch (error) {
                console.error('[SINGLE FILE] Error starting update metadata for file:', error);
                showMessage('Failed to update metadata: ' + error.message, 'error');
                closeProgressModal();
            } finally {
                currentEditFile = null;
            }
        }
        
        function batchUpdateTags() {
            if (selectedFiles.size === 0) return;
            document.getElementById('batchModal').classList.add('active');
        }
        
        function closeBatchModal() {
            document.getElementById('batchModal').classList.remove('active');
            document.getElementById('batchForm').reset();
        }
        
        async function saveBatchTags() {
            const form = document.getElementById('batchForm');
            const formData = new FormData(form);
            const metadata = {};
            
            // Only include non-empty fields
            for (let [key, value] of formData.entries()) {
                if (value.trim()) {
                    // Capitalize first letter to match ComicMetadata property names
                    const propertyName = key.charAt(0).toUpperCase() + key.slice(1);
                    if (propertyName === 'Year') {
                        const parsedYear = parseInt(value, 10);
                        if (!isNaN(parsedYear)) {
                            metadata[propertyName] = parsedYear;
                        }
                        continue;
                    }
                    metadata[propertyName] = value;
                }
            }
            
            if (Object.keys(metadata).length === 0) {
                showMessage('Please enter at least one tag to update', 'error');
                return;
            }
            
            closeBatchModal();
            showProgressModal('Updating metadata...');
            
            const files = Array.from(selectedFiles);
            
            try {
                console.log('[BATCH] Starting update metadata selected files request...');
                // Start the job using the jobs endpoint
                const response = await fetch(apiUrl('/api/jobs/update-metadata-selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ Files: files, Metadata: metadata })
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[BATCH] Failed to start updating metadata for selected files (HTTP ${response.status})`);
                    throw new Error('Failed to start update metadata job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;
                
                console.log(`[BATCH] Created job ${jobId} for ${totalItems} selected files`);
                showMessage(`Started updating metadata for ${totalItems} selected files in background`, 'info');
                
                // Track job status
                await trackJobStatus(jobId, 'Updating Metadata for Selected Files...');
                
            } catch (error) {
                console.error('[BATCH] Error starting update metadata for selected files:', error);
                showMessage('Failed to start updating metadata: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function processAllFiles() {
            if (!confirm('This will process all files in the watched directory. Continue?')) {
                return;
            }

            const forceReprocess = promptForForceReprocess(
                'This will process all files in the watched directory.',
                'processed');
            
            showProgressModal('Starting processing...');
            
            try {
                console.log('[BATCH] Starting process all files request...');
                // Start the job
                const response = await fetch(apiUrl(`/api/jobs/process-all?forceReprocess=${forceReprocess}`), {
                    method: 'POST',
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[BATCH] Failed to start processing all files (HTTP ${response.status})`);
                    throw new Error('Failed to start processing job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;
                
                console.log(`[BATCH] Created job ${jobId} for ${totalItems} files`);
                showMessage(`Started processing ${totalItems} files in background`, 'info');
                
                // Track job status
                await trackJobStatus(jobId, 'Processing All Files...');
                
            } catch (error) {
                console.error('[BATCH] Error starting process all files:', error);
                showMessage('Failed to start processing: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function processAllFilesAsync() {
            if (!confirm('This will process all files in the watched directory asynchronously. Continue?')) {
                return;
            }

            const forceReprocess = promptForForceReprocess(
                'This will process all files in the watched directory asynchronously.',
                'processed');
            
            showProgressModal('Starting async processing...');
            
            try {
                console.log('[BATCH] Starting process all files request...');
                // Start the job
                const response = await fetch(apiUrl(`/api/jobs/process-all?forceReprocess=${forceReprocess}`), {
                    method: 'POST',
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[BATCH] Failed to start processing (HTTP ${response.status})`);
                    throw new Error('Failed to start processing job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;
                
                console.log(`[BATCH] Created job ${jobId} for ${totalItems} files`);
                showMessage(`Started processing ${totalItems} files in background`, 'info');
                
                // Poll for status
                await trackJobStatus(jobId, 'Processing Files...');
                
            } catch (error) {
                console.error('[BATCH] Error starting process all:', error);
                showMessage('Failed to start processing: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function processSelectedFilesAsync() {
            if (selectedFiles.size === 0) {
                showMessage('Please select at least one file to process', 'error');
                return;
            }
            
            if (!confirm(`This will process ${selectedFiles.size} selected file${selectedFiles.size > 1 ? 's' : ''} asynchronously. Continue?`)) {
                return;
            }
            
            const forceReprocess = promptForForceReprocess(
                `This will process ${selectedFiles.size} selected file${selectedFiles.size > 1 ? 's' : ''}.`,
                'processed');

            showProgressModal('Starting async processing...');
            
            const files = Array.from(selectedFiles);
            
            try {
                console.log(`[BATCH] Starting process selected files request (${files.length} files)...`);
                // Start the job
                const response = await fetch(apiUrl('/api/jobs/process-selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({
                        Files: files,
                        ForceReprocess: forceReprocess
                    })
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[BATCH] Failed to start processing (HTTP ${response.status})`);
                    throw new Error('Failed to start processing job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;
                
                console.log(`[BATCH] Created job ${jobId} for ${totalItems} files`);
                showMessage(`Started processing ${totalItems} files in background`, 'info');
                
                // Poll for status
                await trackJobStatus(jobId, 'Processing Selected Files...');
                
            } catch (error) {
                console.error('[BATCH] Error starting process selected:', error);
                showMessage('Failed to start processing: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function clearSelectedProcessedStatus() {
            if (selectedFiles.size === 0) {
                showMessage('Please select at least one file', 'error');
                return;
            }
            const count = selectedFiles.size;
            if (!confirm(
                `Clear the renamed/normalized flags on ${count} selected file${count > 1 ? 's' : ''}?\n\n` +
                'The files will then be re-processed on the next Process / Rename / Normalize run.')) {
                return;
            }

            const files = Array.from(selectedFiles);
            try {
                const response = await fetch(apiUrl('/api/status/clear-selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    credentials: 'same-origin',
                    body: JSON.stringify({ Files: files })
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    showMessage('Failed to clear processed status', 'error');
                    return;
                }
                const data = await response.json();
                showMessage(
                    `Cleared processed status on ${data.cleared} of ${data.requested} file(s). ` +
                    'Run Process / Rename / Normalize to reapply.',
                    'success');
                // Refresh the file list so badges update.
                if (typeof loadFiles === 'function') {
                    loadFiles(true);
                }
            } catch (err) {
                console.error('clearSelectedProcessedStatus failed', err);
                showMessage('Failed to clear processed status: ' + err.message, 'error');
            }
        }

        async function removeMetadataSelected() {
            if (selectedFiles.size === 0) {
                showMessage('Please select at least one file', 'error');
                return;
            }
            const count = selectedFiles.size;
            if (!confirm(
                `Remove embedded metadata (ComicInfo.xml) from ${count} selected file${count > 1 ? 's' : ''}?\n\n` +
                'Each archive will be rewritten without its ComicInfo.xml and the files will be marked unprocessed so they can be re-processed.')) {
                return;
            }

            showProgressModal('Starting metadata removal...');

            const files = Array.from(selectedFiles);
            try {
                const response = await fetch(apiUrl('/api/jobs/remove-metadata-selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    credentials: 'same-origin',
                    body: JSON.stringify({ Files: files })
                });

                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    throw new Error('Failed to start remove metadata job');
                }

                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;

                showMessage(`Started removing metadata from ${totalItems} selected file${totalItems === 1 ? '' : 's'} in background`, 'info');
                await trackJobStatus(jobId, 'Removing Metadata from Selected Files...');
            } catch (err) {
                console.error('removeMetadataSelected failed', err);
                showMessage('Failed to remove metadata: ' + err.message, 'error');
                closeProgressModal();
            }
        }

        async function removeMetadataAll() {
            if (!confirm(
                'Remove embedded metadata (ComicInfo.xml) from ALL files in the library?\n\n' +
                'Every archive will be rewritten without its ComicInfo.xml and all files will be marked unprocessed so they can be re-processed. This may take a while.')) {
                return;
            }

            showProgressModal('Starting metadata removal...');

            try {
                const response = await fetch(apiUrl('/api/jobs/remove-metadata-all'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    credentials: 'same-origin'
                });

                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    throw new Error('Failed to start remove metadata job');
                }

                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;

                if (!totalItems) {
                    closeProgressModal();
                    showMessage('No files found in library', 'info');
                    return;
                }

                showMessage(`Started removing metadata from ${totalItems} file${totalItems === 1 ? '' : 's'} in background`, 'info');
                await trackJobStatus(jobId, 'Removing Metadata from All Files...');
            } catch (err) {
                console.error('removeMetadataAll failed', err);
                showMessage('Failed to remove metadata: ' + err.message, 'error');
                closeProgressModal();
            }
        }

        async function pollJobStatusOnce(jobId) {
            // Poll job status once to catch up after SSE reconnection or to handle stuck jobs
            console.log(`[JOB ${jobId}] Polling job status once...`);
            
            try {
                const response = await fetch(apiUrl(`/api/jobs/${jobId}`), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    console.warn(`[JOB ${jobId}] Could not fetch job status: ${response.status}`);
                    return;
                }
                
                const status = await response.json();
                const processed = status.processed_items || 0;
                const total = status.total_items || 0;
                setProgressCurrentFile(status.current_file);
                
                // Count successes and errors from results
                let successCount = 0;
                let errorCount = 0;
                if (status.results && Array.isArray(status.results)) {
                    for (const result of status.results) {
                        if (result.success) successCount++;
                        else errorCount++;
                    }
                }
                
                console.log(`[JOB ${jobId}] Current status: ${status.status}, ${processed}/${total} (${successCount} success, ${errorCount} errors)`);
                
                // Update progress UI
                updateProgress(processed, total, successCount, errorCount);
                
                // Populate progress details with already-processed files when resuming
                if (status.results && Array.isArray(status.results)) {
                    for (const result of status.results) {
                        // Only show files that have been processed (either success or error)
                        if (result.success || result.error) {
                            const filename = result.file.split(/[/\\]/).pop(); // Extract just the filename
                            addProgressDetail(filename, result.success, result.error);
                        }
                    }
                }
                
                // Handle completion states
                if (status.status === 'completed' || status.status === 'failed' || status.status === 'cancelled') {
                    console.log(`[JOB ${jobId}] Job is ${status.status}, triggering completion handler`);
                    
                    // Simulate an SSE event to trigger completion logic
                    handleJobUpdatedEvent({
                        job_id: jobId,
                        status: status.status,
                        progress: {
                            processed: processed,
                            total: total,
                            success: successCount,
                            errors: errorCount,
                            percentage: (processed / total * 100) || 0
                        }
                    });
                }
            } catch (error) {
                console.error(`[JOB ${jobId}] Error polling job status:`, error);
            }
        }
        
        async function trackJobStatus(jobId, title) {
            // Job progress updates via SSE only - purely event-based, no polling
            console.log(`[JOB ${jobId}] Tracking job status via SSE events: ${title}`);
            
            // Set active job state IMMEDIATELY to avoid race condition where
            // SSE events arrive before this completes. This ensures we don't
            // Set active job state immediately to track progress
            hasActiveJob = true;
            currentJobId = jobId;  // Track for cancellation
            currentJobTitle = title;  // Track title for progress updates
            
            // Fetch initial job state to display immediately
            await pollJobStatusOnce(jobId);
            
            // From this point on, all updates come from SSE events via handleJobUpdatedEvent
            // Purely event-based - no polling, no watchdog timers
            console.log(`[JOB ${jobId}] Waiting for real-time SSE updates...`);
        }
        
        async function cancelCurrentJob() {
            if (!currentJobId) {
                showMessage('No active job to cancel', 'error');
                return;
            }
            
            if (!confirm('Are you sure you want to cancel the current batch processing job?')) {
                return;
            }
            
            try {
                console.log(`[CANCEL] Cancelling job ${currentJobId}...`);
                
                const response = await fetch(apiUrl(`/api/jobs/${currentJobId}/cancel`), {
                    method: 'POST'
                });
                
                if (!response.ok) {
                    const errorData = await response.json().catch(() => ({}));
                    throw new Error(errorData.error || `Failed to cancel job (HTTP ${response.status})`);
                }
                
                const result = await response.json();
                
                if (result.success) {
                    console.log(`[CANCEL] Job ${currentJobId} cancelled successfully`);
                    showMessage('Batch processing cancelled', 'warning');
                    
                    // The polling loop will detect the cancelled status and exit
                    // We don't need to do anything else here
                } else {
                    throw new Error(result.error || 'Failed to cancel job');
                }
            } catch (error) {
                console.error(`[CANCEL] Error cancelling job ${currentJobId}:`, error);
                showMessage('Failed to cancel job: ' + error.message, 'error');
            }
        }
        
        async function checkAndResumeActiveJob() {
            // Get active job from server
            const activeJob = await getActiveJobFromServer();
            
            if (!activeJob || !activeJob.job_id) {
                console.log('[JOB RESUME] No active job found on server');
                return; // No active job
            }
            
            const activeJobId = activeJob.job_id;
            const activeJobTitle = activeJob.job_title;
            
            // Validate job_id format (should be a UUID)
            const uuidRegex = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
            if (!uuidRegex.test(activeJobId)) {
                console.warn(`[JOB RESUME] Invalid job_id format: ${activeJobId} (expected UUID) - ignoring`);
                return;
            }
            
            console.log(`[JOB RESUME] Found active job ${activeJobId} on server, checking status...`);
            
            try {
                // Check if job still exists and is active
                const response = await fetch(apiUrl(`/api/jobs/${activeJobId}`), {
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) return;
                
                if (!response.ok) {
                    if (response.status === 404) {
                        // Job not found (was cleaned up or deleted)
                        console.warn(`[JOB RESUME] Job ${activeJobId} not found (404) - was cleaned up`);
                        showMessage('Previous batch processing job is no longer available', 'warning');
                        return;
                    } else if (response.status >= 500) {
                        // Server error - don't clear the job, user can refresh to try again
                        console.error(`[JOB RESUME] Server error (${response.status}) checking job ${activeJobId}`);
                        showMessage('Server error checking job status. Please refresh to try again.', 'error');
                        return;
                    } else {
                        // Other client errors
                        console.warn(`[JOB RESUME] Error ${response.status} checking job ${activeJobId}`);
                        showMessage('Previous batch processing job is no longer available', 'warning');
                        return;
                    }
                }
                
                const status = await response.json();
                console.log(`[JOB RESUME] Job ${activeJobId} status: ${status.status}, ${status.processed_items}/${status.total_items} items processed`);
                
                // Resume if job is still running or queued
                if (status.status === 'running' || status.status === 'queued') {
                    console.log(`[JOB RESUME] Resuming job ${activeJobId}`);
                    hasActiveJob = true;
                    showProgressModal(activeJobTitle || 'Resuming Job...');
                    showMessage('Resuming active job...', 'info');
                    await trackJobStatus(activeJobId, activeJobTitle || 'Processing...');
                } else if (status.status === 'completed') {
                    // Job completed while we were away - show results
                    console.log(`[JOB RESUME] Job ${activeJobId} already completed`);
                    let successCount = 0;
                    let errorCount = 0;
                    
                    if (status.results && Array.isArray(status.results)) {
                        for (const result of status.results) {
                            if (result.success) {
                                successCount++;
                            } else {
                                errorCount++;
                            }
                        }
                    }
                    
                    const total = status.total_items || 0;
                    showMessage(`Batch processing completed: ${successCount} of ${total} files processed successfully${errorCount > 0 ? `, ${errorCount} failed` : ''}`, successCount > 0 ? 'success' : 'warning');
                    
                    // Refresh file list to show updated status
                    await loadActiveLibraryView(1, true);
                } else if (status.status === 'failed') {
                    // Job failed while we were away
                    console.error(`[JOB RESUME] Job ${activeJobId} already failed`);
                    showMessage(`Batch processing failed: ${status.error || 'Unknown error'}`, 'error');
                } else if (status.status === 'cancelled') {
                    // Job was cancelled
                    console.log(`[JOB RESUME] Job ${activeJobId} was cancelled`);
                    showMessage('Batch processing was cancelled', 'warning');
                }
            } catch (error) {
                // Network error or other exception
                console.error(`[JOB RESUME] Error checking active job ${activeJobId}:`, error);
                // Don't clear the job on network errors - it might still be running
                // User can refresh to try again
                showMessage('Could not check job status. Please refresh to try again.', 'warning');
            }
        }
        
        async function renameAllFiles() {
            if (!confirm('This will rename all files in the watched directory based on metadata. Continue?')) {
                return;
            }

            const forceReprocess = promptForForceReprocess(
                'This will rename all files in the watched directory based on metadata.',
                'renamed');
            
            showProgressModal('Starting rename...');
            
            try {
                console.log('[BATCH] Starting rename all files request...');
                // Start the job
                const response = await fetch(apiUrl(`/api/jobs/rename-all?forceReprocess=${forceReprocess}`), {
                    method: 'POST',
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[BATCH] Failed to start renaming all files (HTTP ${response.status})`);
                    throw new Error('Failed to start renaming job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;
                
                console.log(`[BATCH] Created job ${jobId} for ${totalItems} files`);
                showMessage(`Started renaming ${totalItems} files in background`, 'info');
                
                // Track job status
                await trackJobStatus(jobId, 'Renaming All Files...');
                
            } catch (error) {
                console.error('[BATCH] Error starting rename all files:', error);
                showMessage('Failed to start renaming: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function normalizeAllFiles() {
            if (!confirm('This will normalize metadata for all files in the watched directory. Continue?')) {
                return;
            }

            const forceReprocess = promptForForceReprocess(
                'This will normalize metadata for all files in the watched directory.',
                'normalized');
            
            showProgressModal('Starting normalize...');
            
            try {
                console.log('[BATCH] Starting normalize all files request...');
                // Start the job
                const response = await fetch(apiUrl(`/api/jobs/normalize-all?forceReprocess=${forceReprocess}`), {
                    method: 'POST',
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[BATCH] Failed to start normalizing all files (HTTP ${response.status})`);
                    throw new Error('Failed to start normalizing job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;
                
                console.log(`[BATCH] Created job ${jobId} for ${totalItems} files`);
                showMessage(`Started normalizing ${totalItems} files in background`, 'info');
                
                // Track job status
                await trackJobStatus(jobId, 'Normalizing All Files...');
                
            } catch (error) {
                console.error('[BATCH] Error starting normalize all files:', error);
                showMessage('Failed to start normalizing: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function processUnmarkedFiles() {
            if (!confirm('This will process all unmarked files in the watched directory. Continue?')) {
                return;
            }
            
            showProgressModal('Starting async processing...');
            
            try {
                console.log('[BATCH] Starting process unmarked files request...');
                // Start the job
                const response = await fetch(apiUrl('/api/jobs/process-unmarked'), {
                    method: 'POST',
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[BATCH] Failed to start processing unmarked files (HTTP ${response.status})`);
                    throw new Error('Failed to start processing job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;
                
                console.log(`[BATCH] Created job ${jobId} for ${totalItems} unmarked files`);
                showMessage(`Started processing ${totalItems} unmarked files in background`, 'info');
                
                // Poll for status
                await trackJobStatus(jobId, 'Processing Unmarked Files...');
                
            } catch (error) {
                console.error('[BATCH] Error starting process unmarked files:', error);
                showMessage('Failed to start processing: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function renameUnmarkedFiles() {
            if (!confirm('This will rename all unmarked files in the watched directory based on metadata. Continue?')) {
                return;
            }
            
            showProgressModal('Starting async renaming...');
            
            try {
                console.log('[BATCH] Starting rename unmarked files request...');
                // Start the job
                const response = await fetch(apiUrl('/api/jobs/rename-unmarked'), {
                    method: 'POST',
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[BATCH] Failed to start renaming unmarked files (HTTP ${response.status})`);
                    throw new Error('Failed to start renaming job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;
                
                console.log(`[BATCH] Created job ${jobId} for ${totalItems} unmarked files`);
                showMessage(`Started renaming ${totalItems} unmarked files in background`, 'info');
                
                // Poll for status
                await trackJobStatus(jobId, 'Renaming Unmarked Files...');
                
            } catch (error) {
                console.error('[BATCH] Error starting rename unmarked files:', error);
                showMessage('Failed to start renaming: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function normalizeUnmarkedFiles() {
            if (!confirm('This will normalize metadata for all unmarked files in the watched directory. Continue?')) {
                return;
            }

            const forceReprocess = promptForForceReprocess(
                'This will normalize metadata for all unmarked files in the watched directory.',
                'normalized');

            showProgressModal('Starting async normalizing...');
            
            try {
                console.log('[BATCH] Starting normalize unmarked files request...');
                // Start the job
                const response = await fetch(apiUrl(`/api/jobs/normalize-unmarked?forceReprocess=${forceReprocess}`), {
                    method: 'POST',
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[BATCH] Failed to start normalizing unmarked files (HTTP ${response.status})`);
                    throw new Error('Failed to start normalizing job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;
                
                console.log(`[BATCH] Created job ${jobId} for ${totalItems} unmarked files`);
                showMessage(`Started normalizing ${totalItems} unmarked files in background`, 'info');
                
                // Poll for status
                await trackJobStatus(jobId, 'Normalizing Unmarked Files...');
                
            } catch (error) {
                console.error('[BATCH] Error starting normalize unmarked files:', error);
                showMessage('Failed to start normalizing: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function processSelectedFiles() {
            if (selectedFiles.size === 0) {
                showMessage('No files selected', 'error');
                return;
            }
            
            if (!confirm(`This will process ${selectedFiles.size} selected file${selectedFiles.size > 1 ? 's' : ''}. Continue?`)) {
                return;
            }
            
            const forceReprocess = promptForForceReprocess(
                `This will process ${selectedFiles.size} selected file${selectedFiles.size > 1 ? 's' : ''}.`,
                'processed');

            showProgressModal('Starting processing...');
            
            const files = Array.from(selectedFiles);
            
            try {
                console.log('[BATCH] Starting process selected files request...');
                // Start the job using the jobs endpoint
                const response = await fetch(apiUrl('/api/jobs/process-selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ Files: files, ForceReprocess: forceReprocess })
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[BATCH] Failed to start processing selected files (HTTP ${response.status})`);
                    throw new Error('Failed to start processing job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;
                
                console.log(`[BATCH] Created job ${jobId} for ${totalItems} selected files`);
                showMessage(`Started processing ${totalItems} selected files in background`, 'info');
                
                // Track job status
                await trackJobStatus(jobId, 'Processing Selected Files...');
                
            } catch (error) {
                console.error('[BATCH] Error starting process selected files:', error);
                showMessage('Failed to start processing: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function renameSelectedFiles() {
            if (selectedFiles.size === 0) {
                showMessage('No files selected', 'error');
                return;
            }
            
            if (!confirm(`This will rename ${selectedFiles.size} selected file${selectedFiles.size > 1 ? 's' : ''} based on metadata. Continue?`)) {
                return;
            }
            
            const forceReprocess = promptForForceReprocess(
                `This will rename ${selectedFiles.size} selected file${selectedFiles.size > 1 ? 's' : ''} based on metadata.`,
                'renamed');

            showProgressModal('Starting rename...');
            
            const files = Array.from(selectedFiles);
            
            try {
                console.log('[BATCH] Starting rename selected files request...');
                // Start the job using the jobs endpoint
                const response = await fetch(apiUrl('/api/jobs/rename-selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ Files: files, ForceReprocess: forceReprocess })
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[BATCH] Failed to start renaming selected files (HTTP ${response.status})`);
                    throw new Error('Failed to start renaming job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;
                
                console.log(`[BATCH] Created job ${jobId} for ${totalItems} selected files`);
                showMessage(`Started renaming ${totalItems} selected files in background`, 'info');
                
                // Track job status
                await trackJobStatus(jobId, 'Renaming Selected Files...');
                
            } catch (error) {
                console.error('[BATCH] Error starting rename selected files:', error);
                showMessage('Failed to start renaming: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function normalizeSelectedFiles() {
            if (selectedFiles.size === 0) {
                showMessage('No files selected', 'error');
                return;
            }
            
            if (!confirm(`This will normalize metadata for ${selectedFiles.size} selected file${selectedFiles.size > 1 ? 's' : ''}. Continue?`)) {
                return;
            }
            
            const forceReprocess = promptForForceReprocess(
                `This will normalize metadata for ${selectedFiles.size} selected file${selectedFiles.size > 1 ? 's' : ''}.`,
                'normalized');

            showProgressModal('Starting normalize...');
            
            const files = Array.from(selectedFiles);
            
            try {
                console.log('[BATCH] Starting normalize selected files request...');
                // Start the job using the jobs endpoint
                const response = await fetch(apiUrl('/api/jobs/normalize-selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ Files: files, ForceReprocess: forceReprocess })
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[BATCH] Failed to start normalizing selected files (HTTP ${response.status})`);
                    throw new Error('Failed to start normalizing job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;
                
                console.log(`[BATCH] Created job ${jobId} for ${totalItems} selected files`);
                showMessage(`Started normalizing ${totalItems} selected files in background`, 'info');
                
                // Track job status
                await trackJobStatus(jobId, 'Normalizing Selected Files...');
                
            } catch (error) {
                console.error('[BATCH] Error starting normalize selected files:', error);
                showMessage('Failed to start normalizing: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function processSingleFile(filepath) {
            if (!confirm(`Process ${filepath}?`)) {
                return;
            }

            const forceReprocess = promptForForceReprocess(
                `This will process ${filepath}.`,
                'processed');
            
            showProgressModal('Starting processing...');
            
            try {
                console.log('[SINGLE FILE] Starting process file request...');
                // Start the job using the process-selected endpoint with a single file
                const response = await fetch(apiUrl('/api/jobs/process-selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ Files: [filepath], ForceReprocess: forceReprocess })
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[SINGLE FILE] Failed to start processing file (HTTP ${response.status})`);
                    throw new Error('Failed to start processing job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                
                console.log(`[SINGLE FILE] Created job ${jobId} for file: ${filepath}`);
                showMessage(`Started processing file in background`, 'info');
                
                // Track job status
                await trackJobStatus(jobId, 'Processing File...');
                
            } catch (error) {
                console.error('[SINGLE FILE] Error starting process file:', error);
                showMessage('Failed to start processing: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function renameSingleFile(filepath) {
            if (!confirm(`Rename ${filepath} based on metadata?`)) {
                return;
            }

            const forceReprocess = promptForForceReprocess(
                `This will rename ${filepath} based on metadata.`,
                'renamed');
            
            showProgressModal('Starting rename...');
            
            try {
                console.log('[SINGLE FILE] Starting rename file request...');
                // Start the rename job
                const encodedPath = encodeFilePathForUrl(filepath);
                const response = await fetch(apiUrl(`/api/files/${encodedPath}/rename?forceReprocess=${forceReprocess}`), {
                    method: 'POST',
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[SINGLE FILE] Failed to start renaming file (HTTP ${response.status})`);
                    throw new Error('Failed to start renaming job');
                }
                
                const data = await response.json();
                const jobId = data.jobId;
                
                if (!jobId) {
                    throw new Error('No job ID returned');
                }
                
                console.log(`[SINGLE FILE] Created job ${jobId} for file: ${filepath}`);
                showMessage(`Started renaming file in background`, 'info');
                
                // Track job status
                await trackJobStatus(jobId, 'Renaming File...');
                
            } catch (error) {
                console.error('[SINGLE FILE] Error starting rename file:', error);
                showMessage('Failed to start renaming: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function normalizeSingleFile(filepath) {
            if (!confirm(`Normalize metadata for ${filepath}?`)) {
                return;
            }

            const forceReprocess = promptForForceReprocess(
                `This will normalize metadata for ${filepath}.`,
                'normalized');
            
            showProgressModal('Starting normalize...');
            
            try {
                console.log('[SINGLE FILE] Starting normalize file request...');
                // Start the normalize job
                const response = await fetch(apiUrl('/api/jobs/normalize-selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ Files: [filepath], ForceReprocess: forceReprocess })
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[SINGLE FILE] Failed to start normalizing file (HTTP ${response.status})`);
                    throw new Error('Failed to start normalizing job');
                }
                
                const result = await response.json();
                const jobId = result.job_id;
                
                if (!jobId) {
                    throw new Error('No job ID returned');
                }
                
                console.log(`[SINGLE FILE] Created job ${jobId} for file: ${filepath}`);
                showMessage(`Started normalizing file in background`, 'info');
                
                // Track job status
                await trackJobStatus(jobId, 'Normalizing File...');
                
            } catch (error) {
                console.error('[SINGLE FILE] Error starting normalize file:', error);
                showMessage('Failed to start normalizing: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function deleteSingleFile(filepath) {
            if (!confirm(`Are you sure you want to delete ${filepath}?\n\nThis action cannot be undone!`)) {
                return;
            }
            
            showMessage('Deleting file...', 'info');
            
            try {
                // Use RESTful endpoint: DELETE /api/files/{encodedFilePath}
                const encodedPath = encodeFilePathForUrl(filepath);
                const response = await fetch(apiUrl(`/api/files/${encodedPath}`), {
                    method: 'DELETE',
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                showMessage('File deleted successfully!', 'success');
                await loadActiveLibraryView(1, true);
            } catch (error) {
                showMessage('Failed to delete file: ' + error.message, 'error');
            }
        }

        async function removeFileMetadata(filepath) {
            if (!confirm(`Remove embedded metadata (ComicInfo.xml) from:\n${filepath}\n\nThe archive will be rewritten without its ComicInfo.xml and the file will be marked as unprocessed. Continue?`)) {
                return;
            }

            showMessage('Removing metadata...', 'info');

            try {
                const encodedPath = encodeFilePathForUrl(filepath);
                const response = await fetch(apiUrl(`/api/files/${encodedPath}/metadata`), {
                    method: 'DELETE',
                    headers: getAuthHeaders()
                });

                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }

                showMessage('Metadata removed and file marked unprocessed.', 'success');
                await loadActiveLibraryView(1, true);
            } catch (error) {
                showMessage('Failed to remove metadata: ' + error.message, 'error');
            }
        }
        
        async function markFileRead(filepath) {
            try {
                showMessage('Marking file as read...', 'info');
                
                const encodedPath = encodeFilePathForUrl(filepath);
                const response = await fetch(apiUrl(`/api/files/${encodedPath}/read`), {
                    method: 'POST',
                    headers: {
                        ...getAuthHeaders(),
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({ read: true })
                });
                
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                showMessage('File marked as read!', 'success');
                await loadActiveLibraryView(1, true);
            } catch (error) {
                showMessage('Failed to mark file as read: ' + error.message, 'error');
            }
        }
        
        async function markFileUnread(filepath) {
            try {
                showMessage('Marking file as unread...', 'info');
                
                const encodedPath = encodeFilePathForUrl(filepath);
                const response = await fetch(apiUrl(`/api/files/${encodedPath}/read`), {
                    method: 'POST',
                    headers: {
                        ...getAuthHeaders(),
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({ read: false })
                });
                
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                showMessage('File marked as unread!', 'success');
                await loadActiveLibraryView(1, true);
            } catch (error) {
                showMessage('Failed to mark file as unread: ' + error.message, 'error');
            }
        }
        
        async function fetchAllLibraryFilePaths() {
            // Use the legacy /api/files endpoint with per_page=-1 to grab every
            // path for whole-library bulk operations.
            const url = apiUrl('/api/files?per_page=-1');
            const response = await fetch(url, { headers: getAuthHeaders() });
            if (handleAuthError(response)) return [];
            if (!response.ok) {
                throw new Error(`HTTP error! status: ${response.status}`);
            }
            const data = await response.json();
            return (data.files || []).map(f => f.relative_path);
        }

        async function markAllFilesRead() {
            if (!confirm('Mark all files as read?')) {
                return;
            }
            
            try {
                showMessage('Marking all files as read...', 'info');
                
                const allFilePaths = await fetchAllLibraryFilePaths();
                const response = await fetch(apiUrl('/api/files/read-batch'), {
                    method: 'POST',
                    headers: {
                        ...getAuthHeaders(),
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({ files: allFilePaths, read: true })
                });
                
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                showMessage('All files marked as read!', 'success');
                await loadActiveLibraryView(1, true);
            } catch (error) {
                showMessage('Failed to mark files as read: ' + error.message, 'error');
            }
        }
        
        async function markAllFilesUnread() {
            if (!confirm('Mark all files as unread?')) {
                return;
            }
            
            try {
                showMessage('Marking all files as unread...', 'info');
                
                const allFilePaths = await fetchAllLibraryFilePaths();
                const response = await fetch(apiUrl('/api/files/read-batch'), {
                    method: 'POST',
                    headers: {
                        ...getAuthHeaders(),
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({ files: allFilePaths, read: false })
                });
                
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                showMessage('All files marked as unread!', 'success');
                await loadActiveLibraryView(1, true);
            } catch (error) {
                showMessage('Failed to mark files as unread: ' + error.message, 'error');
            }
        }
        
        async function markSelectedFilesRead() {
            if (selectedFiles.size === 0) {
                showMessage('No files selected', 'warning');
                return;
            }
            
            try {
                showMessage('Marking selected files as read...', 'info');
                
                const selectedFilePaths = Array.from(selectedFiles);
                const response = await fetch(apiUrl('/api/files/read-batch'), {
                    method: 'POST',
                    headers: {
                        ...getAuthHeaders(),
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({ files: selectedFilePaths, read: true })
                });
                
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                showMessage(`${selectedFiles.size} file(s) marked as read!`, 'success');
                await loadActiveLibraryView(1, true);
            } catch (error) {
                showMessage('Failed to mark files as read: ' + error.message, 'error');
            }
        }
        
        async function markSelectedFilesUnread() {
            if (selectedFiles.size === 0) {
                showMessage('No files selected', 'warning');
                return;
            }
            
            try {
                showMessage('Marking selected files as unread...', 'info');
                
                const selectedFilePaths = Array.from(selectedFiles);
                const response = await fetch(apiUrl('/api/files/read-batch'), {
                    method: 'POST',
                    headers: {
                        ...getAuthHeaders(),
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({ files: selectedFilePaths, read: false })
                });
                
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                showMessage(`${selectedFiles.size} file(s) marked as unread!`, 'success');
                await loadActiveLibraryView(1, true);
            } catch (error) {
                showMessage('Failed to mark files as unread: ' + error.message, 'error');
            }
        }
        
        function refreshFiles() {
            showMessage('Refreshing file list...', 'info');
            loadActiveLibraryView(1, true);
        }
        
        function showMessage(message, type = 'info') {
            const container = document.getElementById('messageContainer');
            const messageEl = document.createElement('div');
            messageEl.className = `message ${type}`;
            messageEl.textContent = message;
            
            container.appendChild(messageEl);
            
            setTimeout(() => {
                messageEl.remove();
            }, 5000);
        }
        
        async function openSettings() {
            try {
                // Load all settings
                const settingsUrl = apiUrl('/api/settings');
                console.log('[SETTINGS] Fetching settings from URL:', settingsUrl);
                
                const settingsResponse = await fetch(settingsUrl, {
                    headers: getAuthHeaders()
                });

                console.log('[SETTINGS] Response status:', settingsResponse.status, 'OK:', settingsResponse.ok, 'URL:', settingsResponse.url);

                if (handleAuthError(settingsResponse)) return;

                if (!settingsResponse.ok) {
                    const errorText = await settingsResponse.text();
                    console.error('[SETTINGS] Failed to load settings. Status:', settingsResponse.status, 'Response:', errorText);
                    throw new Error(`HTTP error! status: ${settingsResponse.status} - ${errorText}`);
                }
                
                const settingsData = await settingsResponse.json();
                console.log('[SETTINGS] Settings data loaded:', settingsData);
                
                document.getElementById('filenameFormat').value = settingsData.filename_format || '';
                document.getElementById('currentFormat').textContent = settingsData.filename_format || '{series} - Chapter {issue}';
                
                // Load current theme
                const currentTheme = document.documentElement.getAttribute('data-theme') || 'light';
                document.getElementById('themeSelect').value = currentTheme;
                
                // Load watcher enable rename status
                document.getElementById('watcherEnableRenameCheckbox').checked = settingsData.watcher_enable_rename;
                
                // Load watcher enable normalize status
                document.getElementById('watcherEnableNormalizeCheckbox').checked = settingsData.watcher_enable_normalize;
                
                // Load log max size (convert bytes to MB)
                const BYTES_PER_MB = 1048576;
                const logMaxMB = settingsData.log_max_bytes / BYTES_PER_MB;
                document.getElementById('logMaxSize').value = Math.round(logMaxMB);
                console.log('[SETTINGS] Log max bytes:', settingsData.log_max_bytes, 'converted to MB:', Math.round(logMaxMB));
                
                // Load issue number padding
                document.getElementById('issueNumberPadding').value = settingsData.issue_number_padding;
                
                // Load database cleanup interval
                document.getElementById('dbCleanupInterval').value = settingsData.database_cleanup_interval_hours;

                // Load external metadata settings
                document.getElementById('enableExternalSeriesMetadata').checked = !!settingsData.enable_external_series_metadata;
                document.getElementById('comicVineApiKey').value = settingsData.comicvine_api_key || '';
                document.getElementById('comicVineBaseUrl').value = settingsData.comicvine_base_url || 'https://comicvine.gamespot.com/api';
                document.getElementById('enableMangaDexMetadata').checked = !!settingsData.enable_mangadex_metadata;
                document.getElementById('mangaDexBaseUrl').value = settingsData.mangadex_base_url || 'https://api.mangadex.org';
                document.getElementById('enableAniListMetadata').checked = !!settingsData.enable_anilist_metadata;
                document.getElementById('aniListBaseUrl').value = settingsData.anilist_base_url || 'https://graphql.anilist.co';

                // Load default library view
                const defaultLibraryViewSelect = document.getElementById('defaultLibraryViewSelect');
                if (defaultLibraryViewSelect) {
                    const defaultView = settingsData.default_library_view === 'series' ? 'series' : 'files';
                    defaultLibraryViewSelect.value = defaultView;
                }

                // Load default preferred-language fallback. Server returns
                // null/empty when no global default is configured.
                const defaultPreferredLanguageSelect = document.getElementById('defaultPreferredLanguageSelect');
                if (defaultPreferredLanguageSelect) {
                    defaultPreferredLanguageSelect.value = settingsData.default_preferred_language || '';
                }
                
                console.log('[SETTINGS] All settings loaded successfully, opening modal');
                document.getElementById('settingsModal').classList.add('active');
            } catch (error) {
                console.error('[SETTINGS] Error in openSettings():', error);
                showMessage('Failed to load settings: ' + error.message, 'error');
            }
        }
        
        function closeSettings() {
            document.getElementById('settingsModal').classList.remove('active');
        }
        
        function showResetConfirmation() {
            document.getElementById('resetConfirmationModal').classList.add('active');
        }
        
        function closeResetConfirmation() {
            document.getElementById('resetConfirmationModal').classList.remove('active');
        }
        
        async function confirmReset() {
            try {
                closeResetConfirmation();
                
                // Show a loading message
                showMessage('Resetting database... This may take a moment.', 'info');
                
                const response = await fetch(apiUrl('/api/settings/reset'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    }
                });
                
                if (handleAuthError(response)) return;
                
                if (!response.ok) {
                    const errorData = await response.json();
                    throw new Error(errorData.error || `HTTP error! status: ${response.status}`);
                }
                
                const result = await response.json();
                
                if (result.success) {
                    showMessage('Database reset completed successfully', 'success');
                    // Clear in-memory selection state — the file list is now empty.
                    if (typeof selectedFiles !== 'undefined' && selectedFiles && typeof selectedFiles.clear === 'function') {
                        selectedFiles.clear();
                    }
                    // The server emits file_list_updated on completion, which
                    // triggers handleFileListUpdatedEvent → in-place refresh.
                    // Refresh counts and active library view defensively in
                    // case the SSE connection is briefly disconnected.
                    if (typeof loadLibraryHealth === 'function') {
                        try { await loadLibraryHealth(); } catch (_e) { /* ignore */ }
                    }
                    if (typeof loadActiveLibraryView === 'function') {
                        try { await loadActiveLibraryView(1, true); } catch (_e) { /* ignore */ }
                    }
                } else {
                    showMessage(result.error || 'Failed to reset database', 'error');
                }
            } catch (error) {
                showMessage('Failed to reset database: ' + error.message, 'error');
            }
        }
        
        async function openLogsModal() {
            document.getElementById('logsModal').classList.add('active');
            await loadLogFiles();
        }
        
        function closeLogsModal() {
            document.getElementById('logsModal').classList.remove('active');
        }
        
        async function openProcessingHistoryModal() {
            document.getElementById('processingHistoryModal').classList.add('active');
            historyCurrentPage = 1;
            await loadProcessingHistory();
        }
        
        function closeProcessingHistoryModal() {
            document.getElementById('processingHistoryModal').classList.remove('active');
        }
        
        async function loadProcessingHistory() {
            const loadingIndicator = document.getElementById('historyLoadingIndicator');
            const contentDiv = document.getElementById('historyContent');
            const pageInfo = document.getElementById('historyPageInfo');
            const prevBtn = document.getElementById('historyPrevBtn');
            const nextBtn = document.getElementById('historyNextBtn');
            
            try {
                loadingIndicator.style.display = 'block';
                contentDiv.innerHTML = '';
                
                const offset = (historyCurrentPage - 1) * historyPerPage;
                const response = await fetch(apiUrl(`/api/processing-history?limit=${historyPerPage}&offset=${offset}`), {
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) return;
                
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                const data = await response.json();
                historyTotal = data.total;
                const totalPages = Math.ceil(historyTotal / historyPerPage);
                
                loadingIndicator.style.display = 'none';
                
                if (data.history.length === 0) {
                    contentDiv.innerHTML = '<div style="padding: 40px; text-align: center; color: var(--text-muted);">No processing history found</div>';
                    pageInfo.textContent = 'No results';
                    prevBtn.disabled = true;
                    nextBtn.disabled = true;
                    return;
                }
                
                // Render history items
                let html = '<div style="display: flex; flex-direction: column; gap: 15px;">';
                data.history.forEach(item => {
                    html += renderHistoryItem(item);
                });
                html += '</div>';
                
                contentDiv.innerHTML = html;
                
                // Update pagination controls
                pageInfo.textContent = `Page ${historyCurrentPage} of ${totalPages} (${historyTotal} total)`;
                prevBtn.disabled = historyCurrentPage <= 1;
                nextBtn.disabled = historyCurrentPage >= totalPages;
                
            } catch (error) {
                loadingIndicator.style.display = 'none';
                contentDiv.innerHTML = `<div style="padding: 20px; color: red;">Error loading history: ${error.message}</div>`;
            }
        }
        
        function renderHistoryItem(item) {
            const timestamp = new Date(item.timestamp * 1000).toLocaleString();
            const filepath = item.after_filename || item.before_filename || item.filepath;
            
            // Determine status styling
            const statusIcon = item.success ? '✅' : '❌';
            const statusText = item.success ? 'Success' : 'Failed';
            const statusColor = item.success ? '#10b981' : '#ef4444';
            const borderColor = item.success ? 'var(--border-secondary)' : '#ef4444';
            
            let html = `
                <div style="border: 1px solid ${borderColor}; border-radius: 8px; padding: 15px; background: var(--bg-secondary);">
                    <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 12px; border-bottom: 1px solid var(--border-primary); padding-bottom: 10px;">
                        <div style="flex: 1;">
                            <div style="display: flex; align-items: center; gap: 8px; margin-bottom: 4px;">
                                <span style="font-size: 18px;" title="${statusText}">${statusIcon}</span>
                                <strong style="font-size: 15px; color: var(--text-primary);">${escapeHtml(filepath)}</strong>
                            </div>
                            <div style="font-size: 12px; color: var(--text-muted);">${timestamp}</div>
                        </div>
                        <span style="background: var(--bg-hover); padding: 4px 12px; border-radius: 4px; font-size: 12px; color: var(--text-secondary);">${item.operation_type}</span>
                    </div>
            `;
            
            // Show error message if operation failed
            if (!item.success && item.error_message) {
                html += `
                    <div style="background: #fef2f2; border-left: 3px solid #ef4444; padding: 10px; margin-bottom: 12px; border-radius: 4px;">
                        <div style="font-weight: 500; color: #dc2626; margin-bottom: 4px;">Error</div>
                        <div style="color: #991b1b; font-size: 13px;">${escapeHtml(item.error_message)}</div>
                    </div>
                `;
            }
            
            html += `<div style="display: grid; grid-template-columns: 1fr 1fr; gap: 10px; font-size: 13px;">
            `;
            
            // Show changes
            const fields = [
                { label: 'Filename', before: item.before_filename, after: item.after_filename },
                { label: 'Title', before: item.before_title, after: item.after_title },
                { label: 'Series', before: item.before_series, after: item.after_series },
                { label: 'Issue', before: item.before_issue, after: item.after_issue },
                { label: 'Publisher', before: item.before_publisher, after: item.after_publisher },
                { label: 'Year', before: item.before_year, after: item.after_year },
                { label: 'Volume', before: item.before_volume, after: item.after_volume }
            ];
            
            // Check if there are any changes to display
            const hasChanges = fields.some(field => field.before !== field.after && (field.before || field.after));
            
            if (hasChanges) {
                fields.forEach(field => {
                    if (field.before !== field.after && (field.before || field.after)) {
                        html += `
                            <div style="grid-column: 1 / -1; border-left: 3px solid var(--border-secondary); padding-left: 10px; margin: 5px 0;">
                                <div style="font-weight: 500; color: var(--text-secondary); margin-bottom: 5px;">${field.label}</div>
                                <div style="display: flex; gap: 10px; align-items: center;">
                                    <div style="flex: 1; padding: 6px 10px; background: var(--bg-hover); border-radius: 4px; color: var(--text-muted);">
                                        <span style="font-size: 11px; text-transform: uppercase; opacity: 0.7;">Before:</span>
                                        <div style="margin-top: 3px; color: var(--text-primary);">${field.before || '<em style="opacity: 0.5;">(empty)</em>'}</div>
                                    </div>
                                    <span style="color: var(--text-muted);">→</span>
                                    <div style="flex: 1; padding: 6px 10px; background: var(--bg-hover); border-radius: 4px; color: var(--text-muted);">
                                        <span style="font-size: 11px; text-transform: uppercase; opacity: 0.7;">After:</span>
                                        <div style="margin-top: 3px; color: var(--text-primary);">${field.after || '<em style="opacity: 0.5;">(empty)</em>'}</div>
                                    </div>
                                </div>
                            </div>
                        `;
                    }
                });
            } else if (item.success) {
                // No changes but operation was successful (e.g., "already normalized", "rename disabled")
                html += `
                    <div style="grid-column: 1 / -1; padding: 10px; color: var(--text-muted); font-size: 13px; font-style: italic; text-align: center;">
                        No changes made
                    </div>
                `;
            }
            
            html += `
                    </div>
                </div>
            `;
            
            return html;
        }
        
        async function loadPreviousHistoryPage() {
            if (historyCurrentPage > 1) {
                historyCurrentPage--;
                await loadProcessingHistory();
            }
        }
        
        async function loadNextHistoryPage() {
            const totalPages = Math.ceil(historyTotal / historyPerPage);
            if (historyCurrentPage < totalPages) {
                historyCurrentPage++;
                await loadProcessingHistory();
            }
        }
        
        async function openAboutModal() {
            try {
                // Load version
                const response = await fetch(apiUrl('/api/version'), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                const data = await response.json();
                const aboutVersionElement = document.getElementById('aboutVersion');
                if (aboutVersionElement && data.version) {
                    aboutVersionElement.textContent = `v${data.version}`;
                }
                
                document.getElementById('aboutModal').classList.add('active');
            } catch (error) {
                showMessage('Failed to load version information: ' + error.message, 'error');
            }
        }
        
        function closeAboutModal() {
            document.getElementById('aboutModal').classList.remove('active');
        }
        
        function openChangePasswordModal() {
            const modal = document.getElementById('changePasswordModal');
            const form = document.getElementById('changePasswordForm');
            const errorEl = document.getElementById('changePasswordError');
            const successEl = document.getElementById('changePasswordSuccess');
            
            // Reset form and messages
            form.reset();
            errorEl.textContent = '';
            errorEl.classList.remove('show');
            successEl.textContent = '';
            successEl.classList.remove('show');
            
            modal.classList.add('active');
        }
        
        function closeChangePasswordModal() {
            document.getElementById('changePasswordModal').classList.remove('active');
        }
        
        async function changePassword() {
            const currentPassword = document.getElementById('currentPassword').value;
            const newPassword = document.getElementById('newPassword').value;
            const confirmNewPassword = document.getElementById('confirmNewPassword').value;
            const errorEl = document.getElementById('changePasswordError');
            const successEl = document.getElementById('changePasswordSuccess');
            const btn = document.getElementById('changePasswordBtn');
            
            // Helper to reset button state
            const resetButtonState = () => {
                btn.disabled = false;
                btn.textContent = 'Change Password';
            };
            
            // Clear previous messages
            errorEl.textContent = '';
            errorEl.classList.remove('show');
            successEl.textContent = '';
            successEl.classList.remove('show');
            
            // Validation
            if (!currentPassword || !newPassword || !confirmNewPassword) {
                errorEl.textContent = 'All fields are required';
                errorEl.classList.add('show');
                return;
            }
            
            if (newPassword.length < 8) {
                errorEl.textContent = 'New password must be at least 8 characters long';
                errorEl.classList.add('show');
                return;
            }
            
            if (newPassword !== confirmNewPassword) {
                errorEl.textContent = 'New passwords do not match';
                errorEl.classList.add('show');
                return;
            }
            
            // Disable button during request
            btn.disabled = true;
            btn.textContent = 'Changing...';
            
            try {
                const response = await fetch(apiUrl('/api/auth/change-password'), {
                    method: 'POST',
                    headers: getAuthHeaders(),
                    body: JSON.stringify({
                        currentPassword: currentPassword,
                        newPassword: newPassword
                    })
                });
                
                if (handleAuthError(response)) {
                    resetButtonState();
                    return;
                }
                
                const data = await response.json();
                
                if (response.ok) {
                    successEl.textContent = 'Password changed successfully!';
                    successEl.classList.add('show');
                    
                    // Clear form
                    document.getElementById('changePasswordForm').reset();
                    
                    // Close modal after a short delay
                    setTimeout(() => {
                        closeChangePasswordModal();
                    }, 2000);
                } else {
                    errorEl.textContent = data.error || 'Failed to change password';
                    errorEl.classList.add('show');
                }
            } catch (error) {
                console.error('Error changing password:', error);
                errorEl.textContent = 'An error occurred. Please try again.';
                errorEl.classList.add('show');
            } finally {
                // Re-enable button
                resetButtonState();
            }
        }
        
        // Helper: toggle the progress indicator pill, adding the
        // mobile-sticky modifier on small viewports so it pins to the bottom
        // of the screen (the in-header pill is invisible once users scroll).
        function setProgressIndicatorVisible(visible) {
            const indicator = document.getElementById('progressIndicator');
            if (!indicator) return;
            if (visible) {
                indicator.style.display = 'flex';
                if (isMobileLibraryViewport()) {
                    indicator.classList.add('progress-indicator-btn--mobile-sticky');
                } else {
                    indicator.classList.remove('progress-indicator-btn--mobile-sticky');
                }
            } else {
                indicator.style.display = 'none';
                indicator.classList.remove('progress-indicator-btn--mobile-sticky');
            }
        }

        // Re-evaluate sticky-class placement on viewport rotation/resize so
        // the indicator follows the user between mobile and desktop sizes.
        window.addEventListener('resize', () => {
            const indicator = document.getElementById('progressIndicator');
            if (indicator && indicator.style.display !== 'none') {
                setProgressIndicatorVisible(true);
            }
        });

        function showProgressModal(title) {
            const modal = document.getElementById('progressModal');
            const indicator = document.getElementById('progressIndicator');
            document.getElementById('progressTitle').textContent = title;
            document.getElementById('progressBarFill').style.width = '0%';
            document.getElementById('progressText').textContent = '0 / 0 files';
            document.getElementById('progressPercent').textContent = '0%';
            document.getElementById('progressProcessedCount').textContent = '0';
            document.getElementById('progressSuccessCount').textContent = '0';
            document.getElementById('progressErrorCount').textContent = '0';
            document.getElementById('progressCurrentFile').textContent = 'Waiting for the job to start...';
            progressResults = [];
            progressResultLookup = new Set();
            progressResultElements = new Map();
            renderProgressDetails();
            document.getElementById('progressCloseBtn').style.display = 'none';
            document.getElementById('progressCancelBtn').style.display = 'inline-block';  // Show cancel button
            
            // Check if the modal was previously minimized
            let wasMinimized = false;
            try {
                wasMinimized = localStorage.getItem('progressModalMinimized') === 'true';
            } catch (e) {
                // localStorage may be unavailable (e.g., private browsing mode)
                console.warn('Could not access localStorage:', e);
            }
            
            if (wasMinimized) {
                // Show the minimized indicator instead of the full modal
                modal.classList.remove('active');
                setProgressIndicatorVisible(true);
                const indicatorText = document.getElementById('progressIndicatorText');
                indicatorText.textContent = `⏳ ${title}`;
            } else {
                // Show the full modal
                modal.classList.add('active');
                setProgressIndicatorVisible(false);
            }
        }
        
        function updateProgress(current, total, successCount, errorCount) {
            const percent = total > 0 ? Math.round((current / total) * 100) : 0;
            document.getElementById('progressBarFill').style.width = percent + '%';
            document.getElementById('progressText').textContent = `${current} / ${total} files`;
            document.getElementById('progressPercent').textContent = percent + '%';
            document.getElementById('progressProcessedCount').textContent = current.toString();
            document.getElementById('progressSuccessCount').textContent = successCount.toString();
            document.getElementById('progressErrorCount').textContent = errorCount.toString();
            
            // Update title with success/error counts if any errors, preserving the original job title
            let baseTitle = currentJobTitle || 'Processing Files...';
            let title = baseTitle;
            if (errorCount > 0) {
                title = `${baseTitle} - ${successCount} succeeded, ${errorCount} failed`;
            }
            document.getElementById('progressTitle').textContent = title;
            
            // Update indicator if it's visible (modal is minimized)
            const indicator = document.getElementById('progressIndicator');
            if (indicator.style.display !== 'none') {
                const indicatorText = document.getElementById('progressIndicatorText');
                indicatorText.textContent = `⏳ ${current} / ${total} files (${percent}%)`;
            }
        }

        // Update the progress modal label with the latest file or status message.
        function setProgressCurrentFile(filepath, prefix = 'Latest update') {
            const label = document.getElementById('progressCurrentFile');
            if (!label) {
                return;
            }

            if (!filepath) {
                label.textContent = 'Waiting for the next completed file...';
                return;
            }

            label.textContent = `${prefix}: ${extractDisplayName(filepath)}`;
        }

        // Render the per-file processing results shown inside the progress modal.
        function renderProgressDetails() {
            const details = document.getElementById('progressDetails');
            const countLabel = document.getElementById('progressResultsCount');
            countLabel.textContent = `${progressResults.length} result${progressResults.length === 1 ? '' : 's'}`;

            if (progressResults.length === 0) {
                details.innerHTML = '<div class="progress-results-empty">Per-file processing updates will appear here as each file completes.</div>';
                return;
            }
            
            details.innerHTML = '';
            progressResults.forEach(result => {
                const entry = document.createElement('div');
                entry.className = `progress-result-item ${result.success ? 'success' : 'error'}`;
                entry.innerHTML = `
                    <div class="progress-result-status">${result.success ? '✅' : '❌'}</div>
                    <div class="progress-result-text">
                        <strong>${escapeHtml(result.filename)}</strong>
                        <span>${result.success ? 'Completed successfully' : escapeHtml(result.error || 'Failed')}</span>
                    </div>
                `;
                details.appendChild(entry);
                progressResultElements.set(result.key, entry);
            });
        }
        
        function addProgressDetail(filename, success, error = null) {
            const resultKey = filename;
            const nextResult = {
                key: resultKey,
                filename: extractDisplayName(filename),
                success,
                error
            };

            const details = document.getElementById('progressDetails');
            const emptyState = details.querySelector('.progress-results-empty');
            if (emptyState) {
                emptyState.remove();
            }

            if (progressResultLookup.has(resultKey)) {
                const existingIndex = progressResults.findIndex(item => item.key === resultKey);
                if (existingIndex >= 0) {
                    progressResults.splice(existingIndex, 1);
                }

                const existingElement = progressResultElements.get(resultKey);
                if (existingElement) {
                    existingElement.remove();
                    progressResultElements.delete(resultKey);
                }
            }

            progressResults.unshift(nextResult);
            progressResultLookup.add(resultKey);

            const entry = document.createElement('div');
            entry.className = `progress-result-item ${success ? 'success' : 'error'}`;
            entry.innerHTML = `
                <div class="progress-result-status">${success ? '✅' : '❌'}</div>
                <div class="progress-result-text">
                    <strong>${escapeHtml(nextResult.filename)}</strong>
                    <span>${success ? 'Completed successfully' : escapeHtml(error || 'Failed')}</span>
                </div>
            `;
            details.prepend(entry);
            progressResultElements.set(resultKey, entry);

            if (progressResults.length > MAX_PROGRESS_RESULTS) {
                const removedResult = progressResults.pop();
                if (removedResult) {
                    progressResultLookup.delete(removedResult.key);
                    const removedElement = progressResultElements.get(removedResult.key);
                    if (removedElement) {
                        removedElement.remove();
                    }
                    progressResultElements.delete(removedResult.key);
                }
            }

            setProgressCurrentFile(filename, success ? 'Completed' : 'Failed');
            document.getElementById('progressResultsCount').textContent = `${progressResults.length} result${progressResults.length === 1 ? '' : 's'}`;
        }
        
        function completeProgress() {
            document.getElementById('progressCloseBtn').style.display = 'block';
            document.getElementById('progressCancelBtn').style.display = 'none';  // Hide cancel button when complete
            setProgressCurrentFile(currentJobTitle || 'Batch job', 'Finished');
            
            // Update indicator to show completion
            const indicator = document.getElementById('progressIndicator');
            if (indicator.style.display !== 'none') {
                const indicatorText = document.getElementById('progressIndicatorText');
                indicatorText.textContent = '✅ Processing Complete';
            }
        }
        
        function closeProgressModal() {
            document.getElementById('progressModal').classList.remove('active');
            setProgressIndicatorVisible(false);
            document.getElementById('progressCancelBtn').style.display = 'none';  // Hide cancel button
            // Clear minimized state when modal is closed
            try {
                localStorage.removeItem('progressModalMinimized');
            } catch (e) {
                // localStorage may be unavailable (e.g., private browsing mode)
                console.warn('Could not clear localStorage:', e);
            }
        }
        
        function minimizeProgressModal() {
            const modal = document.getElementById('progressModal');
            const indicatorText = document.getElementById('progressIndicatorText');
            
            // Hide the modal
            modal.classList.remove('active');
            
            // Show the indicator with current progress
            const percentText = document.getElementById('progressPercent').textContent;
            const progressText = document.getElementById('progressText').textContent;
            indicatorText.textContent = `⏳ ${progressText} (${percentText})`;
            setProgressIndicatorVisible(true);
            
            // Save minimized state to localStorage
            try {
                localStorage.setItem('progressModalMinimized', 'true');
            } catch (e) {
                // localStorage may be unavailable (e.g., private browsing mode, quota exceeded)
                console.warn('Could not save to localStorage:', e);
            }
        }
        
        function restoreProgressModal() {
            const modal = document.getElementById('progressModal');
            
            // Show the modal
            modal.classList.add('active');
            
            // Hide the indicator
            setProgressIndicatorVisible(false);
            
            // Clear minimized state when modal is restored
            try {
                localStorage.removeItem('progressModalMinimized');
            } catch (e) {
                // localStorage may be unavailable (e.g., private browsing mode)
                console.warn('Could not clear localStorage:', e);
            }
        }
        
        async function loadLogFiles() {
            const logFileSelect = document.getElementById('logFile');
            const logType = document.getElementById('logType').value;
            
            try {
                logFileSelect.innerHTML = '<option value="">Loading...</option>';
                
                const response = await fetch(apiUrl(`/api/logs/files?type=${logType}`), {
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) return;
                
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                const data = await response.json();
                
                // Clear and populate the dropdown
                logFileSelect.innerHTML = '';
                
                if (data.files && data.files.length > 0) {
                    // Add "Most Recent" option
                    const mostRecentOption = document.createElement('option');
                    mostRecentOption.value = '';
                    mostRecentOption.textContent = 'Most Recent';
                    logFileSelect.appendChild(mostRecentOption);
                    
                    // Add each log file as an option
                    data.files.forEach(file => {
                        const option = document.createElement('option');
                        option.value = file.filename;
                        option.textContent = `${file.filename} (${file.size_mb} MB, ${file.last_modified})`;
                        logFileSelect.appendChild(option);
                    });
                } else {
                    const noFilesOption = document.createElement('option');
                    noFilesOption.value = '';
                    noFilesOption.textContent = 'No log files available';
                    logFileSelect.appendChild(noFilesOption);
                }
                
                // Load logs for the selected file (most recent by default)
                await loadLogs();
            } catch (error) {
                logFileSelect.innerHTML = '<option value="">Error loading files</option>';
                console.error('Failed to load log files:', error);
            }
        }
        
        async function onLogTypeChange() {
            await loadLogFiles();
        }
        
        async function loadLogs() {
            const logsContent = document.getElementById('logsContent');
            const logsLoadingIndicator = document.getElementById('logsLoadingIndicator');
            const logStats = document.getElementById('logStats');
            const lines = document.getElementById('logLines').value;
            const logType = document.getElementById('logType').value;
            const logFile = document.getElementById('logFile').value;
            
            try {
                logsLoadingIndicator.style.display = 'block';
                logsContent.textContent = '';
                logStats.textContent = '';
                
                let url = `/api/logs?lines=${lines}&type=${logType}`;
                if (logFile) {
                    url += `&filename=${encodeURIComponent(logFile)}`;
                }
                
                const response = await fetch(apiUrl(url), {
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) return;
                
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                const data = await response.json();
                
                if (data.error) {
                    logsContent.textContent = 'Error: ' + data.error;
                } else {
                    logsContent.textContent = data.content || 'No logs available';
                    const fileInfo = data.filename ? ` from ${data.filename}` : '';
                    logStats.textContent = `Showing ${data.shown_lines} of ${data.total_lines} total lines${fileInfo}`;
                }
            } catch (error) {
                logsContent.textContent = 'Failed to load logs: ' + error.message;
            } finally {
                logsLoadingIndicator.style.display = 'none';
            }
        }
        
        async function saveFilenameFormat() {
            const format = document.getElementById('filenameFormat').value.trim();
            const logMaxSize = parseFloat(document.getElementById('logMaxSize').value);
            const issueNumberPadding = parseInt(document.getElementById('issueNumberPadding').value);
            const dbCleanupInterval = parseInt(document.getElementById('dbCleanupInterval').value);
            const enableExternalSeriesMetadata = document.getElementById('enableExternalSeriesMetadata').checked;
            const comicVineApiKey = document.getElementById('comicVineApiKey').value.trim();
            const comicVineBaseUrl = document.getElementById('comicVineBaseUrl').value.trim();
            const enableMangaDexMetadata = document.getElementById('enableMangaDexMetadata').checked;
            const mangaDexBaseUrl = document.getElementById('mangaDexBaseUrl').value.trim();
            const enableAniListMetadata = document.getElementById('enableAniListMetadata').checked;
            const aniListBaseUrl = document.getElementById('aniListBaseUrl').value.trim();
            
            if (!format) {
                showMessage('Filename format cannot be empty', 'error');
                return;
            }
            
            if (isNaN(logMaxSize) || logMaxSize <= 0) {
                showMessage('Log max size must be a positive number', 'error');
                return;
            }
            
            if (isNaN(issueNumberPadding) || issueNumberPadding < 0) {
                showMessage('Issue number padding must be 0 or greater', 'error');
                return;
            }
            
            if (isNaN(dbCleanupInterval) || dbCleanupInterval < 0) {
                showMessage('Database cleanup interval must be 0 or greater', 'error');
                return;
            }
            
            try {
                // Save filename format
                const formatResponse = await fetch(apiUrl('/api/settings/filename-format'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({ format: format })
                });
                
                if (!formatResponse.ok) {
                    throw new Error(`HTTP error! status: ${formatResponse.status}`);
                }
                const formatResult = await formatResponse.json();
                
                if (formatResult.success === false) {
                    showMessage(formatResult.error || 'Failed to save filename format', 'error');
                    return;
                }
                
                // Save log max size
                const logResponse = await fetch(apiUrl('/api/settings/log-max-bytes'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({ maxMB: logMaxSize })
                });
                
                if (!logResponse.ok) {
                    throw new Error(`HTTP error! status: ${logResponse.status}`);
                }
                const logResult = await logResponse.json();
                
                if (logResult.success === false) {
                    showMessage(logResult.error || 'Failed to save log max size', 'error');
                    return;
                }
                
                // Save issue number padding
                const paddingResponse = await fetch(apiUrl('/api/settings/issue-number-padding'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({ padding: issueNumberPadding })
                });
                
                if (!paddingResponse.ok) {
                    throw new Error(`HTTP error! status: ${paddingResponse.status}`);
                }
                const paddingResult = await paddingResponse.json();
                
                if (paddingResult.success === false) {
                    showMessage(paddingResult.error || 'Failed to save issue number padding', 'error');
                    return;
                }
                
                // Save database cleanup interval
                const cleanupResponse = await fetch(apiUrl('/api/settings/database-cleanup-interval-hours'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({ hours: dbCleanupInterval })
                });
                
                if (!cleanupResponse.ok) {
                    throw new Error(`HTTP error! status: ${cleanupResponse.status}`);
                }
                const cleanupResult = await cleanupResponse.json();
                
                if (cleanupResult.success === false) {
                    showMessage(cleanupResult.error || 'Failed to save database cleanup interval', 'error');
                    return;
                }

                // Save external series metadata settings
                const metadataResponse = await fetch(apiUrl('/api/settings/external-series-metadata'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({
                        enabled: enableExternalSeriesMetadata,
                        comicVineApiKey: comicVineApiKey,
                        comicVineBaseUrl: comicVineBaseUrl || 'https://comicvine.gamespot.com/api',
                        enableMangaDex: enableMangaDexMetadata,
                        mangaDexBaseUrl: mangaDexBaseUrl || 'https://api.mangadex.org',
                        enableAniList: enableAniListMetadata,
                        aniListBaseUrl: aniListBaseUrl || 'https://graphql.anilist.co'
                    })
                });

                if (!metadataResponse.ok) {
                    throw new Error(`HTTP error! status: ${metadataResponse.status}`);
                }

                const metadataResult = await metadataResponse.json();
                if (metadataResult.success === false) {
                    showMessage(metadataResult.error || 'Failed to save external metadata settings', 'error');
                    return;
                }
                
                showMessage('Settings saved successfully! Changes to log rotation, external metadata, and database cleanup will take effect on restart.', 'success');
                closeSettings();
            } catch (error) {
                showMessage('Failed to save settings: ' + error.message, 'error');
            }
        }
        
        async function resetFilenameFormat() {
            try {
                const response = await fetch(apiUrl('/api/settings/filename-format'), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                const data = await response.json();
                
                document.getElementById('filenameFormat').value = data.default;
                showMessage('Reset to default format', 'info');
            } catch (error) {
                showMessage('Failed to reset format: ' + error.message, 'error');
            }
        }
        
        async function cleanupDatabaseNow() {
            try {
                showMessage('Starting database cleanup...', 'info');
                
                const response = await fetch(apiUrl('/api/settings/cleanup-database'), {
                    method: 'POST',
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                const result = await response.json();
                
                if (result.success) {
                    showMessage(`Database cleanup completed. Removed ${result.removedCount} stale entries.`, 'success');
                } else {
                    showMessage(result.error || 'Failed to cleanup database', 'error');
                }
            } catch (error) {
                showMessage('Failed to cleanup database: ' + error.message, 'error');
            }
        }
        
        function toggleDropdown(event, filepath) {
            event.stopPropagation();
            
            const dropdownId = getDropdownId(filepath);
            const dropdown = document.getElementById(dropdownId);
            
            if (!dropdown) {
                console.error('Dropdown not found for filepath:', filepath, 'ID:', dropdownId);
                return;
            }
            
            // Close all other dropdowns
            document.querySelectorAll('.dropdown-menu.show').forEach(menu => {
                if (menu.id !== dropdownId) {
                    menu.classList.remove('show');
                    menu.classList.remove('show-above');
                }
            });
            
            // Toggle this dropdown
            const isCurrentlyShown = dropdown.classList.contains('show');
            dropdown.classList.toggle('show');
            
            // If we're showing the dropdown, position it relative to the button
            if (!isCurrentlyShown) {
                // Remove any previous positioning class
                dropdown.classList.remove('show-above');
                
                // Get the dropdown button position
                const button = event.target.closest('.dropdown-toggle');
                if (button) {
                    const buttonRect = button.getBoundingClientRect();
                    
                    // Temporarily show dropdown to get its dimensions
                    dropdown.style.visibility = 'hidden';
                    dropdown.style.display = 'block';
                    const dropdownRect = dropdown.getBoundingClientRect();
                    dropdown.style.visibility = '';
                    dropdown.style.display = '';
                    
                    const viewportHeight = window.innerHeight;
                    const viewportWidth = window.innerWidth;
                    
                    // Constants for positioning calculations
                    const MARGIN = 20; // Safety margin to prevent edge cutoff
                    const DROPDOWN_OFFSET = 2; // Spacing between button and dropdown
                    const VIEWPORT_MARGIN = 10; // Minimum margin from viewport edges
                    
                    // Check if dropdown would overflow the bottom of the viewport
                    const spaceBelow = viewportHeight - buttonRect.bottom;
                    const spaceAbove = buttonRect.top;
                    
                    // Determine if we should show above or below
                    const notEnoughSpaceBelow = spaceBelow < (dropdownRect.height + MARGIN);
                    const inBottomHalf = buttonRect.bottom > (viewportHeight / 2);
                    const moreSpaceAbove = spaceAbove > spaceBelow;
                    const showAbove = notEnoughSpaceBelow || (inBottomHalf && moreSpaceAbove);
                    
                    // Calculate position
                    let top, left;
                    
                    if (showAbove) {
                        // Position above the button
                        top = buttonRect.top - dropdownRect.height - DROPDOWN_OFFSET;
                        dropdown.classList.add('show-above');
                    } else {
                        // Position below the button
                        top = buttonRect.bottom + DROPDOWN_OFFSET;
                    }
                    
                    // Align to the right edge of the button
                    left = buttonRect.right - dropdownRect.width;
                    
                    // Make sure dropdown doesn't go off the left edge of the viewport
                    if (left < VIEWPORT_MARGIN) {
                        left = VIEWPORT_MARGIN;
                    }
                    
                    // Make sure dropdown doesn't go off the right edge of the viewport
                    if (left + dropdownRect.width > viewportWidth - VIEWPORT_MARGIN) {
                        left = viewportWidth - dropdownRect.width - VIEWPORT_MARGIN;
                    }
                    
                    // Apply the position
                    dropdown.style.top = `${top}px`;
                    dropdown.style.left = `${left}px`;
                }
            } else {
                // If we're hiding it, also remove the positioning class
                dropdown.classList.remove('show-above');
            }
        }
        
        function closeAllDropdowns() {
            document.querySelectorAll('.dropdown-menu.show').forEach(menu => {
                menu.classList.remove('show');
                menu.classList.remove('show-above');
            });
            // Also close filter dropdown
            const filterMenu = document.getElementById('filterDropdownMenu');
            if (filterMenu) {
                filterMenu.classList.remove('show');
            }
        }
        
        // Close dropdowns when clicking outside
        document.addEventListener('click', function(event) {
            if (!event.target.closest('.file-actions-dropdown')) {
                closeAllDropdowns();
            }
            // Close header filter dropdown when clicking outside
            if (!event.target.closest('.header-filter-dropdown')) {
                const filterMenu = document.getElementById('headerFilterMenu');
                if (filterMenu) {
                    filterMenu.classList.remove('show');
                }
            }
            // Close header sort dropdown when clicking outside
            if (!event.target.closest('.header-sort-dropdown')) {
                const sortMenu = document.getElementById('headerSortMenu');
                if (sortMenu) {
                    sortMenu.classList.remove('show');
                }
            }
            // Close settings dropdown when clicking outside
            if (!event.target.closest('.settings-menu-wrapper')) {
                const settingsMenu = document.getElementById('settingsDropdownMenu');
                if (settingsMenu) {
                    settingsMenu.classList.remove('show');
                }
            }
            // Close action dropdowns when clicking outside
            if (!event.target.closest('.action-dropdown')) {
                closeAllActionDropdowns();
            }
        });
        
        // Function to toggle action dropdowns
        function toggleActionDropdown(event, dropdownId) {
            event.stopPropagation();
            
            const dropdown = document.getElementById(dropdownId);
            if (!dropdown) {
                console.error('Action dropdown not found:', dropdownId);
                return;
            }
            
            // Close all other action dropdowns
            document.querySelectorAll('.action-dropdown-menu.show').forEach(menu => {
                if (menu.id !== dropdownId) {
                    menu.classList.remove('show');
                }
            });
            
            // Toggle this dropdown
            dropdown.classList.toggle('show');
        }
        
        // Function to close all action dropdowns
        function closeAllActionDropdowns() {
            document.querySelectorAll('.action-dropdown-menu.show').forEach(menu => {
                menu.classList.remove('show');
            });
        }
        
        // Function to delete selected files
        async function deleteSelectedFiles() {
            const selectedFilesArray = Array.from(selectedFiles);

            if (selectedFilesArray.length === 0) {
                showMessage('No files selected', 'error');
                return;
            }

            // Confirm deletion
            if (!confirm(`Are you sure you want to delete ${selectedFilesArray.length} file(s)? This action cannot be undone.`)) {
                return;
            }

            showProgressModal(`Deleting ${selectedFilesArray.length} file(s)...`);

            try {
                console.log(`[BULK DELETE] Starting delete-selected job for ${selectedFilesArray.length} file(s)...`);
                const response = await fetch(apiUrl('/api/jobs/delete-selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ Files: selectedFilesArray })
                });

                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }

                if (!response.ok) {
                    const errorData = await response.json().catch(() => ({}));
                    throw new Error(errorData.error || `Failed to start delete job (HTTP ${response.status})`);
                }

                const data = await response.json();
                const jobId = data.jobId || data.job_id;
                const totalItems = data.totalItems || data.total_items || selectedFilesArray.length;

                console.log(`[BULK DELETE] Created job ${jobId} for ${totalItems} file(s)`);
                showMessage(`Deleting ${totalItems} file(s) in background`, 'info');

                // Clear selected files now; the server is authoritative from this
                // point on and SSE events (job_updated / file_processed /
                // file_list_updated) will refresh the library in place.
                selectedFiles.clear();

                // Track via the standard SSE-driven progress modal.
                await trackJobStatus(jobId, `Deleting ${totalItems} file(s)...`);
            } catch (error) {
                console.error('[BULK DELETE] Error starting delete-selected job:', error);
                showMessage('Failed to start delete: ' + error.message, 'error');
                closeProgressModal();
            }
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
            const statusIndicator = document.getElementById('watcherStatus');
            const iconElement = statusIndicator.querySelector('.watcher-icon');
            const textElement = statusIndicator.querySelector('.watcher-text');
            
            // Remove previous status classes
            statusIndicator.classList.remove('running', 'stopped');
            
            if (running === null || enabled === null) {
                // Unknown status
                iconElement.textContent = '❓';
                textElement.textContent = 'Status Unknown';
                statusIndicator.title = 'Unable to determine watcher status';
            } else if (running) {
                statusIndicator.classList.add('running');
                iconElement.textContent = '✅';
                textElement.textContent = 'Watcher Running';
                statusIndicator.title = 'File watcher is running and monitoring for changes';
            } else {
                statusIndicator.classList.add('stopped');
                iconElement.textContent = '⛔';
                textElement.textContent = 'Watcher Stopped';
                if (enabled) {
                    statusIndicator.title = 'File watcher is enabled but not running';
                } else {
                    statusIndicator.title = 'File watcher is disabled';
                }
            }
        }

        // ====================================================================
        // External Series Metadata (manual refresh + alias management)
        // ====================================================================

        let manageSeriesState = { seriesTitle: '', record: null };

        async function refreshAllExternalMetadata() {
            if (!confirm('Queue an external metadata refresh for every series in the library? Lookups happen in the background and progress is reported as a job.')) {
                return;
            }
            try {
                const response = await fetch(apiUrl('/api/metadata/refresh-all'), {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    credentials: 'same-origin'
                });
                if (!response.ok) {
                    const error = await response.text();
                    showMessage('Failed to queue metadata refresh: ' + error, 'error');
                    return;
                }
                const data = await response.json();
                showMessage(`Metadata refresh job queued for ${data.totalSeries || 0} series.`, 'success');
                // Surface progress through the same toast/poll flow used by the
                // per-series refresh so the user can see the job advancing.
                if (data.jobId) {
                    trackMetadataRefreshJob(data.jobId, `All Series (${data.totalSeries || 0})`);
                }
            } catch (err) {
                console.error('refreshAllExternalMetadata failed', err);
                showMessage('Failed to queue metadata refresh', 'error');
            }
        }

        async function matchAllUnmatchedSeries() {
            if (!confirm('Queue an external metadata lookup for every series that is not yet matched? Lookups happen in the background and progress is reported as a job.')) {
                return;
            }
            try {
                const response = await fetch(apiUrl('/api/metadata/match-unmatched'), {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    credentials: 'same-origin'
                });
                if (!response.ok) {
                    const error = await response.text();
                    showMessage('Failed to queue match-unmatched: ' + error, 'error');
                    return;
                }
                const data = await response.json();
                const total = data.totalSeries || 0;
                if (total === 0) {
                    showMessage('No unmatched series to process.', 'info');
                    return;
                }
                showMessage(`Match job queued for ${total} unmatched series.`, 'success');
                if (data.jobId) {
                    trackMetadataRefreshJob(data.jobId, `Unmatched Series (${total})`);
                }
            } catch (err) {
                console.error('matchAllUnmatchedSeries failed', err);
                showMessage('Failed to queue match-unmatched', 'error');
            }
        }

        async function refreshSeriesMetadataDirect(seriesTitle) {
            if (!seriesTitle) return;
            try {
                // Always queue the refresh as a background job so the UI is never
                // blocked on the (potentially slow) external lookup. Progress is
                // surfaced via the same inline toast used by refresh-all /
                // refresh-folder.
                const response = await fetch(apiUrl(`/api/metadata/refresh/${encodeURIComponent(seriesTitle)}?queue=true`), {
                    method: 'POST',
                    headers: getAuthHeaders ? getAuthHeaders() : undefined,
                    credentials: 'same-origin'
                });
                if (!response.ok) {
                    showMessage('Failed to refresh metadata for ' + seriesTitle, 'error');
                    return;
                }
                const data = await response.json();
                if (data && data.jobId) {
                    showMessage(`Queued metadata refresh for "${seriesTitle}"`, 'info');
                    trackMetadataRefreshJob(data.jobId, seriesTitle);
                } else {
                    // Fallback: server returned the sync record (legacy path).
                    const status = data.lookup_status || 'success';
                    if (status === 'not_found') {
                        showMessage(`No external metadata found for "${seriesTitle}"`, 'info');
                    } else if (status === 'error') {
                        showMessage(`External lookup failed for "${seriesTitle}"`, 'error');
                    } else {
                        showMessage(`Metadata refreshed for "${seriesTitle}"${data.source ? ' from ' + data.source : ''}`, 'success');
                    }
                    if (typeof loadSeriesLibrary === 'function') {
                        loadSeriesLibrary(1, true);
                    }
                    loadProviderHealth();
                }
            } catch (err) {
                console.error('refreshSeriesMetadataDirect failed', err);
                showMessage('Failed to refresh metadata', 'error');
            }
        }

        // Queue a metadata refresh for every title that maps to one series card.
        // Surfaces progress through the same toast/poll flow as the single-title
        // refresh so the user can see something is happening.
        async function refreshSeriesFolder(seriesId, seriesTitle) {
            if (!seriesId) return;
            try {
                const response = await fetch(apiUrl('/api/metadata/refresh/folder'), {
                    method: 'POST',
                    headers: Object.assign(
                        { 'Content-Type': 'application/json' },
                        getAuthHeaders ? getAuthHeaders() : {}),
                    credentials: 'same-origin',
                    body: JSON.stringify({ seriesId })
                });
                if (response.status === 404) {
                    showMessage(`Series "${seriesTitle || seriesId}" not found`, 'error');
                    return;
                }
                if (!response.ok) {
                    showMessage('Failed to queue folder metadata refresh', 'error');
                    return;
                }
                const data = await response.json();
                showMessage(`Queued metadata refresh for ${data.totalSeries} title(s) in "${seriesTitle || seriesId}"`, 'info');
                trackMetadataRefreshJob(data.jobId, seriesTitle || seriesId);
            } catch (err) {
                console.error('refreshSeriesFolder failed', err);
                showMessage('Failed to queue folder metadata refresh', 'error');
            }
        }

        async function resetSeriesProcessedStatus(seriesId, seriesTitle) {
            if (!seriesId) return;
            const label = seriesTitle || seriesId;
            const proceed = confirm(
                `Reset the renamed/normalized flags on every file in "${label}"?\n\n` +
                'The files will then be re-processed on the next Process / Rename / Normalize run. ' +
                'Use this when a series metadata or filename change refuses to apply.');
            if (!proceed) return;
            try {
                const response = await fetch(apiUrl(`/api/status/clear-series/${encodeURIComponent(seriesId)}`), {
                    method: 'POST',
                    headers: Object.assign(
                        { 'Content-Type': 'application/json' },
                        getAuthHeaders ? getAuthHeaders() : {}),
                    credentials: 'same-origin'
                });
                if (response.status === 404) {
                    showMessage(`Series "${label}" not found`, 'error');
                    return;
                }
                if (!response.ok) {
                    showMessage('Failed to reset processed status', 'error');
                    return;
                }
                const data = await response.json();
                showMessage(
                    `Cleared processed status on ${data.cleared} of ${data.requested} file(s) in "${label}". ` +
                    'Run Process / Rename / Normalize to reapply.',
                    'success');
                if (typeof loadSeriesLibrary === 'function') {
                    loadSeriesLibrary({ refresh: true });
                }
            } catch (err) {
                console.error('resetSeriesProcessedStatus failed', err);
                showMessage('Failed to reset processed status', 'error');
            }
        }

        // Lightweight poller for metadata refresh jobs. We intentionally don't
        // hook into the SSE current-job slot (which is reserved for the heavy
        // scan/process pipeline) — these short jobs poll their own status and
        // render an inline progress toast.
        function trackMetadataRefreshJob(jobId, label) {
            if (!jobId || metadataRefreshJobs.has(jobId)) return;
            metadataRefreshJobs.set(jobId, { label });
            const intervalMs = 1500;
            const tick = async () => {
                try {
                    const response = await fetch(apiUrl(`/api/metadata/refresh/job/${jobId}`), {
                        headers: getAuthHeaders ? getAuthHeaders() : undefined,
                        credentials: 'same-origin'
                    });
                    if (!response.ok) {
                        metadataRefreshJobs.delete(jobId);
                        return;
                    }
                    const job = await response.json();
                    renderMetadataRefreshToast(jobId, label, job);
                    const status = (job.status || '').toString().toLowerCase();
                    if (status === 'completed' || status === 'failed' || status === 'cancelled') {
                        metadataRefreshJobs.delete(jobId);
                        // Final library refresh + provider health update.
                        if (typeof loadSeriesLibrary === 'function') {
                            loadSeriesLibrary({ refresh: true });
                        }
                        loadProviderHealth();
                        return;
                    }
                    setTimeout(tick, intervalMs);
                } catch (err) {
                    console.warn('metadata refresh poll failed', err);
                    metadataRefreshJobs.delete(jobId);
                }
            };
            setTimeout(tick, intervalMs);
        }

        function renderMetadataRefreshToast(jobId, label, job) {
            let host = document.getElementById('metadataRefreshToasts');
            if (!host) {
                host = document.createElement('div');
                host.id = 'metadataRefreshToasts';
                host.className = 'metadata-refresh-toasts';
                document.body.appendChild(host);
            }
            let toast = document.getElementById(`mdrToast-${jobId}`);
            if (!toast) {
                toast = document.createElement('div');
                toast.id = `mdrToast-${jobId}`;
                toast.className = 'metadata-refresh-toast';
                host.appendChild(toast);
            }
            const total = job.totalSeries || job.TotalSeries || 0;
            const processed = job.processedSeries || job.ProcessedSeries || 0;
            const successes = job.successes || job.Successes || 0;
            const failures = job.failures || job.Failures || 0;
            const status = (job.status || job.Status || 'queued').toString().toLowerCase();
            const current = job.currentSeries || job.CurrentSeries || '';
            const pct = total > 0 ? Math.round((processed / total) * 100) : 0;
            toast.innerHTML = `
                <div class="metadata-refresh-toast-header">
                    <strong>Refreshing ${escapeHtml(label)}</strong>
                    <span class="metadata-refresh-toast-status metadata-refresh-toast-status--${status}">${escapeHtml(status)}</span>
                </div>
                <div class="metadata-refresh-toast-bar"><div class="metadata-refresh-toast-bar-fill" style="width:${pct}%"></div></div>
                <div class="metadata-refresh-toast-meta">${processed}/${total} · ✓ ${successes} · ✗ ${failures}${current ? ` · now: ${escapeHtml(current)}` : ''}</div>
            `;
            if (status === 'completed' || status === 'failed' || status === 'cancelled') {
                toast.classList.add('metadata-refresh-toast--done');
                setTimeout(() => { toast.remove(); }, 6000);
            }
        }

        async function openManageSeriesNamesModal(seriesTitle) {
            manageSeriesState = { seriesTitle, record: null };
            document.getElementById('manageSeriesTitle').textContent = seriesTitle;
            document.getElementById('manageSeriesProviderAliases').textContent = 'Loading...';
            document.getElementById('manageSeriesProviderSource').textContent = '';
            document.getElementById('manageSeriesUserAliases').textContent = 'Loading...';
            const nameSelect = document.getElementById('manageSeriesName');
            if (nameSelect) {
                nameSelect.innerHTML = '<option value="">Automatic (use language preference)</option>';
                nameSelect.value = '';
            }
            const resolved = document.getElementById('manageSeriesResolvedName');
            if (resolved) resolved.textContent = '';
            document.getElementById('manageSeriesSearchInput').value = seriesTitle;
            document.getElementById('manageSeriesSearchResults').innerHTML = '';
            document.getElementById('manageSeriesNamesModal').classList.add('active');
            await loadManageSeriesRecord();
        }

        function closeManageSeriesNamesModal() {
            document.getElementById('manageSeriesNamesModal').classList.remove('active');
            manageSeriesState = { seriesTitle: '', record: null };
        }

        async function loadManageSeriesRecord() {
            const { seriesTitle } = manageSeriesState;
            if (!seriesTitle) return;
            try {
                const response = await fetch(apiUrl(`/api/metadata/series/${encodeURIComponent(seriesTitle)}`), {
                    credentials: 'same-origin'
                });
                if (!response.ok) {
                    showMessage('Failed to load series metadata', 'error');
                    return;
                }
                const record = await response.json();
                manageSeriesState.record = record;
                const prefSelect = document.getElementById('manageSeriesPreferredLanguage');
                if (prefSelect) {
                    prefSelect.value = record.preferred_language || '';
                }
                populateManageSeriesNameOptions(record);
                renderManageSeriesProviderAliases(record);
                renderManageSeriesUserAliases(record);
                renderManageSeriesImage(record);
            } catch (err) {
                console.error('loadManageSeriesRecord failed', err);
                showMessage('Failed to load series metadata', 'error');
            }
        }

        function renderManageSeriesImage(record) {
            const preview = document.getElementById('manageSeriesImagePreview');
            const status = document.getElementById('manageSeriesImageStatus');
            if (!preview || !status) return;
            // Reset preview before fetching the new one to avoid showing a
            // stale image when switching series.
            preview.removeAttribute('src');
            const key = record && record.series_id;
            if (record && record.has_image && key) {
                const isUser = !!record.is_user_image;
                const downloadedAt = record.image_downloaded_utc ? new Date(record.image_downloaded_utc).toLocaleString() : 'unknown';
                status.textContent = `${isUser ? 'User-uploaded' : 'Downloaded from provider'} · ${downloadedAt}`;
                fetchProtectedImageInto(preview, `/api/series-images/${encodeURIComponent(key)}`);
            } else {
                status.textContent = 'No image cached. Refresh metadata or upload one below.';
            }
        }

        async function fetchProtectedImageInto(imgElement, apiPath) {
            try {
                const response = await fetch(apiUrl(apiPath), { headers: getAuthHeaders() });
                if (!response.ok) return;
                const blob = await response.blob();
                imgElement.src = URL.createObjectURL(blob);
            } catch (err) {
                console.error('fetchProtectedImageInto failed', err);
            }
        }

        async function uploadManageSeriesImage() {
            const { seriesTitle } = manageSeriesState;
            if (!seriesTitle) return;
            const input = document.getElementById('manageSeriesImageInput');
            const file = input && input.files && input.files[0];
            if (!file) return;
            // Allow only image content-types client-side; the server enforces
            // the same allowlist + magic-byte validation.
            if (!/^image\/(jpeg|png|webp)$/i.test(file.type)) {
                showMessage('Only JPEG, PNG, or WEBP images are supported', 'error');
                input.value = '';
                return;
            }
            const formData = new FormData();
            formData.append('file', file);
            try {
                const response = await fetch(apiUrl(`/api/series-images/${encodeURIComponent(seriesTitle)}`), {
                    method: 'PUT',
                    headers: getAuthHeaders(),
                    body: formData
                });
                if (!response.ok) {
                    let msg = 'Failed to upload series image';
                    try { const j = await response.json(); if (j && j.error) msg = j.error; } catch {}
                    showMessage(msg, 'error');
                    return;
                }
                const record = await response.json();
                manageSeriesState.record = record;
                renderManageSeriesImage(record);
                showMessage('Series image updated', 'success');
                if (typeof loadSeriesLibrary === 'function') {
                    loadSeriesLibrary(1, true);
                }
            } catch (err) {
                console.error('uploadManageSeriesImage failed', err);
                showMessage('Failed to upload series image', 'error');
            } finally {
                input.value = '';
            }
        }

        async function clearManageSeriesImage() {
            const record = manageSeriesState.record;
            const key = record && record.series_id;
            if (!key) {
                showMessage('No image to clear', 'info');
                return;
            }
            try {
                const response = await fetch(apiUrl(`/api/series-images/${encodeURIComponent(key)}`), {
                    method: 'DELETE',
                    headers: getAuthHeaders()
                });
                if (!response.ok && response.status !== 404) {
                    showMessage('Failed to clear series image', 'error');
                    return;
                }
                if (response.ok) {
                    const updated = await response.json();
                    manageSeriesState.record = updated;
                    renderManageSeriesImage(updated);
                }
                showMessage('Series image cleared', 'success');
                if (typeof loadSeriesLibrary === 'function') {
                    loadSeriesLibrary(1, true);
                }
            } catch (err) {
                console.error('clearManageSeriesImage failed', err);
                showMessage('Failed to clear series image', 'error');
            }
        }

        // ---- Fetch series image from external provider --------------------

        function openFetchSeriesImageFromProvider() {
            const panel = document.getElementById('manageSeriesImageProviderPanel');
            if (!panel) return;
            panel.style.display = 'block';
            const queryInput = document.getElementById('manageSeriesImageProviderQuery');
            if (queryInput) {
                queryInput.value = (manageSeriesState && manageSeriesState.seriesTitle) || '';
                queryInput.focus();
                queryInput.select();
                // Convenience: press Enter to search.
                queryInput.onkeydown = (e) => {
                    if (e.key === 'Enter') {
                        e.preventDefault();
                        searchSeriesImageCandidates();
                    }
                };
            }
            const results = document.getElementById('manageSeriesImageProviderResults');
            if (results) results.innerHTML = '';
            const status = document.getElementById('manageSeriesImageProviderStatus');
            if (status) status.textContent = 'Enter a title and click Search to find cover candidates from the configured providers.';
        }

        function closeFetchSeriesImageFromProvider() {
            const panel = document.getElementById('manageSeriesImageProviderPanel');
            if (panel) panel.style.display = 'none';
        }

        async function searchSeriesImageCandidates() {
            const queryInput = document.getElementById('manageSeriesImageProviderQuery');
            const status = document.getElementById('manageSeriesImageProviderStatus');
            const results = document.getElementById('manageSeriesImageProviderResults');
            if (!queryInput || !status || !results) return;
            const query = (queryInput.value || '').trim();
            if (!query) {
                status.textContent = 'Enter a title to search.';
                return;
            }
            status.textContent = 'Searching providers...';
            results.innerHTML = '';
            try {
                const response = await fetch(
                    apiUrl(`/api/series-images/candidates?query=${encodeURIComponent(query)}&limit=12`),
                    { headers: getAuthHeaders() }
                );
                if (!response.ok) {
                    status.textContent = 'Search failed.';
                    return;
                }
                const data = await response.json();
                const candidates = (data && data.candidates) || [];
                if (!candidates.length) {
                    status.textContent = 'No provider returned a cover image for that query.';
                    return;
                }
                status.textContent = `${candidates.length} candidate${candidates.length === 1 ? '' : 's'} — click a thumbnail to apply.`;
                results.innerHTML = '';
                candidates.forEach((cand) => {
                    const previewUrl = cand.thumbnail_url || cand.image_url;
                    const card = document.createElement('div');
                    card.style.cssText = 'display: flex; flex-direction: column; align-items: center; gap: 4px; width: 90px; cursor: pointer; padding: 4px; border-radius: 4px; background: var(--bg-primary); border: 1px solid var(--border-primary);';
                    card.title = `${cand.canonical_title || ''} (${cand.source || ''})`;
                    // Use DOM APIs (not innerHTML) so provider-controlled text
                    // can't introduce script in the picker.
                    const img = document.createElement('img');
                    img.alt = cand.canonical_title || 'Series cover';
                    img.referrerPolicy = 'no-referrer';
                    img.loading = 'lazy';
                    img.style.cssText = 'width: 80px; height: 120px; object-fit: cover; border-radius: 4px; background: var(--bg-hover);';
                    img.src = previewUrl;
                    const sourceLabel = document.createElement('div');
                    sourceLabel.textContent = cand.source || '';
                    sourceLabel.style.cssText = 'font-size: 11px; color: var(--text-secondary);';
                    const titleLabel = document.createElement('div');
                    titleLabel.textContent = cand.canonical_title || '';
                    titleLabel.style.cssText = 'font-size: 11px; color: var(--text-primary); text-align: center; max-width: 88px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;';
                    card.appendChild(img);
                    card.appendChild(titleLabel);
                    card.appendChild(sourceLabel);
                    card.onclick = () => applySeriesImageCandidate(cand.image_url, cand.source);
                    results.appendChild(card);
                });
            } catch (err) {
                console.error('searchSeriesImageCandidates failed', err);
                status.textContent = 'Search failed.';
            }
        }

        async function applySeriesImageCandidate(imageUrl, source) {
            const { seriesTitle } = manageSeriesState;
            if (!seriesTitle || !imageUrl) return;
            const status = document.getElementById('manageSeriesImageProviderStatus');
            if (status) status.textContent = 'Downloading selected image...';
            try {
                const response = await fetch(
                    apiUrl(`/api/series-images/${encodeURIComponent(seriesTitle)}/from-provider`),
                    {
                        method: 'POST',
                        headers: Object.assign({ 'Content-Type': 'application/json' }, getAuthHeaders()),
                        body: JSON.stringify({ imageUrl, source })
                    }
                );
                if (!response.ok) {
                    let msg = 'Failed to apply provider image';
                    try { const j = await response.json(); if (j && j.error) msg = j.error; } catch {}
                    if (status) status.textContent = msg;
                    showMessage(msg, 'error');
                    return;
                }
                const record = await response.json();
                manageSeriesState.record = record;
                renderManageSeriesImage(record);
                showMessage('Series image updated from provider', 'success');
                closeFetchSeriesImageFromProvider();
                if (typeof loadSeriesLibrary === 'function') {
                    loadSeriesLibrary(1, true);
                }
            } catch (err) {
                console.error('applySeriesImageCandidate failed', err);
                if (status) status.textContent = 'Failed to apply provider image';
                showMessage('Failed to apply provider image', 'error');
            }
        }

        function renderManageSeriesProviderAliases(record) {
            const container = document.getElementById('manageSeriesProviderAliases');
            const aliases = record.aliases || [];
            // Build a lookup of alias -> language from localized_titles so we
            // can render a small language badge next to each alias.
            const langByTitle = new Map();
            (record.localized_titles || []).forEach(lt => {
                if (lt && lt.title && lt.language) {
                    langByTitle.set(lt.title.toLowerCase(), lt.language);
                }
            });
            if (!aliases.length) {
                container.textContent = 'None';
            } else {
                container.innerHTML = aliases.map(a => {
                    const lang = langByTitle.get(a.toLowerCase());
                    const langBadge = lang
                        ? ` <span style="font-size: 11px; padding: 1px 6px; margin-left: 4px; background: var(--bg-primary); border: 1px solid var(--border-primary); border-radius: 8px; color: var(--text-secondary);">${escapeHtml(lang)}</span>`
                        : '';
                    return `<span class="badge" style="display: inline-block; padding: 4px 8px; margin: 2px; background: var(--bg-secondary); border-radius: 12px; font-size: 13px;">${escapeHtml(a)}${langBadge}</span>`;
                }).join('');
            }
            const sourceLabel = document.getElementById('manageSeriesProviderSource');
            if (record.source) {
                const ts = record.last_lookup_utc ? new Date(record.last_lookup_utc).toLocaleString() : 'never';
                sourceLabel.textContent = `Source: ${record.source} · Last lookup: ${ts} · Status: ${record.lookup_status || 'unknown'}`;
            } else {
                sourceLabel.textContent = 'No external lookup yet — use "Refresh from Provider" to populate.';
            }
        }

        // Save just the per-series preferred language (called on the select's
        // change event so users see the effect immediately without having to
        // hit the modal-wide "Save" button).
        async function saveManageSeriesPreferredLanguage() {
            const { seriesTitle } = manageSeriesState;
            if (!seriesTitle) return;
            const select = document.getElementById('manageSeriesPreferredLanguage');
            if (!select) return;
            const value = select.value || null;
            try {
                const response = await fetch(apiUrl(`/api/metadata/series/${encodeURIComponent(seriesTitle)}/preferred-language`), {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    credentials: 'same-origin',
                    body: JSON.stringify({ language: value })
                });
                if (!response.ok) {
                    let msg = 'Failed to update preferred language';
                    try { const j = await response.json(); if (j && (j.error || typeof j === 'string')) msg = j.error || j; } catch {}
                    showMessage(msg, 'error');
                    return;
                }
                const record = await response.json();
                manageSeriesState.record = record;
                showMessage(value ? `Preferred language set to ${value}` : 'Preferred language cleared', 'success');
                if (typeof loadSeriesLibrary === 'function') {
                    loadSeriesLibrary(1, true);
                }
            } catch (err) {
                console.error('saveManageSeriesPreferredLanguage failed', err);
                showMessage('Failed to update preferred language', 'error');
            }
        }

        // Build the Series Name <select> from every name the system knows for
        // this series: its canonical title, provider aliases, user aliases and
        // provider-supplied localized titles (with language hints). The first
        // option is "Automatic", which clears the user-selected name and falls
        // back to the language-preference rule. Case-insensitive dedup avoids
        // listing the same title twice.
        function populateManageSeriesNameOptions(record) {
            const select = document.getElementById('manageSeriesName');
            if (!select) return;
            const seen = new Set();
            const options = ['<option value="">Automatic (use language preference)</option>'];
            const pushOption = (title, languageHint) => {
                if (!title) return;
                const key = title.trim().toLowerCase();
                if (!key || seen.has(key)) return;
                seen.add(key);
                const label = languageHint
                    ? `${escapeHtml(title)} (${escapeHtml(languageHint)})`
                    : escapeHtml(title);
                options.push(`<option value="${escapeHtml(title)}">${label}</option>`);
            };
            if (record && record.canonical_title) {
                pushOption(record.canonical_title, 'canonical');
            }
            if (record && Array.isArray(record.localized_titles)) {
                for (const lt of record.localized_titles) {
                    pushOption(lt && lt.title, lt && lt.language ? lt.language : null);
                }
            }
            if (record && Array.isArray(record.aliases)) {
                for (const a of record.aliases) pushOption(a, 'provider alias');
            }
            if (record && Array.isArray(record.user_aliases)) {
                for (const a of record.user_aliases) pushOption(a, 'your alias');
            }
            select.innerHTML = options.join('');
            // Preserve the user's current selection even if it isn't otherwise
            // listed (server validation already accepted it at write time).
            const selected = (record && record.series_name) || '';
            if (selected && !seen.has(selected.trim().toLowerCase())) {
                const opt = document.createElement('option');
                opt.value = selected;
                opt.textContent = selected;
                select.appendChild(opt);
            }
            select.value = selected;

            const resolved = document.getElementById('manageSeriesResolvedName');
            if (resolved) {
                const shown = (record && record.resolved_series_name) || '';
                resolved.textContent = selected
                    ? `Using your selected name: "${shown}".`
                    : (shown ? `Automatic name currently resolves to: "${shown}".` : '');
            }
        }

        // Save just the per-series name (called on the select's change event,
        // same UX pattern as the preferred-language control). An empty value
        // reverts to automatic resolution.
        async function saveManageSeriesName() {
            const { seriesTitle } = manageSeriesState;
            if (!seriesTitle) return;
            const select = document.getElementById('manageSeriesName');
            if (!select) return;
            const value = select.value || null;
            try {
                const response = await fetch(apiUrl(`/api/metadata/series/${encodeURIComponent(seriesTitle)}/name`), {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    credentials: 'same-origin',
                    body: JSON.stringify({ name: value })
                });
                if (!response.ok) {
                    let msg = 'Failed to update series name';
                    try { const j = await response.json(); if (j && (j.error || typeof j === 'string')) msg = j.error || j; } catch {}
                    showMessage(msg, 'error');
                    // Revert the select to the last known good value.
                    const current = (manageSeriesState.record && manageSeriesState.record.series_name) || '';
                    select.value = current;
                    return;
                }
                const record = await response.json();
                manageSeriesState.record = record;
                populateManageSeriesNameOptions(record);
                renderManageSeriesUserAliases(record);
                showMessage(value ? `Series name set to "${value}"` : 'Series name set to automatic', 'success');
                if (typeof loadSeriesLibrary === 'function') {
                    loadSeriesLibrary(1, true);
                }
            } catch (err) {
                console.error('saveManageSeriesName failed', err);
                showMessage('Failed to update series name', 'error');
            }
        }

        function renderManageSeriesUserAliases(record) {
            const container = document.getElementById('manageSeriesUserAliases');
            const aliases = record.user_aliases || [];
            if (!aliases.length) {
                container.textContent = 'None';
                return;
            }
            container.innerHTML = aliases.map(a => `
                <span class="badge" style="display: inline-flex; align-items: center; padding: 4px 4px 4px 8px; margin: 2px; background: var(--bg-secondary); border-radius: 12px; font-size: 13px;">
                    ${escapeHtml(a)}
                    <button type="button" onclick="removeManageSeriesAlias('${escapeJs(a)}')" style="margin-left: 6px; background: transparent; border: none; color: var(--text-secondary); cursor: pointer; font-size: 14px;" title="Remove alias">×</button>
                </span>
            `).join('');
        }

        function addManageSeriesAlias() {
            const input = document.getElementById('manageSeriesNewAlias');
            const value = (input.value || '').trim();
            if (!value) return;
            if (!manageSeriesState.record) {
                manageSeriesState.record = { user_aliases: [], aliases: [] };
            }
            const current = manageSeriesState.record.user_aliases || [];
            if (!current.some(a => a.toLowerCase() === value.toLowerCase())) {
                current.push(value);
                manageSeriesState.record.user_aliases = current;
                renderManageSeriesUserAliases(manageSeriesState.record);
                // Surface the new alias as a selectable "Series Name" option so
                // it can immediately be picked as the sticky series name.
                populateManageSeriesNameOptions(manageSeriesState.record);
            }
            input.value = '';
        }

        function removeManageSeriesAlias(alias) {
            if (!manageSeriesState.record) return;
            const current = manageSeriesState.record.user_aliases || [];
            manageSeriesState.record.user_aliases = current.filter(a => a.toLowerCase() !== alias.toLowerCase());
            renderManageSeriesUserAliases(manageSeriesState.record);
            // Keep the "Series Name" dropdown in sync with the alias list.
            populateManageSeriesNameOptions(manageSeriesState.record);
        }

        async function saveManageSeriesNames() {
            const { seriesTitle, record } = manageSeriesState;
            if (!seriesTitle || !record) return;
            const payload = {
                aliases: record.user_aliases || []
            };
            try {
                const response = await fetch(apiUrl(`/api/metadata/series/${encodeURIComponent(seriesTitle)}/aliases`), {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    credentials: 'same-origin',
                    body: JSON.stringify(payload)
                });
                if (!response.ok) {
                    showMessage('Failed to save series names', 'error');
                    return;
                }
                showMessage('Series names saved', 'success');
                closeManageSeriesNamesModal();
                if (typeof loadSeriesLibrary === 'function') {
                    loadSeriesLibrary(1, true);
                }
                // Aliases may have collapsed previously-distinct folders into
                // a single combinable group, so refresh the dashboard count
                // and re-enable the "Combine Folders" button if needed.
                if (typeof loadLibraryHealth === 'function') {
                    loadLibraryHealth();
                }
            } catch (err) {
                console.error('saveManageSeriesNames failed', err);
                showMessage('Failed to save series names', 'error');
            }
        }

        async function refreshSeriesMetadata() {
            const { seriesTitle } = manageSeriesState;
            if (!seriesTitle) return;
            try {
                const response = await fetch(apiUrl(`/api/metadata/refresh/${encodeURIComponent(seriesTitle)}`), {
                    method: 'POST',
                    credentials: 'same-origin'
                });
                if (!response.ok) {
                    showMessage('Failed to refresh metadata', 'error');
                    return;
                }
                const record = await response.json();
                manageSeriesState.record = record;
                renderManageSeriesProviderAliases(record);
                renderManageSeriesUserAliases(record);
                renderManageSeriesImage(record);
                // Keep the "Series Name" options in sync with the refreshed
                // metadata, mirroring the manual-match path.
                populateManageSeriesNameOptions(record);
                const successStatuses = new Set(['success', 'manual_match']);
                const isSuccess = successStatuses.has(record.lookup_status);
                showMessage(isSuccess ? 'Metadata refreshed' : `Lookup status: ${record.lookup_status || 'unknown'}`, isSuccess ? 'success' : 'info');
            } catch (err) {
                console.error('refreshSeriesMetadata failed', err);
                showMessage('Failed to refresh metadata', 'error');
            }
        }

        // Manual trigger from the Manage Series Names modal: re-runs the
        // alias-aware folder-combine scan and opens the Combine Folders modal
        // so the user can immediately act on any newly-merged groups.
        async function checkCombinableFoldersFromManageSeries() {
            try {
                showMessage('Checking for combinable folders...', 'info');
                if (typeof loadLibraryHealth === 'function') {
                    // Refresh the dashboard counter so the badge reflects any
                    // new groups produced by alias edits.
                    loadLibraryHealth();
                }
                closeManageSeriesNamesModal();
                await openCombineFoldersModal();
            } catch (err) {
                console.error('checkCombinableFoldersFromManageSeries failed', err);
                showMessage('Failed to check for combinable folders', 'error');
            }
        }

        async function searchExternalSeries() {
            const query = (document.getElementById('manageSeriesSearchInput').value || '').trim();
            const container = document.getElementById('manageSeriesSearchResults');
            if (!query) {
                container.innerHTML = '<p style="color: var(--text-secondary);">Enter a query above to search.</p>';
                return;
            }
            container.innerHTML = '<p style="color: var(--text-secondary);">Searching...</p>';
            try {
                const response = await fetch(apiUrl(`/api/metadata/search?query=${encodeURIComponent(query)}&limit=10`), {
                    credentials: 'same-origin'
                });
                if (!response.ok) {
                    container.innerHTML = '<p style="color: var(--text-error);">Search failed.</p>';
                    return;
                }
                const data = await response.json();
                const results = data.results || [];
                if (!results.length) {
                    container.innerHTML = '<p style="color: var(--text-secondary);">No matches found. Check that external metadata providers are enabled in Settings.</p>';
                    return;
                }
                container.innerHTML = results.map((r, idx) => {
                    // Build a lookup of alias -> language from the provider's
                    // localized titles so each alias can be rendered with a
                    // small language badge ("en", "ja", "ko", ...). Without
                    // this the manual-match list shows bare strings and the
                    // user can't tell which alias is English.
                    const langByTitle = new Map();
                    (r.localized_titles || []).forEach(lt => {
                        if (lt && lt.title && lt.language) {
                            langByTitle.set(lt.title.toLowerCase(), lt.language);
                        }
                    });
                    const aliasList = r.aliases || [];
                    const aliases = aliasList.length
                        ? aliasList.map(a => {
                            const lang = langByTitle.get(a.toLowerCase());
                            const langBadge = lang
                                ? ` <span style="font-size: 11px; padding: 1px 6px; margin-left: 4px; background: var(--bg-primary); border: 1px solid var(--border-primary); border-radius: 8px; color: var(--text-secondary);">${escapeHtml(lang)}</span>`
                                : '';
                            return `<span style="display: inline-block; margin-right: 4px;">${escapeHtml(a)}${langBadge}</span>`;
                        }).join(', ')
                        : '<em>none</em>';
                    // Show the canonical title's language too so the user can
                    // see that (for example) the chosen canonical is English.
                    const canonicalLang = langByTitle.get((r.canonical_title || '').toLowerCase());
                    const canonicalLangBadge = canonicalLang
                        ? ` <span style="font-size: 11px; padding: 1px 6px; margin-left: 4px; background: var(--bg-primary); border: 1px solid var(--border-primary); border-radius: 8px; color: var(--text-secondary);">${escapeHtml(canonicalLang)}</span>`
                        : '';
                    // Server returns match_score on [0,100]. Render a colour
                    // hint so the user can see at a glance which candidate is
                    // the most-likely match when the automatic pick was wrong.
                    const score = typeof r.match_score === 'number' ? r.match_score : null;
                    let scoreBadge = '';
                    if (score !== null) {
                        const tone = score >= 90 ? 'var(--accent-success, #1f9d55)'
                                   : score >= 60 ? 'var(--accent-warning, #c69026)'
                                   : 'var(--text-secondary)';
                        scoreBadge = `<span title="Confidence that this is the right match" style="font-size: 12px; padding: 2px 8px; border-radius: 10px; background: var(--bg-secondary); color: ${tone}; border: 1px solid ${tone};">${score.toFixed(1)}% match</span>`;
                    }
                    return `
                        <div style="border: 1px solid var(--border-primary); border-radius: 5px; padding: 10px; margin-bottom: 8px;">
                            <div style="display: flex; justify-content: space-between; align-items: center; gap: 8px; flex-wrap: wrap;">
                                <strong>${escapeHtml(r.canonical_title || '')}${canonicalLangBadge}</strong>
                                <div style="display: flex; gap: 6px; align-items: center;">
                                    ${scoreBadge}
                                    <span style="font-size: 12px; color: var(--text-secondary);">${escapeHtml(r.source || '')}</span>
                                </div>
                            </div>
                            <div style="font-size: 13px; color: var(--text-secondary); margin-top: 4px;">Aliases: ${aliases}</div>
                            <div style="display: flex; gap: 6px; margin-top: 8px; flex-wrap: wrap;">
                                <button class="btn btn-small btn-primary" type="button" onclick="applySearchResultAsMatch(${idx})" title="Mark this candidate as the correct match for the series">✅ Use This Match</button>
                                <button class="btn btn-small" type="button" onclick="adoptSearchResult(${idx}, 'canonical')">Adopt as Canonical</button>
                                <button class="btn btn-small" type="button" onclick="adoptSearchResult(${idx}, 'aliases')">Add Aliases</button>
                            </div>
                        </div>
                    `;
                }).join('');
                window.__manageSeriesSearchResults = results;
            } catch (err) {
                console.error('searchExternalSeries failed', err);
                container.innerHTML = '<p style="color: var(--text-error);">Search failed.</p>';
            }
        }

        function adoptSearchResult(index, mode) {
            const results = window.__manageSeriesSearchResults || [];
            const result = results[index];
            if (!result) return;
            if (!manageSeriesState.record) {
                manageSeriesState.record = { user_aliases: [], aliases: [] };
            }
            const current = manageSeriesState.record.user_aliases || [];
            const addIfNew = (value) => {
                if (value && !current.some(a => a.toLowerCase() === value.toLowerCase())) {
                    current.push(value);
                }
            };
            if (mode === 'canonical') {
                // Keep the previous canonical (the series being managed) as a
                // user alias, register the chosen title, then set it as the
                // sticky series name.
                addIfNew(manageSeriesState.seriesTitle);
                if (result.canonical_title) {
                    addIfNew(result.canonical_title);
                }
            }
            (result.aliases || []).forEach(addIfNew);
            if (mode === 'aliases' && result.canonical_title) {
                addIfNew(result.canonical_title);
            }
            manageSeriesState.record.user_aliases = current;
            renderManageSeriesUserAliases(manageSeriesState.record);
            populateManageSeriesNameOptions(manageSeriesState.record);
            if (mode === 'canonical' && result.canonical_title) {
                const select = document.getElementById('manageSeriesName');
                if (select) {
                    if (![...select.options].some(o => o.value === result.canonical_title)) {
                        const opt = document.createElement('option');
                        opt.value = result.canonical_title;
                        opt.textContent = result.canonical_title;
                        select.appendChild(opt);
                    }
                    select.value = result.canonical_title;
                    saveManageSeriesName();
                }
            }
        }

        // Mark one of the candidates returned by /search as the *correct*
        // external match for the series. Used when the automatic best-match
        // chose the wrong candidate. Server-side this overwrites the cached
        // provider aliases / source / lookup status with the chosen ones
        // (preserving any user canonical-title override and user aliases).
        async function applySearchResultAsMatch(index) {
            const { seriesTitle } = manageSeriesState;
            if (!seriesTitle) return;
            const results = window.__manageSeriesSearchResults || [];
            const result = results[index];
            if (!result || !result.canonical_title) return;
            if (!confirm(`Mark "${result.canonical_title}" (${result.source || 'unknown source'}) as the correct match for "${seriesTitle}"? This replaces the cached provider metadata for this series.`)) {
                return;
            }
            try {
                const response = await fetch(apiUrl(`/api/metadata/series/${encodeURIComponent(seriesTitle)}/apply-match`), {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    credentials: 'same-origin',
                    body: JSON.stringify({
                        canonicalTitle: result.canonical_title,
                        aliases: result.aliases || [],
                        source: result.source || null,
                        imageUrl: result.image_url || null,
                        thumbnailUrl: result.thumbnail_url || null,
                        // Forward the provider's per-title language tags so
                        // the cached record preserves them and the per-series
                        // preferred-language resolver can later swap the
                        // display title to the user's chosen language.
                        localizedTitles: (result.localized_titles || [])
                            .filter(lt => lt && lt.title)
                            .map(lt => ({ title: lt.title, language: lt.language || null }))
                    })
                });
                if (!response.ok) {
                    let msg = 'Failed to apply match';
                    try { const j = await response.json(); if (j && (j.error || typeof j === 'string')) msg = j.error || j; } catch {}
                    showMessage(msg, 'error');
                    return;
                }
                const record = await response.json();
                manageSeriesState.record = record;
                renderManageSeriesProviderAliases(record);
                renderManageSeriesUserAliases(record);
                renderManageSeriesImage(record);
                // Refresh the "Series Name" options so the newly-matched
                // canonical title / localized titles / provider aliases are
                // immediately selectable — matching the dialog-open behavior so
                // a manually-picked match behaves identically to an automatic one.
                populateManageSeriesNameOptions(record);
                showMessage('Match applied', 'success');
                if (typeof loadSeriesLibrary === 'function') {
                    loadSeriesLibrary(1, true);
                }
            } catch (err) {
                console.error('applySearchResultAsMatch failed', err);
                showMessage('Failed to apply match', 'error');
            }
        }

        // Wipe the cached external metadata for the current series (provider
        // aliases, source, lookup status, and any provider-downloaded image).
        // User aliases and user-uploaded images are preserved.
        async function clearSeriesExternalMetadata() {
            const { seriesTitle } = manageSeriesState;
            if (!seriesTitle) return;
            if (!confirm(`Clear cached external metadata for "${seriesTitle}"? User aliases and a user-uploaded image will be kept.`)) {
                return;
            }
            try {
                const response = await fetch(apiUrl(`/api/metadata/series/${encodeURIComponent(seriesTitle)}/external`), {
                    method: 'DELETE',
                    credentials: 'same-origin'
                });
                if (response.status === 404) {
                    showMessage('Nothing to clear — no external metadata cached for this series.', 'info');
                    return;
                }
                if (!response.ok) {
                    showMessage('Failed to clear external metadata', 'error');
                    return;
                }
                const record = await response.json();
                manageSeriesState.record = record;
                renderManageSeriesProviderAliases(record);
                renderManageSeriesUserAliases(record);
                renderManageSeriesImage(record);
                // Cleared external metadata drops provider canonical/aliases, so
                // refresh the "Series Name" options to drop stale choices.
                populateManageSeriesNameOptions(record);
                showMessage('External metadata cleared', 'success');
                if (typeof loadSeriesLibrary === 'function') {
                    loadSeriesLibrary(1, true);
                }
            } catch (err) {
                console.error('clearSeriesExternalMetadata failed', err);
                showMessage('Failed to clear external metadata', 'error');
            }
        }
