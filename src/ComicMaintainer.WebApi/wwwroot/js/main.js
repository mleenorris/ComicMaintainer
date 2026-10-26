        // ComicMaintainer SPA — bootstrap. Loaded LAST, after every script in
        // /js/app/ (see index.html), so everything here may rely on all app
        // functions being defined.
        //
        // Code that runs at load time (listener registrations, readyState
        // checks) lives here rather than next to the functions it uses, so it
        // can never observe a partially-loaded app. It keeps the order it had
        // when the app was a single main.js. readComic(), the hand-off to
        // reader.html, sits with the reader-return-state restore it pairs with.

        onViewportResize(applyMobileLibraryView);
        onViewportResize(updateSettingsNavActive);

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
                const sidebarUser = document.getElementById('sidebarUser');
                const sidebarUsername = document.getElementById('sidebarUsername');
                if (sidebarUser && sidebarUsername) {
                    sidebarUsername.textContent = username;
                    sidebarUser.hidden = false;
                }
            }
            
            loadCurrentUser();
            
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
                    document.getElementById('headerFilterLabel').textContent = FILTER_LABELS[filterMode] || FILTER_LABELS['all'];
                    
                    // Update active class on dropdown items
                    document.querySelectorAll('#headerFilterMenu .header-dropdown-item').forEach(item => {
                        if (item.dataset.filter === filterMode) {
                            item.classList.add('active');
                        } else {
                            item.classList.remove('active');
                        }
                    });
                }

                // Restore sort mode from preferences
                const oldSortMode = sortMode;
                if (prefs.sortMode) {
                    sortMode = prefs.sortMode;
                    applySortUi();
                }

                // The Files library view has been removed; always force series
                // mode regardless of what the server has cached for this user.
                libraryViewMode = 'series';

                updateLibraryViewButtons();
                updateLibraryViewLayout();
                
                // Reload files if perPage changed from default
                if (libraryViewMode !== oldLibraryViewMode || sortMode !== oldSortMode || (perPage !== oldPerPage && perPage !== DEFAULT_PER_PAGE)) {
                    loadActiveLibraryView(1);
                }
            });
            
            // These run in the background so the file list can render immediately.
            // Errors are handled here to avoid unhandled promise rejections.
            jobCheckPromise.catch(error => console.error('Failed to check active job:', error));
            libraryHealthPromise.catch(error => console.error('Failed to load library health:', error));

            // Wire up hash-based routing and render the initial view. Default
            // new sessions to the overview page (#/home).
            window.addEventListener('hashchange', handleRouteChange);
            if (!window.location.hash || window.location.hash === '#') {
                window.location.hash = '#/home';
            } else {
                handleRouteChange();
            }

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
                    syncHeaderSearchClear();
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
                    // Only remember a series-detail panel to reopen when the
                    // user is actually viewing one. Capturing it from the
                    // overview/home or library list would make the reader
                    // return reopen a stale series and hide the overview.
                    currentSeriesDetailId: (typeof currentView !== 'undefined' && currentView === 'series'
                        && typeof currentSeriesDetailId !== 'undefined') ? currentSeriesDetailId : null,
                    timestamp: Date.now()
                };
                sessionStorage.setItem('comicReaderReturnState', JSON.stringify(snapshot));
            } catch (_e) {
                // sessionStorage can throw in private modes — ignore.
            }
            // Open comic reader in the same window
            window.location.href = `/reader.html?file=${encodeURIComponent(filepath)}`;
        }

        // Re-evaluate sticky-class placement on viewport rotation/resize so
        // the indicator follows the user between mobile and desktop sizes.
        onViewportResize(() => {
            const indicator = document.getElementById('progressIndicator');
            if (indicator && indicator.style.display !== 'none') {
                setProgressIndicatorVisible(true);
            }
        });

        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', initModalAccessibility);
        } else {
            initModalAccessibility();
        }

        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', initDropdownAriaExpandedSync);
        } else {
            initDropdownAriaExpandedSync();
        }

        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', initConnectivityAwareness);
        } else {
            initConnectivityAwareness();
        }

        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', initLibraryKeyboardShortcuts);
        } else {
            initLibraryKeyboardShortcuts();
        }

        window.addEventListener('unhandledrejection', event => {
            reportUnexpectedError(event.reason, 'unhandledrejection');
        });

        window.addEventListener('error', event => {
            // Failed <img>/<script>/<link> loads also fire 'error', but they
            // bubble from the element rather than the window and carry no
            // Error object. Covers that 404 are routine here, so ignore them.
            if (!event.error) return;
            reportUnexpectedError(event.error, 'error', event.filename);
        });
