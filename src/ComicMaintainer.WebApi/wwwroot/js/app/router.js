        // ComicMaintainer SPA — hash routing (#/home, #/library, #/series/{id}, #/settings, #/jobs) and the overview page.
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

        // Initialization function that runs after DOM is ready
        // ── Top-level SPA routing ─────────────────────────────────────────────
        // Hash-based routes keep the app a static SPA while giving each
        // top-level page a shareable/bookmarkable URL and working browser
        // back/forward:
        //   #/home          → overview page (default landing)
        //   #/library       → full series library (grid/list, filters, bulk)
        //   #/series/{id}    → a single series' page (synopsis + files)
        //   #/settings      → the settings page
        //   #/jobs          → the scheduled jobs page
        let currentView = 'home';           // 'home' | 'library' | 'series' | 'settings' | 'jobs'
        let settingsReturnHash = '#/home';  // where leaving Settings goes back to
        let lastRoutedPage = null;          // top-level page last shown (see enterPage)

        // Overview, Library, Settings and Scheduled Jobs are separate pages
        // that share one document. Give each its own tab title and, when the
        // user moves to a different page, start it at the top and move focus
        // to the content so assistive tech announces the change like a real
        // navigation. Moving between the library and a series stays on the
        // "library" page so its scroll position survives the round trip.
        const PAGE_TITLES = { home: 'Overview', library: 'Library', settings: 'Settings', jobs: 'Scheduled Jobs' };
        // Pages that scroll inside their own section (on desktop) rather than
        // the document.
        const PAGE_SCROLLERS = { settings: 'settingsView', jobs: 'jobsView' };
        function enterPage(view) {
            const page = view === 'series' ? 'library' : view;
            document.title = (PAGE_TITLES[page] || 'Overview') + ' · Comic Maintainer';
            const isInitialLoad = lastRoutedPage === null;
            const changed = page !== lastRoutedPage;
            lastRoutedPage = page;
            if (isInitialLoad || !changed) return;
            window.scrollTo(0, 0);
            const pageScroller = PAGE_SCROLLERS[page] && document.getElementById(PAGE_SCROLLERS[page]);
            if (pageScroller) pageScroller.scrollTop = 0;
            const main = document.getElementById('mainContent');
            if (main && !main.contains(document.activeElement)) {
                main.focus({ preventScroll: true });
            }
        }
        let seriesDetailOrigin = 'library'; // where the series "Back" returns to
        let overviewLoaded = false;

        function parseHash() {
            const raw = (window.location.hash || '').replace(/^#/, '');
            if (raw.startsWith('/series/')) {
                return { view: 'series', seriesId: decodeURIComponent(raw.slice('/series/'.length)) };
            }
            if (raw === '/library') return { view: 'library' };
            if (raw === '/settings') return { view: 'settings' };
            if (raw === '/jobs') return { view: 'jobs' };
            return { view: 'home' };
        }

        // Navigate to a route. Setting the hash fires `hashchange`, which the
        // router handles; if the hash is already set we re-run the handler so
        // repeat clicks still refresh the view.
        function navigate(hash) {
            if (window.location.hash === hash) {
                handleRouteChange();
            } else {
                window.location.hash = hash;
            }
        }

        function handleRouteChange() {
            const route = parseHash();
            if (route.view !== 'series' || currentSeriesDetailId !== route.seriesId) {
                lastSelectedIssuePath = null;
                seriesIssueRangeSelectArmed = false;
            }
            currentView = route.view;
            if (route.view !== 'settings') {
                settingsReturnHash = window.location.hash || '#/home';
            }
            if (route.view !== 'jobs') {
                // Scheduled Jobs polls for status only while it is on screen.
                stopScheduledJobsRefresh();
            }
            applyViewVisibility(route.view);
            enterPage(route.view);
            if (route.view === 'settings' || route.view === 'jobs') {
                if (currentSeriesDetailId) {
                    disconnectSeriesIssuesObserver();
                    currentSeriesDetailId = null;
                    currentSeriesDetailTitleKeys = null;
                    currentSeriesDetailSeries = null;
                }
                if (route.view === 'settings') {
                    loadSettings();
                } else {
                    loadScheduledJobs();
                }
            } else if (route.view === 'home') {
                // The overview/home page is not a series detail. Clear any
                // lingering detail state so it can't leak into the reader
                // return snapshot (readComic) and cause the app to reopen a
                // stale series — which would hide the overview and make the
                // "Series Updates" / "Newly Added" lists look emptied out on
                // return from the reader.
                if (currentSeriesDetailId) {
                    disconnectSeriesIssuesObserver();
                    currentSeriesDetailId = null;
                    currentSeriesDetailTitleKeys = null;
                    currentSeriesDetailSeries = null;
                }
                renderOverview();
            } else if (route.view === 'series') {
                renderSeriesDetailView(route.seriesId);
            } else {
                // Library list: ensure we're not stuck in a detail panel.
                if (currentSeriesDetailId) {
                    disconnectSeriesIssuesObserver();
                    currentSeriesDetailId = null;
                    currentSeriesDetailTitleKeys = null;
                    currentSeriesDetailSeries = null;
                }
                renderLibraryView();
            }
            updateNavActiveState();
        }

        // Show/hide the top-level sections and the header controls that only
        // make sense inside the library / series contexts.
        function applyViewVisibility(view) {
            const isHome = view === 'home';
            const isSettings = view === 'settings';
            const isJobs = view === 'jobs';
            const hideLibrary = isHome || isSettings || isJobs;
            const overview = document.getElementById('overviewView');
            const health = document.getElementById('libraryHealthDashboard');
            const files = document.getElementById('libraryFilesView');
            const settings = document.getElementById('settingsView');
            const jobs = document.getElementById('jobsView');
            if (overview) overview.hidden = !isHome;
            if (settings) settings.hidden = !isSettings;
            if (jobs) jobs.hidden = !isJobs;
            if (health) health.style.display = hideLibrary ? 'none' : '';
            if (files) files.style.display = hideLibrary ? 'none' : '';
            document.body.classList.toggle('view-settings', isSettings);

            // Search / filter / sort apply to the library and series files.
            const headerSearch = document.querySelector('.header-search');
            const headerFilter = document.querySelector('.header-filter-dropdown');
            const headerSort = document.querySelector('.header-sort-dropdown');
            [headerSearch, headerFilter, headerSort].forEach(el => {
                if (el) el.style.display = hideLibrary ? 'none' : '';
            });

            // The list/grid/compact layout toggle is only meaningful on the
            // full library grid (not the overview or a single series page).
            // updateSeriesLayoutButtons() decides both its visibility and
            // which button is active, so the toggle never appears with no
            // (or the wrong) button marked.
            updateSeriesLayoutButtons();
        }

        function updateNavActiveState() {
            const activeByView = {
                navOverviewBtn: currentView === 'home',
                navLibraryBtn: currentView === 'library' || currentView === 'series',
                sidebarOverviewBtn: currentView === 'home',
                sidebarLibraryBtn: currentView === 'library' || currentView === 'series',
                sidebarSettingsBtn: currentView === 'settings',
                sidebarJobsBtn: currentView === 'jobs'
            };
            const topbarTitle = document.getElementById('topbarTitle');
            if (topbarTitle) {
                topbarTitle.textContent = ({ home: 'Overview', library: 'Library', series: 'Library', settings: 'Settings', jobs: 'Scheduled Jobs' })[currentView] || '';
            }
            Object.entries(activeByView).forEach(([id, active]) => {
                const el = document.getElementById(id);
                if (!el) return;
                el.classList.toggle('active', active);
                if (active) {
                    el.setAttribute('aria-current', 'page');
                } else {
                    el.removeAttribute('aria-current');
                }
            });
        }

        // Render the full library list (reuses the existing series grid/list).
        function renderLibraryView() {
            if (seriesLibrary.length) {
                renderSeriesLibrary();
            } else if (!seriesLoading) {
                loadActiveLibraryView();
            }
            updatePagination();
            updateLibraryViewLayout();
        }

        // Open a single series' page. Resilient to deep-links / refreshes where
        // the library list hasn't been fetched yet.
        async function renderSeriesDetailView(seriesId) {
            if (!seriesId) { navigate('#/library'); return; }
            if (!seriesLibrary.length && !seriesLoading) {
                await loadActiveLibraryView();
            }
            currentSeriesDetailId = seriesId;
            const found = seriesLibrary.find(item => item && item.id === seriesId);
            captureSeriesDetailIdentity(found);
            if (found) {
                currentSeriesDetailSeries = found;
            } else if (!currentSeriesDetailSeries || currentSeriesDetailSeries.id !== seriesId) {
                // The series isn't on the currently-loaded library page — this
                // happens when opening a series from the overview/home page (or
                // a deep link) where only the first library page, or no page at
                // all, has been fetched. Seed a minimal placeholder so
                // renderSeriesDetail keeps the panel open and shows a loading
                // state instead of immediately closing it; loadSeriesIssues
                // refreshes it with the real metadata from the issues response.
                currentSeriesDetailSeries = { id: seriesId, title: '', total_size: 0, issue_count: 0 };
            }
            renderSeriesDetail(seriesId);
            loadSeriesIssues(seriesId);
            updateLibraryViewLayout();
            // Decides whether the missing-issues banner offers the Suwayomi
            // download action; re-renders the panel if the answer changes.
            refreshSuwayomiAvailability();
        }

        // ── Overview page ─────────────────────────────────────────────────────
        async function renderOverview() {
            const container = document.getElementById('overviewRows');
            if (!container) return;
            if (!overviewLoaded) {
                renderHtml(container, html`<div class="loading"><div class="spinner"></div><p>Loading overview...</p></div>`);
            }
            let data;
            try {
                const resp = await fetch(apiUrl('/api/overview'), {
                    headers: getAuthHeaders ? getAuthHeaders() : undefined,
                    credentials: 'same-origin'
                });
                if (!resp.ok) throw new Error('overview ' + resp.status);
                data = await resp.json();
            } catch (err) {
                console.error('Failed to load overview', err);
                renderHtml(container, html`<div class="empty-state"><p>Couldn't load your overview. <button type="button" class="btn btn-small" onclick="renderOverview()">Retry</button></p></div>`);
                return;
            }
            overviewLoaded = true;
            const rows = [
                { title: 'Continue Reading', icon: 'book-open', items: data.continue_reading || [], empty: 'Nothing in progress yet.', resume: true, seeAll: false },
                { title: 'Series Updates', icon: 'refresh', items: data.series_updates || [], empty: 'No new files in the last 30 days.', resume: false, seeAll: true },
                { title: 'Newly Added Series', icon: 'plus', items: data.newly_added_series || [], empty: 'No new series in the last 30 days.', resume: false, seeAll: true }
            ];
            renderHtml(container, rawHtml(rows.map(renderOverviewRow).join('')));
            hydrateProtectedImages(container);
        }

        function renderOverviewRow(row) {
            const cards = row.items.length
                ? row.items.map(series => renderOverviewCard(series, row.resume)).join('')
                : `<div class="overview-empty">${escapeHtml(row.empty)}</div>`;
            return `
                <section class="overview-row">
                    <div class="overview-row-header">
                        <h2 class="overview-row-title"><span class="overview-row-icon">${uiIcon(row.icon)}</span>${escapeHtml(row.title)}</h2>
                        ${row.seeAll && row.items.length ? `<button type="button" class="overview-see-all" onclick="navigate('#/library')">See all →</button>` : ''}
                    </div>
                    <div class="overview-carousel">${cards}</div>
                </section>
            `;
        }

        function renderOverviewCard(series, resume) {
            const cover = series.has_external_image && series.external_image_url ? series.external_image_url : series.cover_file_path;
            const fallback = series.has_external_image && series.external_image_url ? series.cover_file_path : '';
            const resumeBtn = (resume && series.resume_file_path)
                ? `<button type="button" class="overview-resume-btn" onclick="event.stopPropagation(); readComic('${escapeJs(series.resume_file_path)}')">${uiIcon('play')} Resume</button>`
                : '';
            return `
                <div class="overview-card">
                    <button class="series-card overview-series-card" type="button" aria-label="Open series ${escapeHtml(series.title)}" onclick="openSeriesDetail('${escapeJs(series.id)}')">
                        <div class="series-cover-wrapper">
                            <img class="series-cover" data-protected-image="${escapeHtml(cover)}" data-protected-image-fallback="${escapeHtml(fallback)}" alt="${escapeHtml(series.title)} cover" loading="lazy" decoding="async" fetchpriority="low">
                            <div class="series-cover-overlay"></div>
                            <span class="series-count-badge">${series.issue_count}</span>
                            ${renderLookupStatusBadge(series)}
                            <div class="series-card-body">
                                <h3 class="series-title" title="${escapeHtml(series.title)}">${escapeHtml(series.title)}</h3>
                                <div class="series-meta">${formatFileSize(series.total_size)}</div>
                            </div>
                        </div>
                    </button>
                    ${resumeBtn}
                </div>
            `;
        }
