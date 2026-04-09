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
        let allFoldersExpanded = true;
        let currentPage = 1;
        let totalPages = 1;
        let totalFiles = 0;
        let unmarkedCount = 0;
        let perPage = DEFAULT_PER_PAGE; // Will be loaded from server preferences
        let filterMode = 'all'; // 'all', 'marked', 'unmarked', 'duplicates'
        let libraryViewMode = 'files';
        let seriesLibrary = [];
        let currentSeriesDetailId = null;
        let searchDebounceTimer = null;
        let historyCurrentPage = 1;
        let historyPerPage = 50;
        let historyTotal = 0;
        let libraryHealthRefreshTimer = null;
        let libraryHealthRequestInFlight = false;
        let libraryHealthRefreshPending = false;
        const MOBILE_LIBRARY_VIEW_BREAKPOINT = 768; // Matches the existing mobile CSS breakpoint.
        const DEFAULT_MOBILE_LIBRARY_VIEW = 'files';
        let currentMobileLibraryView = DEFAULT_MOBILE_LIBRARY_VIEW;
        let progressResults = [];
        let progressResultLookup = new Set();
        let progressResultElements = new Map();
        let duplicateReviewFiles = [];
        let duplicateReviewIndex = 0;
        let protectedImageUrls = new Map();
        const MAX_PROTECTED_IMAGE_CACHE_ENTRIES = 150;
        
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
            
            // Refresh file list to show updated status
            loadActiveLibraryView(currentPage, false);
            scheduleLibraryHealthRefresh();
        }
        
        // Handle file list updated events
        function handleFileListUpdatedEvent(data) {
            console.log('SSE: File list updated');
            
            // Refresh file list to show new/removed files
            loadActiveLibraryView(currentPage, false);
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
                        // Clear selected files and refresh the file list
                        selectedFiles.clear();
                        await loadActiveLibraryView(currentPage, true);
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

                if (summary) {
                    summary.textContent = total > 0
                        ? `${processed.toLocaleString()} processed, ${unprocessed.toLocaleString()} still need attention, ${duplicates.toLocaleString()} duplicate${duplicates === 1 ? '' : 's'} ready for review, and ${combinableFolders.toLocaleString()} folder${combinableFolders === 1 ? '' : 's'} that could be combined by metadata.`
                        : 'No files have been indexed yet.';
                }

                const reviewDuplicatesBtn = document.getElementById('reviewDuplicatesBtn');
                if (reviewDuplicatesBtn) {
                    reviewDuplicatesBtn.disabled = duplicates === 0;
                }
            } catch (error) {
                console.error('Failed to load library health:', error);
                if (summary) {
                    summary.textContent = 'Unable to load library health right now.';
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
            const overviewButton = document.getElementById('mobileOverviewViewBtn');
            const filesButton = document.getElementById('mobileFilesViewBtn');

            if (!overviewButton || !filesButton) {
                return;
            }

            if (!overviewButton.dataset.bound) {
                overviewButton.addEventListener('click', () => setMobileLibraryView('overview'));
                overviewButton.dataset.bound = 'true';
            }

            if (!filesButton.dataset.bound) {
                filesButton.addEventListener('click', () => setMobileLibraryView('files'));
                filesButton.dataset.bound = 'true';
            }
        }

        function applyMobileLibraryView() {
            const toggle = document.getElementById('mobileLibraryViewToggle');
            const dashboard = document.getElementById('libraryHealthDashboard');
            const filesView = document.getElementById('libraryFilesView');
            const overviewButton = document.getElementById('mobileOverviewViewBtn');
            const filesButton = document.getElementById('mobileFilesViewBtn');

            if (!toggle || !dashboard || !filesView || !overviewButton || !filesButton) {
                console.warn('Mobile library view controls are missing from the page.', {
                    toggleMissing: !toggle,
                    dashboardMissing: !dashboard,
                    filesViewMissing: !filesView,
                    overviewButtonMissing: !overviewButton,
                    filesButtonMissing: !filesButton
                });
                return;
            }

            const isMobile = isMobileLibraryViewport();
            const showingOverview = currentMobileLibraryView === 'overview';

            toggle.hidden = !isMobile;
            dashboard.hidden = isMobile && !showingOverview;
            filesView.hidden = isMobile && showingOverview;

            overviewButton.classList.toggle('active', showingOverview);
            overviewButton.setAttribute('aria-pressed', showingOverview ? 'true' : 'false');

            filesButton.classList.toggle('active', !showingOverview);
            filesButton.setAttribute('aria-pressed', !showingOverview ? 'true' : 'false');
        }

        function setMobileLibraryView(view) {
            if (view !== 'overview' && view !== 'files') {
                return;
            }

            currentMobileLibraryView = view;
            applyMobileLibraryView();

            const status = document.getElementById('mobileLibraryViewStatus');
            if (status) {
                status.textContent = view === 'overview'
                    ? 'Overview view selected.'
                    : 'Files view selected.';
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
                        
                        // Listen for updates
                        registration.addEventListener('updatefound', () => {
                            const newWorker = registration.installing;
                            console.log('PWA: New service worker installing...');
                            
                            newWorker.addEventListener('statechange', () => {
                                if (newWorker.state === 'installed' && navigator.serviceWorker.controller) {
                                    // New service worker available, notify user
                                    console.log('PWA: New version available! Reloading page...');
                                    // Automatically reload to get the new version
                                    // This ensures users always get the latest version
                                    window.location.reload();
                                }
                            });
                        });
                    })
                    .catch((error) => {
                        console.log('PWA: Service Worker registration failed:', error);
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
                alert('App is already installed or installation is not available.');
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
                        'duplicates': '🔁 Duplicates'
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

                if (prefs.libraryViewMode === 'series') {
                    libraryViewMode = 'series';
                }

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
        
        async function loadActiveLibraryView(page = 1, refresh = false) {
            if (libraryViewMode === 'series') {
                return loadSeriesLibrary(page, refresh);
            }

            return loadFiles(page, refresh);
        }

        function updateLibraryViewButtons() {
            document.getElementById('filesViewModeBtn')?.classList.toggle('active', libraryViewMode === 'files');
            document.getElementById('seriesViewModeBtn')?.classList.toggle('active', libraryViewMode === 'series');
        }

        function updateLibraryViewLayout() {
            const controlsWrapper = document.querySelector('#libraryFilesView .controls-wrapper');
            const pagination = document.getElementById('pagination');
            const isFileMode = libraryViewMode === 'files';

            if (controlsWrapper) {
                controlsWrapper.style.display = isFileMode ? '' : 'none';
            }

            if (!isFileMode && currentSeriesDetailId && pagination) {
                pagination.style.display = 'none';
            }
        }

        async function setLibraryViewMode(mode) {
            if (isMobileLibraryViewport()) {
                setMobileLibraryView('files');
            }

            if (libraryViewMode === mode && !(mode === 'series' && currentSeriesDetailId)) {
                return;
            }

            libraryViewMode = mode;
            currentSeriesDetailId = null;
            updateLibraryViewButtons();
            updateLibraryViewLayout();
            await setPreferences({ libraryViewMode: mode });
            await loadActiveLibraryView(1, true);
        }

        function promptForForceReprocess(actionDescription, statusDescription) {
            return confirm(`${actionDescription}\n\nClick OK to force files already marked as ${statusDescription}. Click Cancel to skip files already marked as ${statusDescription}.`);
        }

        async function loadSeriesLibrary(page = 1, refresh = false) {
            try {
                let url = apiUrl(`/api/files/series?page=${page}&per_page=${perPage}`);
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
                seriesLibrary = data.series || [];
                currentPage = data.page;
                totalPages = data.total_pages;
                totalFiles = data.total_series || 0;
                unmarkedCount = data.unmarked_count || 0;

                if (currentSeriesDetailId) {
                    renderSeriesDetail(currentSeriesDetailId);
                } else {
                    renderSeriesLibrary();
                }

                updatePagination();
                updateButtonVisibility();
                updateLibraryViewLayout();

                if (refresh) {
                    loadLibraryHealth();
                }
            } catch (error) {
                showMessage('Failed to load series: ' + error.message, 'error');
            }
        }

        async function loadFiles(page = 1, refresh = false) {
            try {
                let url = apiUrl(`/api/files?page=${page}&per_page=${perPage}`);
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
                
                files = data.files;
                currentPage = data.page;
                totalPages = data.total_pages;
                totalFiles = data.total_files;
                unmarkedCount = data.unmarked_count || 0;
                
                renderFileList();
                updatePagination();
                updateButtonVisibility();
                if (refresh) {
                    loadLibraryHealth();
                }
            } catch (error) {
                showMessage('Failed to load files: ' + error.message, 'error');
            }
        }
        
        function updatePagination() {
            const paginationDiv = document.getElementById('pagination');
            const pageInfo = document.getElementById('pageInfo');
            const prevBtn = document.getElementById('prevBtn');
            const nextBtn = document.getElementById('nextBtn');

            if (libraryViewMode === 'series' && currentSeriesDetailId) {
                paginationDiv.style.display = 'none';
                return;
            }
            
            if (totalPages > 1 || totalFiles > 0) {
                paginationDiv.style.display = 'flex';
                const itemLabel = libraryViewMode === 'series' ? 'series' : 'file';
                let pageText = `Page ${currentPage} of ${totalPages} (${totalFiles} ${itemLabel}${totalFiles !== 1 ? 's' : ''}`;
                if (searchQuery || filterMode !== 'all') {
                    pageText += ' matching';
                }
                pageText += ')';
                pageInfo.textContent = pageText;
                
                // Hide Previous and Next buttons when "All" option is selected
                if (perPage === -1) {
                    prevBtn.style.display = 'none';
                    nextBtn.style.display = 'none';
                } else {
                    prevBtn.style.display = '';
                    nextBtn.style.display = '';
                    prevBtn.disabled = currentPage <= 1;
                    nextBtn.disabled = currentPage >= totalPages;
                }
            } else {
                paginationDiv.style.display = 'none';
            }
        }
        
        function updateButtonVisibility() {
            // Get all unmarked-related buttons
            const processUnmarkedBtn = document.querySelector('button[onclick="processUnmarkedFiles()"]');
            const renameUnmarkedBtn = document.querySelector('button[onclick="renameUnmarkedFiles()"]');
            const normalizeUnmarkedBtn = document.querySelector('button[onclick="normalizeUnmarkedFiles()"]');
            const filterUnmarkedBtn = document.getElementById('filterUnmarked');
            const controlsWrapper = document.querySelector('#libraryFilesView .controls-wrapper');
            
            // Show or hide buttons based on whether there are unmarked files
            const hasUnmarkedFiles = unmarkedCount > 0;
            const displayStyle = hasUnmarkedFiles ? '' : 'none';
            
            if (processUnmarkedBtn) processUnmarkedBtn.style.display = displayStyle;
            if (renameUnmarkedBtn) renameUnmarkedBtn.style.display = displayStyle;
            if (normalizeUnmarkedBtn) normalizeUnmarkedBtn.style.display = displayStyle;
            if (filterUnmarkedBtn) filterUnmarkedBtn.style.display = displayStyle;
            if (controlsWrapper) controlsWrapper.style.display = libraryViewMode === 'files' ? '' : 'none';
        }
        
        async function changePerPage() {
            const perPageSelect = document.getElementById('perPageSelect');
            perPage = parseInt(perPageSelect.value);
            
            // Save to server
            await setPreferences({ perPage: perPage });
            
            // Reload files from page 1 with new per-page value
            loadActiveLibraryView(1);
        }
        
        function nextPage() {
            if (currentPage < totalPages) {
                loadActiveLibraryView(currentPage + 1);
            }
        }
        
        function previousPage() {
            if (currentPage > 1) {
                loadActiveLibraryView(currentPage - 1);
            }
        }
        
        function filterFiles() {
            searchQuery = document.getElementById('headerSearchInput').value;
            // Reload from page 1 with new search query
            currentSeriesDetailId = null;
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
                'unread': '📚 Unread'
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
            
            // Reload from page 1 with new filter
            currentSeriesDetailId = null;
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
            
            // Reload from page 1 with new sort order
            currentSeriesDetailId = null;
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
            return apiUrl(`/api/comicreader/page?filePath=${encodeURIComponent(filePath)}&page=1`);
        }

        async function hydrateProtectedImages(container = document) {
            const images = container.querySelectorAll('[data-protected-image]');
            const imageQueue = Array.from(images);
            const batchSize = 8;

            for (let index = 0; index < imageQueue.length; index += batchSize) {
                const batch = imageQueue.slice(index, index + batchSize);
                await Promise.all(batch.map(async image => {
                    const filePath = image.dataset.protectedImage;
                    if (!filePath) {
                        return;
                    }

                    if (protectedImageUrls.has(filePath)) {
                        image.src = protectedImageUrls.get(filePath);
                        return;
                    }

                    try {
                        const response = await fetch(getSeriesCoverUrl(filePath), {
                            headers: getAuthHeaders()
                        });
                        if (!response.ok) {
                            return;
                        }

                        const blob = await response.blob();
                        const objectUrl = URL.createObjectURL(blob);
                        if (protectedImageUrls.size >= MAX_PROTECTED_IMAGE_CACHE_ENTRIES) {
                            const oldestKey = protectedImageUrls.keys().next().value;
                            if (oldestKey) {
                                URL.revokeObjectURL(protectedImageUrls.get(oldestKey));
                                protectedImageUrls.delete(oldestKey);
                            }
                        }
                        protectedImageUrls.set(filePath, objectUrl);
                        image.src = objectUrl;
                    } catch (error) {
                        console.error('Failed to load protected image', error);
                    }
                }));
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

            fileList.innerHTML = `
                <div class="series-grid">
                    ${seriesLibrary.map(series => `
                        <button class="series-card" type="button" aria-expanded="${currentSeriesDetailId === series.id ? 'true' : 'false'}" aria-controls="seriesDetailPanel" aria-label="Open series ${escapeHtml(series.title)}" onclick="openSeriesDetail('${escapeJs(series.id)}')">
                            <img class="series-cover" data-protected-image="${escapeHtml(series.cover_file_path)}" alt="${escapeHtml(series.title)} cover" loading="lazy">
                            <div class="series-card-body">
                                <h3 class="series-title">${escapeHtml(series.title)}</h3>
                                <div class="series-meta">${series.issue_count} issue${series.issue_count === 1 ? '' : 's'} · ${formatFileSize(series.total_size)}</div>
                                ${series.aliases?.length ? `<div class="series-aliases">Aliases: ${escapeHtml(series.aliases.join(', '))}</div>` : ''}
                                ${series.metadata_source ? `<div class="series-meta">Source: ${escapeHtml(series.metadata_source)}</div>` : ''}
                            </div>
                        </button>
                    `).join('')}
                </div>
            `;

            hydrateProtectedImages(fileList);
        }

        function openSeriesDetail(seriesId) {
            currentSeriesDetailId = seriesId;
            renderSeriesDetail(seriesId);
        }

        function closeSeriesDetail() {
            currentSeriesDetailId = null;
            renderSeriesLibrary();
            updatePagination();
            updateLibraryViewLayout();
        }

        function renderSeriesDetail(seriesId) {
            const fileList = document.getElementById('fileList');
            const series = seriesLibrary.find(item => item.id === seriesId);
            if (!series) {
                closeSeriesDetail();
                return;
            }

            fileList.innerHTML = `
                <div class="series-detail" id="seriesDetailPanel">
                    <div class="series-detail-header">
                        <button type="button" class="btn btn-small series-detail-back" onclick="closeSeriesDetail()">← Back to Series</button>
                        <div class="series-detail-summary">
                            <img class="series-detail-cover" data-protected-image="${escapeHtml(series.cover_file_path)}" alt="${escapeHtml(series.title)} cover" loading="lazy">
                            <div class="series-detail-summary-body">
                                <h2>${escapeHtml(series.title)}</h2>
                                <div class="series-detail-meta">${series.issue_count} issue${series.issue_count === 1 ? '' : 's'} · ${formatFileSize(series.total_size)}</div>
                                ${series.aliases?.length ? `<div class="series-detail-meta">Aliases: ${escapeHtml(series.aliases.join(', '))}</div>` : ''}
                                ${series.metadata_source ? `<div class="series-detail-meta">Metadata source: ${escapeHtml(series.metadata_source)}</div>` : ''}
                            </div>
                        </div>
                    </div>
                    <div class="series-issues-grid">
                        ${series.issues.map(issue => `
                            <div class="series-issue-card">
                                <button type="button" class="series-issue-cover-button" aria-label="Read ${escapeHtml(issue.title || issue.file_name)}" onclick="readComic('${escapeJs(issue.file_path)}')">
                                    <img class="series-issue-cover" data-protected-image="${escapeHtml(issue.file_path)}" alt="${escapeHtml(issue.file_name)} cover" loading="lazy">
                                </button>
                                <div class="series-issue-body">
                                    <h3 class="series-issue-title">${escapeHtml(issue.title || issue.file_name)}</h3>
                                    <p class="series-issue-subtitle">Issue ${escapeHtml(issue.issue || 'Unknown')}${issue.year ? ` · ${issue.year}` : ''}</p>
                                    ${issue.volume ? `<p class="series-issue-subtitle">Volume ${escapeHtml(issue.volume)}</p>` : ''}
                                    <div class="series-detail-meta">${escapeHtml(issue.file_name)}</div>
                                    <div class="series-detail-meta">${formatFileSize(issue.size)} · ${formatModifiedDate(issue.modified)}</div>
                                </div>
                            </div>
                        `).join('')}
                    </div>
                </div>
            `;

            updateLibraryViewLayout();
            hydrateProtectedImages(fileList);
        }
        
        function renderFileList() {
            const fileList = document.getElementById('fileList');
            
            // Clean up selectedFiles to remove files that no longer exist
            // This must happen before the early return for empty file lists
            const currentFilePaths = new Set(files.map(f => f.relative_path));
            for (const filepath of selectedFiles) {
                if (!currentFilePaths.has(filepath)) {
                    selectedFiles.delete(filepath);
                }
            }
            
            if (files.length === 0) {
                // Check if we have search/filter active to show appropriate message
                if (searchQuery || filterMode !== 'all') {
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
                // Update UI state after clearing selections
                updateSelectInfo();
                updateSelectAllCheckbox();
                return;
            }
            
            // Group files by directory (filtering is now done on backend)
            const filesByDirectory = {};
            files.forEach(file => {
                // Skip files with missing relative_path (should not happen with proper API response)
                if (!file.relative_path) {
                    console.error('File object missing relative_path:', file);
                    return;
                }
                
                const dirPath = file.relative_path.includes('/') || file.relative_path.includes('\\') 
                    ? file.relative_path.substring(0, file.relative_path.lastIndexOf(file.relative_path.includes('/') ? '/' : '\\'))
                    : '';
                if (!filesByDirectory[dirPath]) {
                    filesByDirectory[dirPath] = [];
                }
                filesByDirectory[dirPath].push(file);
            });
            
            // Sort directories
            const sortedDirs = Object.keys(filesByDirectory).sort();
            
            let html = `
                <div class="file-list-header">
                    <input type="checkbox" id="selectAll" onchange="toggleSelectAll(this.checked)">
                    <button class="toggle-all-btn" onclick="toggleAllFolders()" id="toggleAllBtn" title="Expand/Collapse All">
                        ${allFoldersExpanded ? '▼' : '▶'}
                    </button>
                    <div>File</div>
                    <div>Size</div>
                    <div>Modified</div>
                    <div>Actions</div>
                </div>
            `;
            
            // Render files grouped by directory
            sortedDirs.forEach(dir => {
                const isCollapsed = collapsedDirectories.has(dir);
                const fileCount = filesByDirectory[dir].length;
                
                if (dir) {
                    const allSelected = filesByDirectory[dir].every(file => selectedFiles.has(file.relative_path));
                    const someSelected = filesByDirectory[dir].some(file => selectedFiles.has(file.relative_path));
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
                            <span class="directory-file-count">${fileCount} file${fileCount !== 1 ? 's' : ''}</span>
                            <!-- Empty spans fill grid columns 5-6 to maintain 6-column alignment with file-list-header -->
                            <!-- These are hidden in responsive views via CSS -->
                            <span></span>
                            <span></span>
                        </div>
                    `;
                }
                
                html += `<div class="directory-content ${isCollapsed ? 'collapsed' : ''}" data-dir="${escapeHtml(dir)}">`;
                
                filesByDirectory[dir].forEach(file => {
                    const isSelected = selectedFiles.has(file.relative_path);
                    const fileSize = formatFileSize(file.size);
                    const modifiedDate = formatModifiedDate(file.modified);
                    
                    // Determine read status indicator
                    let readIcon = '';
                    let readTitle = '';
                    if (file.read) {
                        readIcon = '👁️';
                        readTitle = 'Read';
                    }
                    
                    // Determine status icon and class based on processing state
                    // Priority: duplicate > fully processed (both) > renamed only > normalized only > unmarked
                    let statusIcon = '';
                    let statusTitle = '';
                    let statusClass = '';
                    
                    if (file.duplicate) {
                        statusIcon = '🔁';
                        statusTitle = 'Duplicate';
                        statusClass = 'status-duplicate';
                    } else if (file.renamed && file.normalized) {
                        // Both renamed AND normalized = processed
                        statusIcon = '✅';
                        statusTitle = 'Processed (Renamed & Normalized)';
                        statusClass = 'status-marked';
                    } else if (file.renamed && !file.normalized) {
                        // Renamed only
                        statusIcon = '🔵';
                        statusTitle = 'Renamed';
                        statusClass = 'status-renamed';
                    } else if (file.normalized && !file.renamed) {
                        // Normalized only
                        statusIcon = '🔴';
                        statusTitle = 'Normalized';
                        statusClass = 'status-normalized';
                    } else {
                        // Neither renamed nor normalized = unmarked
                        statusIcon = '⚠️';
                        statusTitle = 'Unmarked';
                        statusClass = 'status-unmarked';
                    }
                    
                    // Split filename for middle truncation
                    const filenameParts = truncateFilenameMiddle(file.name);
                    const filenameHtml = filenameParts.end 
                        ? `<span class="file-name-start">${escapeHtml(filenameParts.start)}</span><span class="file-name-end">${escapeHtml(filenameParts.end)}</span>`
                        : `<span class="file-name-content">${escapeHtml(filenameParts.start)}</span>`;
                    
                    html += `
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
                                        <div class="dropdown-divider"></div>
                                        <button class="dropdown-item" onclick="deleteSingleFile('${escapeJs(file.relative_path)}'); closeAllDropdowns();">
                                            🗑️ Delete
                                        </button>
                                    </div>
                                </div>
                            </div>
                        </div>
                    `;
                });
                
                html += `</div>`;
            });
            
            fileList.innerHTML = html;
            
            updateSelectInfo();
            updateSelectAllCheckbox();
            updateToggleAllButton();
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
            // Escape single quotes, double quotes, backslashes, and other special characters for JavaScript strings
            return text.replace(/\\/g, '\\\\')
                       .replace(/'/g, "\\'")
                       .replace(/"/g, '\\"')
                       .replace(/\n/g, '\\n')
                       .replace(/\r/g, '\\r')
                       .replace(/\t/g, '\\t');
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
            selectedFiles.clear();
            if (checked) {
                files.forEach(file => selectedFiles.add(file.relative_path));
            }
            renderFileList();
        }
        
        function toggleFileSelection(filepath, checked) {
            if (checked) {
                selectedFiles.add(filepath);
            } else {
                selectedFiles.delete(filepath);
            }
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
            
            if (count === 0) {
                info.textContent = 'No files selected';
                batchBtn.disabled = true;
                if (deleteSelectedBtn) deleteSelectedBtn.disabled = true;
                if (processSelectedItem) processSelectedItem.disabled = true;
                if (renameSelectedItem) renameSelectedItem.disabled = true;
                if (normalizeSelectedItem) normalizeSelectedItem.disabled = true;
                if (markSelectedReadItem) markSelectedReadItem.disabled = true;
                if (markSelectedUnreadItem) markSelectedUnreadItem.disabled = true;
            } else {
                info.textContent = `${count} file${count > 1 ? 's' : ''} selected`;
                batchBtn.disabled = false;
                if (deleteSelectedBtn) deleteSelectedBtn.disabled = false;
                if (processSelectedItem) processSelectedItem.disabled = false;
                if (renameSelectedItem) renameSelectedItem.disabled = false;
                if (normalizeSelectedItem) normalizeSelectedItem.disabled = false;
                if (markSelectedReadItem) markSelectedReadItem.disabled = false;
                if (markSelectedUnreadItem) markSelectedUnreadItem.disabled = false;
            }
        }
        
        function updateSelectAllCheckbox() {
            const selectAllCheckbox = document.getElementById('selectAll');
            if (!selectAllCheckbox) return;
            
            if (files.length === 0) {
                selectAllCheckbox.checked = false;
                selectAllCheckbox.indeterminate = false;
            } else {
                const allSelected = files.every(file => selectedFiles.has(file.relative_path));
                const someSelected = files.some(file => selectedFiles.has(file.relative_path));
                
                selectAllCheckbox.checked = allSelected;
                selectAllCheckbox.indeterminate = someSelected && !allSelected;
            }
        }
        
        function toggleDirectory(dir) {
            if (collapsedDirectories.has(dir)) {
                collapsedDirectories.delete(dir);
            } else {
                collapsedDirectories.add(dir);
            }
            updateToggleAllButton();
            renderFileList();
        }
        
        function toggleAllFolders() {
            if (allFoldersExpanded) {
                collapseAllFolders();
            } else {
                expandAllFolders();
            }
        }
        
        function expandAllFolders() {
            collapsedDirectories.clear();
            allFoldersExpanded = true;
            updateToggleAllButton();
            renderFileList();
        }
        
        function collapseAllFolders() {
            // Get all directories from files
            const allDirs = new Set();
            files.forEach(file => {
                const dirPath = file.relative_path.includes('/') || file.relative_path.includes('\\') 
                    ? file.relative_path.substring(0, file.relative_path.lastIndexOf(file.relative_path.includes('/') ? '/' : '\\'))
                    : '';
                if (dirPath) {
                    allDirs.add(dirPath);
                }
            });
            
            // Collapse all directories
            collapsedDirectories = new Set(allDirs);
            allFoldersExpanded = false;
            updateToggleAllButton();
            renderFileList();
        }
        
        function updateToggleAllButton() {
            // Count total directories
            const allDirs = new Set();
            files.forEach(file => {
                const dirPath = file.relative_path.includes('/') || file.relative_path.includes('\\') 
                    ? file.relative_path.substring(0, file.relative_path.lastIndexOf(file.relative_path.includes('/') ? '/' : '\\'))
                    : '';
                if (dirPath) {
                    allDirs.add(dirPath);
                }
            });
            
            // Update state based on collapsed directories
            if (collapsedDirectories.size === allDirs.size && allDirs.size > 0) {
                allFoldersExpanded = false;
            } else {
                allFoldersExpanded = true;
            }
        }
        
        function toggleDirectorySelection(dir, checked) {
            // Find all files in this directory
            const dirFiles = files.filter(file => {
                const fileDirPath = file.relative_path.includes('/') || file.relative_path.includes('\\') 
                    ? file.relative_path.substring(0, file.relative_path.lastIndexOf(file.relative_path.includes('/') ? '/' : '\\'))
                    : '';
                return fileDirPath === dir;
            });
            
            // Update selection
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
            
            // Find the file in the files array to get size info
            const file = files.find(f => f.relative_path === filepath);
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
            // Open comic reader in the same window
            window.location.href = `/reader.html?file=${encodeURIComponent(filepath)}`;
        }
        
        function closeModal() {
            document.getElementById('tagModal').classList.remove('active');
            currentEditFile = null;
        }
        
        async function saveTags() {
            if (!currentEditFile) return;
            
            const form = document.getElementById('tagForm');
            const formData = new FormData(form);
            const metadata = {};
            
            for (let [key, value] of formData.entries()) {
                // Capitalize first letter to match ComicMetadata property names
                const propertyName = key.charAt(0).toUpperCase() + key.slice(1);
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
                    body: JSON.stringify({ Files: [currentEditFile], Metadata: metadata })
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
                
                console.log(`[SINGLE FILE] Created job ${jobId} for file: ${currentEditFile}`);
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
            
            showProgressModal('Starting async normalizing...');
            
            try {
                console.log('[BATCH] Starting normalize unmarked files request...');
                // Start the job
                const response = await fetch(apiUrl('/api/jobs/normalize-unmarked'), {
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
                await loadActiveLibraryView(currentPage, true);
            } catch (error) {
                showMessage('Failed to delete file: ' + error.message, 'error');
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
                await loadActiveLibraryView(currentPage, true);
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
                await loadActiveLibraryView(currentPage, true);
            } catch (error) {
                showMessage('Failed to mark file as unread: ' + error.message, 'error');
            }
        }
        
        async function markAllFilesRead() {
            if (!confirm('Mark all files as read?')) {
                return;
            }
            
            try {
                showMessage('Marking all files as read...', 'info');
                
                const allFilePaths = files.map(f => f.relative_path);
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
                await loadActiveLibraryView(currentPage, true);
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
                
                const allFilePaths = files.map(f => f.relative_path);
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
                await loadActiveLibraryView(currentPage, true);
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
                await loadActiveLibraryView(currentPage, true);
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
                await loadActiveLibraryView(currentPage, true);
            } catch (error) {
                showMessage('Failed to mark files as unread: ' + error.message, 'error');
            }
        }
        
        function refreshFiles() {
            showMessage('Refreshing file list...', 'info');
            loadActiveLibraryView(currentPage, true);
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
                    showMessage('Database reset completed successfully! Reloading page...', 'success');
                    
                    // Reload the page after a short delay to see the success message
                    setTimeout(() => {
                        window.location.reload();
                    }, 2000);
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
                indicator.style.display = 'flex';
                const indicatorText = document.getElementById('progressIndicatorText');
                indicatorText.textContent = `⏳ ${title}`;
            } else {
                // Show the full modal
                modal.classList.add('active');
                indicator.style.display = 'none';
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
            document.getElementById('progressIndicator').style.display = 'none';
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
            const indicator = document.getElementById('progressIndicator');
            const indicatorText = document.getElementById('progressIndicatorText');
            
            // Hide the modal
            modal.classList.remove('active');
            
            // Show the indicator with current progress
            const percentText = document.getElementById('progressPercent').textContent;
            const progressText = document.getElementById('progressText').textContent;
            indicatorText.textContent = `⏳ ${progressText} (${percentText})`;
            indicator.style.display = 'flex';
            
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
            const indicator = document.getElementById('progressIndicator');
            
            // Show the modal
            modal.classList.add('active');
            
            // Hide the indicator
            indicator.style.display = 'none';
            
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
                        comicVineBaseUrl: comicVineBaseUrl || 'https://comicvine.gamespot.com/api'
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
            
            let successCount = 0;
            let failCount = 0;
            
            // Delete files one by one
            for (let i = 0; i < selectedFilesArray.length; i++) {
                const filepath = selectedFilesArray[i];
                updateProgress(i + 1, selectedFilesArray.length, successCount, failCount);
                
                try {
                    // Use RESTful endpoint: DELETE /api/files/{encodedFilePath}
                    const encodedPath = encodeFilePathForUrl(filepath);
                    const response = await fetch(apiUrl(`/api/files/${encodedPath}`), {
                        method: 'DELETE',
                        headers: getAuthHeaders()
                    });
                    
                    if (response.ok) {
                        successCount++;
                        addProgressDetail(filepath, true);
                    } else {
                        const errorText = await response.text();
                        failCount++;
                        addProgressDetail(filepath, false, errorText || 'Unknown error');
                    }
                } catch (error) {
                    failCount++;
                    addProgressDetail(filepath, false, error.message);
                }
            }
            
            completeProgress();
            
            if (failCount === 0) {
                showMessage(`Deleted ${successCount} file(s) successfully!`, 'success');
            } else {
                showMessage(`Deleted ${successCount} file(s), ${failCount} failed`, 'warning');
            }
            
            // Clear selected files and refresh file list
            selectedFiles.clear();
            await loadActiveLibraryView(1, true);
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
