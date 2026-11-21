        // ComicMaintainer SPA — offline reading from the library (js/app/offline.js).
        //
        // The service worker (sw.js) keeps downloaded comics in a cache of their
        // own, which the reader reads from with no network. The reader can save
        // the comic it shows; this file saves issues from the library without
        // opening them (one, a series, or the selected series), marks the saved
        // ones, and lists every saved comic in the Offline Comics dialog, which
        // also opens by itself when the app starts with no connection. The
        // worker downloads; the page only asks it, passing its sign-in (the
        // worker cannot read the token), and follows the progress it reports.
        // Comics are saved one at a time, in the order asked.

        // No word from the worker on the running download for this long: ask
        // whether it is still running (the browser may have stopped the worker).
        const OFFLINE_SAVE_STALL_MS = 30000;

        // The saves still to do, kept for the tab (sessionStorage): opening the
        // reader leaves this page, which stops the queue but not the download
        // the worker is running, and the rest resume when the library is back.
        const OFFLINE_QUEUE_KEY = 'offlineSaveQueue';

        // What the worker holds: saved comics (path → page count), interrupted
        // downloads (path → { savedPages, pageCount }), the download running
        // (path → percent, null before the first page) and those queued after it.
        const offlineComics = {
            saved: new Map(),
            partial: new Map(),
            active: new Map(),
            queue: [],
            stallTimer: null,
            listening: false,
            // The tab's stored queue is written only once it has been read
            // back: an app opened offline never reads it, and must not erase it.
            queueRead: false
        };

        function offlineWorker() {
            return ('serviceWorker' in navigator && navigator.serviceWorker.controller) || null;
        }

        // Asks the worker and waits for its answer: null when there is no
        // worker (or no way to hear back from one), or it does not answer
        // within five seconds.
        function askOfflineWorker(message) {
            const worker = offlineWorker();
            if (!worker || typeof MessageChannel !== 'function') return Promise.resolve(null);
            return new Promise(resolve => {
                const channel = new MessageChannel();
                const finish = answer => {
                    clearTimeout(timer);
                    channel.port1.onmessage = null;
                    channel.port1.close();
                    resolve(answer);
                };
                const timer = setTimeout(() => finish(null), 5000);
                channel.port1.onmessage = event => finish(event.data);
                try {
                    worker.postMessage(message, [channel.port2]);
                } catch (error) {
                    finish(null);
                }
            });
        }

        function listenToOfflineWorker() {
            if (offlineComics.listening || !('serviceWorker' in navigator)) return;
            offlineComics.listening = true;
            navigator.serviceWorker.addEventListener('message', handleOfflineWorkerMessage);
        }

        // Reads what the worker holds; false when it cannot be asked.
        async function refreshOfflineComics() {
            listenToOfflineWorker();
            const answer = await askOfflineWorker({ type: 'LIST_OFFLINE_COMICS' });
            if (!answer) return false;
            offlineComics.saved = new Map(answer.comics.map(comic => [comic.filePath, comic.pageCount]));
            // A download another page follows (the reader's) is not this page's to report.
            offlineComics.partial = new Map(answer.incomplete
                .filter(comic => !comic.active)
                .map(comic => [comic.filePath, { savedPages: comic.savedPages, pageCount: comic.pageCount }]));
            redrawOfflineMarks();
            return true;
        }

        // Reads the saved comics once the app has started, and again whenever a
        // worker takes over the page, and resumes the saves this tab left undone.
        async function initOfflineComics() {
            if (!('serviceWorker' in navigator)) return;
            const left = storedOfflineQueue();
            offlineComics.queueRead = true;
            listenToOfflineWorker();
            navigator.serviceWorker.addEventListener('controllerchange', () => { void refreshOfflineComics(); });
            if (!await refreshOfflineComics()) return;
            // The first may still be downloading: asking for it again follows it.
            const pending = left.filter(path => !offlineComics.saved.has(path)
                && !offlineComics.active.has(path) && !offlineComics.queue.includes(path));
            if (!pending.length) return;
            offlineComics.queue.push(...pending);
            showMessage(`Saving ${counted(pending.length, 'more issue')} for offline reading…`, 'info');
            redrawOfflineMarks();
            startNextOfflineSave();
        }

        function storedOfflineQueue() {
            try {
                const stored = JSON.parse(sessionStorage.getItem(OFFLINE_QUEUE_KEY) || '[]');
                return Array.isArray(stored) ? stored.filter(path => typeof path === 'string' && path) : [];
            } catch (error) {
                return [];
            }
        }

        function rememberOfflineQueue() {
            if (!offlineComics.queueRead) return;
            try {
                const pending = [...offlineComics.active.keys(), ...offlineComics.queue];
                if (pending.length) {
                    sessionStorage.setItem(OFFLINE_QUEUE_KEY, JSON.stringify(pending));
                } else {
                    sessionStorage.removeItem(OFFLINE_QUEUE_KEY);
                }
            } catch (error) {
                // Not kept: leaving the page drops the saves not yet started.
            }
        }

        // The app started with no connection: the library cannot load, so the
        // comics saved on this device are shown instead, and the app starts
        // properly once the connection returns.
        function startOffline(startLibrary) {
            window.addEventListener('online', () => { void startLibrary(); }, { once: true });
            void openOfflineComicsModal();
        }

        // "1 page", "3 pages".
        function counted(count, noun) {
            return `${count} ${noun}${count === 1 ? '' : 's'}`;
        }

        function percentOf(done, total) {
            return total ? Math.round((done / total) * 100) : 0;
        }

        // A comic's offline copy: 'saving' (with a percent), 'queued', 'saved',
        // 'partial' (with its counts), or null when it has none.
        function offlineStateOf(filePath) {
            if (offlineComics.active.has(filePath)) return { kind: 'saving', percent: offlineComics.active.get(filePath) };
            if (offlineComics.queue.includes(filePath)) return { kind: 'queued' };
            if (offlineComics.saved.has(filePath)) return { kind: 'saved' };
            if (offlineComics.partial.has(filePath)) return { kind: 'partial', ...offlineComics.partial.get(filePath) };
            return null;
        }

        // The mark an issue card shows: empty for a comic with no offline copy.
        function offlineMarkText(filePath) {
            const state = offlineStateOf(filePath);
            if (!state) return '';
            switch (state.kind) {
                case 'saving': return state.percent === null ? 'Saving…' : `Saving ${state.percent}%`;
                case 'queued': return 'Queued';
                case 'saved': return 'Offline';
                default: return `Offline ${state.savedPages}/${state.pageCount}`;
            }
        }

        function offlineMarkHtml(filePath) {
            const text = offlineMarkText(filePath);
            return `<span class="series-issue-offline" data-offline-path="${escapeHtml(filePath)}" title="Saved for reading offline"${text ? '' : ' hidden'}>${escapeHtml(text)}</span>`;
        }

        // The issue menu's offline item: what clicking it does now.
        function offlineMenuLabel(filePath) {
            const state = offlineStateOf(filePath);
            if (!state) return 'Save Offline';
            switch (state.kind) {
                case 'saved': return 'Remove Offline Copy';
                case 'partial': return `Resume Offline Save (${state.savedPages}/${state.pageCount})`;
                default: return 'Saving Offline…';
            }
        }

        function offlineMenuItemHtml(filePath) {
            return `<button class="dropdown-item" data-offline-menu="${escapeHtml(filePath)}" onclick="toggleOfflineCopy('${escapeJs(filePath)}'); closeAllDropdowns();" title="Keep this issue on this device for reading with no connection">
                ${uiIcon('download')} <span>${escapeHtml(offlineMenuLabel(filePath))}</span>
            </button>`;
        }

        // Redraws every offline mark and menu item, and the dialog when it is
        // open. Every change of state redraws, so the queue is kept here too.
        function redrawOfflineMarks() {
            rememberOfflineQueue();
            for (const mark of document.querySelectorAll('[data-offline-path]')) {
                const text = offlineMarkText(mark.dataset.offlinePath);
                mark.textContent = text;
                mark.hidden = !text;
            }
            for (const item of document.querySelectorAll('[data-offline-menu]')) {
                item.querySelector('span').textContent = offlineMenuLabel(item.dataset.offlineMenu);
            }
            if (document.getElementById('offlineComicsModal').classList.contains('active')) {
                renderOfflineComicsList();
            }
        }

        // The sign-in the worker sends with each request: the bearer token, or
        // nothing with Authelia, whose cookies go along by themselves.
        function offlineDownloadHeaders() {
            const headers = getAuthHeaders();
            return headers.Authorization ? { Authorization: headers.Authorization } : {};
        }

        // Saves `filePaths` for offline reading, one after another. Comics
        // already saved, saving or queued are left as they are; interrupted
        // ones resume. More than one is confirmed first: they take space.
        async function saveComicsOffline(filePaths) {
            if (!offlineWorker()) {
                showMessage('Offline reading is not available in this browser.', 'error');
                return;
            }
            listenToOfflineWorker();
            const wanted = [...new Set(filePaths)].filter(path => path && !offlineComics.saved.has(path)
                && !offlineComics.active.has(path) && !offlineComics.queue.includes(path));
            if (!wanted.length) {
                showMessage('Already saved for offline reading.', 'info');
                return;
            }
            if (wanted.length > 1 && !confirm(`Save ${wanted.length} issues on this device for reading offline? They take up storage space until you remove them.`)) {
                return;
            }
            // Ask the browser to keep the downloads when it runs short of space.
            try {
                if (navigator.storage && navigator.storage.persist) await navigator.storage.persist();
            } catch (error) {
                // Saved all the same; the browser may clear them when space runs short.
            }
            offlineComics.queue.push(...wanted);
            showMessage(wanted.length === 1 ? 'Saving for offline reading…' : `Saving ${wanted.length} issues for offline reading…`, 'info');
            redrawOfflineMarks();
            startNextOfflineSave();
        }

        function startNextOfflineSave() {
            if (offlineComics.active.size || !offlineComics.queue.length) return;
            const worker = offlineWorker();
            if (!worker) {
                offlineComics.queue = [];
                redrawOfflineMarks();
                return;
            }
            const filePath = offlineComics.queue.shift();
            offlineComics.active.set(filePath, null);
            redrawOfflineMarks();
            armOfflineSaveStallTimer();
            worker.postMessage({ type: 'DOWNLOAD_COMIC', filePath, headers: offlineDownloadHeaders() });
        }

        function armOfflineSaveStallTimer() {
            clearTimeout(offlineComics.stallTimer);
            offlineComics.stallTimer = offlineComics.active.size ? setTimeout(checkStalledOfflineSave, OFFLINE_SAVE_STALL_MS) : null;
        }

        // Nothing heard for a while: the worker finished without the page
        // hearing, is still busy on a slow page, or was stopped.
        async function checkStalledOfflineSave() {
            const [filePath] = offlineComics.active.keys();
            const answer = await askOfflineWorker({ type: 'LIST_OFFLINE_COMICS' });
            if (!offlineComics.active.has(filePath)) return;
            if (answer && answer.comics.some(comic => comic.filePath === filePath)) {
                finishOfflineSave(filePath, { saved: true });
                return;
            }
            const partial = answer && answer.incomplete.find(comic => comic.filePath === filePath);
            if (partial && partial.active) {
                offlineComics.active.set(filePath, percentOf(partial.savedPages, partial.pageCount));
                redrawOfflineMarks();
                armOfflineSaveStallTimer();
                return;
            }
            finishOfflineSave(filePath, { partial: partial ? { savedPages: partial.savedPages, pageCount: partial.pageCount } : null });
        }

        function handleOfflineWorkerMessage(event) {
            const data = event.data;
            if (!data || !data.filePath || !offlineComics.active.has(data.filePath)) return;
            if (data.type === 'OFFLINE_DOWNLOAD_PROGRESS') {
                offlineComics.active.set(data.filePath, percentOf(data.completed, data.total));
                redrawOfflineMarks();
                armOfflineSaveStallTimer();
            } else if (data.type === 'OFFLINE_DOWNLOAD_COMPLETE') {
                finishOfflineSave(data.filePath, { saved: true, pageCount: data.total });
            } else if (data.type === 'OFFLINE_DOWNLOAD_FAILED') {
                // A dropped connection keeps the pages saved so far; anything
                // else (the server refused the comic, storage ran out) kept none.
                finishOfflineSave(data.filePath, {
                    partial: data.resumable ? { savedPages: data.completed, pageCount: data.total } : null
                });
            }
        }

        // The running save ended: record how, say so, and start the next one.
        function finishOfflineSave(filePath, { saved = false, pageCount = 0, partial = null } = {}) {
            offlineComics.active.delete(filePath);
            const name = filePath.split(/[\\/]/).pop();
            if (saved) {
                offlineComics.saved.set(filePath, pageCount);
                offlineComics.partial.delete(filePath);
                showMessage(offlineComics.queue.length
                    ? `${name} is saved for offline reading; ${offlineComics.queue.length} to go`
                    : `${name} is available for offline reading`, 'success');
            } else {
                if (partial) {
                    offlineComics.partial.set(filePath, partial);
                } else {
                    offlineComics.partial.delete(filePath);
                }
                showMessage(partial
                    ? `${name} was not saved completely (${partial.savedPages} of ${partial.pageCount} pages); save it again to finish`
                    : `${name} could not be saved for offline reading`, 'error');
            }
            armOfflineSaveStallTimer();
            redrawOfflineMarks();
            startNextOfflineSave();
        }

        // Offered for a saved or interrupted copy only, never one queued or saving.
        async function removeOfflineCopy(filePath) {
            const answer = await askOfflineWorker({ type: 'REMOVE_OFFLINE_COMIC', filePath });
            if (!answer) {
                showMessage('The offline copy could not be removed.', 'error');
                redrawOfflineMarks();
                return;
            }
            offlineComics.saved.delete(filePath);
            offlineComics.partial.delete(filePath);
            redrawOfflineMarks();
            showMessage('Removed from offline storage', 'info');
        }

        // The issue menu's item: save the issue, finish its interrupted save,
        // or remove its saved copy.
        function toggleOfflineCopy(filePath) {
            const state = offlineStateOf(filePath);
            if (state && state.kind === 'saved') return removeOfflineCopy(filePath);
            if (state && (state.kind === 'saving' || state.kind === 'queued')) {
                showMessage('Already being saved for offline reading.', 'info');
                return;
            }
            return saveComicsOffline([filePath]);
        }

        // Every issue of one series.
        async function saveSeriesOffline(seriesId) {
            const paths = await fetchSeriesFilePaths(seriesId);
            if (!paths.length) {
                showMessage('This series has no issues to save.', 'error');
                return;
            }
            return saveComicsOffline(paths);
        }

        // Every issue of the selected series (and any issues selected on their own).
        async function saveSelectedOffline() {
            while (true) {
                const pendingLoads = Array.from(selectedSeries, id => selectedSeriesPathLoads.get(id)).filter(Boolean);
                if (!pendingLoads.length) break;
                await Promise.all(pendingLoads);
            }
            const files = Array.from(selectedFiles);
            if (!files.length) {
                showMessage('Select the series to save for offline reading first.', 'error');
                return;
            }
            return saveComicsOffline(files);
        }

        // ── Offline Comics dialog ────────────────────────────────────────────

        async function openOfflineComicsModal() {
            document.getElementById('offlineComicsModal').classList.add('active');
            document.getElementById('offlineComicsSummary').textContent = 'Reading what is saved on this device…';
            renderOfflineComicsList();
            if (!offlineWorker()) {
                document.getElementById('offlineComicsSummary').textContent = 'Offline reading is not available in this browser.';
                return;
            }
            await refreshOfflineComics();
            renderOfflineComicsList();
            await renderOfflineStorage();
        }

        function closeOfflineComicsModal() {
            document.getElementById('offlineComicsModal').classList.remove('active');
        }

        function renderOfflineComicsList() {
            const list = document.getElementById('offlineComicsList');
            const paths = [...new Set([
                ...offlineComics.active.keys(),
                ...offlineComics.queue,
                ...offlineComics.saved.keys(),
                ...offlineComics.partial.keys()
            ])].sort((a, b) => a.localeCompare(b));
            document.getElementById('offlineComicsEmpty').hidden = paths.length > 0;
            renderHtml(list, paths.map(path => {
                const state = offlineStateOf(path);
                const segments = path.split(/[\\/]/);
                const pages = state.kind === 'saved' ? offlineComics.saved.get(path) : 0;
                return html`
                    <li class="offline-comic-row" data-offline-row="${path}">
                        <div class="offline-comic-name">
                            <strong>${segments[segments.length - 1]}</strong>
                            <small>${segments.slice(-2, -1).join('')}${pages ? ` · ${counted(pages, 'page')}` : ''}</small>
                        </div>
                        <span class="offline-comic-state">${offlineMarkText(path)}</span>
                        <div class="offline-comic-actions">
                            ${state.kind === 'saved'
                                ? html`<button type="button" class="btn btn-small btn-primary" onclick="readComic('${jsArg(path)}')">${uiIcon('book-open')} Read</button>`
                                : ''}
                            ${state.kind === 'partial'
                                ? html`<button type="button" class="btn btn-small" onclick="saveComicsOffline(['${jsArg(path)}'])">${uiIcon('download')} Resume</button>`
                                : ''}
                            ${state.kind === 'saved' || state.kind === 'partial'
                                ? html`<button type="button" class="btn btn-small" onclick="removeOfflineCopy('${jsArg(path)}')">${uiIcon('trash')} Remove</button>`
                                : ''}
                        </div>
                    </li>
                `;
            }));
        }

        async function renderOfflineStorage() {
            const summary = document.getElementById('offlineComicsSummary');
            const count = offlineComics.saved.size;
            let text = `${counted(count, 'comic')} saved on this device.`;
            try {
                // Not every browser reports storage (nor does the test DOM).
                const storage = navigator.storage || {};
                if (storage.estimate) {
                    const { usage, quota } = await storage.estimate();
                    text += ` This site uses ${formatFileSize(usage)} of the ${formatFileSize(quota)} the browser allows it.`;
                }
                if (count && storage.persisted && !(await storage.persisted())) {
                    text += ' The browser may remove them if the device runs short of space.';
                }
            } catch (error) {
                // The count alone, then.
            }
            summary.textContent = text;
        }
