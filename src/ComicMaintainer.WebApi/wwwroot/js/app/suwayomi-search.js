// ComicMaintainer SPA — find a series on the Suwayomi server's sources and add it to its library.
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

        // Each source search makes Suwayomi query the source's site, so a few
        // run at once: enough that one slow site does not hold up the rest.
        const SUWAYOMI_SOURCE_SEARCH_CONCURRENCY = 4;
        // localStorage key of the language the search was last narrowed to.
        const SUWAYOMI_SEARCH_LANGUAGE_KEY = 'suwayomiSearchLanguage';
        // Language picker value that searches every source.
        const SUWAYOMI_ANY_LANGUAGE = '*';
        // Suwayomi's language for sources that serve every language; they are
        // searched whichever language is picked.
        const SUWAYOMI_MULTI_LANGUAGE = 'all';

        // The installed sources, loaded once per opening of the dialog.
        let suwayomiSourcesPromise = null;
        // Bumped to abandon the running search; its late answers are dropped.
        let suwayomiSearchGeneration = 0;
        let suwayomiSearchAbort = null;
        // Whether this opening of the dialog has searched yet, so a new
        // language repeats the search.
        let suwayomiSearchRan = false;
        let suwayomiLanguageNames;

        // Shows or hides every "Find on Suwayomi" action for the latest
        // status probe (see refreshSuwayomiAvailability).
        function updateSuwayomiSearchEntryPoints() {
            document.querySelectorAll('[data-suwayomi-search-entry]').forEach(element => {
                element.hidden = !suwayomiAvailable;
            });
        }

        // Offered by the library's "No series found" state: the series the
        // search names may be on one of Suwayomi's sources.
        function renderSuwayomiSearchOffer(term) {
            const query = typeof term === 'string' ? term.trim() : '';
            if (!query) return '';
            return html`
                <button type="button" class="btn btn-primary suwayomi-search-offer" data-requires-write data-suwayomi-search-entry ${suwayomiAvailable ? '' : 'hidden'} onclick="openSuwayomiSearchModal('${jsArg(query)}')">
                    ${uiIcon('search')} Search Suwayomi for “${query}”
                </button>
            `;
        }

        // Opens the search dialog. A `query` is searched for straight away;
        // without one the library search, if any, is offered as the title.
        function openSuwayomiSearchModal(query) {
            const modal = document.getElementById('suwayomiSearchModal');
            const input = document.getElementById('suwayomiSearchInput');
            if (!modal || !input) return;
            cancelSuwayomiSearch();
            suwayomiSearchRan = false;
            suwayomiSourcesPromise = null;
            const term = typeof query === 'string' ? query.trim() : '';
            input.value = term || (typeof searchQuery === 'string' ? searchQuery.trim() : '');
            setSuwayomiSearchStatus('');
            clearChildren(document.getElementById('suwayomiSearchResults'));
            clearChildren(document.getElementById('suwayomiSearchMessages'));
            modal.classList.add('active');

            if (term) {
                runSuwayomiSearch();
                return;
            }
            // A search started meanwhile reports the failure itself, and a
            // closed dialog has nothing to report it in.
            const generation = suwayomiSearchGeneration;
            loadSuwayomiSearchSources().catch(error => {
                if (generation === suwayomiSearchGeneration && !error.authHandled) {
                    setSuwayomiSearchStatus(withApiFailureReference(`Could not load the Suwayomi sources: ${error.message}`, error));
                }
            });
        }

        function closeSuwayomiSearchModal() {
            cancelSuwayomiSearch();
            const modal = document.getElementById('suwayomiSearchModal');
            if (modal) modal.classList.remove('active');
        }

        function cancelSuwayomiSearch() {
            suwayomiSearchGeneration++;
            if (suwayomiSearchAbort) {
                suwayomiSearchAbort.abort();
                suwayomiSearchAbort = null;
            }
            const results = document.getElementById('suwayomiSearchResults');
            if (results) results.removeAttribute('aria-busy');
        }

        function setSuwayomiSearchStatus(text) {
            const status = document.getElementById('suwayomiSearchStatus');
            if (status) status.textContent = text;
        }

        // Reports in the dialog while it is open, as it covers the page's
        // messages; one closed before the answer came reports on the page.
        function showSuwayomiSearchMessage(text, type, options = {}) {
            const modal = document.getElementById('suwayomiSearchModal');
            const container = document.getElementById('suwayomiSearchMessages');
            const inDialog = !!modal && !!container && modal.classList.contains('active');
            return showMessage(text, type, inDialog ? { ...options, container } : options);
        }

        // The error thrown for a failed response, carrying the server's own
        // message when it sent one.
        async function suwayomiResponseError(response) {
            let message = `HTTP error! status: ${response.status}`;
            let fromServer = false;
            try {
                const body = await response.json();
                if (body && typeof body.error === 'string' && body.error) {
                    message = body.error;
                    fromServer = true;
                }
            } catch (e) { /* not a JSON error body */ }
            const error = apiResponseError(response, message);
            error.fromServer = fromServer;
            return error;
        }

        async function fetchSuwayomiSearchJson(path, options = {}) {
            const response = await fetch(apiUrl(path), {
                headers: getAuthHeaders(),
                credentials: 'same-origin',
                ...options
            });
            if (handleAuthError(response)) {
                const error = new Error('Your session has expired.');
                error.authHandled = true;
                throw error;
            }
            if (!response.ok) throw await suwayomiResponseError(response);
            return await response.json();
        }

        // Loads the installed sources and fills the language picker with
        // their languages. A failed load is forgotten so the next search
        // tries again.
        function loadSuwayomiSearchSources() {
            if (suwayomiSourcesPromise) return suwayomiSourcesPromise;
            const promise = fetchSuwayomiSearchJson('/api/suwayomi/sources').then(data => {
                const sources = (Array.isArray(data && data.sources) ? data.sources : [])
                    .filter(source => source && typeof source.id === 'string' && /^[0-9]+$/.test(source.id));
                if (suwayomiSourcesPromise === promise) populateSuwayomiSearchLanguages(sources);
                return sources;
            });
            promise.catch(() => {
                if (suwayomiSourcesPromise === promise) suwayomiSourcesPromise = null;
            });
            suwayomiSourcesPromise = promise;
            return promise;
        }

        function suwayomiLanguageName(lang) {
            try {
                // The rest of the interface is in English, so are the names.
                suwayomiLanguageNames = suwayomiLanguageNames || new Intl.DisplayNames(['en'], { type: 'language' });
                const name = suwayomiLanguageNames.of(lang);
                if (name) return name;
            } catch (e) { /* not a language tag Intl knows */ }
            return lang;
        }

        function populateSuwayomiSearchLanguages(sources) {
            const select = document.getElementById('suwayomiSearchLanguage');
            if (!select) return;
            const multiLanguage = sources.filter(source => source.lang === SUWAYOMI_MULTI_LANGUAGE).length;
            const counts = new Map();
            for (const source of sources) {
                if (typeof source.lang !== 'string' || !source.lang || source.lang === SUWAYOMI_MULTI_LANGUAGE) continue;
                counts.set(source.lang, (counts.get(source.lang) || 0) + 1);
            }
            const languages = Array.from(counts, ([lang, count]) => ({ lang, label: suwayomiLanguageName(lang), count: count + multiLanguage }))
                .sort((a, b) => a.label.localeCompare(b.label));
            renderHtml(select, html`
                <option value="${SUWAYOMI_ANY_LANGUAGE}">All languages (${sources.length})</option>
                ${languages.map(language => html`<option value="${language.lang}">${language.label} (${language.count})</option>`)}
            `);
            select.value = pickSuwayomiSearchLanguage(languages.map(language => language.lang));
        }

        // The language picked last time, else the browser's, else every one.
        function pickSuwayomiSearchLanguage(available) {
            let saved = null;
            try {
                saved = localStorage.getItem(SUWAYOMI_SEARCH_LANGUAGE_KEY);
            } catch (e) { /* storage unavailable */ }
            if (saved === SUWAYOMI_ANY_LANGUAGE || (saved && available.includes(saved))) return saved;

            const preferred = Array.isArray(navigator.languages) && navigator.languages.length
                ? navigator.languages
                : [navigator.language];
            for (const tag of preferred) {
                if (typeof tag !== 'string' || !tag) continue;
                const wanted = tag.toLowerCase();
                const exact = available.find(lang => lang.toLowerCase() === wanted);
                if (exact) return exact;
                const base = available.find(lang => lang.toLowerCase() === wanted.split('-')[0]);
                if (base) return base;
            }
            return SUWAYOMI_ANY_LANGUAGE;
        }

        function changeSuwayomiSearchLanguage() {
            const select = document.getElementById('suwayomiSearchLanguage');
            if (!select) return;
            try {
                localStorage.setItem(SUWAYOMI_SEARCH_LANGUAGE_KEY, select.value);
            } catch (e) { /* storage unavailable */ }
            if (suwayomiSearchRan) runSuwayomiSearch();
        }

        function isSuwayomiSearchSeries(series) {
            return !!series && Number.isInteger(series.id) && series.id > 0 && typeof series.title === 'string';
        }

        // Series are shown once per search, so their ids are unique on the page.
        function renderSuwayomiSearchItem(series) {
            const titleId = `suwayomiSearchTitle${series.id}`;
            const cover = series.thumbnailUrl
                ? html`<img data-protected-image="/api/suwayomi/series/${series.id}/thumbnail" alt="" loading="lazy" decoding="async">`
                : '';
            return html`
                <li class="suwayomi-search-item" data-suwayomi-series-id="${series.id}" data-in-library="${series.inLibrary ? 'true' : 'false'}">
                    <div class="suwayomi-search-cover" aria-hidden="true">${cover}</div>
                    <div class="suwayomi-search-info">
                        <span class="suwayomi-search-title" id="${titleId}">${series.title}</span>
                        <span class="suwayomi-search-badge" ${series.inLibrary ? '' : 'hidden'}>In Suwayomi library</span>
                    </div>
                    <button type="button" class="btn btn-small" data-requires-write aria-describedby="${titleId}" onclick="addSuwayomiSeries('${jsArg(series.id)}', this)">
                        ${uiIcon('download')} <span class="suwayomi-search-action">${suwayomiAddActionLabel(series.inLibrary)}</span>
                    </button>
                </li>
            `;
        }

        function suwayomiAddActionLabel(inLibrary) {
            return inLibrary ? 'Download missing' : 'Add & download';
        }

        // Fills a result section with the series not shown by an earlier one
        // and returns how many that was.
        function showSuwayomiSearchSection(section, found, shown) {
            const fresh = [];
            for (const series of Array.isArray(found) ? found : []) {
                if (!isSuwayomiSearchSeries(series) || shown.has(series.id)) continue;
                shown.add(series.id);
                fresh.push(series);
            }
            if (!fresh.length) return 0;
            appendHtml(section.querySelector('.suwayomi-search-list'), fresh.map(renderSuwayomiSearchItem));
            section.hidden = false;
            hydrateProtectedImages(section);
            return fresh.length;
        }

        // Searches the Suwayomi library, then every installed source of the
        // picked language, showing each source's series as they arrive.
        async function runSuwayomiSearch() {
            const input = document.getElementById('suwayomiSearchInput');
            const results = document.getElementById('suwayomiSearchResults');
            if (!input || !results) return;
            cancelSuwayomiSearch();
            clearChildren(results);
            const query = input.value.trim();
            if (!query) {
                setSuwayomiSearchStatus('Enter the title of a series to search for.');
                return;
            }

            suwayomiSearchRan = true;
            const generation = suwayomiSearchGeneration;
            const isCurrent = () => generation === suwayomiSearchGeneration;
            const controller = new AbortController();
            suwayomiSearchAbort = controller;
            results.setAttribute('aria-busy', 'true');
            setSuwayomiSearchStatus('Searching...');

            let sources;
            try {
                sources = await loadSuwayomiSearchSources();
            } catch (error) {
                if (!isCurrent()) return;
                cancelSuwayomiSearch();
                // An expired session is already on its way to the login page.
                setSuwayomiSearchStatus(error.authHandled
                    ? ''
                    : withApiFailureReference(`Could not load the Suwayomi sources: ${error.message}`, error));
                return;
            }
            if (!isCurrent()) return;

            const select = document.getElementById('suwayomiSearchLanguage');
            const language = select && select.value ? select.value : SUWAYOMI_ANY_LANGUAGE;
            const searched = language === SUWAYOMI_ANY_LANGUAGE
                ? sources
                : sources.filter(source => source.lang === language || source.lang === SUWAYOMI_MULTI_LANGUAGE);

            renderHtml(results, html`
                <section class="suwayomi-search-section" data-suwayomi-section="library" hidden>
                    <h3 class="suwayomi-search-section-title">In your Suwayomi library</h3>
                    <ul class="suwayomi-search-list"></ul>
                </section>
                ${searched.map(source => html`
                    <section class="suwayomi-search-section" data-suwayomi-source-id="${source.id}" hidden>
                        <h3 class="suwayomi-search-section-title">${source.displayName || source.name || source.id}</h3>
                        <ul class="suwayomi-search-list"></ul>
                    </section>
                `)}
                <details class="suwayomi-search-failures" hidden>
                    <summary></summary>
                    <ul></ul>
                </details>
            `);
            const librarySection = results.querySelector('[data-suwayomi-section="library"]');
            const sourceSections = Array.from(results.querySelectorAll('[data-suwayomi-source-id]'));
            const shown = new Set();
            const failures = [];
            let found = 0;
            setSuwayomiSearchStatus(searched.length
                ? `Searching ${searched.length} source${searched.length === 1 ? '' : 's'}...`
                : 'Searching the Suwayomi library...');

            const recordFailure = (label, error) => {
                if (!isCurrent() || error.name === 'AbortError') return;
                // The session expired and the login page is on its way.
                if (error.authHandled) {
                    cancelSuwayomiSearch();
                    setSuwayomiSearchStatus('');
                    return;
                }
                failures.push({ label, message: withApiFailureReference(error.message, error) });
            };

            // The library lookup goes first, so a series already there is
            // listed under it rather than under its source. A query with no
            // letter or digit would match the whole library.
            if (/[\p{L}\p{Nd}]/u.test(query)) {
                try {
                    const data = await fetchSuwayomiSearchJson(
                        `/api/suwayomi/series?query=${encodeURIComponent(query)}&limit=10`,
                        { signal: controller.signal });
                    if (!isCurrent()) return;
                    found += showSuwayomiSearchSection(librarySection, data && data.series, shown);
                } catch (error) {
                    recordFailure('Suwayomi library', error);
                    if (!isCurrent()) return;
                }
            }

            let next = 0;
            let done = 0;
            const searchNext = async () => {
                while (isCurrent() && next < searched.length) {
                    const index = next++;
                    const source = searched[index];
                    try {
                        const data = await fetchSuwayomiSearchJson(
                            `/api/suwayomi/sources/${encodeURIComponent(source.id)}/series?query=${encodeURIComponent(query)}`,
                            { signal: controller.signal });
                        if (!isCurrent()) return;
                        found += showSuwayomiSearchSection(sourceSections[index], data && data.series, shown);
                    } catch (error) {
                        recordFailure(source.displayName || source.name || source.id, error);
                        if (!isCurrent()) return;
                    }
                    done++;
                    setSuwayomiSearchStatus(`Searched ${done} of ${searched.length} sources...`);
                }
            };
            await Promise.all(Array.from(
                { length: Math.min(SUWAYOMI_SOURCE_SEARCH_CONCURRENCY, searched.length) },
                searchNext));
            if (!isCurrent()) return;

            suwayomiSearchAbort = null;
            results.removeAttribute('aria-busy');
            setSuwayomiSearchStatus(describeSuwayomiSearch(found, searched.length, language, failures.length));
            if (failures.length) {
                const details = results.querySelector('.suwayomi-search-failures');
                details.querySelector('summary').textContent = `${failures.length} search${failures.length === 1 ? '' : 'es'} failed`;
                renderHtml(details.querySelector('ul'), failures.map(failure => html`<li><strong>${failure.label}</strong>: ${failure.message}</li>`));
                details.hidden = false;
            }
        }

        function describeSuwayomiSearch(found, sourceCount, language, failed) {
            const plural = (count, word) => `${count} ${word}${count === 1 ? '' : 's'}`;
            let text;
            if (!sourceCount) {
                const none = language === SUWAYOMI_ANY_LANGUAGE
                    ? 'The Suwayomi server has no sources to search; install an extension there first.'
                    : `The Suwayomi server has no ${suwayomiLanguageName(language)} sources to search.`;
                text = found ? `Found ${found} series in the Suwayomi library. ${none}` : none;
            } else {
                text = found
                    ? `Found ${found} series on ${plural(sourceCount, 'source')}.`
                    : `No series found on ${plural(sourceCount, 'source')}.`;
            }
            if (failed) text += ` ${failed === 1 ? 'One search' : `${failed} searches`} failed.`;
            return text;
        }

        // What adding a series did, as one message.
        function describeSuwayomiAddResult(result) {
            const title = result && result.series && typeof result.series.title === 'string' ? result.series.title : 'The series';
            const downloads = (result && result.downloads) || {};
            const queued = Array.isArray(downloads.queuedChapters) ? downloads.queuedChapters.length : 0;
            const chapters = (count) => `${count} chapter${count === 1 ? '' : 's'}`;
            const alreadyInLibrary = !!(result && result.alreadyInLibrary);
            let text = alreadyInLibrary
                ? `"${title}" is already in the Suwayomi library`
                : `Added "${title}" to the Suwayomi library`;
            if (queued) {
                text += `${alreadyInLibrary ? ';' : ' and'} queued ${chapters(queued)} for download.`;
            } else if (!downloads.chapterCount) {
                text += '; its source lists no chapters yet.';
            } else {
                text += '; there is nothing new to download.';
            }
            const owned = downloads.alreadyInLocalLibraryCount || 0;
            if (owned) {
                const local = result.localSeriesTitle ? ` as "${result.localSeriesTitle}"` : '';
                text += ` ${owned === 1 ? '1 issue is' : `${owned} issues are`} already in your library${local}.`;
            }
            const downloaded = downloads.alreadyDownloadedCount || 0;
            if (downloaded) {
                text += ` ${chapters(downloaded)} ${downloaded === 1 ? 'was' : 'were'} already downloaded.`;
            }
            const unnumbered = downloads.unnumberedChapterCount || 0;
            if (unnumbered) {
                text += ` ${unnumbered === 1 ? '1 unnumbered chapter was' : `${unnumbered} unnumbered chapters were`} skipped.`;
            }
            return text;
        }

        // Adds a series to the Suwayomi library (unless it is there already)
        // and queues the chapters of every issue the local library lacks.
        async function addSuwayomiSeries(seriesId, button) {
            const id = Number(seriesId);
            if (!Number.isInteger(id) || id <= 0) return;
            const item = button ? button.closest('.suwayomi-search-item') : null;
            const label = button ? button.querySelector('.suwayomi-search-action') : null;
            const inLibrary = () => !!item && item.dataset.inLibrary === 'true';
            if (button) button.disabled = true;
            if (label) label.textContent = inLibrary() ? 'Queuing...' : 'Adding...';
            try {
                const response = await fetch(apiUrl(`/api/suwayomi/series/${id}/add`), {
                    method: 'POST',
                    headers: getAuthHeaders(),
                    credentials: 'same-origin'
                });
                if (handleAuthError(response)) return;
                if (!response.ok) throw await suwayomiResponseError(response);
                const result = await response.json();
                const queued = result && result.downloads && Array.isArray(result.downloads.queuedChapters)
                    ? result.downloads.queuedChapters.length
                    : 0;
                showSuwayomiSearchMessage(describeSuwayomiAddResult(result), queued || !(result && result.alreadyInLibrary) ? 'success' : 'info');
                markSuwayomiSeriesInLibrary(id);
            } catch (error) {
                console.error('Adding the Suwayomi series failed', error);
                const text = error.fromServer ? error.message : `Could not add the series on Suwayomi: ${error.message}`;
                showSuwayomiSearchMessage(text, 'error', { reference: error });
            } finally {
                if (button) button.disabled = false;
                if (label) label.textContent = suwayomiAddActionLabel(inLibrary());
            }
        }

        function markSuwayomiSeriesInLibrary(id) {
            document.querySelectorAll('#suwayomiSearchResults .suwayomi-search-item').forEach(item => {
                if (item.dataset.suwayomiSeriesId !== String(id)) return;
                item.dataset.inLibrary = 'true';
                const badge = item.querySelector('.suwayomi-search-badge');
                if (badge) badge.hidden = false;
                // A disabled button has a request of its own under way and
                // sets its label once that request settles.
                const button = item.querySelector('button');
                const label = item.querySelector('.suwayomi-search-action');
                if (label && !(button && button.disabled)) label.textContent = suwayomiAddActionLabel(true);
            });
        }
