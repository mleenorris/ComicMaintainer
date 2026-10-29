        // ComicMaintainer SPA — series detail page: issues, missing issues, Suwayomi downloads.
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

        function openSeriesDetail(seriesId) {
            // Remember where we came from so Back returns to the right place,
            // then navigate to the series route (the router renders the detail).
            seriesDetailOrigin = (currentView === 'home') ? 'home' : 'library';
            navigate('#/series/' + encodeURIComponent(seriesId));
        }

        function closeSeriesDetail() {
            disconnectSeriesIssuesObserver();
            currentSeriesDetailId = null;
            currentSeriesDetailTitleKeys = null;
            currentSeriesDetailSeries = null;
            lastSelectedIssuePath = null;
            navigate(seriesDetailOrigin === 'home' ? '#/home' : '#/library');
        }

        // Normalize a title-like value for case-insensitive comparison.
        function normalizeSeriesTitleKey(value) {
            return typeof value === 'string' ? value.trim().toLowerCase() : '';
        }

        // Record the set of titles/aliases for the currently open series so we
        // can remap the detail id if an external metadata refresh causes the
        // backend's series id (union-find representative) to change.
        function captureSeriesDetailIdentity(series) {
            currentSeriesDetailTitleKeys = seriesTitleKeys(series);
        }

        // The normalized title, canonical title and aliases of a series, or
        // null when it has none.
        function seriesTitleKeys(series) {
            if (!series) return null;
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
            return keys.size ? keys : null;
        }

        // Find a series in `library` (the current library unless given) whose
        // title/canonical/aliases overlap with the given set of normalized keys.
        function findSeriesByTitleKeys(keys, library = seriesLibrary) {
            if (!keys || !keys.size) return null;
            for (const series of library) {
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
            // trigger a fresh fetch. Nor entries still loading: their answer
            // re-renders only the stale id, leaving the new one loading forever.
            const oldCached = seriesIssuesCache.get(currentSeriesDetailId);
            if (oldCached && !oldCached.error && !oldCached.loadingPages?.size
                && !seriesIssuesCache.has(remapped.id)) {
                seriesIssuesCache.set(remapped.id, oldCached);
                oldCached.seriesId = remapped.id;
            } else if (oldCached) {
                // The issues the dropped list still had to confirm (e.g. while
                // its reload or 404 lookup is pending) are confirmed by the
                // list of the new id instead.
                handSeriesIssuePathsOver(oldCached, remapped.id);
            }
            transferSeriesIssuePaths(currentSeriesDetailId, remapped.id);
            seriesIssuesCache.delete(currentSeriesDetailId);
            currentSeriesDetailId = remapped.id;
            return remapped;
        }

        // The entire issue list of a series is loaded in a single request
        // (per_page=-1 instructs the API to return all issues at once). The
        // file lists now load quickly enough that incremental/lazy paging is
        // no longer needed. SERIES_ISSUES_PAGE_SIZE is retained only as the
        // cache entry's default perPage placeholder.
        const SERIES_ISSUES_PAGE_SIZE = 100;
        // Sentinel per_page value sent to the API to request every issue.
        const SERIES_ISSUES_LOAD_ALL = -1;

        async function loadSeriesIssues(seriesId, force = false, page = 1) {
            if (!seriesId) return;
            // The list that a forced reload, or a retry after a failure, replaces.
            const previous = seriesIssuesCache.get(seriesId);
            // An entry listed (or being listed) under another filter is stale.
            if (force || (previous && previous.filter !== filterMode)) {
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
                let unconfirmedPaths = takeSeriesIssuePathsToConfirm(previous, filterMode, seriesId);
                const handedOver = takeHandedOverSeriesIssuePaths(seriesId, filterMode);
                if (handedOver && handedOver.paths) {
                    unconfirmedPaths = new Set([...(unconfirmedPaths || []), ...handedOver.paths]);
                }
                entry = {
                    seriesId,
                    issues: [],
                    total: 0,
                    totalPages: 1,
                    perPage: SERIES_ISSUES_PAGE_SIZE,
                    loadedPages: new Set(),
                    loadingPages: new Set(),
                    allLoaded: false,
                    error: false,
                    // The filter the list is fetched with.
                    filter: filterMode,
                    // Issues of the replaced list that this one has to list
                    // again to stay selected (see pruneSeriesIssueSelection).
                    unconfirmedPaths,
                    // The series' titles as last loaded, to look it up by if
                    // its id stops resolving (see confirmIssuesOfMissingSeries).
                    titles: (previous && previous.titles) || (handedOver && handedOver.titles) || null
                };
                seriesIssuesCache.set(seriesId, entry);
            }
            entry.loadingPages.add(page);
            // A page fetched with another filter than the list's makes it a
            // mix that cannot tell which issues are gone.
            if (entry.filter !== filterMode) {
                entry.unconfirmedPaths = null;
            }

            let seriesMissing = false;
            try {
                let url = apiUrl(`/api/files/series/${encodeURIComponent(seriesId)}/issues?per_page=${SERIES_ISSUES_LOAD_ALL}&page=${page}`);
                if (filterMode !== 'all') {
                    url += `&filter=${encodeURIComponent(filterMode)}`;
                }
                const response = await fetch(url, {
                    headers: getAuthHeaders ? getAuthHeaders() : undefined,
                    credentials: 'same-origin'
                });
                if (!response.ok) {
                    entry.error = true;
                    seriesMissing = response.status === 404;
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
                    entry.titles = {
                        title: data.title,
                        canonical_title: data.canonical_title,
                        aliases: data.aliases
                    };
                    // Refresh the cached series metadata snapshot from the
                    // response so the detail view can render correctly even
                    // when the active filter excludes this series from the
                    // library list. An answer whose cache entry was replaced
                    // (listed under an older filter or search) must not
                    // overwrite the snapshot with its counts and size.
                    if (currentSeriesDetailId === seriesId && seriesIssuesCache.get(seriesId) === entry) {
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
                                has_external_image: data.has_external_image,
                                external_image_url: data.external_image_url,
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

            const selectionPruned = pruneSeriesIssueSelection(entry);

            // Re-render only if the user is still on this series and this
            // answer has not been superseded (e.g. by a filter change).
            if (currentSeriesDetailId === seriesId && seriesIssuesCache.get(seriesId) === entry) {
                if (page === 1 || entry.error) {
                    renderSeriesDetail(seriesId);
                } else {
                    appendSeriesIssuePage(seriesId, page);
                }
            } else if (selectionPruned) {
                updateSelectInfo();
            }

            if (seriesMissing) {
                await confirmIssuesOfMissingSeries(seriesId, entry);
            }
        }

        function rememberSeriesIssuePaths(seriesId, paths, filter, titles) {
            if (!seriesId || !paths || !paths.size) return;
            let pending = seriesIssuePathsHandedOver.get(seriesId);
            if (!pending) {
                pending = { pathsByFilter: new Map(), titles: null };
            }
            const filterPaths = pending.pathsByFilter.get(filter) || new Set();
            for (const path of paths) {
                if (typeof path === 'string' && path.length > 0) {
                    filterPaths.add(path);
                }
            }
            pending.pathsByFilter.set(filter, filterPaths);
            pending.titles = titles || pending.titles;
            seriesIssuePathsHandedOver.set(seriesId, pending);
        }

        function takeHandedOverSeriesIssuePaths(seriesId, filter) {
            const pending = seriesIssuePathsHandedOver.get(seriesId);
            if (!pending) return null;
            const paths = pending.pathsByFilter.get(filter) || null;
            pending.pathsByFilter.delete(filter);
            if (!pending.pathsByFilter.size) {
                seriesIssuePathsHandedOver.delete(seriesId);
            }
            return { paths, titles: pending.titles };
        }

        function transferSeriesIssuePaths(fromSeriesId, toSeriesId) {
            const pending = seriesIssuePathsHandedOver.get(fromSeriesId);
            if (!pending) return;
            for (const [filter, paths] of pending.pathsByFilter) {
                rememberSeriesIssuePaths(toSeriesId, paths, filter, pending.titles);
            }
            seriesIssuePathsHandedOver.delete(fromSeriesId);
        }

        // Takes paths represented by the list being replaced. If its filter
        // differs, keep them pending for a reload that can safely confirm them.
        function takeSeriesIssuePathsToConfirm(cached, filter, seriesId) {
            if (!cached) return null;
            const pending = cached.unconfirmedPaths;
            cached.unconfirmedPaths = null;
            const paths = new Set();
            for (const path of pending || []) {
                if (typeof path === 'string' && path.length > 0) {
                    paths.add(path);
                }
            }
            for (const issue of cached.issues) {
                const path = issue && issue.file_path;
                if (typeof path === 'string' && path.length > 0) {
                    paths.add(path);
                }
            }
            if (cached.filter !== filter) {
                rememberSeriesIssuePaths(seriesId, paths, cached.filter, cached.titles);
                return null;
            }
            return paths.size > 0 ? paths : null;
        }

        // Hands the issue paths a list dropped by an id remap still had to
        // confirm over to the list of the series' new id: to a reload of the
        // one cached under it, or else to the one loaded for it next. The
        // dropped list is left nothing to prune, so its pending reload or 404
        // lookup cannot unselect anything.
        function handSeriesIssuePathsOver(cached, seriesId) {
            const paths = cached.unconfirmedPaths;
            cached.unconfirmedPaths = null;
            if (!paths) return;
            rememberSeriesIssuePaths(seriesId, paths, cached.filter, cached.titles);
            const replacement = seriesIssuesCache.get(seriesId);
            if (replacement) {
                // The cached list may predate the change that remapped the series.
                loadSeriesIssues(seriesId, true);
            }
        }

        // Once a reloaded issue list is complete, unselects the issues of the
        // list it replaced that it no longer has — renamed, moved or deleted
        // meanwhile — so the selection stops counting them and no batch action
        // is sent a path that is gone. Issues arrive in pages, and a path
        // missing from a partial list may be on a page still to come, so this
        // waits for the last one. Returns whether the selection changed.
        function pruneSeriesIssueSelection(entry) {
            if (!entry.unconfirmedPaths || !entry.allLoaded || entry.error) return false;
            return unselectSeriesIssuesNotListed(entry, entry.issues);
        }

        // Unselects the paths `entry` has to confirm that `issues` does not
        // list, and leaves it none to confirm. A list superseded meanwhile
        // prunes nothing: one fetched with another filter than the active one,
        // which may only hide an issue that is still there (setHeaderFilter()
        // switches the filter at once, but drops the cached list only after
        // saving the new filter), and one dropped from the cache (e.g. by that
        // filter change). One remapped to a new series id is still cached and
        // prunes as usual. Returns whether the selection changed.
        function unselectSeriesIssuesNotListed(entry, issues) {
            if (entry.filter !== filterMode || ![...seriesIssuesCache.values()].includes(entry)) {
                if (entry.unconfirmedPaths && seriesIssuesCache.get(entry.seriesId) === entry) {
                    rememberSeriesIssuePaths(entry.seriesId, entry.unconfirmedPaths, entry.filter, entry.titles);
                }
                entry.unconfirmedPaths = null;
                return false;
            }
            const listedPaths = new Set(issues
                .map(issue => issue && issue.file_path)
                .filter(path => typeof path === 'string' && path.length > 0));
            let pruned = false;
            for (const path of entry.unconfirmedPaths) {
                if (!listedPaths.has(path) && selectedFiles.delete(path)) {
                    pruned = true;
                }
            }
            entry.unconfirmedPaths = null;
            return pruned;
        }

        // A 404 means the series id no longer resolves: every issue of the
        // series was deleted or moved away, or a change elsewhere gave the
        // series a new id. So the series is looked up by its titles, the way an
        // open series follows a new id (see remapCurrentSeriesDetailId), before
        // the issues the failed list had to confirm count as gone. If it lives
        // on, its issue list decides which of them stay selected, as a reload's
        // would; if not, none do. Without an answer they stay unconfirmed, for
        // a retry or the next reload to settle.
        async function confirmIssuesOfMissingSeries(seriesId, entry) {
            const paths = entry.unconfirmedPaths;
            if (!paths || ![...paths].some(path => selectedFiles.has(path))) return;
            const getJson = async path => {
                const response = await fetch(apiUrl(path), {
                    headers: getAuthHeaders ? getAuthHeaders() : undefined,
                    credentials: 'same-origin'
                });
                if (!response.ok) throw new Error(`HTTP ${response.status}`);
                return response.json();
            };
            const titles = entry.titles || {};
            const keys = seriesTitleKeys(titles);
            // Every name the series is known by, as each may be all that
            // survives a change to the others.
            const names = new Map();
            for (const name of [titles.canonical_title, titles.title, ...(Array.isArray(titles.aliases) ? titles.aliases : [])]) {
                const key = normalizeSeriesTitleKey(name);
                if (key && !names.has(key)) names.set(key, name.trim());
            }
            let issues = [];
            try {
                let series = null;
                for (const name of names.values()) {
                    // Searched without the filter, which may hide a series
                    // that still exists.
                    const found = await getJson(`/api/files/series?per_page=-1&search=${encodeURIComponent(name)}`);
                    series = findSeriesByTitleKeys(keys, found.series || []);
                    if (series) break;
                }
                if (series) {
                    let path = `/api/files/series/${encodeURIComponent(series.id)}/issues?per_page=${SERIES_ISSUES_LOAD_ALL}&page=1`;
                    if (entry.filter !== 'all') {
                        path += `&filter=${encodeURIComponent(entry.filter)}`;
                    }
                    const data = await getJson(path);
                    issues = Array.isArray(data.issues) ? data.issues : [];
                }
            } catch (err) {
                console.warn('Could not look up a series whose id no longer resolves', err);
                return;
            }
            // A newer list has taken the paths over meanwhile.
            if (entry.unconfirmedPaths !== paths) return;
            if (!unselectSeriesIssuesNotListed(entry, issues)) return;
            if (currentSeriesDetailId === seriesId) {
                renderSeriesDetail(seriesId);
            } else {
                updateSelectInfo();
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
            // Close any open action menu first so a menu that was portaled to
            // <body> is restored before its host card is replaced below.
            closeAllDropdowns();
            renderHtml(grid, html`
                ${rawHtml(gridItems.map(renderSeriesIssueGridItemHtml).join(''))}
                ${!allLoaded ? html`<div id="seriesIssuesSentinel" class="series-issues-sentinel" aria-hidden="true"><div class="spinner spinner-small"></div></div>` : ''}
            `);
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

        // Parse a free-form issue identifier into a numeric value when it
        // represents a number, including decimals (e.g. "1", "001", "1.5").
        // Returns null for non-numeric values like "Annual" or empty strings.
        function parseNumericIssueValue(value) {
            if (value === null || value === undefined) return null;
            const text = String(value).trim();
            if (!text) return null;
            // Require the whole token to be a non-negative number (optionally
            // with a single decimal part) so "1a" or "Annual" are not mistaken
            // for issue numbers.
            if (!/^\d+(\.\d+)?$/.test(text)) return null;
            const n = parseFloat(text);
            if (!Number.isFinite(n) || n < 0) return null;
            return n;
        }

        // Build the grid items array for the series-issues-grid, interleaving
        // missing-issue placeholders in their proper position (by integer issue
        // number) so the user can visually see which issues are missing
        // alongside the issues they own. Numeric issues (including decimals such
        // as "1.5") are ordered by their numeric value so they appear in their
        // proper position. Issues without a numeric issue number (e.g. "Annual")
        // are appended at the end in their original order.
        function buildSeriesIssuesGridItems(issues) {
            const items = [];
            const numericIssues = [];
            const nonNumericIssues = [];
            for (const issue of issues || []) {
                const v = parseNumericIssueValue(issue && issue.issue);
                if (v === null) {
                    nonNumericIssues.push(issue);
                } else {
                    numericIssues.push({ value: v, issue });
                }
            }
            // Merge present numeric issues with missing-integer placeholders and
            // order everything by numeric value so decimals slot between the
            // integers they fall between (e.g. 1.5 sits between #1 and #2).
            const entries = numericIssues.map(ni => ({ value: ni.value, kind: 'issue', issue: ni.issue }));
            const missing = computeMissingIssueNumbers(issues);
            for (const n of missing) {
                entries.push({ value: n, kind: 'missing', number: n });
            }
            // Stable sort by value; on ties keep a deterministic order (missing
            // placeholders after real issues, though values never collide today).
            entries
                .map((entry, index) => ({ entry, index }))
                .sort((a, b) => (a.entry.value - b.entry.value) || (a.index - b.index))
                .forEach(({ entry }) => {
                    if (entry.kind === 'missing') {
                        items.push({ kind: 'missing', number: entry.number });
                    } else {
                        items.push({ kind: 'issue', issue: entry.issue });
                    }
                });
            // Append issues whose number is not numeric (e.g. "Annual") after the
            // numbered range so they remain visible.
            for (const issue of nonNumericIssues) {
                items.push({ kind: 'issue', issue });
            }
            return items;
        }

        // ── Suwayomi downloads ────────────────────────────────────────────
        //
        // Whether the configured Suwayomi server answered its status probe.
        // Cached so the missing-issues banner can decide synchronously whether
        // to offer the download action without a request per render.
        let suwayomiAvailable = false;
        let suwayomiStatusPromise = null;

        async function fetchSuwayomiStatus() {
            const response = await fetch(apiUrl('/api/suwayomi/status'), {
                headers: getAuthHeaders(),
                credentials: 'same-origin'
            });
            if (!response.ok) throw new Error('suwayomi status ' + response.status);
            return await response.json();
        }

        // Probes the Suwayomi server once per page load and re-renders the open
        // series detail when the answer changes, so the download action appears
        // without requiring a manual refresh.
        function refreshSuwayomiAvailability() {
            if (suwayomiStatusPromise) return suwayomiStatusPromise;
            suwayomiStatusPromise = (async () => {
                try {
                    const status = await fetchSuwayomiStatus();
                    const available = !!(status && status.enabled && status.connected);
                    if (available !== suwayomiAvailable) {
                        suwayomiAvailable = available;
                        if (currentSeriesDetailId) {
                            renderSeriesDetail(currentSeriesDetailId);
                        }
                    }
                } catch (err) {
                    console.warn('Suwayomi status check failed', err);
                    suwayomiAvailable = false;
                }
                return suwayomiAvailable;
            })();
            return suwayomiStatusPromise;
        }

        // Settings action: probe the configured server and report what it said.
        async function testSuwayomiConnection() {
            const hint = document.getElementById('suwayomiStatusHint');
            if (hint) hint.textContent = 'Testing connection...';
            try {
                const status = await fetchSuwayomiStatus();
                let text;
                if (!status.enabled) {
                    text = 'The Suwayomi integration is disabled.';
                } else if (status.connected) {
                    const name = status.server_name || status.serverName || 'Suwayomi';
                    const version = status.version ? ` ${status.version}` : '';
                    text = `Connected to ${name}${version}.`;
                } else {
                    text = status.error || 'Could not reach the Suwayomi server.';
                }
                if (hint) hint.textContent = text;
                showMessage(text, status.connected ? 'success' : 'error');
                // Reflect the new state in any open series detail.
                suwayomiStatusPromise = null;
                refreshSuwayomiAvailability();
            } catch (err) {
                const text = 'Could not check the Suwayomi server.';
                if (hint) hint.textContent = text;
                showMessage(text, 'error');
            }
        }

        // Series-detail action: ask Suwayomi to download every issue the series
        // is missing. Suwayomi writes them to its own download directory, which
        // the watcher picks up once it is visible to this service.
        async function downloadMissingIssuesFromSuwayomi(seriesId, seriesTitle) {
            let issues;
            // Every title the library knows the series by, the shown one first:
            // Suwayomi may list it under another of them, so the server falls
            // back to the rest when the shown title has no exact match.
            const titles = [];
            try {
                const response = await fetch(
                    apiUrl(`/api/files/series/${encodeURIComponent(seriesId)}/issues?per_page=${SERIES_ISSUES_LOAD_ALL}&page=1`),
                    {
                        headers: getAuthHeaders ? getAuthHeaders() : undefined,
                        credentials: 'same-origin'
                    });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    showMessage(`Could not load the full issue list (HTTP ${response.status}).`, 'error');
                    return;
                }
                const data = await response.json();
                issues = Array.isArray(data.issues) ? data.issues : [];
                const seenTitles = new Set();
                for (const title of [seriesTitle, data.title, data.canonical_title, ...(Array.isArray(data.aliases) ? data.aliases : [])]) {
                    const key = normalizeSeriesTitleKey(title);
                    if (key && !seenTitles.has(key)) {
                        seenTitles.add(key);
                        titles.push(title.trim());
                    }
                }
            } catch (err) {
                console.error('Could not load the full issue list for Suwayomi', err);
                showMessage('Could not load the full issue list for Suwayomi.', 'error');
                return;
            }

            const missing = computeMissingIssueNumbers(issues);
            if (!missing.length) {
                showMessage('This series has no missing issues.', 'info');
                return;
            }

            showMessage(`Asking Suwayomi for ${missing.length} missing issue${missing.length === 1 ? '' : 's'}...`, 'info');
            try {
                const response = await fetch(apiUrl('/api/suwayomi/download-missing'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    credentials: 'same-origin',
                    body: JSON.stringify({ seriesTitle: titles[0] || '', alternateTitles: titles.slice(1), issueNumbers: missing })
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    let errorMsg = `HTTP error! status: ${response.status}`;
                    try {
                        const errBody = await response.json();
                        if (errBody && (errBody.error || errBody.message)) {
                            errorMsg = errBody.error || errBody.message;
                        }
                    } catch (e) { /* ignore parse errors */ }
                    showMessage('Suwayomi download failed: ' + errorMsg, 'error');
                    return;
                }
                const result = await response.json();
                const queued = (result.queuedChapters || result.queued_chapters || []).length;
                const unmatched = (result.unmatchedIssueNumbers || result.unmatched_issue_numbers || []).length;
                // Name the Suwayomi series when it goes by another title, so a
                // match made through one of the other titles is visible.
                const matchedTitle = result.series && typeof result.series.title === 'string' ? result.series.title : '';
                const listedAs = matchedTitle && normalizeSeriesTitleKey(matchedTitle) !== normalizeSeriesTitleKey(titles[0])
                    ? ` (listed there as "${matchedTitle}")`
                    : '';
                let message = queued
                    ? `Queued ${queued} chapter${queued === 1 ? '' : 's'} on Suwayomi${listedAs}.`
                    : `Nothing was queued on Suwayomi${listedAs}.`;
                if (unmatched) {
                    message += ` ${unmatched} issue${unmatched === 1 ? ' has' : 's have'} no matching chapter.`;
                }
                showMessage(message, queued ? 'success' : 'info');
            } catch (err) {
                console.error('Suwayomi download failed', err);
                showMessage('Suwayomi download failed.', 'error');
            }
        }

        // Render a small banner above the issues grid summarizing any missing
        // integer issue numbers between #1 and the highest known issue number.
        function renderMissingIssuesBanner(issues, series) {
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
            // The Suwayomi action is only useful when the integration is
            // configured, so it is rendered lazily once the status probe has
            // reported a reachable server (see refreshSuwayomiAvailability).
            const seriesId = series && series.id ? series.id : '';
            const seriesTitle = series && (series.title || series.canonical_title) ? (series.title || series.canonical_title) : '';
            const action = seriesId && suwayomiAvailable
                ? `<button type="button" class="btn btn-small" data-requires-write onclick="downloadMissingIssuesFromSuwayomi('${escapeJs(seriesId)}','${escapeJs(seriesTitle)}')" title="Ask the configured Suwayomi server to download these issues">${uiIcon('download')} Download from Suwayomi</button>`
                : '';
            return `
                <div class="series-detail-missing-banner" role="status">
                    <span class="series-detail-missing-banner-icon" aria-hidden="true">⚠️</span>
                    <span class="series-detail-missing-banner-text">
                        Missing ${count} issue${count === 1 ? '' : 's'}: ${escapeHtml(label)}
                    </span>
                    ${action}
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
                                <div class="series-issue-card ${issue.read ? 'series-issue-card--read' : 'series-issue-card--unread'} ${selectedFiles.has(issue.file_path) ? 'series-issue-card--selected' : ''} ${issue.duplicate ? 'series-issue-card--duplicate' : ''}" data-file-path="${escapeHtml(issue.file_path)}">
                                    <label class="series-issue-select" aria-label="Select ${escapeHtml(issue.title || issue.file_name)}" title="Shift-click to select every issue between this one and the last one you clicked" onclick="event.stopPropagation()" onmousedown="rememberSeriesIssueRangeModifier(event)" onkeydown="rememberSeriesIssueRangeModifier(event)">
                                        <input type="checkbox"
                                               ${selectedFiles.has(issue.file_path) ? 'checked' : ''}
                                               onchange="toggleSeriesIssueSelection('${escapeJs(issue.file_path)}', this.checked)">
                                    </label>
                                    ${issue.duplicate ? `<span class="series-issue-duplicate-badge" title="Duplicate">🔁 Duplicate</span>` : ''}
                                    <span class="series-issue-read-corner series-issue-read-corner--${issue.read ? 'read' : 'unread'}" title="${issue.read ? 'Read' : 'Unread'}" aria-label="${issue.read ? 'Read' : 'Unread'}"></span>
                                    <button type="button" class="series-issue-cover-button" aria-label="Read ${escapeHtml(issue.title || issue.file_name)}" onclick="readComic('${escapeJs(issue.file_path)}')">
                                        <img class="series-issue-cover" data-protected-image="${escapeHtml(issue.file_path)}" alt="${escapeHtml(issue.file_name)} cover" loading="lazy" decoding="async" fetchpriority="low">
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
                                            ${uiIcon('more')}
                                        </button>
                                        <div class="dropdown-menu" id="${getDropdownId(issue.file_path)}">
                                            <button class="dropdown-item" onclick="showFileInfo('${escapeJs(issue.file_path)}', '${escapeJs(issue.file_name)}'); closeAllDropdowns();">
                                                ${uiIcon('info')} Info
                                            </button>
                                            <button class="dropdown-item" onclick="viewTags('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                ${uiIcon('eye')} View/Edit
                                            </button>
                                            <button class="dropdown-item" onclick="readComic('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                ${uiIcon('book-open')} Read Comic
                                            </button>
                                            <button class="dropdown-item" data-requires-write onclick="openEmailSendModalForFile('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                ${uiIcon('mail')} Email to Ereader…
                                            </button>
                                            ${issue.duplicate
                                                ? `<button class="dropdown-item" onclick="openDuplicateReviewModal('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                    ${uiIcon('copy')} Review Duplicate
                                                </button>`
                                                : ''
                                            }
                                            <div class="dropdown-divider"></div>
                                            ${issue.read
                                                ? `<button class="dropdown-item" onclick="markFileUnread('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                    ${uiIcon('library')} Mark Unread
                                                </button>`
                                                : `<button class="dropdown-item" onclick="markFileRead('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                    ${uiIcon('check-circle')} Mark Read
                                                </button>`
                                            }
                                            <div class="dropdown-divider" data-requires-write></div>
                                            <button class="dropdown-item" data-requires-write onclick="processSingleFile('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                ${uiIcon('play')} Process
                                            </button>
                                            <button class="dropdown-item" data-requires-write onclick="renameSingleFile('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                ${uiIcon('pencil')} Rename
                                            </button>
                                            <button class="dropdown-item" data-requires-write onclick="normalizeSingleFile('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                ${uiIcon('tag')} Normalize
                                            </button>
                                            <button class="dropdown-item" data-requires-write onclick="removeFileMetadata('${escapeJs(issue.file_path)}'); closeAllDropdowns();" title="Open the archive, delete the embedded ComicInfo.xml, and mark the file as unprocessed so it can be re-processed.">
                                                ${uiIcon('eraser')} Remove Metadata
                                            </button>
                                            <div class="dropdown-divider" data-requires-write></div>
                                            <button class="dropdown-item" data-requires-write onclick="deleteSingleFile('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                ${uiIcon('trash')} Delete
                                            </button>
                                        </div>
                                    </div>
                                </div>`;
        }

        // Renders the external-metadata synopsis block shown at the top of the
        // series page. Long synopses are clamped with a Show more / Show less
        // toggle. Returns an empty string when the series has no synopsis.
        function renderSeriesSynopsis(series) {
            const synopsis = series && typeof series.synopsis === 'string' ? series.synopsis.trim() : '';
            if (!synopsis) return '';
            const isLong = synopsis.length > 320;
            return `
                <div class="series-detail-synopsis${isLong ? ' series-detail-synopsis--clamped' : ''}" data-synopsis-block>
                    <p class="series-detail-synopsis-text">${escapeHtml(synopsis)}</p>
                    ${isLong ? `<button type="button" class="series-detail-synopsis-toggle" onclick="toggleSeriesSynopsis(this)">Show more</button>` : ''}
                </div>
            `;
        }

        // Expands/collapses a clamped synopsis block.
        function toggleSeriesSynopsis(btn) {
            const block = btn.closest('[data-synopsis-block]');
            if (!block) return;
            const expanded = block.classList.toggle('series-detail-synopsis--expanded');
            block.classList.toggle('series-detail-synopsis--clamped', !expanded);
            btn.textContent = expanded ? 'Show less' : 'Show more';
        }

        function renderSeriesDetail(seriesId) {
            const fileList = document.getElementById('fileList');
            // Restore/close any open action menu (which may be portaled to
            // <body>) before the panel's DOM is rebuilt below.
            closeAllDropdowns();
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

            // Every Series Actions item needs write access, so read-only users
            // don't get the menu at all: data-requires-write is on the dropdown
            // and, like the issue menus, on each item (an open menu is moved
            // to <body>, out of the dropdown).
            renderHtml(fileList, html`
                <div class="series-detail" id="seriesDetailPanel">
                    <div class="series-detail-header">
                        <button type="button" class="btn btn-small series-detail-back" onclick="closeSeriesDetail()">${uiIcon('arrow-left')} Back to Series</button>
                        <div class="series-detail-summary">
                            <img class="series-detail-cover" data-protected-image="${series.has_external_image && series.external_image_url ? series.external_image_url : series.cover_file_path}" data-protected-image-fallback="${series.has_external_image && series.external_image_url ? series.cover_file_path : ''}" alt="${series.title} cover" loading="lazy" decoding="async" fetchpriority="low">
                            <div class="series-detail-summary-body">
                                <h2>${series.title} ${rawHtml(renderLookupStatusBadge(series))}</h2>
                                <div class="series-detail-meta">${issueCount} issue${issueCount === 1 ? '' : 's'} · ${formatFileSize(series.total_size)}</div>
                                ${series.aliases?.length ? html`<div class="series-detail-meta">Also known as: ${series.aliases.join(', ')}</div>` : ''}
                                ${series.metadata_source ? html`<div class="series-detail-meta">Source: ${series.metadata_source}${series.last_lookup_utc ? ` · ${new Date(series.last_lookup_utc).toLocaleString()}` : ''}</div>` : ''}
                                ${rawHtml(renderSeriesSynopsis(series))}
                                <div class="series-detail-actions">
                                    ${issues.length ? html`<button type="button" class="btn btn-small btn-primary" onclick="readComic('${jsArg(issues[0].file_path)}')">${uiIcon('book-open')} Read First Issue</button>` : ''}
                                    <div class="file-actions-dropdown series-actions-dropdown" data-requires-write>
                                        <button type="button" class="dropdown-toggle" onclick="toggleDropdown(event, '${jsArg(SERIES_ACTIONS_DROPDOWN_KEY)}')">
                                            ${uiIcon('settings')} Actions
                                        </button>
                                        <div class="dropdown-menu" id="${getDropdownId(SERIES_ACTIONS_DROPDOWN_KEY)}">
                                            <button class="dropdown-item" data-requires-write onclick="openManageSeriesNamesModal('${jsArg(series.title)}'); closeAllDropdowns();">
                                                ${uiIcon('tag')} Manage Names
                                            </button>
                                            <button class="dropdown-item" data-requires-write onclick="openSeriesFoldersModal('${jsArg(series.id)}','${jsArg(series.title)}'); closeAllDropdowns();" title="See the on-disk folders contributing to this series and merge them into one">
                                                ${uiIcon('folder')} Manage Folders
                                            </button>
                                            <div class="dropdown-divider" data-requires-write></div>
                                            <button class="dropdown-item" data-requires-write onclick="refreshSeriesMetadataDirect('${jsArg(series.title)}'); closeAllDropdowns();">
                                                ${uiIcon('globe')} Refresh Metadata
                                            </button>
                                            <button class="dropdown-item" data-requires-write onclick="refreshSeriesFolder('${jsArg(series.id)}','${jsArg(series.title)}'); closeAllDropdowns();" title="Refresh metadata for every folder/alias that groups under this series">
                                                ${uiIcon('folder')} Refresh Folder
                                            </button>
                                            <button class="dropdown-item" data-requires-write onclick="updateSeriesCoversDirect('${jsArg(series.title)}'); closeAllDropdowns();" title="Re-apply the current cached series image to this series' cover sidecars and first-issue archive. This may rewrite the archive file.">
                                                ${uiIcon('image')} Update Covers
                                            </button>
                                            <button class="dropdown-item" data-requires-write onclick="openEmailSendModalForSeries('${jsArg(series.id)}','${jsArg(series.title)}'); closeAllDropdowns();">
                                                ${uiIcon('mail')} Email All Issues
                                            </button>
                                            <button class="dropdown-item" data-requires-write onclick="openEmailSubscriptionModal('${jsArg(series.title)}'); closeAllDropdowns();">
                                                ${uiIcon('copy')} Auto-send New Issues
                                            </button>
                                            <div class="dropdown-divider" data-requires-write></div>
                                            <button class="dropdown-item" data-requires-write onclick="resetSeriesProcessedStatus('${jsArg(series.id)}','${jsArg(series.title)}'); closeAllDropdowns();" title="Clear the renamed/normalized flags on every file in this series so they will be re-processed on the next Process / Rename / Normalize run. Use this if a metadata or filename change is not being applied.">
                                                ${uiIcon('rotate-ccw')} Reset Processed Status
                                            </button>
                                        </div>
                                    </div>
                                </div>
                            </div>
                        </div>
                    </div>
                    ${!issuesLoading && !issuesFailed && issues.length ? html`
                        <div class="series-detail-selection-bar">
                            <label class="series-detail-select-all" for="selectAll">
                                <input type="checkbox"
                                       id="selectAll"
                                       onchange="toggleSelectAll(this.checked)"
                                       ${allIssuesSelected ? rawHtml('checked') : ''}>
                                <span>Select all issues</span>
                            </label>
                            <span class="series-detail-selection-meta">${issueCount} issue${issueCount === 1 ? '' : 's'} in this series${!allLoaded ? ` · showing ${issues.length}` : ''}</span>
                            <button type="button" class="btn btn-small" data-requires-write onclick="openEmailSendModalForSelected()">${uiIcon('mail')} Email Selected</button>
                        </div>
                        ${rawHtml(renderMissingIssuesBanner(issues, series))}
                    ` : ''}
                    ${issuesLoading ? html`
                        <div class="loading">
                            <div class="spinner"></div>
                            <p>Loading issues...</p>
                        </div>
                    ` : issuesFailed ? html`
                        <div class="empty-state"><p>Failed to load issues. <button type="button" class="btn btn-small" onclick="loadSeriesIssues('${jsArg(seriesId)}', true)">Retry</button></p></div>
                    ` : issues.length === 0 ? html`
                        <div class="empty-state">
                            <p>${filterMode !== 'all' || searchQuery ? 'No issues in this series match the current filter.' : 'No issues in this series.'}</p>
                            ${filterMode !== 'all' ? html`<button type="button" class="btn btn-small" onclick="setHeaderFilter('all')">Clear filter</button>` : ''}
                        </div>
                    ` : html`
                        <div class="series-issues-grid">
                            ${rawHtml(gridItems.map(renderSeriesIssueGridItemHtml).join(''))}
                            ${!allLoaded ? html`<div id="seriesIssuesSentinel" class="series-issues-sentinel" aria-hidden="true"><div class="spinner spinner-small"></div></div>` : ''}
                        </div>
                    `}
                </div>
            `);

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
