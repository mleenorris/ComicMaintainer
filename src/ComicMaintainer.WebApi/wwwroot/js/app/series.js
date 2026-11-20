        // ComicMaintainer SPA — series detail page: issues, missing issues, Suwayomi downloads.
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

        // Shared state declared in state.js and reassigned here.
        /* global libraryScrollBeforeSeries: true */

        function openSeriesDetail(seriesId) {
            // Remember where we came from so Back returns to the right place,
            // then navigate to the series route (the router renders the detail).
            seriesDetailOrigin = (currentView === 'home') ? 'home' : 'library';
            if (seriesDetailOrigin === 'library') {
                // The series replaces the list; Back renders the list again
                // and returns to this place in it.
                const fileList = document.getElementById('fileList');
                libraryScrollBeforeSeries = { listTop: fileList ? fileList.scrollTop : 0, pageTop: window.scrollY };
            }
            navigate('#/series/' + encodeURIComponent(seriesId));
        }

        function closeSeriesDetail() {
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
        // Series ids the server answered under a newer id (former id → current
        // id), as the issues of a former id are (see fetchSeriesIssues).
        const movedSeriesIds = new Map();

        // The id a series has now, following its moves (a refresh queued
        // before one still names the former id).
        function seriesIdNow(seriesId) {
            let id = seriesId;
            for (let hops = 0; movedSeriesIds.has(id) && hops < 10; hops++) {
                id = movedSeriesIds.get(id);
            }
            return id;
        }

        function remapCurrentSeriesDetailId() {
            if (!currentSeriesDetailId) return null;
            const remapped = findSeriesByTitleKeys(currentSeriesDetailTitleKeys);
            if (!remapped || remapped.id === currentSeriesDetailId) return null;
            // The server said the listed id moved to the open one (see
            // fetchSeriesIssues): the list is older than that answer.
            if (movedSeriesIds.get(remapped.id) === currentSeriesDetailId) return null;
            // Migrate cached issues to the new id so we don't refetch when
            // the underlying content hasn't changed. Drop the old key. Never
            // migrate error entries — a 404/failure cached under the stale id
            // (e.g. from a race during a filter change) must not poison the
            // remapped id; leaving the new key empty lets renderSeriesDetail
            // trigger a fresh fetch. Nor entries still loading: their answer
            // re-renders only the stale id, leaving the new one loading forever.
            const oldCached = seriesIssuesCache.get(currentSeriesDetailId);
            // Paths still pending under the old id move to the new one first,
            // so that a reload of the new id's list below confirms them too.
            transferSeriesIssuePaths(currentSeriesDetailId, remapped.id);
            if (oldCached && !oldCached.error && !oldCached.loading
                && !seriesIssuesCache.has(remapped.id)) {
                seriesIssuesCache.set(remapped.id, oldCached);
                oldCached.seriesId = remapped.id;
            } else if (oldCached) {
                // The issues of the dropped list, and those it still had to
                // confirm (e.g. while its reload or 404 lookup is pending),
                // are confirmed by the list of the new id instead.
                handSeriesIssuePathsOver(oldCached, remapped.id);
            }
            seriesIssuesCache.delete(currentSeriesDetailId);
            currentSeriesDetailId = remapped.id;
            return remapped;
        }

        // The entire issue list of a series is loaded in a single request
        // (per_page=-1 instructs the API to return all issues at once), so
        // the client does no paging.
        const SERIES_ISSUES_LOAD_ALL = -1;

        // Forced reloads of a list that is already shown come in bursts: a
        // batch job broadcasts file_processed for every file it touches,
        // anywhere in the library, and with a series open each of those asks
        // for the series' whole issue list again. So such a refresh starts at
        // once only when no request for the list is in flight and the last
        // refresh has cooled down; otherwise it is folded into a single
        // trailing reload that starts as soon as both hold. Everyone folded
        // into it waits for that reload, which starts after their call and so
        // sees whatever change prompted it. The cool-down grows with how long
        // the last reload took (a big series costs more to list and render),
        // so a long job keeps the list current while spending no more than
        // about a tenth of its time reloading it.
        //
        // The cool-down is there for the burst, not for someone waiting on the
        // result: the reload after a user's action (`immediate`) and the one
        // that shows the end of the job the page is following skip it. They
        // still wait for a request in flight, whose answer predates them.
        const SERIES_ISSUES_REFRESH_MIN_COOLDOWN_MS = 1000;
        const SERIES_ISSUES_REFRESH_MAX_COOLDOWN_MS = 10000;
        const SERIES_ISSUES_REFRESH_LOAD_FACTOR = 10;
        // Per series id: whether a refresh is running or cooling down (and the
        // timer ending the cool-down), the trailing refresh queued meanwhile
        // ({ promise, resolve, urgent }), if any, and whether the last refresh
        // was asked for during the page's job.
        const seriesIssuesRefreshes = new Map();

        async function loadSeriesIssues(seriesId, force = false, { immediate = false } = {}) {
            if (!seriesId) return;
            const cached = seriesIssuesCache.get(seriesId);
            // A forced reload of the list already shown (or being loaded) for
            // the active filter is a refresh; anything else is a first load,
            // a retry or a filter change, which must not wait.
            if (force && cached && !cached.error && cached.filter === filterMode
                && (cached.loaded || cached.loading)) {
                return refreshSeriesIssues(seriesId, immediate);
            }
            return fetchSeriesIssues(seriesId, force);
        }

        function refreshSeriesIssues(seriesId, immediate) {
            let state = seriesIssuesRefreshes.get(seriesId);
            if (!state) {
                state = { busy: false, cooldownTimer: null, trailing: null, duringJob: false };
                seriesIssuesRefreshes.set(seriesId, state);
            }
            // events.js reloads the same way per processed file and when the
            // job ends, but by then it has cleared hasActiveJob.
            const jobEnded = state.duringJob && !hasActiveJob;
            state.duringJob = hasActiveJob;
            const urgent = immediate || jobEnded;
            if (urgent && state.cooldownTimer !== null) {
                clearTimeout(state.cooldownTimer);
                state.cooldownTimer = null;
                state.busy = false;
            }
            if (state.trailing) {
                const { promise } = state.trailing;
                if (urgent) {
                    state.trailing.urgent = true;
                    startTrailingSeriesIssuesRefresh(seriesId, state);
                }
                return promise;
            }
            const cached = seriesIssuesCache.get(seriesId);
            const loading = Boolean(cached && cached.loading);
            if (!state.busy && !loading) {
                return startSeriesIssuesRefresh(seriesId, state);
            }
            let resolve;
            const promise = new Promise(done => { resolve = done; });
            state.trailing = { promise, resolve, urgent };
            if (loading) {
                // The answer in flight predates this call; see fetchSeriesIssues.
                cached.stale = true;
            }
            if (!state.busy) {
                // Another load of the list (opening the series, a filter
                // change) is in flight: go once it has answered.
                Promise.resolve(cached.settled).then(() => startTrailingSeriesIssuesRefresh(seriesId, state));
            }
            return promise;
        }

        // Starts the queued refresh, if any, unless something still holds it back.
        function startTrailingSeriesIssuesRefresh(seriesId, state) {
            if (!state.trailing || state.busy) return;
            seriesId = seriesIdNow(seriesId);
            const cached = seriesIssuesCache.get(seriesId);
            if (cached && cached.loading) {
                Promise.resolve(cached.settled).then(() => startTrailingSeriesIssuesRefresh(seriesId, state));
                return;
            }
            const { resolve } = state.trailing;
            state.trailing = null;
            startSeriesIssuesRefresh(seriesId, state).then(resolve, resolve);
        }

        async function startSeriesIssuesRefresh(seriesId, state) {
            seriesId = seriesIdNow(seriesId);
            state.busy = true;
            const startedAt = Date.now();
            try {
                await fetchSeriesIssues(seriesId, true);
            } finally {
                const cooldown = state.trailing && state.trailing.urgent ? 0 : Math.min(
                    SERIES_ISSUES_REFRESH_MAX_COOLDOWN_MS,
                    Math.max(SERIES_ISSUES_REFRESH_MIN_COOLDOWN_MS, (Date.now() - startedAt) * SERIES_ISSUES_REFRESH_LOAD_FACTOR));
                state.cooldownTimer = setTimeout(() => {
                    state.cooldownTimer = null;
                    state.busy = false;
                    startTrailingSeriesIssuesRefresh(seriesId, state);
                }, cooldown);
            }
        }

        async function fetchSeriesIssues(seriesId, force) {
            // The list that a forced reload, or a retry after a failure, replaces.
            const previous = seriesIssuesCache.get(seriesId);
            // An entry listed (or being listed) under another filter is stale.
            if (force || (previous && previous.filter !== filterMode)) {
                seriesIssuesCache.delete(seriesId);
            }
            const existing = seriesIssuesCache.get(seriesId);
            // Already loaded and not forced — render and exit.
            if (!force && existing && !existing.error && existing.issues.length > 0) {
                renderSeriesDetail(seriesId);
                return;
            }
            // Already loaded (with no issues), or another request is loading it.
            if (existing && !existing.error && (existing.loaded || existing.loading)) {
                return;
            }

            // Prime the cache entry so concurrent callers don't fan out. A
            // failed entry is replaced, so a retry starts from scratch. The
            // entry records the filter it is listed under.
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
                    allLoaded: false,
                    loaded: false,
                    loading: false,
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
            entry.loading = true;
            // Settles when this request does, for a refresh queued meanwhile.
            let settle;
            entry.settled = new Promise(resolve => { settle = resolve; });

            let seriesMissing = false;
            let movedTo = null;
            try {
                let path = `/api/files/series/${encodeURIComponent(seriesId)}/issues?per_page=${SERIES_ISSUES_LOAD_ALL}`;
                if (filterMode !== 'all') {
                    path += `&filter=${encodeURIComponent(filterMode)}`;
                }
                const response = await apiFetch(path, { json: false });
                if (!response.ok) {
                    entry.error = true;
                    seriesMissing = response.status === 404;
                } else {
                    const data = await response.json();
                    entry.issues = Array.isArray(data.issues) ? data.issues : [];
                    // A list that replaced another may have changed (a job filled its issues):
                    // read their story arcs again.
                    if (previous) {
                        seriesStoryArcsCache.delete(seriesId);
                        seriesVolumeLinksCache.delete(seriesId);
                        seriesComicVineLinksCache.delete(seriesId);
                    }
                    entry.total = typeof data.issue_count === 'number'
                        ? data.issue_count
                        : entry.issues.length;
                    entry.loaded = true;
                    entry.allLoaded = true;
                    entry.error = false;
                    // A filter can shift the id of what it shows (fewer files, another
                    // representative): only an unfiltered answer says the series moved.
                    if (entry.filter === 'all' && typeof data.id === 'string' && data.id && data.id !== seriesId) {
                        movedTo = data.id;
                    }
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
                                total_size: data.total_size,
                                synopsis: data.synopsis,
                                genres: data.genres,
                                publication_status: data.publication_status,
                                site_url: data.site_url
                            }
                        );
                    }
                }
            } catch (err) {
                console.error('loadSeriesIssues failed', err);
                entry.error = true;
            } finally {
                entry.loading = false;
                settle();
            }

            // A list that a queued refresh will replace was taken before the
            // change that prompted it (e.g. while a file was being moved), so
            // the newer list decides which selected issues are gone: this one
            // passes the issues it had to confirm on to it.
            const selectionPruned = entry.stale ? false : pruneSeriesIssueSelection(entry);

            // The series answered under another id: processing renamed and
            // regrouped its files (tagged with their ComicVine title), and the
            // server found it by its former name. Open it under its id now, so
            // the address and every later request use it; the list just read
            // is kept for it (as remapCurrentSeriesDetailId moves a list).
            const listedAtNewId = movedTo ? seriesIssuesCache.get(movedTo) : null;
            if (movedTo && currentSeriesDetailId === seriesId && seriesIssuesCache.get(seriesId) === entry
                && !(listedAtNewId && listedAtNewId.loading)) {
                if (listedAtNewId) {
                    // An older list of the new id (opened before, or merged
                    // into it) gives way; what it still had to confirm is
                    // confirmed by this one.
                    handSeriesIssuePathsOver(listedAtNewId, movedTo);
                }
                transferSeriesIssuePaths(seriesId, movedTo);
                // Refreshes of the list, queued or cooling down, go on under the new id.
                const refreshes = seriesIssuesRefreshes.get(seriesId);
                if (refreshes) {
                    seriesIssuesRefreshes.delete(seriesId);
                    seriesIssuesRefreshes.set(movedTo, refreshes);
                }
                // A library list read before the move still has the series
                // under its former id: it must not be remapped back to it.
                movedSeriesIds.set(seriesId, movedTo);
                seriesIssuesCache.set(movedTo, entry);
                entry.seriesId = movedTo;
                seriesIssuesCache.delete(seriesId);
                currentSeriesDetailId = movedTo;
                history.replaceState(history.state, '', '#/series/' + encodeURIComponent(movedTo));
                seriesId = movedTo;
            }

            // Re-render only if the user is still on this series and this
            // answer has not been superseded (e.g. by a filter change).
            if (currentSeriesDetailId === seriesId && seriesIssuesCache.get(seriesId) === entry) {
                renderSeriesDetail(seriesId);
                loadSeriesFolderHint(seriesId);
            } else if (selectionPruned) {
                updateSelectInfo();
            }

            if (seriesMissing && !entry.stale) {
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

        // Takes the issue paths a cached list stands for: the issues it lists,
        // plus any that an unfinished reload of it still has to confirm. Every
        // path is taken, selected or not: the old cards stay clickable while
        // the reload is pending, so whether a path is selected is only checked
        // when pruning. Taking them leaves the list nothing to prune, so a
        // stale response landing after a newer one cannot unselect anything.
        function takeSeriesIssuePaths(cached) {
            const paths = new Set();
            for (const path of cached.unconfirmedPaths || []) {
                if (typeof path === 'string' && path.length > 0) {
                    paths.add(path);
                }
            }
            cached.unconfirmedPaths = null;
            for (const issue of cached.issues) {
                const path = issue && issue.file_path;
                if (typeof path === 'string' && path.length > 0) {
                    paths.add(path);
                }
            }
            return paths;
        }

        // Takes paths represented by the list being replaced. If its filter
        // differs, keep them pending for a reload that can safely confirm them.
        function takeSeriesIssuePathsToConfirm(cached, filter, seriesId) {
            if (!cached) return null;
            const paths = takeSeriesIssuePaths(cached);
            if (cached.filter !== filter) {
                rememberSeriesIssuePaths(seriesId, paths, cached.filter, cached.titles);
                return null;
            }
            return paths.size > 0 ? paths : null;
        }

        // Hands the issue paths of a list dropped by an id remap over to the
        // list of the series' new id: to a reload of the one cached under it,
        // or else to the one loaded for it next. The dropped list is left
        // nothing to prune, so its pending reload or 404 lookup cannot
        // unselect anything.
        function handSeriesIssuePathsOver(cached, seriesId) {
            const paths = takeSeriesIssuePaths(cached);
            if (!paths.size) return;
            rememberSeriesIssuePaths(seriesId, paths, cached.filter, cached.titles);
            if (seriesIssuesCache.has(seriesId)) {
                // The cached list may predate the change that remapped the series.
                loadSeriesIssues(seriesId, true);
            }
        }

        // Once a reloaded issue list is complete, unselects the issues of the
        // list it replaced that it no longer has — renamed, moved or deleted
        // meanwhile — so the selection stops counting them and no batch action
        // is sent a path that is gone. Returns whether the selection changed.
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
                const response = await apiFetch(path, { json: false });
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
                    let path = `/api/files/series/${encodeURIComponent(series.id)}/issues?per_page=${SERIES_ISSUES_LOAD_ALL}`;
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

        // A run whose numbers reach above this is not checked for gaps: its
        // numbers are dates or typos ("20240115"), not the length of a run.
        // The Missing Issues Check job (MissingIssuesCheckJobHandler) uses the
        // same limit.
        const MAX_MISSING_ISSUE_NUMBER = 100000;
        // A run of missing issues longer than this is shown as one card for
        // the whole range instead of a card per issue.
        const MAX_MISSING_PLACEHOLDERS_PER_RUN = 12;
        // Two issue numbers further apart than this, or this many per distinct
        // number in the run when that is more, are in different clusters (see
        // highestCountedIssueNumber).
        const MIN_OUTLIER_JUMP = 1000;
        const OUTLIER_JUMP_PER_ISSUE = 5;

        // The highest issue number gaps are counted up to, from a run's
        // distinct numbers in ascending order. A number far beyond the rest is
        // not where the run ends (Nightwing #1000000 in a series of about 160
        // issues, or an issue numbered by its year among issues below 200).
        // The numbers are split into clusters wherever one is more than the
        // outlier jump above the one before it. The cluster with the most
        // numbers (the lowest of equals) is the run, and gaps count from #1 up
        // to its highest number; clusters above it leave no gap before them.
        // The server uses the same rule (IssueNumberGaps).
        function highestCountedIssueNumber(sorted) {
            const jump = Math.max(MIN_OUTLIER_JUMP, OUTLIER_JUMP_PER_ISSUE * sorted.length);
            let start = 0;
            let largest = 0;
            let highest = sorted[0];
            for (let i = 1; i <= sorted.length; i++) {
                if (i < sorted.length && sorted[i] - sorted[i - 1] <= jump) continue;
                if (i - start > largest) {
                    largest = i - start;
                    highest = sorted[i - 1];
                }
                start = i;
            }
            return highest;
        }

        // Given the issues array returned by the API, identify the runs of
        // integer issue numbers missing between 1 and the highest counted
        // integer issue number (see highestCountedIssueNumber). Returns sorted
        // [start, end] pairs (inclusive). Works from the numbers present, so a
        // large highest number costs nothing.
        function computeMissingIssueRanges(issues) {
            if (!Array.isArray(issues) || !issues.length) return [];
            const present = new Set();
            for (const issue of issues) {
                const n = parseIntegerIssueNumber(issue && issue.issue);
                if (n === null || n < 1) continue;
                present.add(n);
            }
            if (!present.size) return [];
            const sorted = [...present].sort((a, b) => a - b);
            const highest = highestCountedIssueNumber(sorted);
            if (highest > MAX_MISSING_ISSUE_NUMBER) return [];
            const ranges = [];
            let next = 1;
            for (const n of sorted) {
                if (n > highest) break;
                if (n > next) ranges.push([next, n - 1]);
                next = n + 1;
            }
            return ranges;
        }

        // The missing integer issue numbers, one by one, in order.
        function computeMissingIssueNumbers(issues) {
            const missing = [];
            for (const [start, end] of computeMissingIssueRanges(issues)) {
                for (let n = start; n <= end; n++) missing.push(n);
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

        // A series' issues split into runs that are numbered on their own, as
        // on the server (VolumeNumbering), which already orders the issues run
        // by run. Each run gets its own heading and its own missing issues:
        //  - Annuals, Specials and Giant-Size issues (issue.format) are always runs of their own,
        //    after the regular issues: Annual #1 is not issue #1.
        //  - American comics restart their numbering with each volume ("Green
        //    Lantern" #1 of 1990 and #1 of 2005): when volumes (volume_key)
        //    share an issue number, each volume is a run. Volumes that continue
        //    one numbering (a manga's tankōbon volumes) stay one run.
        // Returns [{ label, issues }], or null for one run.
        function groupIssueRuns(issues) {
            const list = issues || [];
            const byKind = new Map();
            for (const issue of list) {
                const kind = issueKind(issue);
                if (!byKind.has(kind)) byKind.set(kind, []);
                byKind.get(kind).push(issue);
            }
            const regular = byKind.get('') || [];
            const regularRuns = splitRunByRestartingVolume(regular, '');
            if (byKind.size === 1 && byKind.has('')) return regularRuns;
            const runs = regular.length ? (regularRuns || [wholeRun(regular, '')]) : [];
            const kinds = [...byKind.keys()].filter(Boolean).sort((a, b) => kindOrder(a) - kindOrder(b) || a.localeCompare(b));
            for (const kind of kinds) {
                const kindIssues = byKind.get(kind);
                runs.push(...(splitRunByRestartingVolume(kindIssues, kind) || [wholeRun(kindIssues, kind)]));
            }
            return runs;
        }

        // The kind of issue with a numbering of its own ("Annual", "Special",
        // "Giant-Size", "One-Shot"), or '' for a regular issue.
        function issueKind(issue) {
            if (!issue) return '';
            return issue.format || (issue.annual ? 'Annual' : '');
        }

        // Annuals, then Specials, then Giant-Size issues, then One-Shots, as on
        // the server (IssueFormat.Kinds); any other kind after them.
        const ISSUE_KINDS = ['Annual', 'Special', 'Giant-Size', 'One-Shot'];
        function kindOrder(kind) {
            const index = ISSUE_KINDS.indexOf(kind);
            return index === -1 ? ISSUE_KINDS.length : index;
        }

        // A run's heading: its volume and kind ("1990", "1990 Annual",
        // "Special"), or `fallback` for regular issues of no known volume.
        function runLabel(volume, kind, fallback) {
            if (!kind) return volume || fallback;
            return volume ? `${volume} ${kind}` : kind;
        }

        // A run named after its issues' volume when they all share one ("1990",
        // "1990 Annual"); otherwise "Issues", "Annual" or "Special".
        function wholeRun(issues, kind) {
            const labels = new Set(issues.map(issue => (issue && issue.volume_label) || ''));
            const volume = labels.size === 1 ? [...labels][0] : '';
            return { label: runLabel(volume, kind, 'Issues'), issues };
        }

        // The issues by volume when their numbering restarts by volume, or null.
        function splitRunByRestartingVolume(issues, kind) {
            const groups = new Map();
            for (const issue of issues) {
                const key = (issue && issue.volume_key) || '';
                if (!groups.has(key)) {
                    groups.set(key, { key, volume: (issue && issue.volume_label) || '', issues: [] });
                }
                groups.get(key).issues.push(issue);
            }
            const known = [...groups.values()].filter(group => group.key);
            if (known.length < 2) return null;
            const seen = new Set();
            let restarts = false;
            for (const group of known) {
                const numbers = new Set();
                for (const issue of group.issues) {
                    const value = parseNumericIssueValue(issue.issue);
                    if (value !== null) numbers.add(Math.floor(value));
                }
                for (const n of numbers) {
                    if (seen.has(n)) restarts = true;
                    seen.add(n);
                }
            }
            if (!restarts) return null;
            return [...groups.values()].map(group => ({
                label: runLabel(group.volume, kind, 'No volume'),
                issues: group.issues
            }));
        }

        // The grid items for the series-issues-grid: one run of issues, or, for
        // a series split into runs, a heading and the issues of each run (see
        // groupIssueRuns).
        function buildSeriesIssuesGridItems(issues) {
            const runs = groupIssueRuns(issues);
            if (!runs) return buildIssueRunGridItems(issues);
            return runs.flatMap(run => [
                { kind: 'volume', label: run.label, count: run.issues.length },
                ...buildIssueRunGridItems(run.issues)
            ]);
        }

        // Build the grid items for one run of issues, interleaving
        // missing-issue placeholders in their proper position (by integer issue
        // number) so the user can visually see which issues are missing
        // alongside the issues they own. Numeric issues (including decimals such
        // as "1.5") are ordered by their numeric value so they appear in their
        // proper position. Issues without a numeric issue number (e.g. "Annual")
        // are appended at the end in their original order.
        function buildIssueRunGridItems(issues) {
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
            for (const [start, end] of computeMissingIssueRanges(issues)) {
                if (end - start + 1 > MAX_MISSING_PLACEHOLDERS_PER_RUN) {
                    entries.push({ value: start, kind: 'missing', number: start, end });
                    continue;
                }
                for (let n = start; n <= end; n++) {
                    entries.push({ value: n, kind: 'missing', number: n });
                }
            }
            // Stable sort by value; on ties keep a deterministic order (missing
            // placeholders after real issues, though values never collide today).
            entries
                .map((entry, index) => ({ entry, index }))
                .sort((a, b) => (a.entry.value - b.entry.value) || (a.index - b.index))
                .forEach(({ entry }) => {
                    if (entry.kind === 'missing') {
                        items.push(entry.end === undefined
                            ? { kind: 'missing', number: entry.number }
                            : { kind: 'missing', number: entry.number, end: entry.end });
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
            const response = await apiFetch('/api/suwayomi/status', { json: false });
            if (!response.ok) throw new Error('suwayomi status ' + response.status);
            return await response.json();
        }

        // Probes the Suwayomi server once per page load and, when the answer
        // changes, re-renders the open series detail and shows or hides the
        // "Find on Suwayomi" actions, so they appear without a manual refresh.
        function refreshSuwayomiAvailability() {
            if (suwayomiStatusPromise) return suwayomiStatusPromise;
            suwayomiStatusPromise = (async () => {
                let available = false;
                try {
                    const status = await fetchSuwayomiStatus();
                    available = !!(status && status.enabled && status.connected);
                } catch (err) {
                    console.warn('Suwayomi status check failed', err);
                }
                if (available !== suwayomiAvailable) {
                    suwayomiAvailable = available;
                    if (currentSeriesDetailId) {
                        renderSeriesDetail(currentSeriesDetailId);
                    }
                    updateSuwayomiSearchEntryPoints();
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
                const response = await apiFetch(`/api/files/series/${encodeURIComponent(seriesId)}/issues?per_page=${SERIES_ISSUES_LOAD_ALL}&page=1`, { json: false });
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
                const response = await apiFetch('/api/suwayomi/download-missing', { method: 'POST', body: { seriesTitle: titles[0] || '', alternateTitles: titles.slice(1), issueNumbers: missing }, json: false });
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
                const failures = result.discoveryFailures || result.discovery_failures || [];
                const cutShort = result.cutShortSearches || result.cut_short_searches || [];
                // Name the Suwayomi series when it goes by another title, so a
                // match made through one of the other titles is visible.
                const matchedTitle = result.series && typeof result.series.title === 'string' ? result.series.title : '';
                const matchedSource = result.series && typeof result.series.source === 'string' ? result.series.source : '';
                const listedAs = matchedTitle && normalizeSeriesTitleKey(matchedTitle) !== normalizeSeriesTitleKey(titles[0])
                    ? `"${matchedTitle}"` : '';
                const firstMatch = listedAs || matchedSource
                    ? ` (first match: ${listedAs}${listedAs && matchedSource ? ' on ' : ''}${matchedSource})`
                    : '';
                let message = queued
                    ? `Queued ${queued} chapter${queued === 1 ? '' : 's'} on Suwayomi${firstMatch}.`
                    : `Nothing was queued on Suwayomi${firstMatch}.`;
                if (unmatched) {
                    message += ` ${unmatched} issue${unmatched === 1 ? ' has' : 's have'} no matching chapter.`;
                }
                if (failures.length) {
                    const first = failures[0] || {};
                    const detail = first.target && first.error ? ` First failure: ${first.target}: ${first.error}` : '';
                    message += ` ${failures.length} source check${failures.length === 1 ? '' : 's'} failed.${detail}`;
                }
                if (cutShort.length) {
                    message += ` ${cutShort.length} search${cutShort.length === 1 ? ' was' : 'es were'} cut short, so a series further down the results may have been missed.`;
                }
                showMessage(message, queued ? 'success' : failures.length ? 'error' : 'info');
            } catch (err) {
                console.error('Suwayomi download failed', err);
                showMessage('Suwayomi download failed.', 'error');
            }
        }

        // The folders of each series that the folder-combine service lists as
        // one group, by series id: a series split over "Nightwing" and
        // "Nightwing (2016-)". Filled by loadSeriesFolderHint for the banner
        // that offers to merge them.
        const seriesFolderHints = new Map();

        // Asks which folders hold the series. It runs each time the series'
        // issues load, so the banner follows a merge or a move. The banner is
        // only a hint: when the question fails, it is not shown.
        async function loadSeriesFolderHint(seriesId) {
            let folders = [];
            try {
                const data = await apiFetch(`/api/files/series/${encodeURIComponent(seriesId)}/folders`);
                if (data.combinable && Array.isArray(data.folders)) {
                    folders = data.folders;
                }
            } catch (error) {
                console.debug('Could not load the folders of the series', error);
            }
            seriesFolderHints.set(seriesId, folders);
            if (currentSeriesDetailId === seriesId) {
                renderSeriesDetail(seriesId);
            }
        }

        // "This series is in 2 folders: Nightwing, Nightwing (2016-)." with a
        // button that opens Manage Folders, where they can be merged.
        function renderSeriesFoldersBanner(seriesId, series) {
            const folders = seriesFolderHints.get(seriesId) || [];
            if (folders.length < 2) return '';
            const names = folders.map(folder => String(folder.directory).split(/[\\/]/).filter(Boolean).pop());
            return html`
                <div class="series-detail-folders-banner" role="status" data-requires-write>
                    <span class="series-detail-folders-banner-text">This series is in ${folders.length} folders: ${names.join(', ')}.</span>
                    <button type="button" class="btn btn-small" data-requires-write onclick="openSeriesFoldersModal('${jsArg(seriesId)}','${jsArg(series.title)}')" title="See the folders and merge them into one">${uiIcon('folder')} Merge</button>
                </div>
            `;
        }

        // Render a small banner above the issues grid summarizing any missing
        // integer issue numbers between #1 and the highest known issue number.
        function renderMissingIssuesBanner(issues, series) {
            // A series split into runs (volumes of a numbering that restarts by
            // volume, Annuals) is checked run by run, each run's gaps named
            // after it ("1990: #3; Vol. 2: #5; Annual: #2").
            const volumes = groupIssueRuns(issues);
            const parts = (volumes || [{ label: '', issues }])
                .map(volume => ({ label: volume.label, runs: computeMissingIssueRanges(volume.issues) }))
                .filter(part => part.runs.length);
            if (!parts.length) return '';
            // Runs of consecutive numbers are shown as ranges (e.g. 2,3,4 -> "2-4").
            const describeRuns = runs => runs.map(([start, end]) => (start === end ? `#${start}` : `#${start}-${end}`)).join(', ');
            const label = volumes
                ? parts.map(part => `${part.label}: ${describeRuns(part.runs)}`).join('; ')
                : describeRuns(parts[0].runs);
            const count = parts.reduce((total, part) => total + part.runs.reduce((sum, [start, end]) => sum + end - start + 1, 0), 0);
            // The Suwayomi action is only useful when the integration is
            // configured, so it is rendered lazily once the status probe has
            // reported a reachable server (see refreshSuwayomiAvailability).
            // Suwayomi has no notion of comic volumes or Annuals, so it is not
            // offered for a split series: "#3" would match any run's #3.
            const seriesId = series && series.id ? series.id : '';
            const seriesTitle = series && (series.title || series.canonical_title) ? (series.title || series.canonical_title) : '';
            const action = seriesId && suwayomiAvailable && !volumes
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
            if (item.kind === 'volume') {
                return `
                                <h3 class="series-issues-volume-heading">${escapeHtml(item.label)} <span class="series-issues-volume-count">${item.count} issue${item.count === 1 ? '' : 's'}</span></h3>`;
            }
            if (item.kind === 'missing') {
                const isRun = item.end !== undefined;
                const label = isRun ? `Missing issues #${item.number}-${item.end}` : `Missing issue #${item.number}`;
                return `
                                <div class="series-issue-card series-issue-card--missing" aria-label="${label}" title="${label}">
                                    <div class="series-issue-cover-button series-issue-cover-button--missing">
                                        <div class="series-issue-cover series-issue-cover--missing">
                                            <span class="series-issue-missing-icon">❔</span>
                                        </div>
                                        <span class="series-issue-badge">#${isRun ? `${item.number}-${item.end}` : item.number}</span>
                                    </div>
                                    <div class="series-issue-body">
                                        <h3 class="series-issue-title">Missing</h3>
                                        <p class="series-issue-subtitle">${isRun ? `${item.end - item.number + 1} issues, #${item.number} to #${item.end}, are` : `Issue #${item.number} is`} not in your collection</p>
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
                                        <div class="series-detail-meta">${formatFileSize(issue.size)}${renderIssueComicVineLink(issue)}</div>
                                    </div>
                                    <div class="series-issue-actions file-actions-dropdown" onclick="event.stopPropagation()">
                                        <button type="button" class="dropdown-toggle series-issue-actions-toggle" aria-label="Issue actions" onclick="fillSeriesIssueMenu('${escapeJs(issue.file_path)}'); toggleDropdown(event, '${escapeJs(issue.file_path)}')">
                                            ${uiIcon('more')}
                                        </button>
                                        <div class="dropdown-menu" id="${getDropdownId(issue.file_path)}"></div>
                                    </div>
                                </div>`;
        }

        // An issue's actions are put in its menu when the menu is first
        // opened: a series of hundreds of issues would otherwise build a dozen
        // buttons and icons for every card up front. A card redrawn (its issue
        // changed) starts with an empty menu again.
        function fillSeriesIssueMenu(filePath) {
            const menu = document.getElementById(getDropdownId(filePath));
            if (!menu || menu.childElementCount) return;
            const cached = currentSeriesDetailId ? seriesIssuesCache.get(currentSeriesDetailId) : null;
            const issue = cached && cached.issues.find(item => item && item.file_path === filePath);
            if (!issue) return;
            renderHtml(menu, rawHtml(renderSeriesIssueMenuItems(issue)));
        }

        function renderSeriesIssueMenuItems(issue) {
            return `
                                            <button class="dropdown-item" onclick="showFileInfo('${escapeJs(issue.file_path)}', '${escapeJs(issue.file_name)}'); closeAllDropdowns();">
                                                ${uiIcon('info')} Info
                                            </button>
                                            <button class="dropdown-item" onclick="viewTags('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                ${uiIcon('eye')} View/Edit
                                            </button>
                                            ${renderIssueComicVineMenuItem(issue)}
                                            <button class="dropdown-item" onclick="readComic('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                ${uiIcon('book-open')} Read Comic
                                            </button>
                                            <button class="dropdown-item" data-requires-write onclick="openEmailSendModalForFile('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                ${uiIcon('mail')} Email to Ereader…
                                            </button>
                                            ${issue.duplicate
                                                ? `<button class="dropdown-item" onclick="openDuplicateReviewModal('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                    ${uiIcon('copy')} Review Duplicate
                                                </button>
                                                <button class="dropdown-item" onclick="openDuplicateCompareModal('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                    ${uiIcon('copy')} Compare Duplicate
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
                                            <button class="dropdown-item" data-requires-write onclick="openReplaceCoverModal('${escapeJs(issue.file_path)}'); closeAllDropdowns();" title="Replace this issue's cover with its cover from ComicVine">
                                                ${uiIcon('image')} Replace Cover…
                                            </button>
                                            <button class="dropdown-item" data-requires-write onclick="openRemovePagesModal('${escapeJs(issue.file_path)}'); closeAllDropdowns();" title="Remove pages such as scan credits or ads from this issue's CBZ">
                                                ${uiIcon('eraser')} Remove Pages…
                                            </button>
                                            <div class="dropdown-divider" data-requires-write></div>
                                            <button class="dropdown-item" data-requires-write onclick="deleteSingleFile('${escapeJs(issue.file_path)}'); closeAllDropdowns();">
                                                ${uiIcon('trash')} Delete
                                            </button>`;
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

        // The provider's page for a series, when its address is an http(s)
        // one. escapeHtml alone would let a javascript: link through.
        function seriesSiteLink(url) {
            return typeof url === 'string' && /^https?:\/\//i.test(url) ? url : '';
        }

        // Genres, status and provider link shown on the series page. A matched
        // series that has none of them yet gets them at its next refresh, and
        // says so; a series with no match has nothing to wait for.
        function renderSeriesDetails(series) {
            const genres = Array.isArray(series.genres) ? series.genres : [];
            const status = series.publication_status ? seriesStatusLabel(series.publication_status) : '';
            const link = seriesSiteLink(series.site_url);
            if (!genres.length && !status && !link) {
                return series.metadata_source
                    ? '<div class="series-detail-details-pending">Genres and status are not available yet. They are added the next time this series\' metadata is refreshed.</div>'
                    : '';
            }
            return `
                <div class="series-detail-details">
                    ${status ? `<span class="series-detail-chip series-detail-chip--status">${escapeHtml(status)}</span>` : ''}
                    ${genres.map(genre => `<span class="series-detail-chip">${escapeHtml(genre)}</span>`).join('')}
                    ${link ? `<a class="series-detail-link" href="${escapeHtml(link)}" target="_blank" rel="noopener noreferrer">View on ${escapeHtml(series.metadata_source || 'provider')}</a>` : ''}
                </div>
            `;
        }

        // ── Volume numbers from ComicVine ────────────────────────────────
        //
        // A volume known only by the year it started ("Batman (2016)") gets a
        // number suggested from ComicVine's volumes of that name
        // (GET /api/metadata/volume-number): Batman (2016) is DC's third
        // "Batman", Vol. 3. Nothing is written until the user applies it.
        let volumeNumbersCurrent = null;

        // The volumes of `issues` known only by their start year (volume_key
        // "y2016"), oldest first: [{ year, paths }].
        function yearOnlyVolumes(issues) {
            const byYear = new Map();
            for (const issue of issues || []) {
                const key = issue && issue.volume_key;
                if (typeof key !== 'string' || !/^y\d{4}$/.test(key)) continue;
                const year = Number(key.slice(1));
                if (!byYear.has(year)) byYear.set(year, []);
                byYear.get(year).push(issue.file_path);
            }
            return [...byYear].sort((a, b) => a[0] - b[0]).map(([year, paths]) => ({ year, paths }));
        }

        async function openVolumeNumbersModal(seriesId, seriesTitle) {
            const list = document.getElementById('volumeNumbersList');
            const emptyState = document.getElementById('volumeNumbersEmptyState');
            document.getElementById('volumeNumbersModalTitle').textContent = `Volume numbers for "${seriesTitle}"`;
            const cached = seriesIssuesCache.get(seriesId);
            const volumes = yearOnlyVolumes(cached ? cached.issues : []);
            const current = { seriesId, title: seriesTitle, volumes };
            volumeNumbersCurrent = current;
            document.getElementById('volumeNumbersModal').classList.add('active');
            if (!volumes.length) {
                clearChildren(list);
                emptyState.style.display = 'block';
                return;
            }
            emptyState.style.display = 'none';
            renderHtml(list, volumes.map(volume => html`
                <div class="volume-number-row combine-folder-row" data-volume-year="${volume.year}">
                    <div class="volume-number-heading"><strong>(${volume.year})</strong> · ${volume.paths.length} issue${volume.paths.length === 1 ? '' : 's'}</div>
                    <div class="volume-number-result combine-folders-meta">Asking ComicVine...</div>
                </div>
            `));
            // One volume at a time: each lookup can take several ComicVine requests.
            for (const volume of volumes) {
                if (volumeNumbersCurrent !== current) return;
                await lookupVolumeNumber(current, volume);
            }
        }

        async function lookupVolumeNumber(current, volume) {
            let data = null;
            let failure = '';
            try {
                data = await apiFetch(`/api/metadata/volume-number?title=${encodeURIComponent(current.title)}&year=${volume.year}`);
            } catch (error) {
                if (error.authHandled) return;
                failure = error.message;
            }
            // The modal was closed or opened for another series meanwhile.
            if (volumeNumbersCurrent !== current) return;
            const result = document.querySelector(`#volumeNumbersList [data-volume-year="${volume.year}"] .volume-number-result`);
            if (failure) {
                result.textContent = `ComicVine could not be asked: ${failure}`;
                return;
            }
            if (!data.number) {
                result.textContent = `ComicVine has no volume named "${current.title}" that started in ${volume.year}.`;
                return;
            }
            volume.number = data.number;
            volume.publisher = data.publisher;
            const listed = Array.isArray(data.volumes) ? data.volumes : [];
            // Each earlier volume can be left out: ComicVine also lists, under
            // the same name, volumes readers do not count. The volume itself
            // always counts.
            renderHtml(result, html`
                <div class="volume-number-summary">${volumeNumberSummary(current, volume)}</div>
                <ol class="volume-number-counted">
                    ${listed.map(item => html`<li><label><input type="checkbox" ${item.matched || item.counted !== false ? 'checked' : ''} ${item.matched ? 'disabled' : ''} onchange="recountVolumeNumber(${volume.year})"> ${volumeNumberSource(item)}</label></li>`)}
                </ol>
                <button type="button" class="btn btn-small btn-primary volume-number-apply" data-requires-write onclick="applyVolumeNumber(${volume.year})">${volumeNumberApplyLabel(volume)}</button>
            `);
        }

        function volumeNumberSummary(current, volume) {
            const from = volume.publisher ? `: the ${ordinalWord(volume.number)} "${current.title}" from ${volume.publisher}` : '';
            return `ComicVine suggests Vol. ${volume.number}${from}.`;
        }

        function volumeNumberApplyLabel(volume) {
            return `Set Vol. ${volume.number} on ${volume.paths.length} issue${volume.paths.length === 1 ? '' : 's'}`;
        }

        // The number once the user has ticked or unticked earlier volumes: the
        // ticked ones, the volume itself among them.
        function recountVolumeNumber(year) {
            const current = volumeNumbersCurrent;
            const volume = current && current.volumes.find(item => item.year === year);
            if (!volume) return;
            const row = document.querySelector(`#volumeNumbersList [data-volume-year="${year}"]`);
            volume.number = row.querySelectorAll('.volume-number-counted input:checked').length;
            row.querySelector('.volume-number-summary').textContent = volumeNumberSummary(current, volume);
            row.querySelector('.volume-number-apply').textContent = volumeNumberApplyLabel(volume);
        }

        // One ComicVine volume: its name, start year and issue count, linked
        // to its ComicVine page.
        function volumeNumberSource(item) {
            const issues = typeof item.issues === 'number' ? ` · ${item.issues} issue${item.issues === 1 ? '' : 's'}` : '';
            const text = `${item.name} (${item.start_year})${issues}`;
            const note = item.matched ? ' (this volume)' : '';
            const link = seriesSiteLink(item.site_url);
            return link
                ? html`<a href="${link}" target="_blank" rel="noopener noreferrer">${text}</a>${note}`
                : html`${text}${note}`;
        }

        function ordinalWord(number) {
            const words = ['first', 'second', 'third', 'fourth', 'fifth', 'sixth', 'seventh', 'eighth', 'ninth', 'tenth'];
            return words[number - 1] || `${number}th`;
        }

        // ── Shift issue numbers ──────────────────────────────────────────────
        // The selected issues of the open series, with what the shift would make of each.
        let shiftIssuesCurrent = null;

        function openShiftIssuesModal() {
            const files = Array.from(selectedFiles);
            if (!files.length) {
                showMessage('Select the issues to shift first.', 'error');
                return;
            }
            const cached = currentSeriesDetailId ? seriesIssuesCache.get(currentSeriesDetailId) : null;
            const byPath = new Map((cached && Array.isArray(cached.issues) ? cached.issues : []).map(issue => [issue.file_path, issue]));
            shiftIssuesCurrent = { files, byPath };
            const input = document.getElementById('shiftIssuesOffset');
            input.value = '';
            renderShiftIssuesPreview();
            document.getElementById('shiftIssuesModal').classList.add('active');
            input.focus();
        }

        // The shift typed in: a whole number other than 0, or null.
        function shiftIssuesOffset() {
            const text = document.getElementById('shiftIssuesOffset').value.trim();
            if (!/^[+-]?\d{1,5}$/.test(text)) return null;
            const offset = Number(text);
            return offset !== 0 && Math.abs(offset) <= 10000 ? offset : null;
        }

        // What the shift makes of one issue number: the new number, or null when it is not a
        // plain number or would fall below 0 (that issue is left out of the shift).
        function shiftedIssueNumber(issue, offset) {
            const text = typeof issue === 'string' ? issue.trim() : (typeof issue === 'number' ? String(issue) : '');
            if (!/^(\d+\.?\d*|\.\d+)$/.test(text)) return null;
            const decimals = (text.split('.')[1] || '').length;
            const shifted = Number((Number(text) + offset).toFixed(decimals));
            return shifted < 0 ? null : String(shifted);
        }

        function renderShiftIssuesPreview() {
            const current = shiftIssuesCurrent;
            const list = document.getElementById('shiftIssuesPreview');
            const apply = document.getElementById('shiftIssuesApply');
            if (!current || !list || !apply) return;
            const offset = shiftIssuesOffset();
            const rows = current.files.map(path => {
                const issue = current.byPath.get(path);
                const name = path.split('/').pop();
                const number = issue ? issue.issue : null;
                const shifted = offset === null || !issue ? null : shiftedIssueNumber(number, offset);
                return { path, name, number, shifted };
            }).sort((a, b) => (parseFloat(a.number) || 0) - (parseFloat(b.number) || 0));
            // Only the issues of this series the shift moves are sent: one picked elsewhere,
            // or whose number cannot move, is left as it is.
            current.shifting = offset === null ? [] : rows.filter(row => row.shifted !== null).map(row => row.path);
            apply.disabled = current.shifting.length === 0;
            renderHtml(list, rows.map(row => html`
                <div class="shift-issues-row">
                    <span class="shift-issues-name">${row.name}</span>
                    <span class="shift-issues-change">${offset === null
                        ? `#${row.number ?? '?'}`
                        : row.shifted === null ? `#${row.number ?? '?'}: left as it is` : `#${row.number} → #${row.shifted}`}</span>
                </div>
            `));
        }

        function closeShiftIssuesModal() {
            document.getElementById('shiftIssuesModal').classList.remove('active');
            shiftIssuesCurrent = null;
        }

        async function applyShiftIssues() {
            const current = shiftIssuesCurrent;
            const offset = shiftIssuesOffset();
            if (!current || offset === null || !current.shifting || !current.shifting.length) return;
            if (!jobProgress.canStartJob()) return;
            closeShiftIssuesModal();
            showProgressModal('Shifting issue numbers...');
            try {
                const data = await apiFetch('/api/jobs/shift-issues-selected', {
                    method: 'POST',
                    body: { Files: current.shifting, IssueOffset: offset },
                    errorMessage: 'Failed to start shifting issue numbers'
                });
                showMessage(`Started shifting the issue numbers of ${data.total_items} issue(s)`, 'info');
                await trackJobStatus(data.job_id, 'Shifting Issue Numbers...');
            } catch (error) {
                if (error.authHandled) return;
                showMessage('Failed to start shifting issue numbers: ' + error.message, 'error');
                closeProgressModal();
            }
        }

        function closeVolumeNumbersModal() {
            document.getElementById('volumeNumbersModal').classList.remove('active');
            volumeNumbersCurrent = null;
        }

        // Writes the suggested number into <Volume> of the volume's issues, as
        // the batch Update dialog does, in a background job.
        async function applyVolumeNumber(year) {
            const current = volumeNumbersCurrent;
            const volume = current && current.volumes.find(item => item.year === year);
            if (!volume || !volume.number) return;
            if (!jobProgress.canStartJob()) return;
            closeVolumeNumbersModal();
            showProgressModal('Setting the volume number...');
            try {
                const data = await apiFetch('/api/jobs/update-metadata-selected', {
                    method: 'POST',
                    body: { Files: volume.paths, Metadata: { Volume: String(volume.number) }, Fields: ['volume'] },
                    errorMessage: 'Failed to start setting the volume number'
                });
                showMessage(`Setting Vol. ${volume.number} on ${volume.paths.length} issue${volume.paths.length === 1 ? '' : 's'} of ${current.title} (${year})`, 'info');
                await trackJobStatus(data.job_id, 'Setting the Volume Number...');
            } catch (error) {
                if (error.authHandled) return;
                showMessage('Failed to set the volume number: ' + error.message, 'error');
                closeProgressModal();
            }
        }

        // ── Issue details and story arcs from ComicVine ──────────────────
        //
        // "Fetch Issue Details" fills the series' issues with ComicVine's
        // titles, cover dates, summaries, writers and story arcs, in a job
        // (POST /api/metadata/issue-details/series/{id}). The story arcs the
        // issues are part of are listed under the synopsis; each opens its
        // reading order (GET /api/metadata/story-arcs/{id}).

        // seriesId -> { loading: true } while asked, then { arcs: [...] }.
        const seriesStoryArcsCache = new Map();
        // The story arc the modal is showing, so a late answer for another is dropped.
        let storyArcModalCurrent = null;

        async function fetchSeriesIssueDetails(seriesId, seriesTitle) {
            if (!jobProgress.canStartJob()) return;
            showProgressModal('Fetching issue details from ComicVine...');
            try {
                const data = await apiFetch(`/api/metadata/issue-details/series/${encodeURIComponent(seriesId)}`, {
                    method: 'POST',
                    errorMessage: 'Failed to start fetching issue details'
                });
                showMessage(`Fetching ComicVine issue details for ${data.total_items} issue${data.total_items === 1 ? '' : 's'} of ${seriesTitle}`, 'info');
                await trackJobStatus(data.job_id, 'Fetching Issue Details...');
            } catch (error) {
                if (error.authHandled) return;
                showMessage('Failed to fetch issue details: ' + error.message, 'error');
                closeProgressModal();
            }
        }

        // The story arcs of a series, under its synopsis; asked for the first
        // time the series is shown (and after its issues reload).
        // The series' volumes and the ComicVine volume each is linked to
        // (GET /api/metadata/volumes/series/{id}), under the synopsis:
        // "Vol. 2 (2016) · 19 issues · All-New X-Men on ComicVine". Read once
        // per series, and again after its issues reload.
        const seriesVolumeLinksCache = new Map();

        function renderSeriesVolumeLinks(seriesId) {
            const entry = seriesVolumeLinksCache.get(seriesId);
            if (!entry) {
                loadSeriesVolumeLinks(seriesId);
                return '';
            }
            const volumes = entry.volumes || [];
            if (!volumes.some(volumeWorthShowing)) return '';
            const items = volumes.map((volume, index) => {
                const label = [volume.volume_number ? `Vol. ${Number(volume.volume_number)}` : '', volume.start_year ? `(${Number(volume.start_year)})` : '']
                    .filter(Boolean).join(' ') || 'No volume';
                const count = Number(volume.issues) || 0;
                const head = `${escapeHtml(label)} · ${count} issue${count === 1 ? '' : 's'}`;
                const link = seriesSiteLink(volume.site_url);
                let state;
                if (volume.decision === 'review') {
                    state = `<strong class="series-volume-review">needs review</strong>${volume.review_reason ? `: ${escapeHtml(volume.review_reason)}` : ''}`
                        + ` <button type="button" class="btn btn-sm" data-requires-write onclick="openChooseVolumeModal(${index})">Choose Volume…</button>`;
                } else {
                    state = link
                        ? `<a class="series-detail-link" href="${escapeHtml(link)}" target="_blank" rel="noopener noreferrer">${escapeHtml(volume.name || 'ComicVine')} on ComicVine</a>${volume.publisher ? ` (${escapeHtml(volume.publisher)})` : ''}`
                        : volume.decision === 'manual' ? 'no ComicVine volume, as you chose'
                            : volume.searched ? 'not found on ComicVine' : 'not linked to ComicVine yet';
                    if (volume.decision === 'manual') state += ' · your choice';
                    if (link || volume.searched || volume.decision || listOf(volume.candidates).length) {
                        const label = link || volume.decision === 'manual' ? 'Change…' : 'Choose Volume…';
                        state += ` <button type="button" class="btn btn-sm" data-requires-write onclick="openChooseVolumeModal(${index})">${label}</button>`;
                    }
                }
                const conflicts = listOf(volume.conflicts);
                const conflictNote = conflicts.length
                    ? ` · <span class="series-volume-conflict" title="${escapeHtml(conflicts.map(conflict => `#${conflict.issue}: yours ${Number(conflict.year)}, ComicVine's ${Number(conflict.comicvine_year)}`).join('\n'))}">${conflicts.length} issue${conflicts.length === 1 ? '' : 's'} dated years from ComicVine's</span>`
                    : '';
                const mixed = listOf(volume.mixed_years);
                const mixedNote = mixed.length
                    ? ` · <span class="series-volume-conflict">issue numbers repeat across ${escapeHtml(mixed.map(Number).join(', '))}: these may be several volumes; set their volume years</span>`
                    : '';
                return `<li>${head} · ${state}${conflictNote}${mixedNote}</li>`;
            }).join('');
            return `<div class="series-volume-links"><span class="series-detail-meta">Volumes:</span><ul>${items}</ul></div>`;
        }

        // The ComicVine page of each issue of the open series
        // (GET /api/metadata/comicvine-links/series/{id}), by file path: the
        // issue's own page, else its volume's. Read once per series, and again
        // after its issues reload. Shown on the issue card and in its menu.
        const seriesComicVineLinksCache = new Map();

        // The link to show for an issue, or null: { url, label, title }.
        function issueComicVineLink(filePath) {
            const seriesId = currentSeriesDetailId;
            if (!seriesId) return null;
            const entry = seriesComicVineLinksCache.get(seriesId);
            if (!entry) {
                loadSeriesComicVineLinks(seriesId);
                return null;
            }
            const link = entry.links ? entry.links.get(filePath) : null;
            if (!link) return null;
            const volume = link.volume_name || 'Its volume';
            const issueUrl = seriesSiteLink(link.issue_url);
            if (issueUrl) {
                const name = [link.issue_number ? `#${link.issue_number}` : '', link.issue_name || ''].filter(Boolean).join(' ');
                const how = link.filled ? '' : ', matched by its number';
                return { url: issueUrl, label: 'ComicVine', title: `${name || 'This issue'} on ComicVine (${volume}${how})` };
            }
            const volumeUrl = seriesSiteLink(link.volume_url);
            if (!volumeUrl) return null;
            return { url: volumeUrl, label: 'Volume on ComicVine', title: `${volume} on ComicVine; this issue is not in its listing yet` };
        }

        function renderIssueComicVineLink(issue) {
            const link = issueComicVineLink(issue.file_path);
            if (!link) return '';
            return ` · <a class="series-detail-link series-issue-comicvine" href="${escapeHtml(link.url)}" target="_blank" rel="noopener noreferrer" title="${escapeHtml(link.title)}">${escapeHtml(link.label)}</a>`;
        }

        function renderIssueComicVineMenuItem(issue) {
            const link = issueComicVineLink(issue.file_path);
            if (!link) return '';
            const label = link.label === 'ComicVine' ? 'Open on ComicVine' : 'Open Volume on ComicVine';
            return `<a class="dropdown-item" href="${escapeHtml(link.url)}" target="_blank" rel="noopener noreferrer" title="${escapeHtml(link.title)}" onclick="closeAllDropdowns();">
                                                ${uiIcon('globe')} ${label}
                                            </a>`;
        }

        async function loadSeriesComicVineLinks(seriesId) {
            const entry = { loading: true };
            seriesComicVineLinksCache.set(seriesId, entry);
            const links = new Map();
            try {
                const data = await apiFetch(`/api/metadata/comicvine-links/series/${encodeURIComponent(seriesId)}`);
                for (const link of Array.isArray(data.issues) ? data.issues : []) {
                    if (link && typeof link.file_path === 'string') links.set(link.file_path, link);
                }
            } catch (error) {
                // The links are extra: the issues still show without them.
                if (!error.authHandled) console.warn('Could not load the ComicVine links of the series:', error);
            }
            // Dropped when the issues reloaded meanwhile: that load asks again.
            if (seriesComicVineLinksCache.get(seriesId) !== entry) return;
            seriesComicVineLinksCache.set(seriesId, { links });
            if (links.size && currentSeriesDetailId === seriesId) renderSeriesDetail(seriesId);
        }

        function listOf(value) {
            return Array.isArray(value) ? value : [];
        }

        // A volume shown under the synopsis: linked, looked for on ComicVine, left for review, or with
        // issues dated or filed wrongly. A series never looked for (no ComicVine) shows none.
        function volumeWorthShowing(volume) {
            return !!(volume.site_url || volume.searched || volume.decision || listOf(volume.candidates).length
                || listOf(volume.conflicts).length || listOf(volume.mixed_years).length);
        }

        // "Choose Volume…": the ComicVine volumes weighed for a series volume, best first with
        // the evidence for each, or another one by its link or id
        // (POST /api/metadata/volumes/series/{id}/choose). The choice is final.
        let chooseVolumeCurrent = null;

        function openChooseVolumeModal(index) {
            const seriesId = currentSeriesDetailId;
            const entry = seriesId ? seriesVolumeLinksCache.get(seriesId) : null;
            const volume = entry && entry.volumes ? entry.volumes[index] : null;
            if (!volume) return;
            chooseVolumeCurrent = { seriesId, volume, selected: volume.comicvine_volume_id || null };
            const label = [volume.volume_number ? `Vol. ${Number(volume.volume_number)}` : '', volume.start_year ? `(${Number(volume.start_year)})` : '']
                .filter(Boolean).join(' ') || 'the issues that name no volume';
            document.getElementById('chooseVolumeNote').textContent = volume.review_reason
                ? `${volume.review_reason}. Which is ${label}?`
                : `Which ComicVine volume is ${label}?`;
            document.getElementById('chooseVolumeLink').value = '';
            const candidates = listOf(volume.candidates);
            const list = document.getElementById('chooseVolumeList');
            if (!candidates.length) {
                list.textContent = 'No volumes were weighed for it: paste the link of its ComicVine volume.';
            } else {
                renderHtml(list, rawHtml(`<div class="choose-volume-list">${candidates.map(chooseVolumeOption).join('')}</div>`));
            }
            markChooseVolume();
            document.getElementById('chooseVolumeModal').classList.add('active');
        }

        function chooseVolumeOption(candidate) {
            const id = Number(candidate.volume_id) || 0;
            const facts = [candidate.start_year ? `started ${Number(candidate.start_year)}` : '', candidate.publisher || '',
                candidate.issues ? `${Number(candidate.issues)} issues` : ''].filter(Boolean).map(escapeHtml).join(' · ');
            const link = seriesSiteLink(candidate.site_url);
            const reasons = listOf(candidate.reasons).map(reason => `<li>${escapeHtml(reason)}</li>`).join('');
            return `<div class="choose-volume-option" data-volume-id="${id}">
                <button type="button" class="choose-volume-pick" aria-pressed="false" onclick="selectChooseVolume(${id})">
                    <strong>${escapeHtml(candidate.name || 'ComicVine volume')}</strong> <span class="series-detail-meta">${facts}</span>
                </button>
                ${link ? `<a class="series-detail-link" href="${escapeHtml(link)}" target="_blank" rel="noopener noreferrer">on ComicVine</a>` : ''}
                ${reasons ? `<ul class="choose-volume-reasons">${reasons}</ul>` : ''}
            </div>`;
        }

        function selectChooseVolume(id) {
            if (!chooseVolumeCurrent) return;
            chooseVolumeCurrent.selected = id || null;
            document.getElementById('chooseVolumeLink').value = '';
            markChooseVolume();
        }

        function chooseVolumeLinkTyped() {
            if (!chooseVolumeCurrent) return;
            chooseVolumeCurrent.selected = null;
            markChooseVolume();
        }

        function markChooseVolume() {
            const current = chooseVolumeCurrent;
            document.querySelectorAll('#chooseVolumeList .choose-volume-option').forEach(option => {
                const picked = !!current && Number(option.dataset.volumeId) === current.selected;
                option.classList.toggle('choose-volume-option--selected', picked);
                const button = option.querySelector('.choose-volume-pick');
                if (button) button.setAttribute('aria-pressed', picked ? 'true' : 'false');
            });
            document.getElementById('chooseVolumeApplyBtn').disabled = !current || !(current.selected || typedVolumeId());
        }

        // A ComicVine volume's id from its link (".../4050-796/") or the id itself.
        function typedVolumeId() {
            const text = document.getElementById('chooseVolumeLink').value.trim();
            const match = /(?:^|4050-)(\d{1,9})\/?$/.exec(text);
            const id = match ? Number(match[1]) : 0;
            return id > 0 ? id : null;
        }

        function closeChooseVolumeModal() {
            document.getElementById('chooseVolumeModal').classList.remove('active');
            chooseVolumeCurrent = null;
        }

        async function applyChooseVolume(none) {
            const current = chooseVolumeCurrent;
            if (!current) return;
            const volumeId = none ? null : (typedVolumeId() || current.selected);
            if (!none && !volumeId) return;
            try {
                await apiFetch(`/api/metadata/volumes/series/${encodeURIComponent(current.seriesId)}/choose`, {
                    method: 'POST',
                    body: {
                        series_key: current.volume.series_key || null,
                        start_year: current.volume.start_year || 0,
                        volume_number: current.volume.volume_number || 0,
                        volume_id: volumeId
                    },
                    errorMessage: 'Failed to choose the volume'
                });
            } catch (error) {
                if (error.authHandled) return;
                showMessage('Failed to choose the volume: ' + error.message, 'error');
                return;
            }
            closeChooseVolumeModal();
            showMessage(none ? 'The volume is kept off ComicVine' : 'Volume chosen: run Fetch Issue Details to fill its issues', 'success');
            seriesVolumeLinksCache.delete(current.seriesId);
            if (currentSeriesDetailId === current.seriesId) renderSeriesDetail(current.seriesId);
        }

        async function loadSeriesVolumeLinks(seriesId) {
            const entry = { loading: true };
            seriesVolumeLinksCache.set(seriesId, entry);
            let volumes = [];
            try {
                const data = await apiFetch(`/api/metadata/volumes/series/${encodeURIComponent(seriesId)}`);
                volumes = Array.isArray(data.volumes) ? data.volumes : [];
            } catch (error) {
                // The volume links are extra: a series without them still shows.
                if (!error.authHandled) console.warn('Could not load the volumes of the series:', error);
            }
            // Dropped when the issues reloaded meanwhile: that load asks again.
            if (seriesVolumeLinksCache.get(seriesId) !== entry) return;
            seriesVolumeLinksCache.set(seriesId, { volumes });
            if (volumes.some(volumeWorthShowing) && currentSeriesDetailId === seriesId) renderSeriesDetail(seriesId);
        }

        function renderSeriesStoryArcs(seriesId) {
            const entry = seriesStoryArcsCache.get(seriesId);
            if (!entry) {
                loadSeriesStoryArcs(seriesId);
                return '';
            }
            if (!entry.arcs || !entry.arcs.length) return '';
            const chips = entry.arcs.map(arc => {
                const count = Array.isArray(arc.file_paths) ? arc.file_paths.length : 0;
                return `<button type="button" class="series-detail-chip series-story-arc-chip" onclick="openStoryArcModal(${Number(arc.id)})">${escapeHtml(arc.name)} · ${count} issue${count === 1 ? '' : 's'}</button>`;
            }).join('');
            return `<div class="series-detail-details series-story-arcs"><span class="series-detail-meta">Story arcs:</span> ${chips}</div>`;
        }

        async function loadSeriesStoryArcs(seriesId) {
            const entry = { loading: true };
            seriesStoryArcsCache.set(seriesId, entry);
            let arcs = [];
            try {
                const data = await apiFetch(`/api/metadata/story-arcs/series/${encodeURIComponent(seriesId)}`);
                arcs = Array.isArray(data.arcs) ? data.arcs : [];
            } catch (error) {
                // Story arcs are extra: a series without them still shows.
                if (!error.authHandled) console.warn('Could not load the story arcs of the series:', error);
            }
            // Dropped when the issues reloaded meanwhile: that load asks again.
            if (seriesStoryArcsCache.get(seriesId) !== entry) return;
            seriesStoryArcsCache.set(seriesId, { arcs });
            if (arcs.length && currentSeriesDetailId === seriesId) renderSeriesDetail(seriesId);
        }

        async function openStoryArcModal(storyArcId) {
            const list = document.getElementById('storyArcIssues');
            const summary = document.getElementById('storyArcSummary');
            const link = document.getElementById('storyArcLink');
            const title = document.getElementById('storyArcModalTitle');
            title.textContent = 'Story Arc';
            summary.textContent = '';
            link.style.display = 'none';
            renderHtml(list, html`<div class="loading"><div class="spinner"></div><p>Loading the story arc...</p></div>`);
            storyArcModalCurrent = storyArcId;
            document.getElementById('storyArcModal').classList.add('active');

            let data;
            try {
                data = await apiFetch(`/api/metadata/story-arcs/${storyArcId}`);
            } catch (error) {
                if (error.authHandled || storyArcModalCurrent !== storyArcId) return;
                list.textContent = error.status === 404
                    ? 'ComicVine has no such story arc.'
                    : `The story arc could not be loaded: ${error.message}`;
                return;
            }
            if (storyArcModalCurrent !== storyArcId) return;

            const issues = Array.isArray(data.issues) ? data.issues : [];
            const owned = issues.filter(issue => issue.file_path).length;
            title.textContent = data.name;
            summary.textContent = `${data.summary ? `${data.summary} ` : ''}You have ${owned} of its ${issues.length} issue${issues.length === 1 ? '' : 's'}.`;
            const site = seriesSiteLink(data.site_url);
            if (site) {
                link.href = site;
                link.style.display = '';
            }
            renderHtml(list, issues.length ? html`<ol class="story-arc-issues">${issues.map(storyArcIssueItem)}</ol>` : html`<p>ComicVine lists no issues for this story arc.</p>`);
        }

        // One issue of a story arc: its volume, number and name, with Read when
        // the library has it.
        function storyArcIssueItem(issue) {
            const number = issue.issue_number ? ` #${issue.issue_number}` : '';
            const name = `${issue.volume || 'Unknown volume'}${number}${issue.name ? ` · ${issue.name}` : ''}`;
            return issue.file_path
                ? html`<li class="story-arc-issue">${name} <button type="button" class="btn btn-small" onclick="readComic('${jsArg(issue.file_path)}')">Read</button></li>`
                : html`<li class="story-arc-issue story-arc-issue--missing">${name} <span class="series-detail-meta">Not in your library</span></li>`;
        }

        function closeStoryArcModal() {
            document.getElementById('storyArcModal').classList.remove('active');
            storyArcModalCurrent = null;
        }

        // Re-checks the issues of the open series flagged as duplicates (see
        // startDuplicateRecheck), as listed on the series page.
        function recheckSeriesDuplicates(seriesId, seriesTitle) {
            const cached = seriesIssuesCache.get(seriesId);
            const paths = (cached ? cached.issues : [])
                .filter(issue => issue && issue.duplicate && issue.file_path)
                .map(issue => issue.file_path);
            if (!paths.length) {
                showMessage(`No issue of ${seriesTitle} is flagged as a duplicate`, 'info');
                return;
            }
            return startDuplicateRecheck(paths);
        }

        // ── Replacing an issue's cover with ComicVine's ──────────────────
        //
        // "Replace Cover…" lists the covers ComicVine has for the issue's
        // volume (GET /api/covers/candidates), with the issue's own selected,
        // and makes the chosen one page 1 of the CBZ (POST /api/covers/replace).
        let replaceCoverCurrent = null;

        async function openReplaceCoverModal(filePath) {
            const current = { filePath, selected: null };
            replaceCoverCurrent = current;
            const list = document.getElementById('replaceCoverList');
            const note = document.getElementById('replaceCoverNote');
            const apply = document.getElementById('replaceCoverApplyBtn');
            const now = document.getElementById('replaceCoverCurrentImage');
            note.textContent = '';
            apply.disabled = true;
            now.removeAttribute('src');
            now.dataset.protectedImage = filePath;
            now.dataset.protectedImageLoaded = '';
            renderHtml(list, html`<div class="loading"><div class="spinner"></div><p>Asking ComicVine...</p></div>`);
            document.getElementById('replaceCoverModal').classList.add('active');
            hydrateProtectedImages(document.getElementById('replaceCoverModal'));

            let data;
            try {
                data = await apiFetch(`/api/covers/candidates?filePath=${encodeURIComponent(filePath)}`);
            } catch (error) {
                if (error.authHandled || replaceCoverCurrent !== current) return;
                list.textContent = `ComicVine could not be asked: ${error.message}`;
                return;
            }
            if (replaceCoverCurrent !== current) return;

            const covers = Array.isArray(data.covers) ? data.covers : [];
            const from = data.volume_name ? `Covers of ${data.volume_name} on ComicVine.` : '';
            note.textContent = [from, data.reason || ''].filter(Boolean).join(' ');
            if (!covers.length) {
                list.textContent = 'ComicVine has no covers to choose from.';
                return;
            }
            renderHtml(list, html`<div class="replace-cover-grid">${covers.map(replaceCoverOption)}</div>`);
            hydrateProtectedImages(list);
            if (covers.some(cover => cover.issue_id === data.matched_issue_id)) {
                selectReplaceCover(data.matched_issue_id);
            }
        }

        // One cover to choose: its image, issue number and name. ComicVine
        // refuses to show its images on other sites, so the image comes
        // through the server (GET /api/covers/candidates/{id}/image), loaded
        // like any protected image.
        function replaceCoverOption(cover) {
            const label = `#${cover.issue_number}${cover.name ? ` ${cover.name}` : ''}`;
            return html`
                <button type="button" class="replace-cover-option" data-issue-id="${cover.issue_id}" aria-pressed="false" title="${label}" onclick="selectReplaceCover(${Number(cover.issue_id)})">
                    <img data-protected-image="/api/covers/candidates/${Number(cover.issue_id)}/image?size=thumb" alt="Cover of ${label}" loading="lazy">
                    <span>${label}${cover.matched ? ' (this issue)' : ''}</span>
                </button>`;
        }

        function selectReplaceCover(issueId) {
            if (!replaceCoverCurrent) return;
            replaceCoverCurrent.selected = issueId;
            for (const option of document.querySelectorAll('#replaceCoverList .replace-cover-option')) {
                const chosen = Number(option.dataset.issueId) === issueId;
                option.classList.toggle('selected', chosen);
                option.setAttribute('aria-pressed', String(chosen));
            }
            document.getElementById('replaceCoverApplyBtn').disabled = false;
        }

        // ── Remove pages ─────────────────────────────────────────────────────
        // "Remove Pages…" shows every page of the issue; the ticked ones are
        // taken out of its CBZ (POST /api/pages/remove).
        let removePagesCurrent = null;

        async function openRemovePagesModal(filePath) {
            if (removePagesCurrent) releaseRemovePagesImages(removePagesCurrent);
            const current = { filePath, name: filePath.split('/').pop(), pages: [], selected: new Set(), urls: [], observer: null };
            removePagesCurrent = current;
            const grid = document.getElementById('removePagesGrid');
            document.getElementById('removePagesModalTitle').textContent = `Remove pages from "${current.name}"`;
            renderHtml(grid, html`<div class="loading"><div class="spinner"></div><p>Loading pages...</p></div>`);
            updateRemovePagesSelection();
            document.getElementById('removePagesModal').classList.add('active');

            let info;
            let list;
            try {
                const query = `filePath=${encodeURIComponent(filePath)}`;
                [info, list] = await Promise.all([
                    apiFetch(`/api/comicreader/info?${query}`),
                    apiFetch(`/api/comicreader/pages?${query}`)
                ]);
            } catch (error) {
                if (error.authHandled || removePagesCurrent !== current) return;
                grid.textContent = `The pages could not be read: ${error.message}`;
                return;
            }
            if (removePagesCurrent !== current) return;
            current.pages = Array.isArray(list && list.pages) ? list.pages : [];
            if (!current.pages.length) {
                grid.textContent = 'The archive has no pages.';
                return;
            }
            // The file's version keeps a cached page of an earlier file of that name out.
            const version = info && typeof info.version === 'string' && info.version ? `&v=${encodeURIComponent(info.version)}` : '';
            renderHtml(grid, current.pages.map((name, index) => html`
                <label class="replace-cover-option remove-pages-option" data-page-index="${index}" title="${name}">
                    <img data-page-url="/api/comicreader/page?filePath=${encodeURIComponent(filePath)}&page=${index + 1}${version}" alt="Page ${index + 1}" decoding="async">
                    <span><input type="checkbox" onchange="toggleRemovePage(${index}, this.checked)"> Page ${index + 1}</span>
                </label>
            `));
            loadRemovePagesImages(current, grid);
        }

        // The pages are full-size images: each is loaded when it nears the view, into a blob
        // URL of the dialog's own, all released when it closes. They stay out of the library's
        // cover cache, which they would push the covers out of.
        function loadRemovePagesImages(current, grid) {
            const load = async image => {
                const path = image.dataset.pageUrl;
                if (!path || image.dataset.pageLoaded) return;
                image.dataset.pageLoaded = '1';
                try {
                    const response = await apiFetch(path, { json: false });
                    if (!response.ok || removePagesCurrent !== current) return;
                    const url = URL.createObjectURL(await response.blob());
                    if (removePagesCurrent !== current) {
                        URL.revokeObjectURL(url);
                        return;
                    }
                    current.urls.push(url);
                    image.src = url;
                } catch (error) {
                    // The tile keeps its page number; the page can still be ticked.
                }
            };
            const images = Array.from(grid.querySelectorAll('img[data-page-url]'));
            if (typeof IntersectionObserver === 'function') {
                current.observer = new IntersectionObserver(entries => {
                    for (const entry of entries) {
                        if (!entry.isIntersecting) continue;
                        current.observer.unobserve(entry.target);
                        load(entry.target);
                    }
                }, { root: grid, rootMargin: '400px' });
                images.forEach(image => current.observer.observe(image));
            } else {
                images.forEach(load);
            }
        }

        function releaseRemovePagesImages(current) {
            if (current.observer) current.observer.disconnect();
            current.observer = null;
            current.urls.forEach(url => URL.revokeObjectURL(url));
            current.urls = [];
        }

        function toggleRemovePage(index, checked) {
            const current = removePagesCurrent;
            if (!current) return;
            if (checked) current.selected.add(index); else current.selected.delete(index);
            const option = document.querySelector(`#removePagesGrid [data-page-index="${index}"]`);
            if (option) option.classList.toggle('selected', checked);
            updateRemovePagesSelection();
        }

        function updateRemovePagesSelection() {
            const current = removePagesCurrent;
            const count = current ? current.selected.size : 0;
            const apply = document.getElementById('removePagesApplyBtn');
            const summary = document.getElementById('removePagesSummary');
            // Every page cannot go: a comic keeps at least one.
            apply.disabled = count === 0 || (current && count >= current.pages.length);
            apply.textContent = count ? `Remove ${count} Page${count === 1 ? '' : 's'}` : 'Remove Pages';
            summary.textContent = current && current.pages.length && count >= current.pages.length
                ? 'A comic must keep at least one page.'
                : '';
        }

        function closeRemovePagesModal() {
            document.getElementById('removePagesModal').classList.remove('active');
            if (removePagesCurrent) releaseRemovePagesImages(removePagesCurrent);
            removePagesCurrent = null;
        }

        async function applyRemovePages() {
            const current = removePagesCurrent;
            if (!current || !current.selected.size) return;
            const indexes = [...current.selected].sort((a, b) => a - b);
            const count = indexes.length;
            if (!confirm(`Remove ${count} page${count === 1 ? '' : 's'} (${indexes.map(index => index + 1).join(', ')}) from "${current.name}"?\n\nThe archive is written again without them. This cannot be undone.`)) return;
            const apply = document.getElementById('removePagesApplyBtn');
            apply.disabled = true;
            let data;
            try {
                data = await apiFetch('/api/pages/remove', {
                    method: 'POST',
                    body: { FilePath: current.filePath, Pages: indexes.map(index => current.pages[index]) },
                    errorMessage: 'Failed to remove the pages'
                });
            } catch (error) {
                if (error.authHandled) return;
                showMessage('Failed to remove the pages: ' + error.message, 'error');
                if (removePagesCurrent !== current) return;
                // The archive changed since its pages were listed: list them again.
                if (/reload its pages/.test(error.message || '')) {
                    openRemovePagesModal(current.filePath);
                    return;
                }
                updateRemovePagesSelection();
                return;
            }
            // Another issue's dialog may be open by now; it is left as it is.
            if (removePagesCurrent === current) closeRemovePagesModal();
            showMessage(`Removed ${data.removed} page${data.removed === 1 ? '' : 's'}; ${data.pages_left} left`, 'success');
            // Page 1 may have gone: the issue's cover is drawn again.
            await refreshFileCover(current.filePath);
            if (currentSeriesDetailId) loadSeriesIssues(currentSeriesDetailId, true, { immediate: true });
        }

        function closeReplaceCoverModal() {
            document.getElementById('replaceCoverModal').classList.remove('active');
            replaceCoverCurrent = null;
        }

        async function applyReplaceCover() {
            const current = replaceCoverCurrent;
            if (!current || !current.selected) return;
            const apply = document.getElementById('replaceCoverApplyBtn');
            apply.disabled = true;
            let data;
            try {
                data = await apiFetch('/api/covers/replace', {
                    method: 'POST',
                    body: { FilePath: current.filePath, ComicVineIssueId: current.selected },
                    errorMessage: 'Failed to replace the cover'
                });
            } catch (error) {
                if (error.authHandled) return;
                showMessage('Failed to replace the cover: ' + error.message, 'error');
                apply.disabled = false;
                return;
            }
            closeReplaceCoverModal();
            if (!data.replaced) {
                showMessage('The issue already has that cover', 'info');
                return;
            }
            showMessage('Cover replaced', 'success');
            await refreshFileCover(current.filePath);
        }

        // Shows a file's new cover. The browser keeps covers for an hour and
        // the page keeps each as a blob, so the cover is fetched again past
        // the browser's copy, and every image of it is drawn again.
        async function refreshFileCover(filePath) {
            try {
                await apiFetch(getSeriesCoverPath(filePath), { json: false, cache: 'reload' });
            } catch (error) {
                // The images below ask for it again anyway.
            }
            if (protectedImageUrls.has(filePath)) {
                URL.revokeObjectURL(protectedImageUrls.get(filePath));
                protectedImageUrls.delete(filePath);
            }
            for (const image of document.querySelectorAll('img[data-protected-image]')) {
                if (image.dataset.protectedImage !== filePath) continue;
                image.dataset.protectedImageLoaded = '';
                image.removeAttribute('src');
            }
            hydrateProtectedImages(document);
        }

        // Expands/collapses a clamped synopsis block.
        function toggleSeriesSynopsis(btn) {
            const block = btn.closest('[data-synopsis-block]');
            if (!block) return;
            const expanded = block.classList.toggle('series-detail-synopsis--expanded');
            block.classList.toggle('series-detail-synopsis--clamped', !expanded);
            btn.textContent = expanded ? 'Show less' : 'Show more';
        }

        // The open series panel as last rendered: its markup without the issue
        // cards (`shell`), each card's markup (`items`) and the panel element.
        // A re-render compares against it to update only the cards that
        // changed, and nothing at all when none did, rather than rebuilding
        // and re-parsing the whole panel (closing any open menu) for every
        // reload while a job processes files elsewhere in the library.
        let lastSeriesDetailRender = null;
        // Stands in for the issue cards in the shell markup.
        const SERIES_ISSUES_GRID_SLOT = '<!--series-issues-grid-items-->';

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
                closeAllDropdowns();
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
            // Prefer the backend-reported total (issue_count / cached.total);
            // until it arrives, fall back to the library card's count.
            const issueCount = cached && typeof cached.total === 'number' && cached.total > 0
                ? cached.total
                : (series.issue_count || issues.length || 0);
            const issuesLoading = !cached || (!cached.loaded && !cached.error);
            const issuesFailed = cached && cached.error;
            const selectableIssuePaths = issues
                .map(issue => issue.file_path)
                .filter(path => typeof path === 'string' && path.length > 0);
            const allIssuesSelected = selectableIssuePaths.length > 0 && selectableIssuePaths.every(path => selectedFiles.has(path));
            const someIssuesSelected = selectableIssuePaths.some(path => selectedFiles.has(path));
            const itemMarkup = buildSeriesIssuesGridItems(issues).map(renderSeriesIssueGridItemHtml);

            // Every Series Actions item needs write access, so read-only users
            // don't get the menu at all: data-requires-write is on the dropdown
            // and, like the issue menus, on each item (an open menu is moved
            // to <body>, out of the dropdown).
            const shell = htmlToString(html`
                <div class="series-detail" id="seriesDetailPanel">
                    <div class="series-detail-header">
                        <button type="button" class="btn btn-small series-detail-back" onclick="closeSeriesDetail()">${uiIcon('arrow-left')} Back to Series</button>
                        <div class="series-detail-summary">
                            <img class="series-detail-cover" data-protected-image="${seriesCoverImage(series, 480)}" data-protected-image-fallback="${series.has_external_image && series.external_image_url ? series.cover_file_path : ''}" alt="${series.title} cover" loading="lazy" decoding="async" fetchpriority="low">
                            <div class="series-detail-summary-body">
                                <h2>${series.title} ${rawHtml(renderLookupStatusBadge(series))}</h2>
                                <div class="series-detail-meta">${issueCount} issue${issueCount === 1 ? '' : 's'} · ${formatFileSize(series.total_size)}</div>
                                ${series.aliases?.length ? html`<div class="series-detail-meta">Also known as: ${series.aliases.join(', ')}</div>` : ''}
                                ${series.metadata_source ? html`<div class="series-detail-meta">Source: ${series.metadata_source}${series.last_lookup_utc ? ` · ${new Date(series.last_lookup_utc).toLocaleString()}` : ''}</div>` : ''}
                                ${rawHtml(renderSeriesDetails(series))}
                                ${rawHtml(renderSeriesSynopsis(series))}
                                ${rawHtml(renderSeriesVolumeLinks(series.id))}
                                ${rawHtml(renderSeriesStoryArcs(series.id))}
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
                                            <button class="dropdown-item" data-requires-write onclick="openVolumeNumbersModal('${jsArg(series.id)}','${jsArg(series.title)}'); closeAllDropdowns();" title="Ask ComicVine for the number of each volume known only by the year it started">
                                                ${uiIcon('globe')} Find Volume Numbers
                                            </button>
                                            <button class="dropdown-item" data-requires-write onclick="fetchSeriesIssueDetails('${jsArg(series.id)}','${jsArg(series.title)}'); closeAllDropdowns();" title="Fill issue titles, cover dates, summaries, writers and story arcs from ComicVine into fields that are empty">
                                                ${uiIcon('globe')} Fetch Issue Details
                                            </button>
                                            <button class="dropdown-item" data-requires-write onclick="recheckSeriesDuplicates('${jsArg(series.id)}','${jsArg(series.title)}'); closeAllDropdowns();" title="Clear the flag of each duplicate issue whose processed name is free again, and process it">
                                                ${uiIcon('rotate-ccw')} Re-check Duplicates
                                            </button>
                                            <button class="dropdown-item" data-requires-write onclick="previewSeriesProcessing('${jsArg(series.id)}','${jsArg(series.title)}'); closeAllDropdowns();" title="See what processing this series would rename and retag, before anything is changed">
                                                ${uiIcon('eye')} Preview Changes
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
                    ${renderSeriesFoldersBanner(seriesId, series)}
                    ${!issuesLoading && !issuesFailed && issues.length ? html`
                        <div class="series-detail-selection-bar">
                            <label class="series-detail-select-all" for="selectAll">
                                <input type="checkbox"
                                       id="selectAll"
                                       onchange="toggleSelectAll(this.checked)"
                                       ${allIssuesSelected ? rawHtml('checked') : ''}>
                                <span>Select all issues</span>
                            </label>
                            <span class="series-detail-selection-meta">${issueCount} issue${issueCount === 1 ? '' : 's'} in this series</span>
                            <button type="button" class="btn btn-small" data-requires-write onclick="openEmailSendModalForSelected()">${uiIcon('mail')} Email Selected</button>
                            <button type="button" class="btn btn-small" data-requires-write onclick="openShiftIssuesModal()" title="Move the issue numbers of the selected issues, when a downloader numbered them from the wrong start">${uiIcon('list')} Shift Issue Numbers</button>
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
                        <div class="series-issues-grid">${rawHtml(SERIES_ISSUES_GRID_SLOT)}</div>
                    `}
                </div>
            `);

            let renderedCards = patchSeriesDetail(fileList, seriesId, shell, itemMarkup);
            if (!renderedCards) {
                // Restore/close any open action menu (which may be portaled to
                // <body>) before the panel's DOM is rebuilt.
                closeAllDropdowns();
                renderHtml(fileList, rawHtml(shell.replace(SERIES_ISSUES_GRID_SLOT, () => itemMarkup.join(''))));
                lastSeriesDetailRender = {
                    seriesId,
                    shell,
                    items: itemMarkup,
                    panel: fileList ? fileList.querySelector('#seriesDetailPanel') : null
                };
                renderedCards = fileList ? [fileList] : [];
            }

            const selectAllCheckbox = document.getElementById('selectAll');
            if (selectAllCheckbox) {
                selectAllCheckbox.indeterminate = someIssuesSelected && !allIssuesSelected;
            }
            updateSelectInfo();
            updateSelectAllCheckbox();
            updateLibraryViewLayout();
            if (renderedCards.length) {
                hydrateProtectedImages(renderedCards);
            }
        }

        // Brings the panel rendered last for this series up to date by
        // replacing only the issue cards whose markup changed. Returns the
        // cards it put in (none when nothing changed), or null when the panel
        // has to be rendered whole: another series or view was rendered since,
        // something outside the cards changed (counts, selection, banner), or
        // the cards are not the ones rendered.
        function patchSeriesDetail(fileList, seriesId, shell, items) {
            const last = lastSeriesDetailRender;
            if (!fileList || !last || last.seriesId !== seriesId || last.shell !== shell
                || last.items.length !== items.length || !last.panel
                || fileList.querySelector('#seriesDetailPanel') !== last.panel) {
                return null;
            }
            const grid = last.panel.querySelector('.series-issues-grid');
            if (items.length && (!grid || grid.children.length !== items.length)) {
                return null;
            }
            const changed = [];
            items.forEach((markup, index) => {
                if (markup !== last.items[index]) changed.push(index);
            });
            const oldCards = changed.map(index => grid.children[index]);
            // An open menu (possibly moved to <body>) of a card about to be
            // replaced is closed first; menus of the other cards stay open.
            const menuOpenOnChangedCard = oldCards.some(card => {
                const path = card.dataset.filePath;
                const menu = path ? document.getElementById(getDropdownId(path)) : null;
                return Boolean(menu && menu.classList.contains('show'));
            });
            if (menuOpenOnChangedCard) {
                closeAllDropdowns();
            }
            const newCards = changed.map((index, i) => {
                const card = htmlToElement(rawHtml(items[index]));
                oldCards[i].replaceWith(card);
                return card;
            });
            last.items = items;
            if (grid) {
                syncSeriesIssueCardSelection(grid);
            }
            return newCards;
        }

        // The selection also changes in place (ticking a box, clearing the
        // selection after a job) without the cards being rendered again, so
        // the cards a patch leaves in place are brought in line with it.
        function syncSeriesIssueCardSelection(grid) {
            grid.querySelectorAll('.series-issue-card[data-file-path]').forEach(card => {
                const selected = selectedFiles.has(card.dataset.filePath);
                card.classList.toggle('series-issue-card--selected', selected);
                const checkbox = card.querySelector('.series-issue-select input[type="checkbox"]');
                if (checkbox) {
                    checkbox.checked = selected;
                }
            });
        }
