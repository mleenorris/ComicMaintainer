        // BASE_PATH and apiUrl are defined in an inline script in the main HTML file
        // (before this external JS is loaded) to support Flask template variable injection.
        // They are available globally when this script executes.
        
        // Authentication check - redirect to login if no token
        (function checkAuth() {
            const token = localStorage.getItem('jwt_token');
            if (!token) {
                window.location.href = '/login.html';
                return;
            }
        })();
        
        // Helper function to get auth headers
        function getAuthHeaders() {
            const token = localStorage.getItem('jwt_token');
            const headers = {
                'Content-Type': 'application/json'
            };
            if (token) {
                headers['Authorization'] = `Bearer ${token}`;
            }
            return headers;
        }
        
        // Helper function to handle auth errors
        function handleAuthError(response) {
            if (response.status === 401) {
                // Token expired or invalid, redirect to login
                localStorage.removeItem('jwt_token');
                localStorage.removeItem('username');
                window.location.href = '/login.html';
                return true;
            }
            return false;
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
                localStorage.removeItem('jwt_token');
                localStorage.removeItem('username');
                window.location.href = '/login.html';
            }
        }
        
        // Constants
        const DEFAULT_PER_PAGE = 100; // Default number of items per page
        
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
        let searchDebounceTimer = null;
        let historyCurrentPage = 1;
        let historyPerPage = 50;
        let historyTotal = 0;
        
        // Server-Sent Events connection for real-time updates
        let eventSource = null;
        let eventSourceReconnectTimer = null;
        const EVENT_SOURCE_RECONNECT_DELAY = 5000; // 5 seconds
        
        // Initialize SSE connection
        function initEventSource() {
            // Close existing connection if any
            if (eventSource) {
                eventSource.close();
            }
            
            try {
                eventSource = new EventSource(apiUrl('/api/events/stream'));
                
                eventSource.onopen = () => {
                    console.log('SSE: Connected to event stream');
                    
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
                
                eventSource.onerror = (error) => {
                    console.warn('SSE: Connection error, will retry in 5s', error);
                    eventSource.close();
                    
                    // Auto-reconnect after delay
                    if (eventSourceReconnectTimer) {
                        clearTimeout(eventSourceReconnectTimer);
                    }
                    eventSourceReconnectTimer = setTimeout(initEventSource, EVENT_SOURCE_RECONNECT_DELAY);
                };
            } catch (error) {
                console.error('SSE: Failed to initialize EventSource:', error);
                // Fallback to polling if SSE is not supported
                console.log('SSE: Falling back to polling mechanisms');
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
            loadFiles(currentPage, false);
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
                        await loadFiles(currentPage, true);
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
        
        // Update watcher from settings modal
        async function updateWatcherFromSettings() {
            const enabled = document.getElementById('watcherToggleCheckbox').checked;
            
            try {
                const response = await fetch(apiUrl('/api/settings/watcher-enabled'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ enabled: enabled })
                });
                
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                const result = await response.json();
                
                if (result.success) {
                    const statusText = result.enabled ? 'enabled' : 'disabled';
                    showMessage(`Watcher ${statusText} successfully!`, 'success');
                } else {
                    showMessage(result.error || 'Failed to update watcher', 'error');
                    // Revert checkbox on error
                    document.getElementById('watcherToggleCheckbox').checked = !enabled;
                }
            } catch (error) {
                showMessage('Failed to update watcher: ' + error.message, 'error');
                // Revert checkbox on error
                document.getElementById('watcherToggleCheckbox').checked = !enabled;
            }
        }
        
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
                    })
                    .catch((error) => {
                        console.log('PWA: Service Worker registration failed:', error);
                    });
            });
        }
        
        // Listen for the beforeinstallprompt event
        window.addEventListener('beforeinstallprompt', (e) => {
            console.log('PWA: beforeinstallprompt event fired');
            // Don't prevent the default behavior - allow native Android install prompt
            // Just stash the event so we can also provide a custom install button
            deferredPrompt = e;
            // Show the install button
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
        
        document.addEventListener('DOMContentLoaded', async function() {
            // Initialize non-async operations immediately
            initTheme();
            
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
            
            // Start loading files immediately without waiting for preferences or job check
            // The file list will use default values (perPage=DEFAULT_PER_PAGE) and update when preferences arrive
            loadFiles();
            
            // Fetch initial watcher status in parallel
            updateWatcherStatus();
            
            // Apply preferences when they arrive (don't block file loading)
            prefsPromise.then(prefs => {
                const oldPerPage = perPage;
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
                
                // Reload files if perPage changed from default
                if (perPage !== oldPerPage && perPage !== DEFAULT_PER_PAGE) {
                    loadFiles(1);
                }
            });
            
            // Job check runs in parallel - no need to await
            // The modal will appear immediately if there's an active job
        });
        
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
            } catch (error) {
                showMessage('Failed to load files: ' + error.message, 'error');
            }
        }
        
        function updatePagination() {
            const paginationDiv = document.getElementById('pagination');
            const pageInfo = document.getElementById('pageInfo');
            const prevBtn = document.getElementById('prevBtn');
            const nextBtn = document.getElementById('nextBtn');
            
            if (totalPages > 1 || totalFiles > 0) {
                paginationDiv.style.display = 'flex';
                let pageText = `Page ${currentPage} of ${totalPages} (${totalFiles} file${totalFiles !== 1 ? 's' : ''}`;
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
            
            // Show or hide buttons based on whether there are unmarked files
            const hasUnmarkedFiles = unmarkedCount > 0;
            const displayStyle = hasUnmarkedFiles ? '' : 'none';
            
            if (processUnmarkedBtn) processUnmarkedBtn.style.display = displayStyle;
            if (renameUnmarkedBtn) renameUnmarkedBtn.style.display = displayStyle;
            if (normalizeUnmarkedBtn) normalizeUnmarkedBtn.style.display = displayStyle;
            if (filterUnmarkedBtn) filterUnmarkedBtn.style.display = displayStyle;
        }
        
        async function changePerPage() {
            const perPageSelect = document.getElementById('perPageSelect');
            perPage = parseInt(perPageSelect.value);
            
            // Save to server
            await setPreferences({ perPage: perPage });
            
            // Reload files from page 1 with new per-page value
            loadFiles(1);
        }
        
        function nextPage() {
            if (currentPage < totalPages) {
                loadFiles(currentPage + 1);
            }
        }
        
        function previousPage() {
            if (currentPage > 1) {
                loadFiles(currentPage - 1);
            }
        }
        
        function filterFiles() {
            searchQuery = document.getElementById('headerSearchInput').value;
            // Reload from page 1 with new search query
            loadFiles(1);
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
                'normalized': '📋 Normalized'
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
            
            // Reload from page 1 with new filter
            loadFiles(1);
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
            loadFiles(1);
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
                                    ${filenameHtml}
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
            
            if (count === 0) {
                info.textContent = 'No files selected';
                batchBtn.disabled = true;
                if (deleteSelectedBtn) deleteSelectedBtn.disabled = true;
                if (processSelectedItem) processSelectedItem.disabled = true;
                if (renameSelectedItem) renameSelectedItem.disabled = true;
                if (normalizeSelectedItem) normalizeSelectedItem.disabled = true;
            } else {
                info.textContent = `${count} file${count > 1 ? 's' : ''} selected`;
                batchBtn.disabled = false;
                if (deleteSelectedBtn) deleteSelectedBtn.disabled = false;
                if (processSelectedItem) processSelectedItem.disabled = false;
                if (renameSelectedItem) renameSelectedItem.disabled = false;
                if (normalizeSelectedItem) normalizeSelectedItem.disabled = false;
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
            if (file) {
                document.getElementById('fileInfoSize').textContent = formatFileSize(file.size);
                document.getElementById('fileInfoProcessed').textContent = file.processed ? '✅ Yes (Renamed & Normalized)' : '⚠️ No';
                document.getElementById('fileInfoRenamed').textContent = file.renamed ? '🔵 Yes' : 'No';
                document.getElementById('fileInfoNormalized').textContent = file.normalized ? '🔴 Yes' : 'No';
                document.getElementById('fileInfoDuplicate').textContent = file.duplicate ? '🔁 Yes' : 'No';
            }
            
            document.getElementById('fileInfoModal').classList.add('active');
        }
        
        function closeFileInfoModal() {
            document.getElementById('fileInfoModal').classList.remove('active');
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
        
        function closeModal() {
            document.getElementById('tagModal').classList.remove('active');
            currentEditFile = null;
        }
        
        async function saveTags() {
            if (!currentEditFile) return;
            
            const form = document.getElementById('tagForm');
            const formData = new FormData(form);
            const tags = {};
            
            for (let [key, value] of formData.entries()) {
                tags[key] = value;
            }
            
            try {
                const encodedPath = encodeFilePathForUrl(currentEditFile);
                const response = await fetch(apiUrl(`/api/files/${encodedPath}/tags`), {
                    method: 'PUT',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify(tags)
                });
                
                if (!response.ok) {
                    const errorText = await response.text();
                    throw new Error(errorText || `HTTP error! status: ${response.status}`);
                }
                
                showMessage('Tags updated successfully!', 'success');
                closeModal();
            } catch (error) {
                showMessage('Failed to save tags: ' + error.message, 'error');
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
            const tags = {};
            
            // Only include non-empty fields
            for (let [key, value] of formData.entries()) {
                if (value.trim()) {
                    tags[key] = value;
                }
            }
            
            if (Object.keys(tags).length === 0) {
                showMessage('Please enter at least one tag to update', 'error');
                return;
            }
            
            closeBatchModal();
            showProgressModal('Updating Tags...');
            
            const files = Array.from(selectedFiles);
            let successCount = 0;
            let errorCount = 0;
            
            try {
                const response = await fetch(apiUrl('/api/files/tags?stream=true'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({
                        files: files,
                        tags: tags
                    })
                });
                
                const reader = response.body.getReader();
                const decoder = new TextDecoder();
                
                while (true) {
                    const { done, value } = await reader.read();
                    if (done) break;
                    
                    const chunk = decoder.decode(value);
                    const lines = chunk.split('\n');
                    
                    for (const line of lines) {
                        if (line.startsWith('data: ')) {
                            const data = JSON.parse(line.substring(6));
                            
                            if (data.done) {
                                completeProgress();
                                showMessage(`Updated ${successCount} of ${files.length} files successfully!`, 'success');
                            } else {
                                if (data.success) {
                                    successCount++;
                                } else {
                                    errorCount++;
                                }
                                updateProgress(data.current, data.total, successCount, errorCount);
                                addProgressDetail(data.file, data.success, data.error);
                            }
                        }
                    }
                }
            } catch (error) {
                showMessage('Failed to batch update: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function processAllFiles() {
            if (!confirm('This will process all files in the watched directory. Continue?')) {
                return;
            }
            
            showProgressModal('Starting processing...');
            
            try {
                console.log('[BATCH] Starting process all files request...');
                // Start the job
                const response = await fetch(apiUrl('/api/jobs/process-all'), {
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
            
            showProgressModal('Starting async processing...');
            
            try {
                console.log('[BATCH] Starting process all files request...');
                // Start the job
                const response = await fetch(apiUrl('/api/jobs/process-all'), {
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
                        Files: files
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
                    await loadFiles(1, true);
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
            
            showProgressModal('Starting rename...');
            
            try {
                console.log('[BATCH] Starting rename all files request...');
                // Start the job
                const response = await fetch(apiUrl('/api/jobs/rename-all'), {
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
            
            showProgressModal('Starting normalize...');
            
            try {
                console.log('[BATCH] Starting normalize all files request...');
                // Start the job
                const response = await fetch(apiUrl('/api/jobs/normalize-all'), {
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
                    body: JSON.stringify({ Files: files })
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
                    body: JSON.stringify({ Files: files })
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
                    body: JSON.stringify({ Files: files })
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
                    body: JSON.stringify({ Files: [filepath] })
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
            
            showProgressModal('Starting rename...');
            
            try {
                console.log('[SINGLE FILE] Starting rename file request...');
                // Start the rename job
                const encodedPath = encodeFilePathForUrl(filepath);
                const response = await fetch(apiUrl(`/api/files/${encodedPath}/rename`), {
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
                    body: JSON.stringify({ Files: [filepath] })
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
                await loadFiles(currentPage, true);
            } catch (error) {
                showMessage('Failed to delete file: ' + error.message, 'error');
            }
        }
        
        function refreshFiles() {
            showMessage('Refreshing file list...', 'info');
            loadFiles(currentPage, true);
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
                // Load filename format
                const formatResponse = await fetch(apiUrl('/api/settings/filename-format'), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(formatResponse)) return;
                if (!formatResponse.ok) {
                    throw new Error(`HTTP error! status: ${formatResponse.status}`);
                }
                const formatData = await formatResponse.json();
                
                document.getElementById('filenameFormat').value = formatData.format || '';
                document.getElementById('currentFormat').textContent = formatData.format || formatData.default;
                
                // Load current theme
                const currentTheme = document.documentElement.getAttribute('data-theme') || 'light';
                document.getElementById('themeSelect').value = currentTheme;
                
                // Load watcher status
                const watcherResponse = await fetch(apiUrl('/api/settings/watcher-enabled'), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(watcherResponse)) return;
                if (!watcherResponse.ok) {
                    throw new Error(`HTTP error! status: ${watcherResponse.status}`);
                }
                const watcherData = await watcherResponse.json();
                document.getElementById('watcherToggleCheckbox').checked = watcherData.enabled;
                
                // Load watcher enable rename status
                const watcherRenameResponse = await fetch(apiUrl('/api/settings/watcher-enable-rename'), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(watcherRenameResponse)) return;
                if (!watcherRenameResponse.ok) {
                    throw new Error(`HTTP error! status: ${watcherRenameResponse.status}`);
                }
                const watcherRenameData = await watcherRenameResponse.json();
                document.getElementById('watcherEnableRenameCheckbox').checked = watcherRenameData.enabled;
                
                // Load watcher enable normalize status
                const watcherNormalizeResponse = await fetch(apiUrl('/api/settings/watcher-enable-normalize'), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(watcherNormalizeResponse)) return;
                if (!watcherNormalizeResponse.ok) {
                    throw new Error(`HTTP error! status: ${watcherNormalizeResponse.status}`);
                }
                const watcherNormalizeData = await watcherNormalizeResponse.json();
                document.getElementById('watcherEnableNormalizeCheckbox').checked = watcherNormalizeData.enabled;
                
                // Load log max size
                const logResponse = await fetch(apiUrl('/api/settings/log-max-bytes'), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(logResponse)) return;
                if (!logResponse.ok) {
                    throw new Error(`HTTP error! status: ${logResponse.status}`);
                }
                const logData = await logResponse.json();
                document.getElementById('logMaxSize').value = Math.round(logData.maxMB);
                
                // Load issue number padding
                const paddingResponse = await fetch(apiUrl('/api/settings/issue-number-padding'), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(paddingResponse)) return;
                if (!paddingResponse.ok) {
                    throw new Error(`HTTP error! status: ${paddingResponse.status}`);
                }
                const paddingData = await paddingResponse.json();
                document.getElementById('issueNumberPadding').value = paddingData.padding;
                
                // Load GitHub token (masked)
                const tokenResponse = await fetch(apiUrl('/api/settings/github-token'), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(tokenResponse)) return;
                if (!tokenResponse.ok) {
                    throw new Error(`HTTP error! status: ${tokenResponse.status}`);
                }
                const tokenData = await tokenResponse.json();
                // Show placeholder if token exists, otherwise empty
                document.getElementById('githubToken').placeholder = tokenData.has_token ? tokenData.token : 'ghp_...';
                document.getElementById('githubToken').value = ''; // Don't populate actual value for security
                
                // Load GitHub repository
                const repoResponse = await fetch(apiUrl('/api/settings/github-repository'), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(repoResponse)) return;
                if (!repoResponse.ok) {
                    throw new Error(`HTTP error! status: ${repoResponse.status}`);
                }
                const repoData = await repoResponse.json();
                document.getElementById('githubRepository').value = repoData.repository;
                
                // Load GitHub issue assignee
                const assigneeResponse = await fetch(apiUrl('/api/settings/github-issue-assignee'), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(assigneeResponse)) return;
                if (!assigneeResponse.ok) {
                    throw new Error(`HTTP error! status: ${assigneeResponse.status}`);
                }
                const assigneeData = await assigneeResponse.json();
                document.getElementById('githubIssueAssignee').value = assigneeData.assignee;
                
                document.getElementById('settingsModal').classList.add('active');
            } catch (error) {
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
            await loadLogs();
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
            
            let html = `
                <div style="border: 1px solid var(--border-secondary); border-radius: 8px; padding: 15px; background: var(--bg-secondary);">
                    <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 12px; border-bottom: 1px solid var(--border-primary); padding-bottom: 10px;">
                        <div>
                            <strong style="font-size: 15px; color: var(--text-primary);">${escapeHtml(filepath)}</strong>
                            <div style="font-size: 12px; color: var(--text-muted); margin-top: 4px;">${timestamp}</div>
                        </div>
                        <span style="background: var(--bg-hover); padding: 4px 12px; border-radius: 4px; font-size: 12px; color: var(--text-secondary);">${item.operation_type}</span>
                    </div>
                    <div style="display: grid; grid-template-columns: 1fr 1fr; gap: 10px; font-size: 13px;">
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
        
        function showProgressModal(title) {
            const modal = document.getElementById('progressModal');
            const indicator = document.getElementById('progressIndicator');
            document.getElementById('progressTitle').textContent = title;
            document.getElementById('progressBarFill').style.width = '0%';
            document.getElementById('progressText').textContent = '0 / 0 files';
            document.getElementById('progressPercent').textContent = '0%';
            document.getElementById('progressDetails').innerHTML = '';
            document.getElementById('progressCloseBtn').style.display = 'none';
            document.getElementById('progressCancelBtn').style.display = 'inline-block';  // Show cancel button
            
            // Check if the modal was previously minimized
            const wasMinimized = localStorage.getItem('progressModalMinimized') === 'true';
            
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
        
        function addProgressDetail(filename, success, error = null) {
            const details = document.getElementById('progressDetails');
            const entry = document.createElement('div');
            entry.style.marginBottom = '5px';
            
            if (success) {
                entry.innerHTML = `✅ ${filename}`;
                entry.style.color = '#2ecc71';
            } else {
                entry.innerHTML = `❌ ${filename}${error ? ': ' + error : ''}`;
                entry.style.color = '#e74c3c';
            }
            
            details.appendChild(entry);
            details.scrollTop = details.scrollHeight;
        }
        
        function completeProgress() {
            document.getElementById('progressCloseBtn').style.display = 'block';
            document.getElementById('progressCancelBtn').style.display = 'none';  // Hide cancel button when complete
            
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
            localStorage.removeItem('progressModalMinimized');
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
            localStorage.setItem('progressModalMinimized', 'true');
        }
        
        function restoreProgressModal() {
            const modal = document.getElementById('progressModal');
            const indicator = document.getElementById('progressIndicator');
            
            // Show the modal
            modal.classList.add('active');
            
            // Hide the indicator
            indicator.style.display = 'none';
            
            // Clear minimized state when modal is restored
            localStorage.removeItem('progressModalMinimized');
        }
        
        async function loadLogs() {
            const logsContent = document.getElementById('logsContent');
            const logsLoadingIndicator = document.getElementById('logsLoadingIndicator');
            const logStats = document.getElementById('logStats');
            const lines = document.getElementById('logLines').value;
            const logType = document.getElementById('logType').value;
            
            try {
                logsLoadingIndicator.style.display = 'block';
                logsContent.textContent = '';
                logStats.textContent = '';
                
                const response = await fetch(apiUrl(`/api/logs?lines=${lines}&type=${logType}`), {
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
                    logStats.textContent = `Showing ${data.shown_lines} of ${data.total_lines} total lines`;
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
                
                if (!formatResult.success) {
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
                
                if (!logResult.success) {
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
                
                if (!paddingResult.success) {
                    showMessage(paddingResult.error || 'Failed to save issue number padding', 'error');
                    return;
                }
                
                // Save GitHub settings
                const githubToken = document.getElementById('githubToken').value.trim();
                const githubRepository = document.getElementById('githubRepository').value.trim();
                const githubIssueAssignee = document.getElementById('githubIssueAssignee').value.trim();
                
                // Only save token if it was entered (not empty)
                if (githubToken) {
                    const tokenResponse = await fetch(apiUrl('/api/settings/github-token'), {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json'
                        },
                        body: JSON.stringify({ token: githubToken })
                    });
                    
                    if (!tokenResponse.ok) {
                        throw new Error(`HTTP error! status: ${tokenResponse.status}`);
                    }
                    const tokenResult = await tokenResponse.json();
                    
                    if (!tokenResult.success) {
                        showMessage(tokenResult.error || 'Failed to save GitHub token', 'error');
                        return;
                    }
                }
                
                // Save GitHub repository (validate if not empty)
                if (githubRepository) {
                    const repoResponse = await fetch(apiUrl('/api/settings/github-repository'), {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json'
                        },
                        body: JSON.stringify({ repository: githubRepository })
                    });
                    
                    if (!repoResponse.ok) {
                        throw new Error(`HTTP error! status: ${repoResponse.status}`);
                    }
                    const repoResult = await repoResponse.json();
                    
                    if (!repoResult.success) {
                        showMessage(repoResult.error || 'Failed to save GitHub repository', 'error');
                        return;
                    }
                }
                
                // Save GitHub issue assignee (can be empty)
                const assigneeResponse = await fetch(apiUrl('/api/settings/github-issue-assignee'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({ assignee: githubIssueAssignee })
                });
                
                if (!assigneeResponse.ok) {
                    throw new Error(`HTTP error! status: ${assigneeResponse.status}`);
                }
                const assigneeResult = await assigneeResponse.json();
                
                if (!assigneeResult.success) {
                    showMessage(assigneeResult.error || 'Failed to save GitHub issue assignee', 'error');
                    return;
                }
                
                showMessage('Settings saved successfully! Log rotation will take effect on restart.', 'success');
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
            await loadFiles(1, true);
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
