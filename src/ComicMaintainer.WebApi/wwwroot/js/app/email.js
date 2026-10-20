        // ComicMaintainer SPA — email delivery: devices, sending, condensed books and subscriptions.
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

        let emailDevicesCache = [];
        let emailStatusCache = null;
        let currentEmailSend = null;
        // Set while the send modal is usable (SMTP configured and a device saved);
        // the condense plan may still block sending on top of it.
        let emailSendAvailable = false;
        let currentEmailCondensePlan = null;
        // Guards against an out-of-order plan response overwriting a newer one.
        let emailCondensePlanToken = 0;
        // Delivery format chosen before condensing narrowed the picker to the
        // generated formats, restored when the user goes back to sending each
        // issue separately.
        let emailFormatBeforeCondense = null;
        // How many condensed books of the current plan have been rendered.
        let emailCondenseBooksShown = 0;
        const EMAIL_CONDENSE_BOOK_PAGE_SIZE = 10;
        // Condensed book builds the user has running or recently finished. They
        // live on the server, so they outlast this dialog and a page reload.
        let emailCondenseBuilds = [];
        // Builds started from this tab, which are saved as soon as they finish.
        const emailCondenseAutoDownloads = new Set();
        let emailCondenseBuildPollTimer = null;
        let emailCondenseBuildPollInFlight = false;
        const EMAIL_CONDENSE_BUILD_POLL_MS = 2000;
        let currentEmailSubscriptionSeriesTitle = '';

        async function fetchEmailJson(path, options = {}) {
            const response = await fetch(apiUrl(path), {
                credentials: 'include',
                ...options,
                headers: {
                    ...getAuthHeaders(),
                    ...(options.headers || {})
                }
            });
            if (handleAuthError(response)) {
                const error = new Error('Authentication required');
                error.status = response.status;
                throw error;
            }
            if (!response.ok) {
                let message = `HTTP error! status: ${response.status}`;
                try {
                    const body = await response.json();
                    if (body && (body.error || body.message)) {
                        message = body.error || body.message;
                    }
                } catch (_) {}
                const error = new Error(message);
                // Carried so a caller can distinguish an HTTP response from
                // a failure that never received one.
                error.status = response.status;
                throw error;
            }
            if (response.status === 204) return {};
            try {
                return await response.json();
            } catch (_) {
                return {};
            }
        }

        async function loadEmailStatus() {
            try {
                const data = await fetchEmailStatus(true);
                const summary = document.getElementById('emailStatusSummary');
                if (summary) {
                    const from = data.from_address ? ` · From: ${data.from_address}` : '';
                    const max = data.max_attachment_mb ? ` · Max: ${data.max_attachment_mb} MB` : '';
                    summary.textContent = `${data.configured ? '✅ Email delivery is configured' : '⚠️ Email delivery is not fully configured'}${from}${max}`;
                }
            } catch (error) {
                const summary = document.getElementById('emailStatusSummary');
                if (summary) summary.textContent = 'Unable to load email status.';
                console.error('Failed to load email status:', error);
            }
        }

        // Shared /api/email/status fetch. Cached so opening the send modal
        // repeatedly doesn't re-query, but always refreshed when the settings
        // page asks for it (the admin may have just changed SMTP settings).
        async function fetchEmailStatus(force = false) {
            if (!force && emailStatusCache) return emailStatusCache;
            emailStatusCache = await fetchEmailJson('/api/email/status');
            return emailStatusCache;
        }

        async function loadEmailDevices(force = false) {
            if (!force && emailDevicesCache.length) return emailDevicesCache;
            const data = await fetchEmailJson('/api/email/devices');
            emailDevicesCache = Array.isArray(data.devices) ? data.devices : [];
            return emailDevicesCache;
        }

        function renderEmailDeviceOptions(select, devices) {
            if (!select) return;
            renderHtml(select, devices.map(device => html`
                <option value="${device.id}">${device.name} (${device.emailAddress})</option>
            `));
        }

        // A device default must name a concrete format; anything unknown falls
        // back to sending the original archive.
        function normalizeEmailDeviceFormat(value) {
            const normalized = String(value || '').toLowerCase();
            return normalized === 'epub' || normalized === 'azw3' ? normalized : 'original';
        }

        function formatEmailDeliveryFormat(value) {
            const normalized = String(value || '').toLowerCase();
            if (normalized === 'epub') return 'EPUB';
            if (normalized === 'azw3') return 'AZW3 (Kindle)';
            if (normalized === 'device') return 'Device default';
            return 'Original archive';
        }

        function formatEmailDate(value) {
            if (!value) return '—';
            const date = new Date(value);
            return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString();
        }

        function renderEmailDevicesList(devices) {
            const list = document.getElementById('emailDevicesList');
            if (!list) return;
            if (!devices.length) {
                renderHtml(list, html`<div class="empty-state"><p>No ereader devices saved yet.</p></div>`);
                return;
            }
            renderHtml(list, html`
                ${devices.map(device => html`
                    <div class="duplicate-review-card" style="margin-bottom: 10px;">
                        <h3>${device.name}</h3>
                        <p>${device.emailAddress}</p>
                        <div class="duplicate-review-meta">
                            <div><span>Format</span><strong>${formatEmailDeliveryFormat(device.deliveryFormat)}</strong></div>
                            <div><span>Updated</span><strong>${formatEmailDate(device.updatedAt || device.createdAt)}</strong></div>
                        </div>
                        <div class="duplicate-review-actions" style="margin-top: 12px;">
                            <button class="btn btn-small" onclick="editEmailDevice('${jsArg(device.id)}')">Edit</button>
                            <button class="btn btn-small" data-requires-write onclick="sendEmailDeviceTest('${jsArg(device.id)}')">Send Test Email</button>
                            <button class="btn btn-danger btn-small" data-requires-write onclick="deleteEmailDevice('${jsArg(device.id)}')">Delete</button>
                        </div>
                    </div>
                `)}
            `);
        }

        async function openEmailDevicesModal() {
            const modal = document.getElementById('emailDevicesModal');
            const list = document.getElementById('emailDevicesList');
            if (!modal || !list) return;
            modal.classList.add('active');
            renderHtml(list, html`<div class="loading"><div class="spinner"></div><p>Loading devices...</p></div>`);
            resetEmailDeviceForm();
            try {
                renderEmailDevicesList(await loadEmailDevices(true));
            } catch (error) {
                renderHtml(list, html`<div class="empty-state"><p>Failed to load devices: ${error.message}</p></div>`);
            }
        }

        function closeEmailDevicesModal() {
            document.getElementById('emailDevicesModal')?.classList.remove('active');
        }

        function resetEmailDeviceForm() {
            const id = document.getElementById('emailDeviceId');
            const title = document.getElementById('emailDeviceFormTitle');
            if (id) id.value = '';
            if (title) title.textContent = 'Add Device';
            const name = document.getElementById('emailDeviceName');
            const address = document.getElementById('emailDeviceAddress');
            const format = document.getElementById('emailDeviceFormat');
            if (name) name.value = '';
            if (address) address.value = '';
            if (format) format.value = 'original';
        }

        function editEmailDevice(id) {
            const device = emailDevicesCache.find(d => String(d.id) === String(id));
            if (!device) return;
            document.getElementById('emailDeviceId').value = device.id;
            document.getElementById('emailDeviceFormTitle').textContent = 'Edit Device';
            document.getElementById('emailDeviceName').value = device.name || '';
            document.getElementById('emailDeviceAddress').value = device.emailAddress || '';
            document.getElementById('emailDeviceFormat').value = normalizeEmailDeviceFormat(device.deliveryFormat);
        }

        async function saveEmailDevice() {
            const id = document.getElementById('emailDeviceId').value;
            const name = document.getElementById('emailDeviceName').value.trim();
            const emailAddress = document.getElementById('emailDeviceAddress').value.trim();
            const deliveryFormat = normalizeEmailDeviceFormat(document.getElementById('emailDeviceFormat').value);
            if (!name || !emailAddress) {
                showMessage('Device name and email address are required.', 'error');
                return;
            }
            try {
                await fetchEmailJson(id ? `/api/email/devices/${encodeURIComponent(id)}` : '/api/email/devices', {
                    method: id ? 'PUT' : 'POST',
                    body: JSON.stringify({ name, emailAddress, deliveryFormat })
                });
                showMessage(id ? 'Ereader device updated.' : 'Ereader device added.', 'success');
                resetEmailDeviceForm();
                renderEmailDevicesList(await loadEmailDevices(true));
            } catch (error) {
                showMessage('Failed to save ereader device: ' + error.message, 'error');
            }
        }

        async function deleteEmailDevice(id) {
            if (!confirm('Delete this ereader device?')) return;
            try {
                await fetchEmailJson(`/api/email/devices/${encodeURIComponent(id)}`, { method: 'DELETE' });
                showMessage('Ereader device deleted.', 'success');
                renderEmailDevicesList(await loadEmailDevices(true));
            } catch (error) {
                showMessage('Failed to delete ereader device: ' + error.message, 'error');
            }
        }

        async function sendEmailDeviceTest(id) {
            try {
                await fetchEmailJson(`/api/email/devices/${encodeURIComponent(id)}/test`, { method: 'POST' });
                showMessage('Test email queued.', 'success');
            } catch (error) {
                showMessage('Failed to send test email: ' + error.message, 'error');
            }
        }

        async function openEmailSendModalForFile(filePath) {
            await openEmailSendModal({ mode: 'files', files: [filePath] });
        }

        async function openEmailSendModalForSelected() {
            while (true) {
                const pendingLoads = Array.from(selectedSeries, id => selectedSeriesPathLoads.get(id)).filter(Boolean);
                if (!pendingLoads.length) break;
                await Promise.all(pendingLoads);
            }
            const files = Array.from(selectedFiles);
            if (!files.length) {
                showMessage('Select at least one file to email.', 'error');
                return;
            }
            await openEmailSendModal({ mode: 'files', files });
        }

        async function openEmailSendModalForSeries(seriesId, seriesTitle) {
            await openEmailSendModal({ mode: 'series', seriesId, seriesTitle, files: [] });
        }

        async function openEmailSendModal(context) {
            const modal = document.getElementById('emailSendModal');
            const deviceSelect = document.getElementById('emailSendDeviceSelect');
            const summary = document.getElementById('emailSendSummary');
            if (!modal || !deviceSelect || !summary) return;
            currentEmailSend = context;
            summary.textContent = context.mode === 'series'
                ? `Send all issues in "${context.seriesTitle || 'this series'}".`
                : `Send ${context.files.length} selected file${context.files.length === 1 ? '' : 's'}.`;
            renderHtml(deviceSelect, html`<option value="">Loading devices...</option>`);
            setEmailSendHint('');
            setEmailSendNoDevicesVisible(false);
            emailSendAvailable = false;
            resetEmailCondenseControls();
            modal.classList.add('active');
            // A build started earlier (possibly before a reload) is still
            // running on the server, so show it as soon as the dialog opens.
            refreshEmailCondenseBuilds();
            try {
                // Both requests are needed before the modal is usable: an
                // unconfigured SMTP server and an empty device list are the two
                // reasons a send would otherwise fail only after pressing Send.
                const [status, devices] = await Promise.all([
                    fetchEmailStatus().catch(() => null),
                    loadEmailDevices(true)
                ]);

                if (!devices.length) {
                    renderHtml(deviceSelect, html`<option value="">No devices saved</option>`);
                    setEmailSendNoDevicesVisible(true);
                    setEmailSendEnabled(false);
                    return;
                }

                renderEmailDeviceOptions(deviceSelect, devices);

                if (status && status.configured === false) {
                    setEmailSendHint('⚠️ Email delivery is not configured yet. Ask an administrator to set the SMTP host and from-address in Settings → Send to Ereader.');
                    setEmailSendEnabled(false);
                    return;
                }

                emailSendAvailable = true;
                // A condense plan may already have disabled Send (or still be
                // running), so never re-enable it unconditionally here.
                setEmailSendEnabled(isEmailSendAllowedByPlan());
            } catch (error) {
                renderHtml(deviceSelect, html`<option value="">Failed to load devices</option>`);
                setEmailSendEnabled(false);
                showMessage('Failed to load ereader devices: ' + error.message, 'error');
            }
        }

        /**
         * True when the current condense settings permit sending: either the
         * issues go out separately, or a finished plan reported that every book
         * fits the attachment limit.
         */
        function isEmailSendAllowedByPlan() {
            const { mode } = getEmailCondenseSettings();
            if (mode === 'none') return true;
            return currentEmailCondensePlan?.can_email === true;
        }

        function setEmailSendEnabled(enabled) {
            const button = document.getElementById('emailSendSubmitBtn');
            if (button) button.disabled = !enabled;
        }

        function setEmailSendHint(text) {
            const hint = document.getElementById('emailSendStatusHint');
            if (!hint) return;
            hint.textContent = text || '';
            hint.hidden = !text;
        }

        function setEmailSendNoDevicesVisible(visible) {
            const empty = document.getElementById('emailSendNoDevices');
            if (empty) empty.hidden = !visible;
            const group = document.getElementById('emailSendDeviceGroup');
            if (group) group.hidden = visible;
        }

        function closeEmailSendModal() {
            document.getElementById('emailSendModal')?.classList.remove('active');
            currentEmailSend = null;
            currentEmailCondensePlan = null;
            emailSendAvailable = false;
        }

        function resetEmailCondenseControls() {
            currentEmailCondensePlan = null;
            emailCondensePlanToken++;
            emailCondenseBooksShown = 0;
            const mode = document.getElementById('emailCondenseModeSelect');
            if (mode) mode.value = 'none';
            const countGroup = document.getElementById('emailCondenseCountGroup');
            if (countGroup) countGroup.hidden = true;
            // Condensing narrows the format picker; a reopened modal must not
            // keep that restriction, nor the format it fell back to.
            applyEmailCondenseFormatRestriction(false);
            emailFormatBeforeCondense = null;
            const panel = document.getElementById('emailCondensePlan');
            if (panel) {
                clearChildren(panel);
                panel.hidden = true;
            }
        }

        function getEmailCondenseSettings() {
            const mode = document.getElementById('emailCondenseModeSelect')?.value || 'none';
            const raw = parseInt(document.getElementById('emailCondenseCountInput')?.value, 10);
            const issuesPerBook = Number.isFinite(raw) ? Math.min(500, Math.max(2, raw)) : 5;
            return { mode, issuesPerBook };
        }

        /** EPUB and AZW3 are the formats a condensed book can be generated in. */
        function isCondensableEmailFormat(value) {
            const normalized = String(value || '').toLowerCase();
            return normalized === 'epub' || normalized === 'azw3';
        }

        /** The format the condensed books of the open send dialog are built in. */
        function getEmailCondenseFormat() {
            const value = document.getElementById('emailSendFormatSelect')?.value;
            return isCondensableEmailFormat(value) ? String(value).toLowerCase() : 'epub';
        }

        function shortEmailFormatLabel(value) {
            return String(value || '').toLowerCase() === 'azw3' ? 'AZW3' : 'EPUB';
        }

        /**
         * Limits the format picker to the formats a condensed book can be
         * generated in while condensing, and restores every option afterwards.
         * The user still chooses between EPUB and AZW3; only the original
         * archives are unavailable, because they cannot be merged.
         */
        function applyEmailCondenseFormatRestriction(condensing) {
            const format = document.getElementById('emailSendFormatSelect');
            const hint = document.getElementById('emailSendFormatHint');
            if (hint) hint.hidden = !condensing;
            if (!format) return;

            // Left enabled while condensing so EPUB and AZW3 stay selectable.
            format.disabled = false;
            for (const option of Array.from(format.options)) {
                const generated = isCondensableEmailFormat(option.value);
                option.disabled = condensing && !generated;
                option.hidden = condensing && !generated;
            }

            if (condensing) {
                if (emailFormatBeforeCondense === null) emailFormatBeforeCondense = format.value;
                if (!isCondensableEmailFormat(format.value)) format.value = 'epub';
            } else if (emailFormatBeforeCondense !== null) {
                format.value = emailFormatBeforeCondense;
                emailFormatBeforeCondense = null;
            }
        }

        function onEmailCondenseChange() {
            const { mode } = getEmailCondenseSettings();
            const countGroup = document.getElementById('emailCondenseCountGroup');
            if (countGroup) countGroup.hidden = mode !== 'count';

            applyEmailCondenseFormatRestriction(mode !== 'none');

            refreshEmailCondensePlan();
        }

        /**
         * Keeps the condense read-out in step with the chosen format: the plan
         * itself does not change, only the format its books are built in.
         */
        function onEmailSendFormatChange() {
            const { mode } = getEmailCondenseSettings();
            if (mode === 'none') return;

            // Picking a format while condensing is a deliberate choice, so it
            // replaces the format remembered before condensing narrowed the
            // picker and survives turning condensing off again.
            emailFormatBeforeCondense = null;

            if (currentEmailCondensePlan) renderEmailCondensePlan(currentEmailCondensePlan);
        }

        function buildEmailCondenseRequest(extra = {}) {
            const { mode, issuesPerBook } = getEmailCondenseSettings();
            // The device and the skip setting change which issues are left to
            // condense, so the plan (and any download built from it) must use
            // the same values the send would.
            const deviceId = parseInt(document.getElementById('emailSendDeviceSelect')?.value, 10);
            return {
                ...(currentEmailSend?.mode === 'series'
                    ? { seriesId: currentEmailSend.seriesId }
                    : { files: currentEmailSend?.files || [] }),
                condenseMode: mode,
                issuesPerBook,
                deliveryFormat: getEmailCondenseFormat(),
                deviceId: Number.isFinite(deviceId) ? deviceId : null,
                skipAlreadyDelivered: document.getElementById('emailSkipAlreadyDeliveredCheckbox')?.checked !== false,
                ...extra
            };
        }

        /**
         * Asks the server what the current condense settings would produce so
         * the user sees, before sending, how many books they get, how large
         * each one is, and which ones have to be downloaded instead of emailed.
         */
        async function refreshEmailCondensePlan() {
            const panel = document.getElementById('emailCondensePlan');
            if (!panel || !currentEmailSend) return;

            const { mode } = getEmailCondenseSettings();
            const token = ++emailCondensePlanToken;
            currentEmailCondensePlan = null;

            if (mode === 'none') {
                clearChildren(panel);
                panel.hidden = true;
                setEmailSendEnabled(emailSendAvailable);
                return;
            }

            panel.hidden = false;
            renderHtml(panel, html`<div>Calculating condensed books...</div>`);
            setEmailSendEnabled(false);

            try {
                const plan = await fetchEmailJson('/api/email/condense-plan', {
                    method: 'POST',
                    body: JSON.stringify(buildEmailCondenseRequest())
                });
                if (token !== emailCondensePlanToken) return;
                currentEmailCondensePlan = plan;
                renderEmailCondensePlan(plan);
                setEmailSendEnabled(emailSendAvailable && plan.can_email === true);
            } catch (error) {
                if (token !== emailCondensePlanToken) return;
                renderHtml(panel, html`<div>Could not calculate the condensed books: ${error.message}</div>`);
                setEmailSendEnabled(false);
            }
        }

        function renderEmailCondensePlan(plan) {
            const panel = document.getElementById('emailCondensePlan');
            if (!panel) return;

            const books = Array.isArray(plan.books) ? plan.books : [];
            if (!books.length) {
                renderHtml(panel, html`<div>Nothing to condense in this selection.</div>`);
                return;
            }

            const limit = formatFileSize(plan.max_attachment_bytes || 0);
            const oversized = plan.oversized_book_count || 0;
            const formatLabel = shortEmailFormatLabel(getEmailCondenseFormat());
            const heading = `${books.length} ${formatLabel}${books.length === 1 ? '' : 's'} from ${plan.total_issues} issue${plan.total_issues === 1 ? '' : 's'} (attachment limit ${limit}).`;

            renderHtml(panel, html`
                <div style="margin-bottom: 8px;">${heading}</div>
                ${oversized
                    ? html`<div style="margin-bottom: 8px; color: var(--text-warning, #d9822b);">
                        ⚠️ ${oversized} of these book${oversized === 1 ? '' : 's'} ${oversized === 1 ? 'is' : 'are'} larger than the ${limit} attachment limit and cannot be emailed. Download ${oversized === 1 ? 'it' : 'them'} instead, or condense fewer issues per book.
                      </div>`
                    : ''}
                <div id="emailCondenseBookList"></div>
                <div id="emailCondenseBookListMore" style="margin-top: 6px;"></div>
            `);

            emailCondenseBooksShown = 0;
            showMoreCondensedBooks();
        }

        /**
         * Appends the next page of planned books. Every book gets its own
         * Download button: an oversized book anywhere in the plan blocks the
         * send, so it must be reachable however far down the list it is.
         */
        function showMoreCondensedBooks() {
            const books = Array.isArray(currentEmailCondensePlan?.books) ? currentEmailCondensePlan.books : [];
            const list = document.getElementById('emailCondenseBookList');
            const more = document.getElementById('emailCondenseBookListMore');
            if (!list) return;

            const next = Math.min(books.length, emailCondenseBooksShown + EMAIL_CONDENSE_BOOK_PAGE_SIZE);
            for (let index = emailCondenseBooksShown; index < next; index++) {
                const book = books[index];
                appendHtml(list, html`
                    <div style="display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 4px 0;">
                        <span>${book.display_name} · ${book.issue_count} issue${book.issue_count === 1 ? '' : 's'} · ~${formatFileSize(book.estimated_bytes || 0)}${book.exceeds_attachment_limit ? ' · too large to email' : ''}</span>
                        <button class="btn btn-small" type="button" onclick="downloadCondensedBook(${index})">Download</button>
                    </div>
                `);
            }
            emailCondenseBooksShown = next;

            if (!more) return;
            if (emailCondenseBooksShown >= books.length) {
                clearChildren(more);
                return;
            }

            renderHtml(more, html`
                <button class="btn btn-small" type="button" onclick="showMoreCondensedBooks()">Show more (${books.length - emailCondenseBooksShown} left)</button>
            `);
        }

        /**
         * Starts a server-side build of one condensed book and follows it to
         * completion. The build is deliberately not awaited inside a single
         * request: condensing a large selection takes minutes, far longer than
         * a browser or a reverse proxy will hold a response open, which used to
         * leave the user with a spinner that could die without ever reporting
         * why. The build now has an id, a progress read-out and a recorded
         * failure reason, all of which survive closing this dialog.
         */
        async function downloadCondensedBook(index) {
            if (!currentEmailSend) return;
            try {
                const build = await fetchEmailJson('/api/email/condense-builds', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(buildEmailCondenseRequest({ bookIndex: index }))
                });
                if (!build || !build.build_id) throw new Error('The server did not return a build id.');

                // Only a build this tab started is saved automatically; one
                // found on a later visit waits behind a Download button.
                emailCondenseAutoDownloads.add(build.build_id);
                upsertEmailCondenseBuild(build);
                renderEmailCondenseBuilds();
                startEmailCondenseBuildPolling();
                showMessage(`Building the condensed ${shortEmailFormatLabel(getEmailCondenseFormat())}. Progress is shown in the send dialog; you can close it and come back.`, 'info');
            } catch (error) {
                showMessage('Failed to start the condensed book build: ' + error.message, 'error');
                reportCondenseFailure('start build', error);
            }
        }

        /** Replaces (or appends) one build in the tracked list. */
        function upsertEmailCondenseBuild(build) {
            const existing = emailCondenseBuilds.findIndex(b => b.build_id === build.build_id);
            if (existing >= 0) emailCondenseBuilds[existing] = build;
            else emailCondenseBuilds.unshift(build);
        }

        function isEmailCondenseBuildActive(build) {
            return build?.status === 'queued' || build?.status === 'running';
        }

        /**
         * Loads the builds this user has running or recently finished, so a
         * build survives closing the dialog or reloading the page.
         */
        async function refreshEmailCondenseBuilds() {
            try {
                const data = await fetchEmailJson('/api/email/condense-builds');
                reconcileEmailCondenseBuilds(Array.isArray(data.builds) ? data.builds : []);
            } catch (error) {
                // A failure here must not break the send dialog; the builds
                // panel simply stays as it was.
                console.warn('Could not load condensed book builds', error);
            }
        }

        function startEmailCondenseBuildPolling() {
            if (emailCondenseBuildPollTimer !== null) return;
            emailCondenseBuildPollTimer = setTimeout(() => {
                emailCondenseBuildPollTimer = null;
                pollEmailCondenseBuilds();
            }, EMAIL_CONDENSE_BUILD_POLL_MS);
        }

        function stopEmailCondenseBuildPolling() {
            if (emailCondenseBuildPollTimer === null) return;
            clearInterval(emailCondenseBuildPollTimer);
            emailCondenseBuildPollTimer = null;
        }

        /**
         * Polls every tracked build, announces the ones that just finished and
         * stops polling once nothing is left running. Polling continues while
         * the dialog is closed so a long build still reports its outcome.
         */
        async function pollEmailCondenseBuilds() {
            if (emailCondenseBuildPollInFlight) return;
            if (emailCondenseBuildPollTimer !== null) {
                clearTimeout(emailCondenseBuildPollTimer);
                emailCondenseBuildPollTimer = null;
            }

            emailCondenseBuildPollInFlight = true;
            try {
                const data = await fetchEmailJson('/api/email/condense-builds');
                reconcileEmailCondenseBuilds(Array.isArray(data.builds) ? data.builds : []);
            } catch (error) {
                // Transient failures (a restart, a dropped connection) must not
                // abandon the build; the next tick tries again.
                console.warn('Could not poll condensed book builds', error);
            } finally {
                emailCondenseBuildPollInFlight = false;
                if (emailCondenseBuilds.some(isEmailCondenseBuildActive)) startEmailCondenseBuildPolling();
                else stopEmailCondenseBuildPolling();
            }
        }

        function reconcileEmailCondenseBuilds(builds) {
            const previous = new Map(emailCondenseBuilds.map(b => [b.build_id, b.status]));
            emailCondenseBuilds = builds.map(build => {
                const current = previous.get(build.build_id);
                return current && !isCondensedBookBuildTerminal(build.status) &&
                    isCondensedBookBuildTerminal(current)
                    ? emailCondenseBuilds.find(existing => existing.build_id === build.build_id)
                    : build;
            });

            for (const build of emailCondenseBuilds) {
                if (previous.get(build.build_id) === build.status) continue;
                if (build.status === 'completed') {
                    if (emailCondenseAutoDownloads.delete(build.build_id)) {
                        downloadBuiltCondensedBook(build.build_id);
                    } else {
                        showMessage(`"${build.display_name}" is ready to download.`, 'success');
                    }
                } else if (build.status === 'failed') {
                    emailCondenseAutoDownloads.delete(build.build_id);
                    showMessage(`Building "${build.display_name}" failed: ${build.error || 'unknown error'}`, 'error');
                } else if (build.status === 'cancelled') {
                    emailCondenseAutoDownloads.delete(build.build_id);
                }
            }

            renderEmailCondenseBuilds();
            if (emailCondenseBuilds.some(isEmailCondenseBuildActive)) startEmailCondenseBuildPolling();
            else stopEmailCondenseBuildPolling();
        }

        function isCondensedBookBuildTerminal(status) {
            return status === 'completed' || status === 'failed' || status === 'cancelled';
        }

        /**
         * Files a condensed-book failure with the automated issue tracker.
         *
         * Condensing a whole series is the longest and most failure-prone thing
         * this UI asks for, and every one of its failures used to end at a
         * banner: the catch blocks below show the user a message and nothing
         * else, so a broken build or a download that never arrives was invisible
         * to everyone except the person looking at it. These paths now report
         * network failures through the same issue pipeline as a server-side
         * error, while HTTP errors remain the server's responsibility.
         *
         * HTTP responses are already handled or reported by the server. Only
         * failures without a response (such as a dropped connection) are
         * reported here, avoiding duplicate issues for server-side failures.
         */
        function reportCondenseFailure(stage, error) {
            if (typeof sendClientErrorReport !== 'function') return;
            if (error?.status != null) return;
            sendClientErrorReport(error, 'error', `condensed book: ${stage}`);
        }

        /** Saves the finished book of a completed build. */
        async function downloadBuiltCondensedBook(buildId) {
            const build = emailCondenseBuilds.find(b => b.build_id === buildId);
            try {
                const response = await fetch(apiUrl(`/api/email/condense-builds/${encodeURIComponent(buildId)}/download`), {
                    credentials: 'include',
                    headers: getAuthHeaders()
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    let message = `HTTP error! status: ${response.status}`;
                    try {
                        const body = await response.json();
                        if (body && (body.error || body.message)) message = body.error || body.message;
                    } catch (_) {}
                    const error = new Error(message);
                    error.status = response.status;
                    throw error;
                }

                const blob = await response.blob();
                const url = URL.createObjectURL(blob);
                const link = document.createElement('a');
                link.href = url;
                // The build reports the format it produced, so an AZW3 is not
                // saved under an .epub name the ereader would reject.
                link.download = `${build?.display_name || 'condensed'}.${build?.delivery_format === 'azw3' ? 'azw3' : 'epub'}`;
                document.body.appendChild(link);
                link.click();
                link.remove();
                // Revoked on a later turn, not in this one: the browser has only
                // been handed the object URL at this point, and tearing it down
                // in the same tick as the click cancels the save outright in
                // some browsers.
                setTimeout(() => URL.revokeObjectURL(url), 60000);
                showMessage(`Condensed ${shortEmailFormatLabel(build?.delivery_format)} downloaded.`, 'success');
            } catch (error) {
                showMessage('Failed to download the condensed book: ' + error.message, 'error');
                reportCondenseFailure('download', error);
            }
        }

        async function cancelCondensedBookBuild(buildId) {
            try {
                await fetchEmailJson(`/api/email/condense-builds/${encodeURIComponent(buildId)}/cancel`, { method: 'POST' });
                emailCondenseAutoDownloads.delete(buildId);
                showMessage('Cancelling the condensed book build...', 'info');
                await pollEmailCondenseBuilds();
            } catch (error) {
                showMessage('Failed to cancel the build: ' + error.message, 'error');
            }
        }

        /** Forgets a finished build and frees the file it produced. */
        async function dismissCondensedBookBuild(buildId) {
            try {
                await fetchEmailJson(`/api/email/condense-builds/${encodeURIComponent(buildId)}`, { method: 'DELETE' });
            } catch (error) {
                showMessage('Failed to remove the build: ' + error.message, 'error');
                return;
            }
            emailCondenseAutoDownloads.delete(buildId);
            emailCondenseBuilds = emailCondenseBuilds.filter(b => b.build_id !== buildId);
            renderEmailCondenseBuilds();
        }

        function describeEmailCondenseBuild(build) {
            const progress = build.progress || {};
            const pass = progress.total_passes > 1 ? ` · pass ${progress.pass}/${progress.total_passes}` : '';
            switch (build.status) {
                case 'queued':
                    return 'Waiting for another book to finish building...';
                case 'running':
                    if (progress.phase === 'reading') {
                        return `Reading issue ${progress.completed_issues + 1} of ${progress.total_issues}...`;
                    }
                    if (progress.phase === 'validating') return `Checking the finished book...${pass}`;
                    if (progress.phase === 'recompressing') return `Too large; rebuilding with stronger compression${pass}`;
                    if (progress.total_pages > 0) {
                        const issue = progress.current_issue ? ` · ${progress.current_issue}` : '';
                        return `Page ${progress.completed_pages} of ${progress.total_pages}${issue}${pass}`;
                    }
                    return 'Starting...';
                case 'completed':
                    return `Ready · ${formatFileSize(build.file_size_bytes || 0)}`;
                case 'failed':
                    return `Failed: ${build.error || 'unknown error'}`;
                case 'cancelled':
                    return 'Cancelled';
                default:
                    return build.status;
            }
        }

        /**
         * Renders the tracked builds: a progress bar while one runs, the
         * failure reason when one breaks, and a Download button for a finished
         * book until the server discards it.
         */
        function renderEmailCondenseBuilds() {
            const panel = document.getElementById('emailCondenseBuilds');
            if (!panel) return;

            if (!emailCondenseBuilds.length) {
                clearChildren(panel);
                panel.hidden = true;
                return;
            }

            panel.hidden = false;
            renderHtml(panel, html`<div style="margin-bottom: 8px;">Condensed book builds</div>`);

            for (const build of emailCondenseBuilds) {
                const progress = build.progress || {};
                const percent = build.status === 'completed' ? 100 : (progress.percentage || 0);
                const active = isEmailCondenseBuildActive(build);
                const failed = build.status === 'failed';
                appendHtml(panel, html`
                    <div style="padding: 6px 0; border-top: 1px solid var(--border-primary);">
                        <div style="display: flex; align-items: center; justify-content: space-between; gap: 8px;">
                            <span>${build.display_name} · ${build.issue_count} issue${build.issue_count === 1 ? '' : 's'}</span>
                            <span style="display: flex; gap: 6px;">
                                ${build.status === 'completed'
                                    ? html`<button class="btn btn-small" type="button" onclick="downloadBuiltCondensedBook('${jsArg(build.build_id)}')">Download</button>`
                                    : ''}
                                ${active
                                    ? html`<button class="btn btn-small" type="button" onclick="cancelCondensedBookBuild('${jsArg(build.build_id)}')">Cancel</button>`
                                    : html`<button class="btn btn-small" type="button" onclick="dismissCondensedBookBuild('${jsArg(build.build_id)}')">Dismiss</button>`}
                            </span>
                        </div>
                        ${active
                            ? html`<div class="progress-bar-container" style="height: 8px; margin: 6px 0;">
                                <div class="progress-bar-fill" style="width: ${percent}%;"></div>
                              </div>`
                            : ''}
                        <div style="${failed ? 'color: var(--text-warning, #d9822b);' : ''}">${describeEmailCondenseBuild(build)}</div>
                    </div>
                `);
            }
        }

        async function openEmailDeliveryHistoryModal() {
            const modal = document.getElementById('emailDeliveryHistoryModal');
            if (!modal) return;
            modal.classList.add('active');
            await loadEmailDeliveryHistory();
        }

        function closeEmailDeliveryHistoryModal() {
            document.getElementById('emailDeliveryHistoryModal')?.classList.remove('active');
        }

        async function loadEmailDeliveryHistory() {
            const list = document.getElementById('emailDeliveryHistoryList');
            if (!list) return;
            renderHtml(list, html`<div class="loading"><div class="spinner"></div><p>Loading deliveries...</p></div>`);
            try {
                const data = await fetchEmailJson('/api/email/deliveries?limit=50');
                renderEmailDeliveryHistory(Array.isArray(data.deliveries) ? data.deliveries : []);
            } catch (error) {
                renderHtml(list, html`<div class="empty-state"><p>Failed to load delivery history: ${error.message}</p></div>`);
            }
        }

        function formatEmailDeliveryStatus(status) {
            const normalized = String(status || '').toLowerCase();
            if (normalized === 'sent') return '✅ Sent';
            if (normalized === 'failed') return '❌ Failed';
            if (normalized === 'pending') return '⏳ Pending';
            return status || 'Unknown';
        }

        function renderEmailDeliveryHistory(deliveries) {
            const list = document.getElementById('emailDeliveryHistoryList');
            if (!list) return;
            if (!deliveries.length) {
                renderHtml(list, html`<div class="empty-state"><p>No comics have been emailed yet.</p></div>`);
                return;
            }
            renderHtml(list, deliveries.map(delivery => html`
                <div class="combine-folder-row" style="padding: 8px 0; border-bottom: 1px solid var(--border-primary);">
                    <div style="font-weight: 500;">${delivery.fileName || delivery.filePath}</div>
                    <div class="combine-folders-meta">
                        ${formatEmailDeliveryStatus(delivery.status)} · ${delivery.deviceName} (${delivery.deviceEmail}) · ${formatEmailDeliveryFormat(delivery.deliveryFormat)}${delivery.issueCount > 1 ? ` · condensed ${delivery.issueCount} issues` : ''} · ${formatEmailDate(delivery.sentAt || delivery.createdAt)}
                    </div>
                    ${delivery.errorMessage
                        ? html`<div class="combine-folders-meta" style="color: var(--text-error, #d9534f);">${delivery.errorMessage}</div>`
                        : ''}
                </div>
            `));
        }

        async function submitEmailSend() {
            if (!currentEmailSend) return;
            const deviceId = document.getElementById('emailSendDeviceSelect').value;
            const format = document.getElementById('emailSendFormatSelect').value;
            const skipAlreadyDelivered = document.getElementById('emailSkipAlreadyDeliveredCheckbox').checked;
            const { mode: condenseMode, issuesPerBook } = getEmailCondenseSettings();
            if (!deviceId) {
                showMessage('Choose an ereader device.', 'error');
                return;
            }
            const button = document.getElementById('emailSendSubmitBtn');
            if (button) button.disabled = true;
            try {
                const result = currentEmailSend.mode === 'series'
                    ? await fetchEmailJson('/api/email/send-series', {
                        method: 'POST',
                        body: JSON.stringify({
                            seriesId: currentEmailSend.seriesId,
                            deviceId,
                            deliveryFormat: format,
                            skipAlreadyDelivered,
                            condenseMode,
                            issuesPerBook
                        })
                    })
                    : await fetchEmailJson('/api/email/send', {
                        method: 'POST',
                        body: JSON.stringify({
                            files: currentEmailSend.files,
                            deviceId,
                            deliveryFormat: format === 'device' ? undefined : format,
                            skipAlreadyDelivered,
                            condenseMode,
                            issuesPerBook
                        })
                    });
                closeEmailSendModal();
                reportEmailSendResult(result);
            } catch (error) {
                showMessage('Failed to email comics: ' + error.message, 'error');
            } finally {
                if (button) button.disabled = false;
            }
        }

        function reportEmailSendResult(result) {
            const queued = Array.isArray(result.queued) ? result.queued.length : 0;
            const skipped = result.skipped && typeof result.skipped === 'object' ? Object.entries(result.skipped) : [];
            showMessage(`Queued ${queued} email deliver${queued === 1 ? 'y' : 'ies'}${skipped.length ? `; skipped ${skipped.length}.` : '.'}`, queued ? 'success' : 'info');
            if (skipped.length) {
                const detail = skipped.slice(0, 5).map(([path, reason]) => `${extractDisplayName(path)}: ${reason}`).join('; ');
                showMessage(`Skipped: ${detail}${skipped.length > 5 ? '; …' : ''}`, 'warning');
            }
        }

        async function openEmailSubscriptionModal(seriesTitle) {
            currentEmailSubscriptionSeriesTitle = seriesTitle || '';
            const modal = document.getElementById('emailSubscriptionModal');
            const series = document.getElementById('emailSubscriptionSeries');
            const deviceSelect = document.getElementById('emailSubscriptionDeviceSelect');
            const list = document.getElementById('emailSubscriptionsList');
            if (!modal || !series || !deviceSelect || !list) return;
            series.textContent = `Series: ${seriesTitle}`;
            renderHtml(deviceSelect, html`<option value="">Loading devices...</option>`);
            renderHtml(list, html`<div class="loading"><div class="spinner"></div><p>Loading auto-send subscriptions...</p></div>`);
            modal.classList.add('active');
            try {
                const devices = await loadEmailDevices(true);
                renderEmailDeviceOptions(deviceSelect, devices);
                const data = await fetchEmailJson(`/api/email/subscriptions?series=${encodeURIComponent(seriesTitle)}`);
                renderEmailSubscriptionsList(Array.isArray(data.subscriptions) ? data.subscriptions : []);
            } catch (error) {
                renderHtml(list, html`<div class="empty-state"><p>Failed to load subscriptions: ${error.message}</p></div>`);
            }
        }

        function renderEmailSubscriptionsList(subscriptions) {
            const list = document.getElementById('emailSubscriptionsList');
            if (!list) return;
            if (!subscriptions.length) {
                renderHtml(list, html`<div class="empty-state"><p>No auto-send subscriptions for this series.</p></div>`);
                return;
            }
            renderHtml(list, subscriptions.map(sub => html`
                <div class="combine-folder-row" style="padding: 8px 0; border-bottom: 1px solid var(--border-primary);">
                    <div style="font-weight: 500;">${sub.deviceName} · ${formatEmailDeliveryFormat(sub.deliveryFormat)} · ${sub.enabled ? 'Enabled' : 'Disabled'}</div>
                    <div class="combine-folders-meta">${sub.deviceEmail}${sub.lastSentAt ? ` · Last sent ${formatEmailDate(sub.lastSentAt)}` : ''}</div>
                    <button class="btn btn-danger btn-small" type="button" data-requires-write onclick="deleteEmailSubscription('${jsArg(sub.id)}')">Delete</button>
                </div>
            `));
        }

        function closeEmailSubscriptionModal() {
            document.getElementById('emailSubscriptionModal')?.classList.remove('active');
            currentEmailSubscriptionSeriesTitle = '';
        }

        async function saveEmailSubscription() {
            const deviceId = document.getElementById('emailSubscriptionDeviceSelect').value;
            const deliveryFormat = document.getElementById('emailSubscriptionFormatSelect').value;
            const enabled = document.getElementById('emailSubscriptionEnabledCheckbox').checked;
            if (!currentEmailSubscriptionSeriesTitle || !deviceId) {
                showMessage('Choose an ereader device for auto-send.', 'error');
                return;
            }
            try {
                await fetchEmailJson('/api/email/subscriptions', {
                    method: 'PUT',
                    body: JSON.stringify({
                        seriesTitle: currentEmailSubscriptionSeriesTitle,
                        deviceId,
                        deliveryFormat,
                        enabled
                    })
                });
                showMessage('Auto-send subscription saved.', 'success');
                const data = await fetchEmailJson(`/api/email/subscriptions?series=${encodeURIComponent(currentEmailSubscriptionSeriesTitle)}`);
                renderEmailSubscriptionsList(Array.isArray(data.subscriptions) ? data.subscriptions : []);
            } catch (error) {
                showMessage('Failed to save auto-send subscription: ' + error.message, 'error');
            }
        }

        async function deleteEmailSubscription(id) {
            try {
                await fetchEmailJson(`/api/email/subscriptions/${encodeURIComponent(id)}`, { method: 'DELETE' });
                showMessage('Auto-send subscription deleted.', 'success');
                const data = await fetchEmailJson(`/api/email/subscriptions?series=${encodeURIComponent(currentEmailSubscriptionSeriesTitle)}`);
                renderEmailSubscriptionsList(Array.isArray(data.subscriptions) ? data.subscriptions : []);
            } catch (error) {
                showMessage('Failed to delete auto-send subscription: ' + error.message, 'error');
            }
        }
