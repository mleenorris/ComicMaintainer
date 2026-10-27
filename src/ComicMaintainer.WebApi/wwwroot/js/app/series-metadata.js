        // ComicMaintainer SPA — external series metadata, covers and series name/alias management.
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

        // ====================================================================
        // External Series Metadata (manual refresh + alias management)
        // ====================================================================

        let manageSeriesState = { seriesTitle: '', record: null, aliasesEdited: false, recordLoaded: false };

        // Keep a record a Manage Names request sent back. The other controls
        // save straight away but alias edits wait for Save, so once the list
        // has been edited the edits replace the stored aliases in it, for Save
        // to persist. Without a loaded record the stored aliases were never
        // listed, so the aliases added meanwhile are merged into them instead.
        function setManageSeriesRecord(record) {
            const { record: current, aliasesEdited, recordLoaded } = manageSeriesState;
            if (aliasesEdited && current && record) {
                const edited = current.user_aliases || [];
                if (recordLoaded) {
                    record.user_aliases = edited;
                } else {
                    const merged = [...(record.user_aliases || [])];
                    edited.forEach(alias => {
                        if (!merged.some(a => a.toLowerCase() === alias.toLowerCase())) merged.push(alias);
                    });
                    record.user_aliases = merged;
                }
            }
            manageSeriesState.record = record;
            manageSeriesState.recordLoaded = Boolean(record);
        }

        async function refreshAllExternalMetadata() {
            if (!confirm('Queue an external metadata refresh for every series in the library? Lookups happen in the background and progress is reported as a job.')) {
                return;
            }
            try {
                const response = await fetch(apiUrl('/api/metadata/refresh-all'), {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json', ...getAuthHeaders() },
                    credentials: 'same-origin'
                });
                if (!response.ok) {
                    const error = await response.text();
                    showMessage('Failed to queue metadata refresh: ' + error, 'error');
                    return;
                }
                const data = await response.json();
                showMessage(`Metadata refresh job queued for ${data.totalSeries || 0} series.`, 'success');
                // Surface progress through the same toast/poll flow used by the
                // per-series refresh so the user can see the job advancing.
                if (data.jobId) {
                    trackMetadataRefreshJob(data.jobId, `All Series (${data.totalSeries || 0})`);
                }
            } catch (err) {
                console.error('refreshAllExternalMetadata failed', err);
                showMessage('Failed to queue metadata refresh', 'error');
            }
        }

        async function matchAllUnmatchedSeries() {
            if (!confirm('Queue an external metadata lookup for every series that is not yet matched? Lookups happen in the background and progress is reported as a job.')) {
                return;
            }
            try {
                const response = await fetch(apiUrl('/api/metadata/match-unmatched'), {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json', ...getAuthHeaders() },
                    credentials: 'same-origin'
                });
                if (!response.ok) {
                    const error = await response.text();
                    showMessage('Failed to queue match-unmatched: ' + error, 'error');
                    return;
                }
                const data = await response.json();
                const total = data.totalSeries || 0;
                if (total === 0) {
                    showMessage('No unmatched series to process.', 'info');
                    return;
                }
                showMessage(`Match job queued for ${total} unmatched series.`, 'success');
                if (data.jobId) {
                    trackMetadataRefreshJob(data.jobId, `Unmatched Series (${total})`);
                }
            } catch (err) {
                console.error('matchAllUnmatchedSeries failed', err);
                showMessage('Failed to queue match-unmatched', 'error');
            }
        }

        function getSelectedSeriesTitlesForCoverUpdate() {
            if (libraryViewMode !== 'series') {
                return [];
            }
            // Series detail view: a single series is "selected" simply by being
            // open, so target that series even though the multi-select
            // checkboxes (and `selectedSeries`) only apply to the top-level grid.
            if (currentSeriesDetailId) {
                const detail = currentSeriesDetailSeries;
                const title = detail
                    ? (detail.canonical_title || detail.title || '').trim()
                    : '';
                return title ? [title] : [];
            }
            const seen = new Set();
            return seriesLibrary
                .filter(series => selectedSeries.has(series.id))
                .map(series => (series.canonical_title || series.title || '').trim())
                .filter(title => {
                    if (!title) return false;
                    const key = title.toLowerCase();
                    if (seen.has(key)) return false;
                    seen.add(key);
                    return true;
                });
        }

        async function updateAllSeriesCovers() {
            if (!confirm('Re-apply the current cached series image to cover sidecars and first-issue archives for ALL series in the library? This may rewrite archive files and can take a while.')) {
                return;
            }
            showProgressModal('Starting cover update...');
            try {
                const response = await fetch(apiUrl('/api/series-images/apply-current/all'), {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json', ...getAuthHeaders() },
                    credentials: 'same-origin'
                });
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    const error = await response.text();
                    closeProgressModal();
                    showMessage('Failed to update series covers: ' + error, 'error');
                    return;
                }
                const data = await response.json();
                await trackSeriesCoverJob(data, 'Embedding Covers (All Series)...');
            } catch (err) {
                console.error('updateAllSeriesCovers failed', err);
                closeProgressModal();
                showMessage('Failed to update series covers', 'error');
            }
        }

        // Shared handler for the series-cover apply endpoints, which now return a
        // background job (job_id / total_items) and run through the same SSE
        // progress pipeline as process / rename / normalize so the user can see
        // each series' cover being embedded. An empty job id (or zero items)
        // means no series had a cached image to apply.
        async function trackSeriesCoverJob(data, title) {
            const jobId = data && (data.job_id || data.jobId);
            const totalItems = data ? (data.total_items || 0) : 0;
            const skipped = data ? (data.skippedSeries || 0) : 0;
            const EMPTY_JOB_ID = '00000000-0000-0000-0000-000000000000';
            if (!jobId || jobId === EMPTY_JOB_ID || totalItems === 0) {
                closeProgressModal();
                showMessage(
                    skipped > 0
                        ? `No covers embedded: none of the ${skipped} series have a cached image to apply.`
                        : 'No covers embedded: no series have a cached image to apply.',
                    'info');
                return;
            }
            showMessage(
                `Embedding covers for ${totalItems} series in background${skipped > 0 ? ` (${skipped} skipped — no cached image)` : ''}`,
                'info');
            await trackJobStatus(jobId, title);
            if (typeof loadSeriesLibrary === 'function') {
                loadSeriesLibrary(1, true);
            }
        }

        async function updateSelectedSeriesCovers() {
            const series = getSelectedSeriesTitlesForCoverUpdate();
            if (!series.length) {
                showMessage('Select at least one series from the library view', 'error');
                return;
            }
            if (!confirm(`Re-apply the current cached series image to ${series.length} selected series${series.length === 1 ? '' : 'es'}? This may rewrite archive files.`)) {
                return;
            }
            showProgressModal('Starting cover update...');
            try {
                const response = await fetch(apiUrl('/api/series-images/apply-current/selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    credentials: 'same-origin',
                    body: JSON.stringify({ series })
                });
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    const error = await response.text();
                    closeProgressModal();
                    showMessage('Failed to update selected series covers: ' + error, 'error');
                    return;
                }
                const data = await response.json();
                await trackSeriesCoverJob(data, 'Embedding Covers (Selected Series)...');
            } catch (err) {
                console.error('updateSelectedSeriesCovers failed', err);
                closeProgressModal();
                showMessage('Failed to update selected series covers', 'error');
            }
        }

        // Re-apply the current cached series image to a single series from the
        // series-detail actions menu. Mirrors updateSelectedSeriesCovers but
        // targets exactly one series by title.
        async function updateSeriesCoversDirect(seriesTitle) {
            if (!seriesTitle) return;
            if (!confirm(`Re-apply the current cached series image to "${seriesTitle}"? This may rewrite the first-issue archive file.`)) {
                return;
            }
            showProgressModal('Starting cover update...');
            try {
                const response = await fetch(apiUrl('/api/series-images/apply-current/selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    credentials: 'same-origin',
                    body: JSON.stringify({ series: [seriesTitle] })
                });
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    const error = await response.text();
                    closeProgressModal();
                    showMessage('Failed to update series covers: ' + error, 'error');
                    return;
                }
                const data = await response.json();
                await trackSeriesCoverJob(data, `Embedding Cover ("${seriesTitle}")...`);
            } catch (err) {
                console.error('updateSeriesCoversDirect failed', err);
                closeProgressModal();
                showMessage('Failed to update series covers', 'error');
            }
        }

        async function refreshSeriesMetadataDirect(seriesTitle) {
            if (!seriesTitle) return;
            try {
                // Always queue the refresh as a background job so the UI is never
                // blocked on the (potentially slow) external lookup. Progress is
                // surfaced via the same inline toast used by refresh-all /
                // refresh-folder.
                const response = await fetch(apiUrl(`/api/metadata/refresh/${encodeURIComponent(seriesTitle)}?queue=true`), {
                    method: 'POST',
                    headers: getAuthHeaders ? getAuthHeaders() : undefined,
                    credentials: 'same-origin'
                });
                if (!response.ok) {
                    showMessage('Failed to refresh metadata for ' + seriesTitle, 'error');
                    return;
                }
                const data = await response.json();
                if (data && data.jobId) {
                    showMessage(`Queued metadata refresh for "${seriesTitle}"`, 'info');
                    trackMetadataRefreshJob(data.jobId, seriesTitle);
                } else {
                    // Fallback: server returned the sync record (legacy path).
                    const status = data.lookup_status || 'success';
                    if (status === 'not_found') {
                        showMessage(`No external metadata found for "${seriesTitle}"`, 'info');
                    } else if (status === 'error') {
                        showMessage(`External lookup failed for "${seriesTitle}"`, 'error');
                    } else {
                        showMessage(`Metadata refreshed for "${seriesTitle}"${data.source ? ' from ' + data.source : ''}`, 'success');
                    }
                    if (typeof loadSeriesLibrary === 'function') {
                        loadSeriesLibrary(1, true);
                    }
                    loadProviderHealth();
                }
            } catch (err) {
                console.error('refreshSeriesMetadataDirect failed', err);
                showMessage('Failed to refresh metadata', 'error');
            }
        }

        // Queue a metadata refresh for every title that maps to one series card.
        // Surfaces progress through the same toast/poll flow as the single-title
        // refresh so the user can see something is happening.
        async function refreshSeriesFolder(seriesId, seriesTitle) {
            if (!seriesId) return;
            try {
                const response = await fetch(apiUrl('/api/metadata/refresh/folder'), {
                    method: 'POST',
                    headers: Object.assign(
                        { 'Content-Type': 'application/json' },
                        getAuthHeaders ? getAuthHeaders() : {}),
                    credentials: 'same-origin',
                    body: JSON.stringify({ seriesId })
                });
                if (response.status === 404) {
                    showMessage(`Series "${seriesTitle || seriesId}" not found`, 'error');
                    return;
                }
                if (!response.ok) {
                    showMessage('Failed to queue folder metadata refresh', 'error');
                    return;
                }
                const data = await response.json();
                showMessage(`Queued metadata refresh for ${data.totalSeries} title(s) in "${seriesTitle || seriesId}"`, 'info');
                trackMetadataRefreshJob(data.jobId, seriesTitle || seriesId);
            } catch (err) {
                console.error('refreshSeriesFolder failed', err);
                showMessage('Failed to queue folder metadata refresh', 'error');
            }
        }

        async function resetSeriesProcessedStatus(seriesId, seriesTitle) {
            if (!seriesId) return;
            const label = seriesTitle || seriesId;
            const proceed = confirm(
                `Reset the renamed/normalized flags on every file in "${label}"?\n\n` +
                'The files will then be re-processed on the next Process / Rename / Normalize run. ' +
                'Use this when a series metadata or filename change refuses to apply.');
            if (!proceed) return;
            try {
                const response = await fetch(apiUrl(`/api/status/clear-series/${encodeURIComponent(seriesId)}`), {
                    method: 'POST',
                    headers: Object.assign(
                        { 'Content-Type': 'application/json' },
                        getAuthHeaders ? getAuthHeaders() : {}),
                    credentials: 'same-origin'
                });
                if (response.status === 404) {
                    showMessage(`Series "${label}" not found`, 'error');
                    return;
                }
                if (!response.ok) {
                    showMessage('Failed to reset processed status', 'error');
                    return;
                }
                const data = await response.json();
                showMessage(
                    `Cleared processed status on ${data.cleared} of ${data.requested} file(s) in "${label}". ` +
                    'Run Process / Rename / Normalize to reapply.',
                    'success');
                if (typeof loadSeriesLibrary === 'function') {
                    loadSeriesLibrary({ refresh: true });
                }
            } catch (err) {
                console.error('resetSeriesProcessedStatus failed', err);
                showMessage('Failed to reset processed status', 'error');
            }
        }

        // Lightweight poller for metadata refresh jobs. We intentionally don't
        // hook into the SSE current-job slot (which is reserved for the heavy
        // scan/process pipeline) — these short jobs poll their own status and
        // render an inline progress toast.
        function trackMetadataRefreshJob(jobId, label) {
            if (!jobId || metadataRefreshJobs.has(jobId)) return;
            metadataRefreshJobs.set(jobId, { label });
            const intervalMs = 1500;
            const tick = async () => {
                try {
                    const response = await fetch(apiUrl(`/api/metadata/refresh/job/${jobId}`), {
                        headers: getAuthHeaders ? getAuthHeaders() : undefined,
                        credentials: 'same-origin'
                    });
                    if (!response.ok) {
                        metadataRefreshJobs.delete(jobId);
                        return;
                    }
                    const job = await response.json();
                    renderMetadataRefreshToast(jobId, label, job);
                    if (isTerminalJobStatus(metadataRefreshJobStatus(job))) {
                        metadataRefreshJobs.delete(jobId);
                        // Final library refresh + provider health update.
                        if (typeof loadSeriesLibrary === 'function') {
                            loadSeriesLibrary({ refresh: true });
                        }
                        loadProviderHealth();
                        return;
                    }
                    setTimeout(tick, intervalMs);
                } catch (err) {
                    console.warn('metadata refresh poll failed', err);
                    metadataRefreshJobs.delete(jobId);
                }
            };
            setTimeout(tick, intervalMs);
        }

        // The job's status as the lower-case name isTerminalJobStatus() and the
        // toast's CSS modifiers use. The API sends the JobStatus name
        // ("Running", "Completed", ...).
        function metadataRefreshJobStatus(job) {
            return (job.status || job.Status || 'queued').toString().toLowerCase();
        }

        function renderMetadataRefreshToast(jobId, label, job) {
            let host = document.getElementById('metadataRefreshToasts');
            if (!host) {
                host = document.createElement('div');
                host.id = 'metadataRefreshToasts';
                host.className = 'metadata-refresh-toasts';
                document.body.appendChild(host);
            }
            let toast = document.getElementById(`mdrToast-${jobId}`);
            if (!toast) {
                toast = document.createElement('div');
                toast.id = `mdrToast-${jobId}`;
                toast.className = 'metadata-refresh-toast';
                host.appendChild(toast);
            }
            const total = job.totalSeries || job.TotalSeries || 0;
            const processed = job.processedSeries || job.ProcessedSeries || 0;
            const successes = job.successes || job.Successes || 0;
            const failures = job.failures || job.Failures || 0;
            const status = metadataRefreshJobStatus(job);
            const current = job.currentSeries || job.CurrentSeries || '';
            const pct = total > 0 ? Math.round((processed / total) * 100) : 0;
            renderHtml(toast, html`
                <div class="metadata-refresh-toast-header">
                    <strong>Refreshing ${label}</strong>
                    <span class="metadata-refresh-toast-status metadata-refresh-toast-status--${status}">${status}</span>
                </div>
                <div class="metadata-refresh-toast-bar"><div class="metadata-refresh-toast-bar-fill" style="width:${pct}%"></div></div>
                <div class="metadata-refresh-toast-meta">${processed}/${total} · ✓ ${successes} · ✗ ${failures}${current ? html` · now: ${current}` : ''}</div>
            `);
            if (isTerminalJobStatus(status)) {
                toast.classList.add('metadata-refresh-toast--done');
                setTimeout(() => { toast.remove(); }, 6000);
            }
        }

        async function openManageSeriesNamesModal(seriesTitle) {
            manageSeriesState = { seriesTitle, record: null, aliasesEdited: false, recordLoaded: false };
            document.getElementById('manageSeriesTitle').textContent = seriesTitle;
            document.getElementById('manageSeriesProviderAliases').textContent = 'Loading...';
            document.getElementById('manageSeriesProviderSource').textContent = '';
            document.getElementById('manageSeriesUserAliases').textContent = 'Loading...';
            const nameSelect = document.getElementById('manageSeriesName');
            if (nameSelect) {
                renderHtml(nameSelect, html`<option value="">Automatic (use language preference)</option>`);
                nameSelect.value = '';
            }
            const resolved = document.getElementById('manageSeriesResolvedName');
            if (resolved) resolved.textContent = '';
            document.getElementById('manageSeriesSearchInput').value = seriesTitle;
            clearChildren(document.getElementById('manageSeriesSearchResults'));
            document.getElementById('manageSeriesNamesModal').classList.add('active');
            await loadManageSeriesRecord();
        }

        function closeManageSeriesNamesModal() {
            document.getElementById('manageSeriesNamesModal').classList.remove('active');
            manageSeriesState = { seriesTitle: '', record: null, aliasesEdited: false, recordLoaded: false };
        }

        async function loadManageSeriesRecord() {
            const { seriesTitle } = manageSeriesState;
            if (!seriesTitle) return;
            try {
                const response = await fetch(apiUrl(`/api/metadata/series/${encodeURIComponent(seriesTitle)}`), {
                    headers: getAuthHeaders(),
                    credentials: 'same-origin'
                });
                if (!response.ok) {
                    showMessage('Failed to load series metadata', 'error');
                    return;
                }
                const record = await response.json();
                manageSeriesState.record = record;
                manageSeriesState.recordLoaded = true;
                manageSeriesState.aliasesEdited = false;
                const prefSelect = document.getElementById('manageSeriesPreferredLanguage');
                if (prefSelect) {
                    prefSelect.value = record.preferred_language || '';
                }
                populateManageSeriesNameOptions(record);
                renderManageSeriesProviderAliases(record);
                renderManageSeriesUserAliases(record);
                renderManageSeriesImage(record);
            } catch (err) {
                console.error('loadManageSeriesRecord failed', err);
                showMessage('Failed to load series metadata', 'error');
            }
        }

        function renderManageSeriesImage(record) {
            const preview = document.getElementById('manageSeriesImagePreview');
            const status = document.getElementById('manageSeriesImageStatus');
            if (!preview || !status) return;
            // Reset preview before fetching the new one to avoid showing a
            // stale image when switching series.
            preview.removeAttribute('src');
            const key = record && record.series_id;
            if (record && record.has_image && key) {
                const isUser = !!record.is_user_image;
                const downloadedAt = record.image_downloaded_utc ? new Date(record.image_downloaded_utc).toLocaleString() : 'unknown';
                status.textContent = `${isUser ? 'User-uploaded' : 'Downloaded from provider'} · ${downloadedAt}`;
                fetchProtectedImageInto(preview, `/api/series-images/${encodeURIComponent(key)}`);
            } else {
                status.textContent = 'No image cached. Refresh metadata or upload one below.';
            }
        }

        async function fetchProtectedImageInto(imgElement, apiPath) {
            try {
                const response = await fetch(apiUrl(apiPath), { headers: getAuthHeaders() });
                if (!response.ok) return;
                const blob = await response.blob();
                imgElement.src = URL.createObjectURL(blob);
            } catch (err) {
                console.error('fetchProtectedImageInto failed', err);
            }
        }

        async function uploadManageSeriesImage() {
            const { seriesTitle } = manageSeriesState;
            if (!seriesTitle) return;
            const input = document.getElementById('manageSeriesImageInput');
            const file = input && input.files && input.files[0];
            if (!file) return;
            // Allow only image content-types client-side; the server enforces
            // the same allowlist + magic-byte validation.
            if (!/^image\/(jpeg|png|webp)$/i.test(file.type)) {
                showMessage('Only JPEG, PNG, or WEBP images are supported', 'error');
                input.value = '';
                return;
            }
            const formData = new FormData();
            formData.append('file', file);
            try {
                // Let the browser set the multipart Content-Type (with
                // boundary); getAuthHeaders() defaults it to JSON.
                const headers = getAuthHeaders();
                delete headers['Content-Type'];
                const response = await fetch(apiUrl(`/api/series-images/${encodeURIComponent(seriesTitle)}`), {
                    method: 'PUT',
                    headers,
                    body: formData
                });
                if (!response.ok) {
                    let msg = 'Failed to upload series image';
                    try { const j = await response.json(); if (j && j.error) msg = j.error; } catch {}
                    showMessage(msg, 'error');
                    return;
                }
                const record = await response.json();
                setManageSeriesRecord(record);
                renderManageSeriesImage(record);
                showMessage('Series image updated', 'success');
                if (typeof loadSeriesLibrary === 'function') {
                    loadSeriesLibrary(1, true);
                }
            } catch (err) {
                console.error('uploadManageSeriesImage failed', err);
                showMessage('Failed to upload series image', 'error');
            } finally {
                input.value = '';
            }
        }

        async function clearManageSeriesImage() {
            const record = manageSeriesState.record;
            const key = record && record.series_id;
            if (!key) {
                showMessage('No image to clear', 'info');
                return;
            }
            try {
                const response = await fetch(apiUrl(`/api/series-images/${encodeURIComponent(key)}`), {
                    method: 'DELETE',
                    headers: getAuthHeaders()
                });
                if (!response.ok && response.status !== 404) {
                    showMessage('Failed to clear series image', 'error');
                    return;
                }
                if (response.ok) {
                    const updated = await response.json();
                    setManageSeriesRecord(updated);
                    renderManageSeriesImage(updated);
                }
                showMessage('Series image cleared', 'success');
                if (typeof loadSeriesLibrary === 'function') {
                    loadSeriesLibrary(1, true);
                }
            } catch (err) {
                console.error('clearManageSeriesImage failed', err);
                showMessage('Failed to clear series image', 'error');
            }
        }

        // ---- Fetch series image from external provider --------------------

        function openFetchSeriesImageFromProvider() {
            const panel = document.getElementById('manageSeriesImageProviderPanel');
            if (!panel) return;
            panel.style.display = 'block';
            const queryInput = document.getElementById('manageSeriesImageProviderQuery');
            if (queryInput) {
                queryInput.value = (manageSeriesState && manageSeriesState.seriesTitle) || '';
                queryInput.focus();
                queryInput.select();
                // Convenience: press Enter to search.
                queryInput.onkeydown = (e) => {
                    if (e.key === 'Enter') {
                        e.preventDefault();
                        searchSeriesImageCandidates();
                    }
                };
            }
            const results = document.getElementById('manageSeriesImageProviderResults');
            if (results) clearChildren(results);
            const status = document.getElementById('manageSeriesImageProviderStatus');
            if (status) status.textContent = 'Enter a title and click Search to find cover candidates from the configured providers.';
        }

        function closeFetchSeriesImageFromProvider() {
            const panel = document.getElementById('manageSeriesImageProviderPanel');
            if (panel) panel.style.display = 'none';
        }

        async function searchSeriesImageCandidates() {
            const queryInput = document.getElementById('manageSeriesImageProviderQuery');
            const status = document.getElementById('manageSeriesImageProviderStatus');
            const results = document.getElementById('manageSeriesImageProviderResults');
            if (!queryInput || !status || !results) return;
            const query = (queryInput.value || '').trim();
            if (!query) {
                status.textContent = 'Enter a title to search.';
                return;
            }
            status.textContent = 'Searching providers...';
            clearChildren(results);
            try {
                const response = await fetch(
                    apiUrl(`/api/series-images/candidates?query=${encodeURIComponent(query)}&limit=12`),
                    { headers: getAuthHeaders() }
                );
                if (!response.ok) {
                    status.textContent = 'Search failed.';
                    return;
                }
                const data = await response.json();
                const candidates = (data && data.candidates) || [];
                if (!candidates.length) {
                    status.textContent = 'No provider returned a cover image for that query.';
                    return;
                }
                status.textContent = `${candidates.length} candidate${candidates.length === 1 ? '' : 's'} — click a thumbnail to apply.`;
                clearChildren(results);
                candidates.forEach((cand) => {
                    const previewUrl = cand.thumbnail_url || cand.image_url;
                    const card = document.createElement('div');
                    card.style.cssText = 'display: flex; flex-direction: column; align-items: center; gap: 4px; width: 90px; cursor: pointer; padding: 4px; border-radius: 4px; background: var(--bg-primary); border: 1px solid var(--border-primary);';
                    card.title = `${cand.canonical_title || ''} (${cand.source || ''})`;
                    // Use DOM APIs (not innerHTML) so provider-controlled text
                    // can't introduce script in the picker.
                    const img = document.createElement('img');
                    img.alt = cand.canonical_title || 'Series cover';
                    img.referrerPolicy = 'no-referrer';
                    img.loading = 'lazy';
                    img.style.cssText = 'width: 80px; height: 120px; object-fit: cover; border-radius: 4px; background: var(--bg-hover);';
                    img.src = previewUrl;
                    const sourceLabel = document.createElement('div');
                    sourceLabel.textContent = cand.source || '';
                    sourceLabel.style.cssText = 'font-size: 11px; color: var(--text-secondary);';
                    const titleLabel = document.createElement('div');
                    titleLabel.textContent = cand.canonical_title || '';
                    titleLabel.style.cssText = 'font-size: 11px; color: var(--text-primary); text-align: center; max-width: 88px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;';
                    card.appendChild(img);
                    card.appendChild(titleLabel);
                    card.appendChild(sourceLabel);
                    card.onclick = () => applySeriesImageCandidate(cand.image_url, cand.source);
                    results.appendChild(card);
                });
            } catch (err) {
                console.error('searchSeriesImageCandidates failed', err);
                status.textContent = 'Search failed.';
            }
        }

        async function applySeriesImageCandidate(imageUrl, source) {
            const { seriesTitle } = manageSeriesState;
            if (!seriesTitle || !imageUrl) return;
            const status = document.getElementById('manageSeriesImageProviderStatus');
            if (status) status.textContent = 'Downloading selected image...';
            try {
                const response = await fetch(
                    apiUrl(`/api/series-images/${encodeURIComponent(seriesTitle)}/from-provider`),
                    {
                        method: 'POST',
                        headers: Object.assign({ 'Content-Type': 'application/json' }, getAuthHeaders()),
                        body: JSON.stringify({ imageUrl, source })
                    }
                );
                if (!response.ok) {
                    let msg = 'Failed to apply provider image';
                    try { const j = await response.json(); if (j && j.error) msg = j.error; } catch {}
                    if (status) status.textContent = msg;
                    showMessage(msg, 'error');
                    return;
                }
                const record = await response.json();
                setManageSeriesRecord(record);
                renderManageSeriesImage(record);
                showMessage('Series image updated from provider', 'success');
                closeFetchSeriesImageFromProvider();
                if (typeof loadSeriesLibrary === 'function') {
                    loadSeriesLibrary(1, true);
                }
            } catch (err) {
                console.error('applySeriesImageCandidate failed', err);
                if (status) status.textContent = 'Failed to apply provider image';
                showMessage('Failed to apply provider image', 'error');
            }
        }

        function renderManageSeriesProviderAliases(record) {
            const container = document.getElementById('manageSeriesProviderAliases');
            const aliases = record.aliases || [];
            // Build a lookup of alias -> language from localized_titles so we
            // can render a small language badge next to each alias.
            const langByTitle = new Map();
            (record.localized_titles || []).forEach(lt => {
                if (lt && lt.title && lt.language) {
                    langByTitle.set(lt.title.toLowerCase(), lt.language);
                }
            });
            if (!aliases.length) {
                container.textContent = 'None';
            } else {
                renderHtml(container, aliases.map(a => {
                    const lang = langByTitle.get(a.toLowerCase());
                    const langBadge = lang
                        ? html` <span style="font-size: 11px; padding: 1px 6px; margin-left: 4px; background: var(--bg-primary); border: 1px solid var(--border-primary); border-radius: 8px; color: var(--text-secondary);">${lang}</span>`
                        : '';
                    return html`<span class="badge" style="display: inline-block; padding: 4px 8px; margin: 2px; background: var(--bg-secondary); border-radius: 12px; font-size: 13px;">${a}${langBadge}</span>`;
                }));
            }
            const sourceLabel = document.getElementById('manageSeriesProviderSource');
            if (record.source) {
                const ts = record.last_lookup_utc ? new Date(record.last_lookup_utc).toLocaleString() : 'never';
                sourceLabel.textContent = `Source: ${record.source} · Last lookup: ${ts} · Status: ${record.lookup_status || 'unknown'}`;
            } else {
                sourceLabel.textContent = 'No external lookup yet — use "Refresh from Provider" to populate.';
            }
        }

        // Save just the per-series preferred language (called on the select's
        // change event so users see the effect immediately without having to
        // hit the modal-wide "Save" button).
        async function saveManageSeriesPreferredLanguage() {
            const { seriesTitle } = manageSeriesState;
            if (!seriesTitle) return;
            const select = document.getElementById('manageSeriesPreferredLanguage');
            if (!select) return;
            const value = select.value || null;
            try {
                const response = await fetch(apiUrl(`/api/metadata/series/${encodeURIComponent(seriesTitle)}/preferred-language`), {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json', ...getAuthHeaders() },
                    credentials: 'same-origin',
                    body: JSON.stringify({ language: value })
                });
                if (!response.ok) {
                    let msg = 'Failed to update preferred language';
                    try { const j = await response.json(); if (j && (j.error || typeof j === 'string')) msg = j.error || j; } catch {}
                    showMessage(msg, 'error');
                    return;
                }
                const record = await response.json();
                setManageSeriesRecord(record);
                showMessage(value ? `Preferred language set to ${value}` : 'Preferred language cleared', 'success');
                if (typeof loadSeriesLibrary === 'function') {
                    loadSeriesLibrary(1, true);
                }
            } catch (err) {
                console.error('saveManageSeriesPreferredLanguage failed', err);
                showMessage('Failed to update preferred language', 'error');
            }
        }

        // Build the Series Name <select> from every name the system knows for
        // this series: its canonical title, provider aliases, user aliases and
        // provider-supplied localized titles (with language hints). The first
        // option is "Automatic", which clears the user-selected name and falls
        // back to the language-preference rule. Case-insensitive dedup avoids
        // listing the same title twice.
        function populateManageSeriesNameOptions(record) {
            const select = document.getElementById('manageSeriesName');
            if (!select) return;
            const seen = new Set();
            const options = ['<option value="">Automatic (use language preference)</option>'];
            const pushOption = (title, languageHint) => {
                if (!title) return;
                const key = title.trim().toLowerCase();
                if (!key || seen.has(key)) return;
                seen.add(key);
                const label = languageHint
                    ? `${escapeHtml(title)} (${escapeHtml(languageHint)})`
                    : escapeHtml(title);
                options.push(`<option value="${escapeHtml(title)}">${label}</option>`);
            };
            if (record && record.canonical_title) {
                pushOption(record.canonical_title, 'canonical');
            }
            if (record && Array.isArray(record.localized_titles)) {
                for (const lt of record.localized_titles) {
                    pushOption(lt && lt.title, lt && lt.language ? lt.language : null);
                }
            }
            if (record && Array.isArray(record.aliases)) {
                for (const a of record.aliases) pushOption(a, 'provider alias');
            }
            if (record && Array.isArray(record.user_aliases)) {
                for (const a of record.user_aliases) pushOption(a, 'your alias');
            }
            renderHtml(select, rawHtml(options.join('')));
            // Preserve the user's current selection even if it isn't otherwise
            // listed (server validation already accepted it at write time).
            const selected = (record && record.series_name) || '';
            if (selected && !seen.has(selected.trim().toLowerCase())) {
                const opt = document.createElement('option');
                opt.value = selected;
                opt.textContent = selected;
                select.appendChild(opt);
            }
            select.value = selected;

            const resolved = document.getElementById('manageSeriesResolvedName');
            if (resolved) {
                const shown = (record && record.resolved_series_name) || '';
                resolved.textContent = selected
                    ? `Using your selected name: "${shown}".`
                    : (shown ? `Automatic name currently resolves to: "${shown}".` : '');
            }
        }

        // Save just the per-series name (called on the select's change event,
        // same UX pattern as the preferred-language control). An empty value
        // reverts to automatic resolution.
        async function saveManageSeriesName() {
            const { seriesTitle } = manageSeriesState;
            if (!seriesTitle) return;
            const select = document.getElementById('manageSeriesName');
            if (!select) return;
            const value = select.value || null;
            try {
                const response = await fetch(apiUrl(`/api/metadata/series/${encodeURIComponent(seriesTitle)}/name`), {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json', ...getAuthHeaders() },
                    credentials: 'same-origin',
                    body: JSON.stringify({ name: value })
                });
                if (!response.ok) {
                    let msg = 'Failed to update series name';
                    try { const j = await response.json(); if (j && (j.error || typeof j === 'string')) msg = j.error || j; } catch {}
                    showMessage(msg, 'error');
                    // Revert the select to the last known good value.
                    const current = (manageSeriesState.record && manageSeriesState.record.series_name) || '';
                    select.value = current;
                    return;
                }
                const record = await response.json();
                setManageSeriesRecord(record);
                populateManageSeriesNameOptions(record);
                renderManageSeriesUserAliases(record);
                showMessage(value ? `Series name set to "${value}"` : 'Series name set to automatic', 'success');
                if (typeof loadSeriesLibrary === 'function') {
                    loadSeriesLibrary(1, true);
                }
            } catch (err) {
                console.error('saveManageSeriesName failed', err);
                showMessage('Failed to update series name', 'error');
            }
        }

        function renderManageSeriesUserAliases(record) {
            const container = document.getElementById('manageSeriesUserAliases');
            const aliases = record.user_aliases || [];
            if (!aliases.length) {
                container.textContent = 'None';
                return;
            }
            renderHtml(container, aliases.map(a => html`
                <span class="badge" style="display: inline-flex; align-items: center; padding: 4px 4px 4px 8px; margin: 2px; background: var(--bg-secondary); border-radius: 12px; font-size: 13px;">
                    ${a}
                    <button type="button" onclick="removeManageSeriesAlias('${jsArg(a)}')" style="margin-left: 6px; background: transparent; border: none; color: var(--text-secondary); cursor: pointer; font-size: 14px;" title="Remove alias">×</button>
                </span>
            `));
        }

        function addManageSeriesAlias() {
            const input = document.getElementById('manageSeriesNewAlias');
            const value = (input.value || '').trim();
            if (!value) return;
            if (!manageSeriesState.record) {
                manageSeriesState.record = { user_aliases: [], aliases: [] };
            }
            const current = manageSeriesState.record.user_aliases || [];
            if (!current.some(a => a.toLowerCase() === value.toLowerCase())) {
                current.push(value);
                manageSeriesState.record.user_aliases = current;
                manageSeriesState.aliasesEdited = true;
                renderManageSeriesUserAliases(manageSeriesState.record);
                // Surface the new alias as a selectable "Series Name" option so
                // it can immediately be picked as the sticky series name.
                populateManageSeriesNameOptions(manageSeriesState.record);
            }
            input.value = '';
        }

        function removeManageSeriesAlias(alias) {
            if (!manageSeriesState.record) return;
            const current = manageSeriesState.record.user_aliases || [];
            manageSeriesState.record.user_aliases = current.filter(a => a.toLowerCase() !== alias.toLowerCase());
            manageSeriesState.aliasesEdited = true;
            renderManageSeriesUserAliases(manageSeriesState.record);
            // Keep the "Series Name" dropdown in sync with the alias list.
            populateManageSeriesNameOptions(manageSeriesState.record);
        }

        // PUT an alias list as the series' user aliases, replacing the stored
        // ones. Resolves to whether the server accepted it.
        async function putManageSeriesUserAliases(seriesTitle, aliases) {
            const response = await fetch(apiUrl(`/api/metadata/series/${encodeURIComponent(seriesTitle)}/aliases`), {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json', ...getAuthHeaders() },
                credentials: 'same-origin',
                body: JSON.stringify({ aliases })
            });
            return response.ok;
        }

        async function saveManageSeriesNames() {
            const { seriesTitle, record } = manageSeriesState;
            if (!seriesTitle || !record) return;
            try {
                if (!(await putManageSeriesUserAliases(seriesTitle, record.user_aliases || []))) {
                    showMessage('Failed to save series names', 'error');
                    return;
                }
                showMessage('Series names saved', 'success');
                closeManageSeriesNamesModal();
                if (typeof loadSeriesLibrary === 'function') {
                    loadSeriesLibrary(1, true);
                }
                // Aliases may have collapsed previously-distinct folders into
                // a single combinable group, so refresh the dashboard count
                // and re-enable the "Combine Folders" button if needed.
                if (typeof loadLibraryHealth === 'function') {
                    loadLibraryHealth();
                }
            } catch (err) {
                console.error('saveManageSeriesNames failed', err);
                showMessage('Failed to save series names', 'error');
            }
        }

        async function refreshSeriesMetadata() {
            const { seriesTitle } = manageSeriesState;
            if (!seriesTitle) return;
            try {
                const response = await fetch(apiUrl(`/api/metadata/refresh/${encodeURIComponent(seriesTitle)}`), {
                    method: 'POST',
                    headers: getAuthHeaders(),
                    credentials: 'same-origin'
                });
                if (!response.ok) {
                    showMessage('Failed to refresh metadata', 'error');
                    return;
                }
                const record = await response.json();
                setManageSeriesRecord(record);
                renderManageSeriesProviderAliases(record);
                renderManageSeriesUserAliases(record);
                renderManageSeriesImage(record);
                // Keep the "Series Name" options in sync with the refreshed
                // metadata, mirroring the manual-match path.
                populateManageSeriesNameOptions(record);
                const successStatuses = new Set(['success', 'manual_match']);
                const isSuccess = successStatuses.has(record.lookup_status);
                showMessage(isSuccess ? 'Metadata refreshed' : `Lookup status: ${record.lookup_status || 'unknown'}`, isSuccess ? 'success' : 'info');
            } catch (err) {
                console.error('refreshSeriesMetadata failed', err);
                showMessage('Failed to refresh metadata', 'error');
            }
        }

        // Manual trigger from the Manage Series Names modal: re-runs the
        // alias-aware folder-combine scan and opens the Combine Folders modal
        // so the user can immediately act on any newly-merged groups.
        async function checkCombinableFoldersFromManageSeries() {
            try {
                showMessage('Checking for combinable folders...', 'info');
                if (typeof loadLibraryHealth === 'function') {
                    // Refresh the dashboard counter so the badge reflects any
                    // new groups produced by alias edits.
                    loadLibraryHealth();
                }
                closeManageSeriesNamesModal();
                await openCombineFoldersModal();
            } catch (err) {
                console.error('checkCombinableFoldersFromManageSeries failed', err);
                showMessage('Failed to check for combinable folders', 'error');
            }
        }

        async function searchExternalSeries() {
            const query = (document.getElementById('manageSeriesSearchInput').value || '').trim();
            const container = document.getElementById('manageSeriesSearchResults');
            if (!query) {
                renderHtml(container, html`<p style="color: var(--text-secondary);">Enter a query above to search.</p>`);
                return;
            }
            renderHtml(container, html`<p style="color: var(--text-secondary);">Searching...</p>`);
            try {
                const response = await fetch(apiUrl(`/api/metadata/search?query=${encodeURIComponent(query)}&limit=10`), {
                    headers: getAuthHeaders(),
                    credentials: 'same-origin'
                });
                if (!response.ok) {
                    renderHtml(container, html`<p style="color: var(--text-error);">Search failed.</p>`);
                    return;
                }
                const data = await response.json();
                const results = data.results || [];
                if (!results.length) {
                    renderHtml(container, html`<p style="color: var(--text-secondary);">No matches found. Check that external metadata providers are enabled in Settings.</p>`);
                    return;
                }
                renderHtml(container, results.map((r, idx) => {
                    // Build a lookup of alias -> language from the provider's
                    // localized titles so each alias can be rendered with a
                    // small language badge ("en", "ja", "ko", ...). Without
                    // this the manual-match list shows bare strings and the
                    // user can't tell which alias is English.
                    const langByTitle = new Map();
                    (r.localized_titles || []).forEach(lt => {
                        if (lt && lt.title && lt.language) {
                            langByTitle.set(lt.title.toLowerCase(), lt.language);
                        }
                    });
                    const aliasList = r.aliases || [];
                    const aliases = aliasList.length
                        ? rawHtml(aliasList.map(a => {
                            const lang = langByTitle.get(a.toLowerCase());
                            const langBadge = lang
                                ? html` <span style="font-size: 11px; padding: 1px 6px; margin-left: 4px; background: var(--bg-primary); border: 1px solid var(--border-primary); border-radius: 8px; color: var(--text-secondary);">${lang}</span>`
                                : '';
                            return htmlToString(html`<span style="display: inline-block; margin-right: 4px;">${a}${langBadge}</span>`);
                        }).join(', '))
                        : html`<em>none</em>`;
                    // Show the canonical title's language too so the user can
                    // see that (for example) the chosen canonical is English.
                    const canonicalLang = langByTitle.get((r.canonical_title || '').toLowerCase());
                    const canonicalLangBadge = canonicalLang
                        ? html` <span style="font-size: 11px; padding: 1px 6px; margin-left: 4px; background: var(--bg-primary); border: 1px solid var(--border-primary); border-radius: 8px; color: var(--text-secondary);">${canonicalLang}</span>`
                        : '';
                    // Server returns match_score on [0,100]. Render a colour
                    // hint so the user can see at a glance which candidate is
                    // the most-likely match when the automatic pick was wrong.
                    const score = typeof r.match_score === 'number' ? r.match_score : null;
                    let scoreBadge = '';
                    if (score !== null) {
                        const tone = score >= 90 ? 'var(--accent-success, #1f9d55)'
                                   : score >= 60 ? 'var(--accent-warning, #c69026)'
                                   : 'var(--text-secondary)';
                        scoreBadge = html`<span title="Confidence that this is the right match" style="font-size: 12px; padding: 2px 8px; border-radius: 10px; background: var(--bg-secondary); color: ${tone}; border: 1px solid ${tone};">${score.toFixed(1)}% match</span>`;
                    }
                    return html`
                        <div style="border: 1px solid var(--border-primary); border-radius: 5px; padding: 10px; margin-bottom: 8px;">
                            <div style="display: flex; justify-content: space-between; align-items: center; gap: 8px; flex-wrap: wrap;">
                                <strong>${r.canonical_title || ''}${canonicalLangBadge}</strong>
                                <div style="display: flex; gap: 6px; align-items: center;">
                                    ${scoreBadge}
                                    <span style="font-size: 12px; color: var(--text-secondary);">${r.source || ''}</span>
                                </div>
                            </div>
                            <div style="font-size: 13px; color: var(--text-secondary); margin-top: 4px;">Aliases: ${aliases}</div>
                            <div style="display: flex; gap: 6px; margin-top: 8px; flex-wrap: wrap;">
                                <button class="btn btn-small btn-primary" type="button" onclick="applySearchResultAsMatch(${idx})" title="Mark this candidate as the correct match for the series">${uiIcon('check-circle')} Use This Match</button>
                                <button class="btn btn-small" type="button" onclick="adoptSearchResult(${idx}, 'canonical')">Adopt as Canonical</button>
                                <button class="btn btn-small" type="button" onclick="adoptSearchResult(${idx}, 'aliases')">Add Aliases</button>
                            </div>
                        </div>
                    `;
                }));
                window.__manageSeriesSearchResults = results;
            } catch (err) {
                console.error('searchExternalSeries failed', err);
                renderHtml(container, html`<p style="color: var(--text-error);">Search failed.</p>`);
            }
        }

        function adoptSearchResult(index, mode) {
            const results = window.__manageSeriesSearchResults || [];
            const result = results[index];
            if (!result) return;
            if (!manageSeriesState.record) {
                manageSeriesState.record = { user_aliases: [], aliases: [] };
            }
            const current = manageSeriesState.record.user_aliases || [];
            const addIfNew = (value) => {
                if (value && !current.some(a => a.toLowerCase() === value.toLowerCase())) {
                    current.push(value);
                }
            };
            if (mode === 'canonical') {
                // Keep the previous canonical (the series being managed) as a
                // user alias, register the chosen title, then set it as the
                // sticky series name.
                addIfNew(manageSeriesState.seriesTitle);
                if (result.canonical_title) {
                    addIfNew(result.canonical_title);
                }
            }
            (result.aliases || []).forEach(addIfNew);
            if (mode === 'aliases' && result.canonical_title) {
                addIfNew(result.canonical_title);
            }
            manageSeriesState.record.user_aliases = current;
            manageSeriesState.aliasesEdited = true;
            renderManageSeriesUserAliases(manageSeriesState.record);
            populateManageSeriesNameOptions(manageSeriesState.record);
            if (mode === 'canonical' && result.canonical_title) {
                const select = document.getElementById('manageSeriesName');
                if (select) {
                    if (![...select.options].some(o => o.value === result.canonical_title)) {
                        const opt = document.createElement('option');
                        opt.value = result.canonical_title;
                        opt.textContent = result.canonical_title;
                        select.appendChild(opt);
                    }
                    select.value = result.canonical_title;
                    saveAdoptedSeriesNames();
                }
            }
        }

        // Adopt as Canonical pins the series name, which saves straight away,
        // so the aliases it adopted are saved first instead of waiting for
        // Save. If they can't be saved the stored name is kept as well, and
        // the adopted aliases stay listed for Save.
        async function saveAdoptedSeriesNames() {
            const state = manageSeriesState;
            const { seriesTitle, record } = state;
            if (!seriesTitle || !record) return;
            // Without a loaded record the list lacks the stored aliases, which
            // saving it would replace: pin the name alone (the server adds it
            // to them) and leave the other adopted names for Save.
            if (!state.recordLoaded) {
                await saveManageSeriesName();
                return;
            }
            let saved = false;
            try {
                saved = await putManageSeriesUserAliases(seriesTitle, record.user_aliases || []);
            } catch (err) {
                console.error('saveAdoptedSeriesNames failed', err);
            }
            // The modal was closed or reopened meanwhile: leave the name alone.
            if (state !== manageSeriesState) return;
            if (!saved) {
                showMessage('Failed to save series names', 'error');
                const select = document.getElementById('manageSeriesName');
                if (select) select.value = (state.record && state.record.series_name) || '';
                return;
            }
            await saveManageSeriesName();
        }

        // Mark one of the candidates returned by /search as the *correct*
        // external match for the series. Used when the automatic best-match
        // chose the wrong candidate. Server-side this overwrites the cached
        // provider aliases / source / lookup status with the chosen ones
        // (preserving any user canonical-title override and user aliases).
        async function applySearchResultAsMatch(index) {
            const { seriesTitle } = manageSeriesState;
            if (!seriesTitle) return;
            const results = window.__manageSeriesSearchResults || [];
            const result = results[index];
            if (!result || !result.canonical_title) return;
            if (!confirm(`Mark "${result.canonical_title}" (${result.source || 'unknown source'}) as the correct match for "${seriesTitle}"? This replaces the cached provider metadata for this series.`)) {
                return;
            }
            try {
                const response = await fetch(apiUrl(`/api/metadata/series/${encodeURIComponent(seriesTitle)}/apply-match`), {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json', ...getAuthHeaders() },
                    credentials: 'same-origin',
                    body: JSON.stringify({
                        canonicalTitle: result.canonical_title,
                        aliases: result.aliases || [],
                        source: result.source || null,
                        imageUrl: result.image_url || null,
                        thumbnailUrl: result.thumbnail_url || null,
                        // Forward the provider's per-title language tags so
                        // the cached record preserves them and the per-series
                        // preferred-language resolver can later swap the
                        // display title to the user's chosen language.
                        localizedTitles: (result.localized_titles || [])
                            .filter(lt => lt && lt.title)
                            .map(lt => ({ title: lt.title, language: lt.language || null }))
                    })
                });
                if (!response.ok) {
                    let msg = 'Failed to apply match';
                    try { const j = await response.json(); if (j && (j.error || typeof j === 'string')) msg = j.error || j; } catch {}
                    showMessage(msg, 'error');
                    return;
                }
                const record = await response.json();
                setManageSeriesRecord(record);
                renderManageSeriesProviderAliases(record);
                renderManageSeriesUserAliases(record);
                renderManageSeriesImage(record);
                // Refresh the "Series Name" options so the newly-matched
                // canonical title / localized titles / provider aliases are
                // immediately selectable — matching the dialog-open behavior so
                // a manually-picked match behaves identically to an automatic one.
                populateManageSeriesNameOptions(record);
                showMessage('Match applied', 'success');
                if (typeof loadSeriesLibrary === 'function') {
                    loadSeriesLibrary(1, true);
                }
            } catch (err) {
                console.error('applySearchResultAsMatch failed', err);
                showMessage('Failed to apply match', 'error');
            }
        }

        // Wipe the cached external metadata for the current series (provider
        // aliases, source, lookup status, and any provider-downloaded image).
        // User aliases and user-uploaded images are preserved.
        async function clearSeriesExternalMetadata() {
            const { seriesTitle } = manageSeriesState;
            if (!seriesTitle) return;
            if (!confirm(`Clear cached external metadata for "${seriesTitle}"? User aliases and a user-uploaded image will be kept.`)) {
                return;
            }
            try {
                const response = await fetch(apiUrl(`/api/metadata/series/${encodeURIComponent(seriesTitle)}/external`), {
                    method: 'DELETE',
                    headers: getAuthHeaders(),
                    credentials: 'same-origin'
                });
                if (response.status === 404) {
                    showMessage('Nothing to clear — no external metadata cached for this series.', 'info');
                    return;
                }
                if (!response.ok) {
                    showMessage('Failed to clear external metadata', 'error');
                    return;
                }
                const record = await response.json();
                setManageSeriesRecord(record);
                renderManageSeriesProviderAliases(record);
                renderManageSeriesUserAliases(record);
                renderManageSeriesImage(record);
                // Cleared external metadata drops provider canonical/aliases, so
                // refresh the "Series Name" options to drop stale choices.
                populateManageSeriesNameOptions(record);
                showMessage('External metadata cleared', 'success');
                if (typeof loadSeriesLibrary === 'function') {
                    loadSeriesLibrary(1, true);
                }
            } catch (err) {
                console.error('clearSeriesExternalMetadata failed', err);
                showMessage('Failed to clear external metadata', 'error');
            }
        }
