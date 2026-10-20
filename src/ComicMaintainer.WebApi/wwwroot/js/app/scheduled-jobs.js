        // ComicMaintainer SPA — scheduled jobs page (#/jobs): schedule, options, run-now and last-run status of the server's recurring jobs.
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

        // Option checkboxes shown on each job's card, keyed by job key.
        // `defaultOn` options are on unless the stored options turn them off,
        // matching the job handler's own defaults. Options a job stores but the
        // page does not show (e.g. metadata-backfill's maxBatch) are kept as
        // they are on save (see buildScheduledJobOptionsJson).
        const SCHEDULED_JOB_OPTIONS = {
            'metadata-audit': [
                { name: 'autoCorrect', label: 'Auto-correct findings (re-normalizes offending files)' }
            ],
            'series-language-audit': [
                { name: 'autoCorrect', label: 'Auto-correct mismatches (queues affected series for retag)' }
            ],
            'file-naming-audit': [
                { name: 'autoCorrect', label: 'Auto-correct filenames (renames mismatched files)' },
                { name: 'forceReprocess', label: 'Force reprocess (include files already marked as renamed)' }
            ],
            'missing-issues-check': [
                { name: 'autoDownload', label: 'Auto-download missing issues from Suwayomi (requires the Suwayomi integration)' }
            ],
            'library-scan': [
                { name: 'scanForNew', label: 'Add and process new files', defaultOn: true },
                { name: 'reconcileDeletions', label: 'Remove files deleted from disk', defaultOn: true },
                { name: 'retagStale', label: 'Retag files with stale series metadata', defaultOn: true },
                { name: 'reprocessModified', label: 'Re-normalize files changed on disk (size or modified time differs)' }
            ]
        };

        const SCHEDULED_JOB_STATUSES = ['neverrun', 'running', 'success', 'failed', 'skipped', 'cancelled'];

        // Status is polled only while the page is on screen: every couple of
        // seconds while a job is running (or a run was just requested), and
        // slowly otherwise so timer-driven runs still show up.
        const SCHEDULED_JOBS_IDLE_REFRESH_MS = 30000;
        const SCHEDULED_JOBS_ACTIVE_REFRESH_MS = 2000;
        // A requested run that has neither started nor finished by then (e.g.
        // skipped because another run was in progress) stops being followed.
        const SCHEDULED_JOB_RUN_START_TIMEOUT_MS = 30000;

        let scheduledJobs = [];
        let scheduledJobsLoadRequest = 0;
        let scheduledJobsRefreshTimer = null;
        let metadataAuditFindingsRequest = 0;
        // Runs started from this page: jobKey → { requestedAt, lastRunUtc, seenRunning }.
        const scheduledJobRunRequests = new Map();

        function openScheduledJobs() {
            navigate('#/jobs');
        }

        /** Fetch a /api/scheduled-jobs endpoint; throws an Error carrying `status` on failure. */
        async function fetchScheduledJobsApi(path, options = {}) {
            const response = await fetch(apiUrl(path), {
                credentials: 'include',
                ...options,
                headers: getAuthHeaders()
            });
            if (handleAuthError(response)) {
                const error = new Error('Authentication required');
                error.status = response.status;
                error.authHandled = true;
                throw error;
            }
            if (!response.ok) {
                let message = response.status === 403
                    ? 'Only administrators can manage scheduled jobs.'
                    : `HTTP error! status: ${response.status}`;
                try {
                    const body = await response.json();
                    if (body && (body.error || body.message)) message = body.error || body.message;
                } catch (_) { /* non-JSON error body */ }
                const error = new Error(message);
                error.status = response.status;
                throw error;
            }
            return response;
        }

        function parseScheduledJobOptions(job) {
            if (!job || !job.optionsJson) return {};
            try {
                const options = JSON.parse(job.optionsJson);
                return options && typeof options === 'object' && !Array.isArray(options) ? options : {};
            } catch (_) {
                return {};
            }
        }

        function isScheduledJobOptionOn(option, options) {
            return option.defaultOn ? options[option.name] !== false : !!options[option.name];
        }

        /**
         * The options JSON to save: the stored options with the card's
         * checkboxes applied on top. Saving replaces the stored options
         * wholesale, so options the card does not show must be carried over.
         */
        function buildScheduledJobOptionsJson(job, shownOptions) {
            if (!Object.keys(shownOptions).length) return job.optionsJson ?? null;
            return JSON.stringify({ ...parseScheduledJobOptions(job), ...shownOptions });
        }

        // "NeverRun" → "Never run", "SeriesMismatch" → "Series mismatch".
        function humanizeScheduledJobValue(value) {
            const spaced = String(value || '').replace(/([a-z])([A-Z])/g, '$1 $2');
            return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase();
        }

        function scheduledJobStatusLabel(status) {
            return humanizeScheduledJobValue(status || 'NeverRun');
        }

        function isScheduledJobRunning(job) {
            return String(job && job.lastStatus).toLowerCase() === 'running';
        }

        function isScheduledJobBusy(job) {
            return isScheduledJobRunning(job) || scheduledJobRunRequests.has(job.jobKey);
        }

        // The API reports UTC times without an offset ("2026-01-02T03:04:05.6789012"),
        // which Date would otherwise read as local time. The fraction is cut
        // to milliseconds, the precision Date's ISO format defines.
        function parseScheduledJobDate(value) {
            if (!value) return null;
            const text = String(value).replace(/\.(\d+)/, (_, fraction) => '.' + (fraction + '00').slice(0, 3));
            const date = new Date(/(?:Z|[+-]\d{2}:?\d{2})$/i.test(text) ? text : text + 'Z');
            return Number.isNaN(date.getTime()) ? null : date;
        }

        function formatScheduledJobDate(value) {
            const date = parseScheduledJobDate(value);
            return date ? date.toLocaleString() : '—';
        }

        function formatScheduledJobDuration(ms) {
            if (ms === null || ms === undefined || ms === '') return '';
            const value = Number(ms);
            if (!Number.isFinite(value) || value < 0) return '';
            if (value < 1000) return `${Math.round(value)} ms`;
            const seconds = Math.floor(value / 1000);
            if (seconds < 60) return `${seconds} s`;
            const minutes = Math.floor(seconds / 60);
            if (minutes < 60) return `${minutes}m ${seconds % 60}s`;
            return `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
        }

        // Plain-language reading of an interval in minutes ("Every 7 days").
        function formatScheduledJobInterval(minutes) {
            const value = Number(minutes);
            if (String(minutes).trim() === '' || !Number.isInteger(value) || value < 1) {
                return 'Enter a whole number of minutes (at least 1).';
            }
            const every = (count, unit) => (count === 1 ? `Every ${unit}` : `Every ${count} ${unit}s`);
            if (value % 1440 === 0) return every(value / 1440, 'day');
            if (value % 60 === 0) return every(value / 60, 'hour');
            return every(value, 'minute');
        }

        function scheduledJobDomId(jobKey) {
            return 'scheduled-job-' + String(jobKey).replace(/[^a-z0-9_-]/gi, '-');
        }

        function findScheduledJobCard(jobKey) {
            const card = document.getElementById(scheduledJobDomId(jobKey));
            return card && card.dataset.jobKey === jobKey ? card : null;
        }

        function scheduledJobIcon(jobKey) {
            switch (jobKey) {
                case 'metadata-audit': return uiIcon('check-circle');
                case 'series-language-audit': return uiIcon('globe');
                case 'file-naming-audit': return uiIcon('pencil');
                case 'missing-issues-check': return uiIcon('puzzle');
                case 'library-scan': return uiIcon('folder');
                case 'metadata-backfill': return uiIcon('tag');
                case 'database-backup': return uiIcon('hard-drive');
                default: return uiIcon('clock');
            }
        }

        function renderScheduledJobStatus(job) {
            const status = String(job.lastStatus || '').toLowerCase();
            const modifier = SCHEDULED_JOB_STATUSES.includes(status) ? status : 'neverrun';
            return html`<span class="scheduled-job-status scheduled-job-status--${modifier}"><span class="visually-hidden">Last run status: </span>${scheduledJobStatusLabel(job.lastStatus)}</span>`;
        }

        function renderScheduledJobRuns(job) {
            const duration = formatScheduledJobDuration(job.lastDurationMs);
            const lastRun = job.lastRunUtc
                ? formatScheduledJobDate(job.lastRunUtc) + (duration ? ` · ${duration}` : '')
                : 'Never';
            const nextRun = job.nextRunUtc ? formatScheduledJobDate(job.nextRunUtc) : 'Not scheduled';
            return html`
                <div class="scheduled-job-run"><dt>Last run</dt><dd>${lastRun}</dd></div>
                <div class="scheduled-job-run"><dt>Next run</dt><dd>${nextRun}</dd></div>
                ${job.lastMessage ? html`<div class="scheduled-job-run scheduled-job-run--message"><dt>Last message</dt><dd>${job.lastMessage}</dd></div>` : ''}
            `;
        }

        function renderScheduledJobRunLabel(job) {
            if (isScheduledJobRunning(job)) return html`${uiIcon('play')} Running…`;
            if (scheduledJobRunRequests.has(job.jobKey)) return html`${uiIcon('play')} Starting…`;
            return html`${uiIcon('play')} Run now`;
        }

        function renderScheduledJobCard(job) {
            const id = scheduledJobDomId(job.jobKey);
            const name = job.displayName || job.jobKey;
            const options = parseScheduledJobOptions(job);
            const optionDefinitions = SCHEDULED_JOB_OPTIONS[job.jobKey] || [];
            const interval = job.intervalMinutes || 60;
            const isMetadataAudit = job.jobKey === 'metadata-audit';
            return html`
                <section class="settings-card scheduled-job-card" id="${id}" data-job-key="${job.jobKey}" aria-labelledby="${id}-title">
                    <header class="settings-card-header">
                        <span class="settings-card-icon" aria-hidden="true">${scheduledJobIcon(job.jobKey)}</span>
                        <div class="scheduled-job-heading">
                            <div class="scheduled-job-title-row">
                                <h3 class="settings-card-title" id="${id}-title">${name}</h3>
                                <span data-job-part="status">${renderScheduledJobStatus(job)}</span>
                            </div>
                            ${job.description ? html`<p class="settings-card-desc">${job.description}</p>` : ''}
                        </div>
                    </header>
                    <dl class="scheduled-job-runs" data-job-part="runs">${renderScheduledJobRuns(job)}</dl>
                    <div class="form-group">
                        <label for="${id}-enabled" class="checkbox-label">
                            <input type="checkbox" id="${id}-enabled" data-job-field="enabled" ${job.enabled ? 'checked' : ''}>
                            <span>Enabled</span>
                        </label>
                    </div>
                    <div class="form-group">
                        <label for="${id}-interval">Interval (minutes)</label>
                        <input type="number" id="${id}-interval" data-job-field="intervalMinutes" min="1" step="1" inputmode="numeric" value="${interval}" aria-describedby="${id}-interval-hint" oninput="updateScheduledJobIntervalHint(this)">
                        <small class="form-hint" id="${id}-interval-hint" data-job-part="interval-hint">${formatScheduledJobInterval(interval)}</small>
                    </div>
                    ${optionDefinitions.length ? html`
                        <fieldset class="scheduled-job-options">
                            <legend>Options</legend>
                            ${optionDefinitions.map(option => html`
                                <label class="checkbox-label checkbox-label--minor">
                                    <input type="checkbox" data-job-option="${option.name}" ${isScheduledJobOptionOn(option, options) ? 'checked' : ''}>
                                    <span>${option.label}</span>
                                </label>
                            `)}
                        </fieldset>
                    ` : ''}
                    <div class="scheduled-job-actions">
                        <button type="button" class="btn btn-primary" data-job-action="save" onclick="saveScheduledJob('${jsArg(job.jobKey)}')">${uiIcon('check')} Save</button>
                        <button type="button" class="btn" data-job-action="run" onclick="runScheduledJob('${jsArg(job.jobKey)}')" ${isScheduledJobBusy(job) ? 'disabled' : ''}>${renderScheduledJobRunLabel(job)}</button>
                        ${isMetadataAudit ? html`<button type="button" class="btn btn-ghost" data-job-action="findings" aria-expanded="false" aria-controls="${id}-findings" onclick="toggleMetadataAuditFindings('${jsArg(job.jobKey)}')">${uiIcon('list')} View findings</button>` : ''}
                    </div>
                    ${isMetadataAudit ? html`<div class="scheduled-job-findings" id="${id}-findings" data-job-part="findings" hidden></div>` : ''}
                </section>
            `;
        }

        function renderScheduledJobsList() {
            const list = document.getElementById('scheduledJobsList');
            if (!list) return;
            if (!scheduledJobs.length) {
                renderHtml(list, html`<div class="empty-state"><p>No scheduled jobs are registered.</p></div>`);
                return;
            }
            renderHtml(list, html`${scheduledJobs.map(renderScheduledJobCard)}`);
        }

        /**
         * Refresh what the server changes on its own — status, run times and
         * the Run now button — and, only when asked (after a save), the form.
         * Leaving the form alone keeps edits the user has not saved yet.
         */
        function updateScheduledJobCard(job, { resetForm = false } = {}) {
            const card = findScheduledJobCard(job.jobKey);
            if (!card) return;
            renderHtml(card.querySelector('[data-job-part="status"]'), renderScheduledJobStatus(job));
            renderHtml(card.querySelector('[data-job-part="runs"]'), renderScheduledJobRuns(job));
            const runButton = card.querySelector('[data-job-action="run"]');
            if (runButton) {
                runButton.disabled = isScheduledJobBusy(job);
                renderHtml(runButton, renderScheduledJobRunLabel(job));
            }
            if (!resetForm) return;

            const enabled = card.querySelector('[data-job-field="enabled"]');
            if (enabled) enabled.checked = !!job.enabled;
            const interval = card.querySelector('[data-job-field="intervalMinutes"]');
            if (interval) {
                interval.value = job.intervalMinutes || 60;
                updateScheduledJobIntervalHint(interval);
            }
            const options = parseScheduledJobOptions(job);
            (SCHEDULED_JOB_OPTIONS[job.jobKey] || []).forEach(option => {
                const input = card.querySelector(`[data-job-option="${option.name}"]`);
                if (input) input.checked = isScheduledJobOptionOn(option, options);
            });
        }

        function updateScheduledJobIntervalHint(input) {
            const card = input.closest('[data-job-key]');
            const hint = card && card.querySelector('[data-job-part="interval-hint"]');
            if (hint) hint.textContent = formatScheduledJobInterval(input.value);
        }

        // Scheduled jobs are administrator-only, as is their API; applyCapabilities
        // hides the entry points from everyone else. Anyone who still reaches
        // the page (an old link, or before their capabilities have loaded)
        // gets a notice in place of the jobs.
        function applyScheduledJobsCapabilities() {
            if (!userCapabilities.canAdminister) showScheduledJobsAdminOnly();
        }

        function showScheduledJobsAdminOnly() {
            ++scheduledJobsLoadRequest;
            stopScheduledJobsRefresh();
            scheduledJobs = [];
            const notice = document.getElementById('scheduledJobsAdminNotice');
            if (notice) notice.hidden = false;
            const list = document.getElementById('scheduledJobsList');
            if (list) {
                list.hidden = true;
                list.setAttribute('aria-busy', 'false');
                clearChildren(list);
            }
        }

        function sameScheduledJobKeys(a, b) {
            return a.length === b.length && a.every((job, index) => job.jobKey === b[index].jobKey);
        }

        /**
         * Load the jobs. A full load (entering the page, Retry) renders every
         * card afresh; a status-only refresh updates the existing cards in
         * place so a half-edited form is not reset under the user.
         */
        async function loadScheduledJobs({ statusOnly = false } = {}) {
            const list = document.getElementById('scheduledJobsList');
            if (!list) return;
            if (!userCapabilities.canAdminister) {
                showScheduledJobsAdminOnly();
                return;
            }
            clearTimeout(scheduledJobsRefreshTimer);
            scheduledJobsRefreshTimer = null;
            const loadRequest = ++scheduledJobsLoadRequest;
            const hasCards = () => !!list.querySelector('[data-job-key]');
            if (!statusOnly) {
                const notice = document.getElementById('scheduledJobsAdminNotice');
                if (notice) notice.hidden = true;
                list.hidden = false;
                list.setAttribute('aria-busy', 'true');
                if (!hasCards()) {
                    renderHtml(list, html`<div class="loading"><div class="spinner"></div><p>Loading scheduled jobs...</p></div>`);
                }
            }
            try {
                const response = await fetchScheduledJobsApi('/api/scheduled-jobs');
                const jobs = await response.json();
                if (loadRequest !== scheduledJobsLoadRequest) return;
                const previous = scheduledJobs;
                scheduledJobs = Array.isArray(jobs) ? jobs : [];
                if (statusOnly && hasCards() && sameScheduledJobKeys(previous, scheduledJobs)) {
                    scheduledJobs.forEach(job => updateScheduledJobCard(job));
                } else {
                    renderScheduledJobsList();
                }
                scheduledJobs.forEach(followScheduledJobRun);
            } catch (error) {
                if (loadRequest !== scheduledJobsLoadRequest || error.authHandled) return;
                if (error.status === 403) {
                    showScheduledJobsAdminOnly();
                    return;
                }
                if (statusOnly && hasCards()) {
                    // Keep the last known status on screen; the next refresh retries.
                    console.warn('Failed to refresh scheduled jobs:', error);
                    return;
                }
                console.error('Failed to load scheduled jobs:', error);
                scheduledJobs = [];
                renderHtml(list, html`
                    <div class="empty-state">
                        <p>Scheduled jobs could not be loaded: ${error.message}</p>
                        <button type="button" class="btn" onclick="loadScheduledJobs()">${uiIcon('refresh')} Retry</button>
                    </div>
                `);
            } finally {
                if (loadRequest === scheduledJobsLoadRequest) {
                    list.setAttribute('aria-busy', 'false');
                    scheduleScheduledJobsRefresh();
                }
            }
        }

        function scheduleScheduledJobsRefresh() {
            clearTimeout(scheduledJobsRefreshTimer);
            scheduledJobsRefreshTimer = null;
            if (currentView !== 'jobs') return;
            const active = scheduledJobRunRequests.size > 0 || scheduledJobs.some(isScheduledJobRunning);
            scheduledJobsRefreshTimer = setTimeout(() => {
                scheduledJobsRefreshTimer = null;
                // A background tab does not need fresh status; check again later.
                if (document.hidden) {
                    scheduleScheduledJobsRefresh();
                    return;
                }
                loadScheduledJobs({ statusOnly: true });
            }, active ? SCHEDULED_JOBS_ACTIVE_REFRESH_MS : SCHEDULED_JOBS_IDLE_REFRESH_MS);
        }

        // Called by the router whenever another page is shown.
        function stopScheduledJobsRefresh() {
            clearTimeout(scheduledJobsRefreshTimer);
            scheduledJobsRefreshTimer = null;
            scheduledJobRunRequests.clear();
        }

        // Follow a run started from this page until it finishes, then say how
        // it went (the card alone is easy to miss).
        function followScheduledJobRun(job) {
            const request = scheduledJobRunRequests.get(job.jobKey);
            if (!request) return;
            if (isScheduledJobRunning(job)) {
                request.seenRunning = true;
                return;
            }
            const lastRun = parseScheduledJobDate(job.lastRunUtc);
            const previousRun = parseScheduledJobDate(request.lastRunUtc);
            const ranSinceRequest = !!lastRun && (!previousRun || lastRun.getTime() !== previousRun.getTime());
            if (request.seenRunning || ranSinceRequest) {
                scheduledJobRunRequests.delete(job.jobKey);
                updateScheduledJobCard(job);
                announceScheduledJobRun(job);
            } else if (Date.now() - request.requestedAt > SCHEDULED_JOB_RUN_START_TIMEOUT_MS) {
                scheduledJobRunRequests.delete(job.jobKey);
                updateScheduledJobCard(job);
            }
        }

        function announceScheduledJobRun(job) {
            const name = job.displayName || job.jobKey;
            const status = String(job.lastStatus || '').toLowerCase();
            const detail = job.lastMessage ? `: ${job.lastMessage}` : '.';
            if (status === 'success') {
                showMessage(`${name} finished${detail}`, 'success');
            } else if (status === 'failed') {
                showMessage(`${name} failed${detail}`, 'error');
            } else {
                showMessage(`${name}: ${scheduledJobStatusLabel(job.lastStatus)}${detail}`, 'info');
            }

            // A finished audit has new findings; refresh them if they are open.
            const card = findScheduledJobCard(job.jobKey);
            const findings = card && card.querySelector('[data-job-part="findings"]');
            if (findings && !findings.hidden) loadMetadataAuditFindings(findings);
        }

        async function saveScheduledJob(jobKey) {
            const card = findScheduledJobCard(jobKey);
            const job = scheduledJobs.find(item => item.jobKey === jobKey);
            if (!card || !job) return;
            const name = job.displayName || jobKey;

            const intervalInput = card.querySelector('[data-job-field="intervalMinutes"]');
            const intervalMinutes = Number(intervalInput.value);
            if (intervalInput.value.trim() === '' || !Number.isInteger(intervalMinutes) || intervalMinutes < 1) {
                showMessage(`Enter an interval for ${name} in whole minutes (at least 1).`, 'error');
                intervalInput.focus();
                return;
            }
            const shownOptions = {};
            card.querySelectorAll('[data-job-option]').forEach(input => {
                shownOptions[input.dataset.jobOption] = input.checked;
            });

            const button = card.querySelector('[data-job-action="save"]');
            if (button) button.disabled = true;
            try {
                const response = await fetchScheduledJobsApi(`/api/scheduled-jobs/${encodeURIComponent(jobKey)}`, {
                    method: 'PUT',
                    body: JSON.stringify({
                        enabled: card.querySelector('[data-job-field="enabled"]').checked,
                        intervalMinutes,
                        optionsJson: buildScheduledJobOptionsJson(job, shownOptions)
                    })
                });
                const saved = await response.json();
                scheduledJobs = scheduledJobs.map(item => (item.jobKey === jobKey ? saved : item));
                updateScheduledJobCard(saved, { resetForm: true });
                showMessage(`${name} saved.`, 'success');
                // Supersede any status refresh that was read before the save.
                loadScheduledJobs({ statusOnly: true });
            } catch (error) {
                if (!error.authHandled) {
                    showMessage(`Failed to save ${name}: ${error.message}`, 'error', { reference: true });
                }
            } finally {
                if (button) button.disabled = false;
            }
        }

        async function runScheduledJob(jobKey) {
            const job = scheduledJobs.find(item => item.jobKey === jobKey);
            if (!job || isScheduledJobBusy(job)) return;
            const name = job.displayName || jobKey;
            const button = findScheduledJobCard(jobKey)?.querySelector('[data-job-action="run"]');
            if (button) button.disabled = true;
            try {
                await fetchScheduledJobsApi(`/api/scheduled-jobs/${encodeURIComponent(jobKey)}/run`, { method: 'POST' });
                scheduledJobRunRequests.set(jobKey, { requestedAt: Date.now(), lastRunUtc: job.lastRunUtc, seenRunning: false });
                updateScheduledJobCard(job);
                showMessage(`${name} started.`, 'info');
                loadScheduledJobs({ statusOnly: true });
            } catch (error) {
                if (button) button.disabled = false;
                if (!error.authHandled) {
                    showMessage(`Failed to start ${name}: ${error.message}`, 'error', { reference: true });
                }
            }
        }

        async function toggleMetadataAuditFindings(jobKey) {
            const card = findScheduledJobCard(jobKey);
            const panel = card && card.querySelector('[data-job-part="findings"]');
            const button = card && card.querySelector('[data-job-action="findings"]');
            if (!panel || !button) return;
            const expand = panel.hidden;
            panel.hidden = !expand;
            button.setAttribute('aria-expanded', String(expand));
            renderHtml(button, html`${uiIcon('list')} ${expand ? 'Hide findings' : 'View findings'}`);
            if (expand) await loadMetadataAuditFindings(panel);
        }

        async function loadMetadataAuditFindings(panel) {
            const loadRequest = ++metadataAuditFindingsRequest;
            panel.setAttribute('aria-busy', 'true');
            renderHtml(panel, html`<div class="loading"><div class="spinner"></div><p>Loading findings...</p></div>`);
            try {
                const response = await fetchScheduledJobsApi('/api/scheduled-jobs/metadata-audit/findings?limit=200');
                const data = await response.json();
                if (loadRequest !== metadataAuditFindingsRequest) return;
                renderMetadataAuditFindings(panel, data);
            } catch (error) {
                if (loadRequest !== metadataAuditFindingsRequest || error.authHandled) return;
                console.error('Failed to load metadata audit findings:', error);
                renderHtml(panel, html`<p class="form-hint">Findings could not be loaded: ${error.message}</p>`);
            } finally {
                if (loadRequest === metadataAuditFindingsRequest) panel.setAttribute('aria-busy', 'false');
            }
        }

        function renderMetadataAuditFindings(panel, data) {
            const findings = Array.isArray(data && data.findings) ? data.findings : [];
            if (!findings.length) {
                renderHtml(panel, html`<p class="form-hint">No findings recorded yet. Run the metadata audit to populate this list.</p>`);
                return;
            }
            const total = Number(data.total) || findings.length;
            renderHtml(panel, html`
                <p class="form-hint">${total} ${total === 1 ? 'finding' : 'findings'} recorded.${total > findings.length ? ` Showing the first ${findings.length}.` : ''}</p>
                <div class="scheduled-job-findings-scroll">
                    <table class="scheduled-job-findings-table">
                        <thead>
                            <tr><th scope="col">Type</th><th scope="col">File</th><th scope="col">Expected series</th><th scope="col">Actual series</th><th scope="col">Details</th></tr>
                        </thead>
                        <tbody>
                            ${findings.map(finding => html`
                                <tr>
                                    <td>${humanizeScheduledJobValue(finding.findingType)}</td>
                                    <td class="scheduled-job-findings-path">${finding.filePath}</td>
                                    <td>${finding.expectedSeries || ''}</td>
                                    <td>${finding.actualSeries || ''}</td>
                                    <td>${finding.details || ''}</td>
                                </tr>
                            `)}
                        </tbody>
                    </table>
                </div>
            `);
        }
