        // ComicMaintainer SPA — app shell: library health dashboard, mobile view, viewport resize, header search, theme, capabilities, connectivity.
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

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

        // Viewport resize handling
        // ------------------------
        // Resize fires continuously while a window is dragged or a device is
        // rotated, and the handlers below re-evaluate media queries and write
        // to the DOM. Running them per event causes needless style/layout work,
        // so they share a single debounced listener rather than registering one
        // each.
        const viewportResizeHandlers = [];
        let viewportResizeTimer = null;
        function onViewportResize(handler) {
            viewportResizeHandlers.push(handler);
        }
        window.addEventListener('resize', () => {
            if (viewportResizeTimer) clearTimeout(viewportResizeTimer);
            viewportResizeTimer = setTimeout(() => {
                viewportResizeTimer = null;
                viewportResizeHandlers.forEach(handler => {
                    // One failing handler must not stop the others running.
                    try {
                        handler();
                    } catch (err) {
                        console.error('Resize handler failed:', err);
                    }
                });
            }, 150);
        });

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
            syncHeaderSearchClear();

            // Clear existing timer
            if (searchDebounceTimer) {
                clearTimeout(searchDebounceTimer);
            }
            
            // Set new timer to trigger after 300ms of inactivity
            searchDebounceTimer = setTimeout(() => {
                filterFiles();
            }, 300);
        }

        // Show the "×" affordance only when there is something to clear, so it
        // never sits on top of the placeholder text.
        function syncHeaderSearchClear() {
            const input = document.getElementById('headerSearchInput');
            const clearBtn = document.getElementById('headerSearchClear');
            if (!input || !clearBtn) return;
            clearBtn.hidden = input.value.length === 0;
        }

        // Empty the search box and re-run the query. Bound to the "×" button
        // and to Escape while the search box has focus; both are no-ops when
        // the box is already empty so Escape can still fall through to
        // whatever else wants it.
        function clearHeaderSearch() {
            const input = document.getElementById('headerSearchInput');
            if (!input) return false;
            if (input.value === '') {
                syncHeaderSearchClear();
                return false;
            }
            input.value = '';
            syncHeaderSearchClear();
            if (searchDebounceTimer) {
                clearTimeout(searchDebounceTimer);
                searchDebounceTimer = null;
            }
            filterFiles();
            input.focus();
            return true;
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
        
        // Current user's capabilities, resolved from GET /api/auth/user. Both
        // default to true so the UI stays fully functional if the capability
        // lookup fails — the API is the real enforcement point.
        let userCapabilities = { canModifyLibrary: true, canAdminister: true };
        let settingsLoaded = false;
        let settingsLoadRequest = 0;

        // Fetch the signed-in user and hide controls they are not allowed to use.
        // The server still enforces the same rules via authorization policies;
        // this only avoids showing actions that would fail with a 403.
        async function loadCurrentUser() {
            try {
                const response = await fetch(apiUrl('/api/auth/user'), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    console.error('Failed to load current user:', response.status);
                    return;
                }
                const user = await response.json();
                userCapabilities = {
                    canModifyLibrary: user.canModifyLibrary !== false,
                    canAdminister: user.canAdminister !== false
                };
                applyCapabilities();
            } catch (error) {
                console.error('Error loading current user:', error);
            }
        }

        function applyCapabilities() {
            // Write-only controls are hidden via CSS (body.user-read-only) so
            // they stay hidden even if other code later toggles their `hidden`
            // attribute (e.g. the duplicate-review / combine-folders buttons).
            document.body.classList.toggle('user-read-only', !userCapabilities.canModifyLibrary);

            // /api/diagnostics reports host paths, disk space and process
            // memory, and scheduled jobs are server administration (backups,
            // library-wide scans and auto-corrections), so both APIs are
            // administrator-only. Hide the entry points rather than let them
            // open a page or panel that can only say "403".
            [
                'diagnosticsMenuItem', 'sidebarDiagnosticsItem',
                'scheduledJobsMenuItem', 'sidebarJobsBtn', 'scheduledJobsShortcutItem'
            ].forEach(id => {
                const adminItem = document.getElementById(id);
                if (adminItem) adminItem.hidden = !userCapabilities.canAdminister;
            });

            // Server-wide settings are administrator-only; the personal
            // appearance preference stays editable for everyone.
            const notice = document.getElementById('settingsReadOnlyNotice');
            if (notice) notice.hidden = userCapabilities.canAdminister;

            // The backups API (a full copy of the database, credentials
            // included) is administrator-only, so the whole Backups section is
            // hidden for everyone else (see settings.js).
            updateBackupsSectionVisibility();

            const settingsBody = document.getElementById('settingsPageBody');
            if (settingsBody) {
                settingsBody.querySelectorAll('input, select, textarea, button').forEach(el => {
                    const isUserSetting = !!el.closest('[data-user-setting]');
                    // Backups talk to their own API, so they do not wait for
                    // /api/settings to load.
                    if (el.closest('#settings-backups')) {
                        el.disabled = !userCapabilities.canAdminister;
                        return;
                    }
                    el.disabled = !settingsLoaded || (!isUserSetting && !userCapabilities.canAdminister);
                });
            }

            ['resetFilenameFormatBtn', 'saveSettingsBtn'].forEach(id => {
                const button = document.getElementById(id);
                if (button) button.disabled = !settingsLoaded || !userCapabilities.canAdminister;
            });

            // Scheduled jobs are administrator-only (see the entry points
            // above); anyone else who reaches the page gets a notice instead.
            applyScheduledJobsCapabilities();
        }

        // =================================================================
        // Connectivity awareness
        // =================================================================
        //
        // Every request in this app goes through fetch(), and a fetch made
        // with no network rejects with a bare "Failed to fetch" TypeError.
        // That surfaces as a generic "Failed to load ..." error which reads
        // like a server fault, so users retry against a server they cannot
        // reach. A persistent banner states the actual cause instead, and is
        // removed as soon as connectivity returns.

        function removeOfflineBanner() {
            const existing = document.getElementById('appOfflineBanner');
            if (existing) existing.remove();
        }

        function showOfflineBanner() {
            if (document.getElementById('appOfflineBanner')) return;

            const banner = document.createElement('div');
            banner.id = 'appOfflineBanner';
            banner.className = 'app-update-banner app-offline-banner';
            // assertive: the user is about to be told their actions are failing,
            // which should not wait behind whatever else is being announced.
            banner.setAttribute('role', 'alert');
            banner.setAttribute('aria-live', 'assertive');

            const text = document.createElement('span');
            text.className = 'app-update-banner-text';
            text.textContent = 'You are offline. Changes cannot be saved until the connection returns.';

            banner.appendChild(text);
            document.body.appendChild(banner);
        }

        function handleConnectivityChange() {
            if (navigator.onLine) {
                removeOfflineBanner();
            } else {
                showOfflineBanner();
            }
        }

        function initConnectivityAwareness() {
            window.addEventListener('online', handleConnectivityChange);
            window.addEventListener('offline', handleConnectivityChange);
            // The page may have been restored from the bfcache, or loaded from
            // the service worker cache, while already offline.
            handleConnectivityChange();
        }
