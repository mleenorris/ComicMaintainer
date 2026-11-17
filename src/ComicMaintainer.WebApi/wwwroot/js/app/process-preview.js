        // ComicMaintainer SPA — Preview Changes: what Process would do, before it does it.
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.
        //
        // A preview is a background job on the server (POST
        // /api/files/process-preview) that works out, for each file, the new
        // name and the tag changes processing would make, and changes nothing.
        // Its progress shows in the usual progress modal; when it ends, the
        // results open here, grouped by series. Processing the ticked files
        // starts the usual process-selected job, as a forced reprocess, since
        // that is what the preview showed.

        // Results are read in pages of this many files.
        const PROCESS_PREVIEW_PAGE_SIZE = 200;
        // The most files one read asks for when collecting the files to process.
        const PROCESS_PREVIEW_APPLY_PAGE_SIZE = 1000;

        // The preview shown in the modal: { jobId, label, filter, items, total,
        // summary, status, choices }. `choices` holds the ticks the user
        // changed, by file path; other files keep their default tick.
        let processPreviewState = null;

        function previewProcessAll() {
            startProcessPreview('all', { label: 'all files' });
        }

        function previewProcessSelected() {
            if (selectedFiles.size === 0) {
                showMessage('Please select at least one file to preview', 'error');
                return;
            }
            const count = selectedFiles.size;
            startProcessPreview('selected', {
                files: Array.from(selectedFiles),
                label: `${count} selected file${count === 1 ? '' : 's'}`
            });
        }

        function previewSeriesProcessing(seriesId, seriesTitle) {
            startProcessPreview('series', { seriesId, label: seriesTitle });
        }

        // Starts the preview job and follows it in the progress modal; its
        // results open once it ends.
        async function startProcessPreview(scope, { files, seriesId, label }) {
            if (!jobProgress.canStartJob()) return;
            showProgressModal('Starting preview...');
            const body = { scope };
            if (files) body.files = files;
            if (seriesId) body.seriesId = seriesId;
            try {
                const data = await apiFetch('/api/files/process-preview', {
                    method: 'POST',
                    body,
                    errorMessage: 'Failed to start the preview'
                });
                if (isEmptyJobStart(data)) {
                    closeProgressModal();
                    showMessage('No files to preview', 'info');
                    return;
                }
                jobProgress.onJobFinished(data.job_id, () => openProcessPreviewModal(data.job_id, label));
                showMessage(`Previewing changes to ${data.total_items} file${data.total_items === 1 ? '' : 's'}. Nothing is changed yet.`, 'info');
                await trackJobStatus(data.job_id, 'Previewing Changes...');
            } catch (error) {
                if (error.authHandled) return;
                showMessage('Failed to start the preview: ' + error.message, 'error');
                closeProgressModal();
            }
        }

        async function openProcessPreviewModal(jobId, label) {
            const state = { jobId, label, filter: 'changes', items: [], total: 0, summary: null, status: '', choices: new Map() };
            processPreviewState = state;
            document.getElementById('processPreviewModalTitle').textContent = `Preview changes: ${label}`;
            document.getElementById('processPreviewOnlyChanges').checked = true;
            document.getElementById('processPreviewSummary').textContent = 'Loading the preview...';
            clearChildren(document.getElementById('processPreviewList'));
            document.getElementById('processPreviewEmptyState').style.display = 'none';
            document.getElementById('processPreviewMoreBtn').style.display = 'none';
            document.getElementById('processPreviewApplyBtn').disabled = true;
            document.getElementById('processPreviewModal').classList.add('active');
            await loadProcessPreviewPage(state);
        }

        function closeProcessPreviewModal() {
            document.getElementById('processPreviewModal').classList.remove('active');
            processPreviewState = null;
        }

        function processPreviewUrl(jobId, filter, offset, limit) {
            return `/api/files/process-preview/${encodeURIComponent(jobId)}?filter=${filter}&offset=${offset}&limit=${limit}`;
        }

        // Reads the next page of the current view and shows it.
        async function loadProcessPreviewPage(state) {
            let page;
            try {
                page = await apiFetch(processPreviewUrl(state.jobId, state.filter, state.items.length, PROCESS_PREVIEW_PAGE_SIZE));
            } catch (error) {
                if (error.authHandled) return;
                if (processPreviewState === state) {
                    document.getElementById('processPreviewSummary').textContent = `Could not load the preview: ${error.message}`;
                }
                return;
            }
            // The modal was closed, or shows another preview or view, meanwhile.
            if (processPreviewState !== state) return;
            state.items.push(...page.items);
            state.total = page.total;
            state.summary = page.summary;
            state.status = page.status;
            renderProcessPreview(state);
        }

        // The modal's own controls call these, so a preview is always open.
        function loadMoreProcessPreview() {
            return loadProcessPreviewPage(processPreviewState);
        }

        // "Only changes" off lists every file, unchanged ones included.
        function toggleProcessPreviewOnlyChanges() {
            const state = {
                ...processPreviewState,
                filter: document.getElementById('processPreviewOnlyChanges').checked ? 'changes' : 'all',
                items: []
            };
            processPreviewState = state;
            return loadProcessPreviewPage(state);
        }

        // A file is ticked unless it would not get its new name or would fail;
        // a file processing would leave alone has nothing to process.
        function isDefaultProcessPreviewTick(item) {
            return item.changed && !item.clash && !item.error;
        }

        function isProcessPreviewItemTicked(state, item) {
            return state.choices.has(item.filePath) ? state.choices.get(item.filePath) : isDefaultProcessPreviewTick(item);
        }

        function toggleProcessPreviewItem(box) {
            processPreviewState.choices.set(box.closest('[data-file-path]').dataset.filePath, box.checked);
        }

        function describeProcessPreviewSummary(state) {
            const s = state.summary;
            const parts = [
                `${s.renamed} renamed`,
                `${s.tagsChanged} with tag changes`,
                `${s.clashes} name clash${s.clashes === 1 ? '' : 'es'}`,
                `${s.errors} that would fail`,
                `${s.unchanged} unchanged`
            ];
            const text = `Checked ${s.checked} file${s.checked === 1 ? '' : 's'}: ${parts.join(', ')}.`;
            return state.status === 'completed'
                ? text
                : `${text} The preview stopped (${state.status}) before it checked every file.`;
        }

        function processPreviewClashText(item) {
            // The server names the other file of every clash.
            const other = extractDisplayName(item.clashWith);
            return item.clash === 'preview'
                ? `${other} would get this name first, so processing would flag this file as a duplicate.`
                : `${other} already has this name, so processing would flag this file as a duplicate.`;
        }

        function renderProcessPreviewItem(state, item) {
            const classes = ['process-preview-item'];
            if (item.clash) classes.push('process-preview-item--clash');
            if (item.error) classes.push('process-preview-item--error');
            return html`
                <div class="${classes.join(' ')}" data-file-path="${item.filePath}">
                    <label class="process-preview-name">
                        ${item.changed ? html`<input type="checkbox" ${isProcessPreviewItemTicked(state, item) ? 'checked' : ''} onchange="toggleProcessPreviewItem(this)">` : ''}
                        <span>${item.fileName}</span>${item.newFileName ? html` <span aria-hidden="true">→</span> <strong>${item.newFileName}</strong>` : ''}
                    </label>
                    ${item.clash ? html`<div class="process-preview-note process-preview-note--clash">${processPreviewClashText(item)}</div>` : ''}
                    ${item.tagChanges.length ? html`
                        <ul class="process-preview-tags">
                            ${item.tagChanges.map(change => html`<li><span class="process-preview-field">${change.field}</span>: ${change.before ?? '(empty)'} → ${change.after ?? '(removed)'}</li>`)}
                        </ul>` : ''}
                    ${item.warning ? html`<div class="process-preview-note">${item.warning}</div>` : ''}
                    ${item.error ? html`<div class="process-preview-note process-preview-note--error">${item.error}</div>` : ''}
                </div>
            `;
        }

        // The items grouped by series; the server sorts them by series, so a
        // series' files are always next to each other.
        function groupProcessPreviewItems(items) {
            const groups = [];
            for (const item of items) {
                const series = item.series || '(no series)';
                const last = groups[groups.length - 1];
                if (last && last.series === series) last.items.push(item);
                else groups.push({ series, items: [item] });
            }
            return groups;
        }

        function renderProcessPreview(state) {
            document.getElementById('processPreviewSummary').textContent = describeProcessPreviewSummary(state);
            const empty = document.getElementById('processPreviewEmptyState');
            empty.style.display = state.total === 0 ? 'block' : 'none';
            empty.textContent = state.filter === 'changes'
                ? 'Processing would change nothing in these files.'
                : 'The preview has not checked any file.';
            renderHtml(document.getElementById('processPreviewList'), groupProcessPreviewItems(state.items).map(group => html`
                <section class="process-preview-series">
                    <h3 class="process-preview-series-title">${group.series} · ${group.items.length} file${group.items.length === 1 ? '' : 's'}</h3>
                    ${group.items.map(item => renderProcessPreviewItem(state, item))}
                </section>
            `));
            const more = document.getElementById('processPreviewMoreBtn');
            more.style.display = state.items.length < state.total ? 'inline-block' : 'none';
            more.textContent = `Show more (${state.total - state.items.length} left)`;
            document.getElementById('processPreviewApplyBtn').disabled = state.summary.changed === 0;
        }

        // Every file with changes, page by page: the ticks cover files not
        // shown yet too.
        async function fetchAllProcessPreviewChanges(jobId) {
            const items = [];
            for (;;) {
                const page = await apiFetch(processPreviewUrl(jobId, 'changes', items.length, PROCESS_PREVIEW_APPLY_PAGE_SIZE));
                items.push(...page.items);
                if (page.items.length === 0 || items.length >= page.total) return items;
            }
        }

        // Processes the ticked files: the process-selected job, forced, so
        // files already marked processed are processed as the preview showed.
        async function applyProcessPreview() {
            const state = processPreviewState;
            if (!jobProgress.canStartJob()) return;
            let changes;
            try {
                changes = await fetchAllProcessPreviewChanges(state.jobId);
            } catch (error) {
                if (error.authHandled) return;
                showMessage('Could not read the preview: ' + error.message, 'error');
                return;
            }
            if (processPreviewState !== state) return;
            const files = changes.filter(item => isProcessPreviewItemTicked(state, item)).map(item => item.filePath);
            if (files.length === 0) {
                showMessage('Tick at least one file to process', 'warning');
                return;
            }
            const count = `${files.length} file${files.length === 1 ? '' : 's'}`;
            if (!confirm(`Process ${count} now? They are renamed and their tags are written as the preview shows.`)) return;
            closeProcessPreviewModal();
            showProgressModal('Starting processing...');
            try {
                const data = await apiFetch('/api/jobs/process-selected', {
                    method: 'POST',
                    body: { Files: files, ForceReprocess: true },
                    errorMessage: 'Failed to start processing job'
                });
                showMessage(`Started processing ${count} in background`, 'info');
                await trackJobStatus(data.job_id, 'Processing Previewed Files...');
            } catch (error) {
                if (error.authHandled) return;
                showMessage('Failed to start processing: ' + error.message, 'error');
                closeProgressModal();
            }
        }
