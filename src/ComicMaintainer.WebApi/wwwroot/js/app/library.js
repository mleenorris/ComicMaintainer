        // ComicMaintainer SPA — library view: layout, series/folder loading, filtering, sorting, protected images, series grid and selection.
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

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
            // The layout toggle only applies to the full library grid/list — not
            // the overview page or a single series' page.
            const inSeriesMode = libraryViewMode === 'series'
                && (typeof currentView === 'undefined' || currentView === 'library');
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

        function updateLibraryViewLayout() {
            // The processing controls now live inside the Library Health
            // dropdown and are available in both the series list view and the
            // series detail (files) view; their visibility is governed by the
            // dropdown's expand/collapse state, not by the current view.
            const controlsWrapper = document.querySelector('.controls-wrapper');
            const pagination = document.getElementById('pagination');

            if (controlsWrapper) {
                controlsWrapper.style.display = '';
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

        // In-flight list requests. A fresh (non-append) reload requested while
        // one of these is running aborts it, so a stale response for the old
        // filter/search/sort is never rendered and the new query starts as
        // soon as the aborted request settles instead of after a full
        // (possibly slow, on large libraries) response.
        let seriesLoadController = null;
        let folderLoadController = null;

        function isAbortError(error) {
            return !!error && error.name === 'AbortError';
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

            if (seriesLoading) {
                // A load is already in flight. For a fresh (non-append) reload —
                // e.g. the user changed the filter/search/sort — remember that a
                // reload is needed so we re-fetch with the latest query once the
                // in-flight request settles. Without this the request would be
                // silently dropped and the list would stay on the previous filter
                // (label updated, content stale) until a manual page refresh.
                if (!append) {
                    seriesReloadPending = true;
                    if (seriesLoadController) {
                        seriesLoadController.abort();
                    }
                }
                return;
            }

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
                // Restore/close any open action menu (which may be portaled to
                // <body>) before the list's DOM is replaced below.
                closeActionDropdowns();
                renderHtml(fileListEl, html`
                    <div class="loading">
                        <div class="spinner"></div>
                        <p>Loading series...</p>
                    </div>
                `);
            }

            seriesLoading = true;
            const controller = typeof AbortController !== 'undefined' ? new AbortController() : null;
            seriesLoadController = controller;
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
                    headers: getAuthHeaders(),
                    signal: controller ? controller.signal : undefined
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
                } else if (!(append && appendSeriesLibraryItems(newSeries))) {
                    renderSeriesLibrary();
                }

                updateButtonVisibility();
                updateLibraryViewLayout();
                ensureScrollObserver();

                if (refresh) {
                    loadLibraryHealth();
                }
            } catch (error) {
                // Superseded by a newer reload (see seriesLoadController); the
                // pending reload below fetches the current selection.
                if (!isAbortError(error)) {
                    showMessage('Failed to load series: ' + error.message, 'error', { reference: true });
                }
            } finally {
                if (seriesLoadController === controller) {
                    seriesLoadController = null;
                }
                seriesLoading = false;
                if (seriesReloadPending) {
                    // A filter/search/sort change arrived while this load was in
                    // flight. Re-run with the latest selection so the displayed
                    // series match the current filter without a page refresh.
                    seriesReloadPending = false;
                    loadSeriesLibrary({ refresh: false });
                }
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

            if (folderLoading) {
                // Mirror the series loader: don't drop a fresh (non-append)
                // reload requested while a load is in flight; re-run it after.
                if (!append) {
                    folderReloadPending = true;
                    if (folderLoadController) {
                        folderLoadController.abort();
                    }
                }
                return;
            }

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
            const controller = typeof AbortController !== 'undefined' ? new AbortController() : null;
            folderLoadController = controller;
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
                    headers: getAuthHeaders(),
                    signal: controller ? controller.signal : undefined
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
                // expanded in this session. The library root (path '') is the
                // exception: it has no header to expand it by, so it is never
                // collapsed and its files are always shown.
                let rootArrived = false;
                for (const f of newFolders) {
                    if (f && typeof f.path === 'string') {
                        if (!f.path) {
                            rootArrived = true;
                        } else if (!folderFiles.has(f.path)) {
                            collapsedDirectories.add(f.path);
                        }
                    }
                }

                // The list has arrived: stop counting as loading before drawing
                // it, or an empty result would be drawn as "Loading files..."
                // (finally{} clears the flag only after this render) instead
                // of its empty state.
                folderLoading = false;
                renderFileList();
                // The root's files are on screen straight away, so fetch them
                // for the current filter/search even when a list from an
                // earlier one is cached.
                if (rootArrived) {
                    ensureFolderLoaded('', true).catch(() => {});
                }
                updateButtonVisibility();
                ensureScrollObserver();
                if (refresh) {
                    loadLibraryHealth();
                }
            } catch (error) {
                if (!isAbortError(error)) {
                    showMessage('Failed to load files: ' + error.message, 'error', { reference: true });
                }
            } finally {
                if (folderLoadController === controller) {
                    folderLoadController = null;
                }
                folderLoading = false;
                if (folderReloadPending) {
                    folderReloadPending = false;
                    loadFolders({ refresh: false });
                }
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
            // The "… Unmarked" actions have nothing to act on without unmarked
            // files, so they are shown only while there are some.
            const displayStyle = unmarkedCount > 0 ? '' : 'none';
            for (const id of ['processUnmarkedItem', 'renameUnmarkedItem', 'normalizeUnmarkedItem']) {
                const item = document.getElementById(id);
                if (item) item.style.display = displayStyle;
            }
            // The processing controls are always available (inside the Library
            // Health dropdown); only their dropdown collapse state hides them.
            const controlsWrapper = document.querySelector('.controls-wrapper');
            if (controlsWrapper) controlsWrapper.style.display = '';
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

        // Syncs the sort dropdown label and active item with the current
        // sortMode/sortDirection. Shared by setSort() and the preference
        // restore path so both stay in sync.
        function applySortUi() {
            const sortLabels = {
                'name': 'Name',
                'date': 'Date',
                'size': 'Size'
            };

            const arrow = sortDirection === 'asc' ? '↑' : '↓';
            const label = document.getElementById('headerSortLabel');
            if (label) {
                label.textContent = (sortLabels[sortMode] || sortLabels['name']) + ' ' + arrow;
            }

            document.querySelectorAll('#headerSortMenu .header-dropdown-item').forEach(item => {
                if (item.dataset.sort === sortMode) {
                    item.classList.add('active');
                } else {
                    item.classList.remove('active');
                }
            });
        }
        
        async function setHeaderFilter(mode) {
            filterMode = mode;
            
            // Update dropdown label and active state
            document.getElementById('headerFilterLabel').textContent = FILTER_LABELS[mode] || FILTER_LABELS['all'];
            
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
            
            applySortUi();

            // Close the dropdown
            document.getElementById('headerSortMenu').classList.remove('show');

            // Persist the chosen sort so it survives a refresh / other devices
            setPreferences({ sortMode: sortMode });

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

        // Keep aria-expanded on the header menu triggers in sync with the
        // menu's visual state. The menus are opened/closed from many places
        // (toggles, item handlers, click-away, Escape), so instead of touching
        // every call site we observe the menu's class attribute.
        function initDropdownAriaExpandedSync() {
            const pairs = [
                ['settingsMenuToggle', 'settingsDropdownMenu'],
                ['headerFilterToggle', 'headerFilterMenu'],
                ['headerSortToggle', 'headerSortMenu']
            ];

            pairs.forEach(([toggleId, menuId]) => {
                const toggle = document.getElementById(toggleId);
                const menu = document.getElementById(menuId);
                if (!toggle || !menu) return;

                const sync = () => {
                    toggle.setAttribute('aria-expanded', menu.classList.contains('show') ? 'true' : 'false');
                };

                sync();

                if (typeof MutationObserver !== 'undefined') {
                    new MutationObserver(sync).observe(menu, {
                        attributes: true,
                        attributeFilter: ['class']
                    });
                }
            });
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

        // Accepts a single container or an array of containers; pass every
        // newly added node in one call so the fallback batching below caps
        // concurrent loads across all of them.
        async function hydrateProtectedImages(container = document) {
            const roots = Array.isArray(container) ? container : [container];
            const images = roots.flatMap(root => Array.from(root.querySelectorAll('[data-protected-image]')));
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

            // Restore/close any open action menu (which may be portaled to
            // <body>) before the list's DOM is rebuilt below.
            closeActionDropdowns();

            // Drop any selected series that are no longer present in the
            // currently loaded library list (e.g. after a filter change) so
            // stale selections don't linger.
            reconcileSeriesSelection();

            if (!seriesLibrary.length) {
                renderHtml(fileList, html`
                    <div class="empty-state">
                        <div class="empty-state-icon">${uiIcon('image')}</div>
                        <h2>No series found</h2>
                        <p>${searchQuery || filterMode !== 'all' ? 'Try a different search term or filter' : 'Process and normalize comics to build your series library view.'}</p>
                    </div>
                `);
                return;
            }

            const layout = getEffectiveSeriesLayout();
            const body = (layout === 'list')
                ? renderSeriesLibraryList()
                : (layout === 'grid')
                    ? renderSeriesLibraryGrid()
                    : renderSeriesLibraryCompact();

            renderHtml(fileList, html`
                ${rawHtml(renderSeriesSelectionToolbar())}
                ${rawHtml(body)}
            `);

            hydrateProtectedImages(fileList);
            updateSelectInfo();
        }

        // Infinite-scroll pages only add cards, so append them to the existing
        // grid/list instead of re-rendering every loaded series. A full render
        // per page made scrolling through a large library O(n^2) in DOM work
        // and re-ran image hydration for every card already on screen.
        // Returns false when there is no rendered container to append to, so
        // the caller falls back to renderSeriesLibrary().
        function appendSeriesLibraryItems(newSeries) {
            const fileList = document.getElementById('fileList');
            if (!fileList) return false;
            const layout = getEffectiveSeriesLayout();
            const selector = (layout === 'list')
                ? '.series-list'
                : (layout === 'grid') ? '.series-grid--titled' : '.series-grid--compact';
            const container = fileList.querySelector(selector);
            if (!container) return false;
            if (!newSeries.length) return true;

            const markup = (layout === 'list')
                ? renderSeriesLibraryList(newSeries)
                : (layout === 'grid')
                    ? renderSeriesLibraryGrid(newSeries)
                    : renderSeriesLibraryCompact(newSeries);
            const scratch = document.createElement('div');
            appendHtml(scratch, rawHtml(markup));
            const source = scratch.querySelector(selector);
            if (!source) return false;
            const added = Array.from(source.children);
            container.append(...added);
            hydrateProtectedImages(added);
            syncSeriesSelectionControls();
            updateSelectInfo();
            return true;
        }

        // Small toolbar shown above the series grid/list letting the user
        // select every loaded series at once (so the bulk process / rename /
        // normalize actions in the Library Health dropdown operate on them) or
        // clear the current selection.
        function renderSeriesSelectionToolbar() {
            const total = seriesLibrary.length;
            const selectedCount = seriesLibrary.reduce(
                (n, s) => n + (selectedSeries.has(s.id) ? 1 : 0), 0);
            const allSelected = total > 0 && selectedCount === total;
            return `
                <div class="series-selection-toolbar">
                    <label class="series-selection-all">
                        <input type="checkbox" id="seriesSelectAll"
                               ${allSelected ? 'checked' : ''}
                               onchange="toggleSelectAllSeries(this.checked)">
                        <span>Select all series</span>
                    </label>
                    <span class="series-selection-count" id="seriesSelectionCount">
                        ${selectedCount > 0 ? `${selectedCount} series selected` : 'No series selected'}
                    </span>
                    <button type="button" class="btn btn-small" id="seriesSelectionClearBtn" onclick="clearSeriesSelection()" ${selectedCount > 0 ? '' : 'hidden'}>Clear</button>
                    <button type="button" class="btn btn-small" id="seriesSelectionEmailBtn" data-requires-write onclick="openEmailSendModalForSelected()" ${selectedCount > 0 ? '' : 'hidden'}>📧 Email Selected</button>
                </div>
            `;
        }

        // Render the small selectable checkbox overlay shown on every series
        // card/row. A <span role="checkbox"> is used (rather than a real
        // <input>) so it is valid inside the surrounding <button> element; the
        // click is stopped from bubbling so it toggles selection instead of
        // opening the series.
        function renderSeriesSelectBox(series) {
            const checked = selectedSeries.has(series.id);
            return `<span class="series-select-box${checked ? ' series-select-box--checked' : ''}"
                role="checkbox" aria-checked="${checked ? 'true' : 'false'}" tabindex="0"
                data-series-id="${escapeHtml(series.id)}"
                title="Select series" aria-label="Select ${escapeHtml(series.title)}"
                onclick="event.stopPropagation(); toggleSeriesSelection('${escapeJs(series.id)}', this.getAttribute('aria-checked') !== 'true')"
                onkeydown="if(event.key===' '||event.key==='Enter'){event.preventDefault(); event.stopPropagation(); toggleSeriesSelection('${escapeJs(series.id)}', this.getAttribute('aria-checked') !== 'true');}">${checked ? '✓' : ''}</span>`;
        }

        // Fetch every file path belonging to a series (ignoring the active
        // filter so the bulk action operates on the whole series), caching the
        // result for the lifetime of the selection.
        async function fetchSeriesFilePaths(seriesId) {
            try {
                const url = apiUrl(`/api/files/series/${encodeURIComponent(seriesId)}/issues?per_page=-1&filter=all`);
                const response = await fetch(url, {
                    headers: getAuthHeaders ? getAuthHeaders() : undefined,
                    credentials: 'same-origin'
                });
                if (!response.ok) return [];
                const data = await response.json();
                const issues = Array.isArray(data.issues) ? data.issues : [];
                return issues
                    .map(issue => issue.file_path)
                    .filter(path => typeof path === 'string' && path.length > 0);
            } catch (err) {
                console.error('fetchSeriesFilePaths failed', err);
                return [];
            }
        }

        // Toggle selection of a single series in the series list view. Selecting
        // a series adds all of its file paths to `selectedFiles` so the bulk
        // process / rename / normalize actions act on those files.
        async function toggleSeriesSelection(seriesId, checked) {
            if (!seriesId) return;
            if (checked) {
                if (selectedSeries.has(seriesId)) return;
                selectedSeries.add(seriesId);
                // Reflect the pending state immediately, then resolve files.
                const pathLoad = fetchSeriesFilePaths(seriesId);
                selectedSeriesPathLoads.set(seriesId, pathLoad);
                updateSeriesSelectionUI();
                const paths = await pathLoad;
                if (selectedSeriesPathLoads.get(seriesId) !== pathLoad) return;
                selectedSeriesPathLoads.delete(seriesId);
                // The user may have deselected while the request was in flight.
                if (!selectedSeries.has(seriesId)) return;
                selectedSeriesFilePaths.set(seriesId, paths);
                paths.forEach(p => selectedFiles.add(p));
            } else {
                selectedSeries.delete(seriesId);
                selectedSeriesPathLoads.delete(seriesId);
                const paths = selectedSeriesFilePaths.get(seriesId) || [];
                paths.forEach(p => selectedFiles.delete(p));
                selectedSeriesFilePaths.delete(seriesId);
            }
            updateSeriesSelectionUI();
            updateSelectInfo();
        }

        async function toggleSelectAllSeries(checked) {
            if (checked) {
                await Promise.all(seriesLibrary.map(s => toggleSeriesSelection(s.id, true)));
            } else {
                clearSeriesSelection();
            }
        }

        function clearSeriesSelection() {
            for (const [, paths] of selectedSeriesFilePaths) {
                paths.forEach(p => selectedFiles.delete(p));
            }
            selectedSeries.clear();
            selectedSeriesFilePaths.clear();
            selectedSeriesPathLoads.clear();
            updateSeriesSelectionUI();
            updateSelectInfo();
        }

        // Clear only the series-selection bookkeeping. Used by code paths that
        // already clear `selectedFiles` wholesale (job completion, DB reset,
        // bulk delete) so stale series highlights don't linger.
        function resetSeriesSelectionState() {
            selectedSeries.clear();
            selectedSeriesFilePaths.clear();
            selectedSeriesPathLoads.clear();
        }

        // Drop selected series that are no longer present in the loaded library
        // list, releasing the file paths they contributed.
        function reconcileSeriesSelection() {
            if (!selectedSeries.size) return;
            const present = new Set(seriesLibrary.map(s => s.id));
            for (const id of Array.from(selectedSeries)) {
                if (!present.has(id)) {
                    const paths = selectedSeriesFilePaths.get(id) || [];
                    paths.forEach(p => selectedFiles.delete(p));
                    selectedSeries.delete(id);
                    selectedSeriesFilePaths.delete(id);
                    selectedSeriesPathLoads.delete(id);
                }
            }
        }

        // Update the series list selection state in place (without rebuilding
        // the whole grid, which would reset the scroll position). Falls back to
        // a full render only when the list hasn't been rendered yet.
        function updateSeriesSelectionUI() {
            if (!(libraryViewMode === 'series' && !currentSeriesDetailId)) return;
            const boxes = document.querySelectorAll('.series-select-box[data-series-id]');
            if (!boxes.length) return;
            boxes.forEach(box => {
                const id = box.dataset.seriesId;
                const checked = selectedSeries.has(id);
                box.classList.toggle('series-select-box--checked', checked);
                box.setAttribute('aria-checked', checked ? 'true' : 'false');
                box.textContent = checked ? '✓' : '';
                const card = box.closest('.series-card, .series-list-row');
                if (card) {
                    card.classList.toggle(
                        card.classList.contains('series-list-row')
                            ? 'series-list-row--selected'
                            : 'series-card--selected',
                        checked);
                }
            });
            syncSeriesSelectionControls();
        }

        // Cover-only cards with title overlaid on the cover (original behaviour).
        function renderSeriesLibraryCompact(items = seriesLibrary) {
            return `
                <div class="series-grid series-grid--compact">
                    ${items.map(series => `
                        <button class="series-card${selectedSeries.has(series.id) ? ' series-card--selected' : ''}" type="button" aria-expanded="${currentSeriesDetailId === series.id ? 'true' : 'false'}" aria-controls="seriesDetailPanel" aria-label="Open series ${escapeHtml(series.title)}" onclick="openSeriesDetail('${escapeJs(series.id)}')">
                            ${renderSeriesSelectBox(series)}
                            <div class="series-cover-wrapper">
                                <img class="series-cover" data-protected-image="${escapeHtml(series.has_external_image && series.external_image_url ? series.external_image_url : series.cover_file_path)}" data-protected-image-fallback="${escapeHtml(series.has_external_image && series.external_image_url ? series.cover_file_path : '')}" alt="${escapeHtml(series.title)} cover" loading="lazy" decoding="async" fetchpriority="low">
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
        function renderSeriesLibraryGrid(items = seriesLibrary) {
            return `
                <div class="series-grid series-grid--titled">
                    ${items.map(series => `
                        <button class="series-card series-card--titled${selectedSeries.has(series.id) ? ' series-card--selected' : ''}" type="button" aria-expanded="${currentSeriesDetailId === series.id ? 'true' : 'false'}" aria-controls="seriesDetailPanel" aria-label="Open series ${escapeHtml(series.title)}" onclick="openSeriesDetail('${escapeJs(series.id)}')">
                            ${renderSeriesSelectBox(series)}
                            <div class="series-cover-wrapper">
                                <img class="series-cover" data-protected-image="${escapeHtml(series.has_external_image && series.external_image_url ? series.external_image_url : series.cover_file_path)}" data-protected-image-fallback="${escapeHtml(series.has_external_image && series.external_image_url ? series.cover_file_path : '')}" alt="${escapeHtml(series.title)} cover" loading="lazy" decoding="async" fetchpriority="low">
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
        function renderSeriesLibraryList(items = seriesLibrary) {
            return `
                <div class="series-list" role="list">
                    ${items.map(series => `
                        <button class="series-list-row${selectedSeries.has(series.id) ? ' series-list-row--selected' : ''}" type="button" role="listitem" aria-expanded="${currentSeriesDetailId === series.id ? 'true' : 'false'}" aria-controls="seriesDetailPanel" aria-label="Open series ${escapeHtml(series.title)}" onclick="openSeriesDetail('${escapeJs(series.id)}')">
                            ${renderSeriesSelectBox(series)}
                            <div class="series-list-thumb-wrapper">
                                <img class="series-list-thumb" data-protected-image="${escapeHtml(series.has_external_image && series.external_image_url ? series.external_image_url : series.cover_file_path)}" data-protected-image-fallback="${escapeHtml(series.has_external_image && series.external_image_url ? series.cover_file_path : '')}" alt="${escapeHtml(series.title)} cover" loading="lazy" decoding="async" fetchpriority="low">
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
            renderHtml(host, rawHtml(renderProviderHealthWidget()));
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
                    renderHtml(container, html`<div style="color: var(--text-muted); font-size: 13px;">Unable to fetch provider status.</div>`);
                    return;
                }
                const data = await response.json();
                const providers = data.providers || [];
                if (!providers.length) {
                    renderHtml(container, html`<div style="color: var(--text-muted); font-size: 13px;">No external providers are configured.</div>`);
                    return;
                }
                renderHtml(container, html`
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
                            return html`<span class="provider-health-pill provider-health-pill--${cls}" title="${tooltipParts}">
                                <span class="provider-health-dot"></span>${p.name}
                            </span>`;
                        })}
                    </div>
                `);
                container.dataset.loaded = 'true';
            } catch (err) {
                console.warn('Provider health fetch failed', err);
                renderHtml(container, html`<div style="color: var(--text-muted); font-size: 13px;">Unable to fetch provider status.</div>`);
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
