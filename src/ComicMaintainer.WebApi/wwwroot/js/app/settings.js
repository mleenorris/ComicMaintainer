        // ComicMaintainer SPA — settings page, logs, processing history, diagnostics, account and maintenance actions.
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

        // Update theme from settings modal
        async function updateThemeFromSettings() {
            const selectedTheme = document.getElementById('themeSelect').value;
            setTheme(selectedTheme);
            await setPreferences({ theme: selectedTheme });
        }

        // Update default library view from settings modal
        async function updateDefaultLibraryViewFromSettings() {
            const select = document.getElementById('defaultLibraryViewSelect');
            if (!select) return;
            const selectedView = select.value === 'series' ? 'series' : 'files';
            try {
                const response = await fetch(apiUrl('/api/settings/default-library-view'), {
                    method: 'PUT',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ view: selectedView })
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                showMessage(`Default library view set to ${selectedView === 'series' ? 'Series' : 'Files (folder view)'}`, 'success');
            } catch (error) {
                console.error('Failed to update default library view:', error);
                showMessage('Failed to update default library view: ' + error.message, 'error');
            }
        }

        // Update the global default preferred series-name language. Posting
        // an empty string clears the default and reverts to the canonical
        // title for any series that doesn't have its own override.
        async function updateDefaultPreferredLanguageFromSettings() {
            const select = document.getElementById('defaultPreferredLanguageSelect');
            if (!select) return;
            const value = select.value || null;
            try {
                const response = await fetch(apiUrl('/api/settings/default-preferred-language'), {
                    method: 'PUT',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ language: value })
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    let msg = `HTTP error! status: ${response.status}`;
                    try { const j = await response.json(); if (j && j.error) msg = j.error; } catch {}
                    throw new Error(msg);
                }
                showMessage('Default preferred language updated', 'success');
                if (typeof loadSeriesLibrary === 'function') {
                    loadSeriesLibrary(1, true);
                }
            } catch (error) {
                console.error('Failed to update default preferred language:', error);
                showMessage('Failed to update default preferred language: ' + error.message, 'error');
            }
        }
        
        // Note: Watcher is now automatically enabled/disabled based on rename and normalize settings
        // No need for explicit watcher toggle
        
        async function updateWatcherEnableRename() {
            const enabled = document.getElementById('watcherEnableRenameCheckbox').checked;
            
            try {
                const response = await fetch(apiUrl('/api/settings/watcher-enable-rename'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ enabled: enabled })
                });
                
                if (handleAuthError(response)) {
                    document.getElementById('watcherEnableRenameCheckbox').checked = !enabled;
                    return;
                }
                
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                const statusText = enabled ? 'enabled' : 'disabled';
                showMessage(`File rename ${statusText} successfully!`, 'success');
            } catch (error) {
                showMessage('Failed to update rename setting: ' + error.message, 'error');
                document.getElementById('watcherEnableRenameCheckbox').checked = !enabled;
            }
        }
        
        async function updateWatcherEnableNormalize() {
            const enabled = document.getElementById('watcherEnableNormalizeCheckbox').checked;
            
            try {
                const response = await fetch(apiUrl('/api/settings/watcher-enable-normalize'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ enabled: enabled })
                });
                
                if (handleAuthError(response)) {
                    document.getElementById('watcherEnableNormalizeCheckbox').checked = !enabled;
                    return;
                }
                
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                const statusText = enabled ? 'enabled' : 'disabled';
                showMessage(`Metadata normalize ${statusText} successfully!`, 'success');
            } catch (error) {
                showMessage('Failed to update normalize setting: ' + error.message, 'error');
                document.getElementById('watcherEnableNormalizeCheckbox').checked = !enabled;
            }
        }
        
        async function updateWriteCoverToFirstArchive() {
            const enabled = document.getElementById('writeCoverToFirstArchiveCheckbox').checked;
            
            try {
                const response = await fetch(apiUrl('/api/settings/write-cover-to-first-archive'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ enabled: enabled })
                });
                
                if (handleAuthError(response)) {
                    document.getElementById('writeCoverToFirstArchiveCheckbox').checked = !enabled;
                    return;
                }
                
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                const statusText = enabled ? 'enabled' : 'disabled';
                showMessage(`Embed cover into first issue archive ${statusText} successfully!`, 'success');
            } catch (error) {
                showMessage('Failed to update cover embed setting: ' + error.message, 'error');
                document.getElementById('writeCoverToFirstArchiveCheckbox').checked = !enabled;
            }
        }
        
        async function updateWriteCoverToSeriesFolder() {
            const enabled = document.getElementById('writeCoverToSeriesFolderCheckbox').checked;
            
            try {
                const response = await fetch(apiUrl('/api/settings/write-cover-to-series-folder'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ enabled: enabled })
                });
                
                if (handleAuthError(response)) {
                    document.getElementById('writeCoverToSeriesFolderCheckbox').checked = !enabled;
                    return;
                }
                
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                const statusText = enabled ? 'enabled' : 'disabled';
                showMessage(`Write cover into series folder ${statusText} successfully!`, 'success');
            } catch (error) {
                showMessage('Failed to update cover folder setting: ' + error.message, 'error');
                document.getElementById('writeCoverToSeriesFolderCheckbox').checked = !enabled;
            }
        }
        
        async function updateAllowRegistration() {
            const checkbox = document.getElementById('allowRegistrationCheckbox');
            const enabled = checkbox.checked;

            try {
                const response = await fetch(apiUrl('/api/settings/allow-registration'), {
                    method: 'PUT',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ enabled: enabled })
                });

                if (handleAuthError(response)) {
                    checkbox.checked = !enabled;
                    return;
                }

                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }

                showMessage(`Self-service registration ${enabled ? 'enabled' : 'disabled'} successfully!`, 'success');
            } catch (error) {
                checkbox.checked = !enabled;
                showMessage('Failed to update registration setting: ' + error.message, 'error');
            }
        }

        async function updateDownloadExternalSeriesImages() {
            const enabled = document.getElementById('downloadExternalSeriesImagesCheckbox').checked;
            
            try {
                const response = await fetch(apiUrl('/api/settings/download-external-series-images'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ enabled: enabled })
                });
                
                if (handleAuthError(response)) {
                    document.getElementById('downloadExternalSeriesImagesCheckbox').checked = !enabled;
                    return;
                }
                
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                const statusText = enabled ? 'enabled' : 'disabled';
                showMessage(`Download external series images ${statusText} successfully!`, 'success');
            } catch (error) {
                showMessage('Failed to update series image download setting: ' + error.message, 'error');
                document.getElementById('downloadExternalSeriesImagesCheckbox').checked = !enabled;
            }
        }
        
        // Settings is a page (#/settings); the router calls loadSettings()
        // whenever it is shown so the form always reflects the server.
        function openSettings() {
            navigate('#/settings');
        }

        function setSettingsLoadStatus(state, message = '') {
            const status = document.getElementById('settingsLoadStatus');
            const messageElement = document.getElementById('settingsLoadMessage');
            const retryButton = document.getElementById('settingsRetryButton');
            if (!status) return;
            status.hidden = state === 'loaded';
            if (messageElement) messageElement.textContent = message;
            if (retryButton) retryButton.hidden = state !== 'error';
        }

        async function loadSettings() {
            const loadRequest = ++settingsLoadRequest;
            settingsLoaded = false;
            setSettingsLoadStatus('loading', 'Loading settings…');
            applyCapabilities();
            loadBackups();
            try {
                // Load all settings
                const settingsUrl = apiUrl('/api/settings');
                console.log('[SETTINGS] Fetching settings from URL:', settingsUrl);
                
                const settingsResponse = await fetch(settingsUrl, {
                    headers: getAuthHeaders()
                });

                console.log('[SETTINGS] Response status:', settingsResponse.status, 'OK:', settingsResponse.ok, 'URL:', settingsResponse.url);

                if (handleAuthError(settingsResponse)) return;

                if (!settingsResponse.ok) {
                    const errorText = await settingsResponse.text();
                    console.error('[SETTINGS] Failed to load settings. Status:', settingsResponse.status, 'Response:', errorText);
                    throw new Error(`HTTP error! status: ${settingsResponse.status} - ${errorText}`);
                }
                
                const settingsData = await settingsResponse.json();
                if (loadRequest !== settingsLoadRequest) return;
                console.log('[SETTINGS] Settings data loaded:', settingsData);
                
                document.getElementById('filenameFormat').value = settingsData.filename_format || '';
                document.getElementById('currentFormat').textContent = settingsData.filename_format || '{series} - Chapter {issue}';
                
                // Load current theme
                const currentTheme = document.documentElement.getAttribute('data-theme') || 'light';
                document.getElementById('themeSelect').value = currentTheme;
                
                // Load watcher enable rename status
                document.getElementById('watcherEnableRenameCheckbox').checked = settingsData.watcher_enable_rename;
                
                // Load watcher enable normalize status
                document.getElementById('watcherEnableNormalizeCheckbox').checked = settingsData.watcher_enable_normalize;
                
                // Load series cover embed status
                const writeCoverCheckbox = document.getElementById('writeCoverToFirstArchiveCheckbox');
                if (writeCoverCheckbox) {
                    writeCoverCheckbox.checked = !!settingsData.write_cover_to_first_archive;
                }

                // Load series cover folder-sidecar status
                const writeCoverFolderCheckbox = document.getElementById('writeCoverToSeriesFolderCheckbox');
                if (writeCoverFolderCheckbox) {
                    writeCoverFolderCheckbox.checked = !!settingsData.write_cover_to_series_folder;
                }

                // Load self-service registration status
                const allowRegistrationCheckbox = document.getElementById('allowRegistrationCheckbox');
                if (allowRegistrationCheckbox) {
                    allowRegistrationCheckbox.checked = !!settingsData.allow_registration;
                }

                // Load external series image download status
                const downloadSeriesImagesCheckbox = document.getElementById('downloadExternalSeriesImagesCheckbox');
                if (downloadSeriesImagesCheckbox) {
                    downloadSeriesImagesCheckbox.checked = !!settingsData.download_external_series_images;
                }

                // Load series image limits (convert bytes to MB for display)
                const SERIES_IMAGE_BYTES_PER_MB = 1048576;
                const seriesImageMaxMBInput = document.getElementById('seriesImageMaxMB');
                if (seriesImageMaxMBInput && typeof settingsData.series_image_max_bytes === 'number') {
                    seriesImageMaxMBInput.value = Math.round(settingsData.series_image_max_bytes / SERIES_IMAGE_BYTES_PER_MB);
                }
                const seriesImageMaxDownloadMBInput = document.getElementById('seriesImageMaxDownloadMB');
                if (seriesImageMaxDownloadMBInput && typeof settingsData.series_image_max_download_bytes === 'number') {
                    seriesImageMaxDownloadMBInput.value = Math.round(settingsData.series_image_max_download_bytes / SERIES_IMAGE_BYTES_PER_MB);
                }
                const seriesImageMaxDimensionInput = document.getElementById('seriesImageMaxDimension');
                if (seriesImageMaxDimensionInput && typeof settingsData.series_image_max_dimension === 'number') {
                    seriesImageMaxDimensionInput.value = settingsData.series_image_max_dimension;
                }
                
                // Load log max size (convert bytes to MB)
                const BYTES_PER_MB = 1048576;
                const logMaxMB = settingsData.log_max_bytes / BYTES_PER_MB;
                document.getElementById('logMaxSize').value = Math.round(logMaxMB);
                console.log('[SETTINGS] Log max bytes:', settingsData.log_max_bytes, 'converted to MB:', Math.round(logMaxMB));
                
                // Load issue number padding
                document.getElementById('issueNumberPadding').value = settingsData.issue_number_padding;
                
                // Load database cleanup interval
                document.getElementById('dbCleanupInterval').value = settingsData.database_cleanup_interval_hours;

                // Load external metadata settings
                document.getElementById('enableExternalSeriesMetadata').checked = !!settingsData.enable_external_series_metadata;
                // The key is never returned by the API; only whether one is stored.
                document.getElementById('comicVineApiKey').value = '';
                document.getElementById('comicVineApiKeyHint').textContent = settingsData.comicvine_api_key_set
                    ? 'Leave blank to keep the stored key.'
                    : 'Leave blank to disable ComicVine lookups. The key is stored in user settings on this instance.';
                document.getElementById('comicVineClearApiKeyCheckbox').checked = false;
                document.getElementById('comicVineBaseUrl').value = settingsData.comicvine_base_url || 'https://comicvine.gamespot.com/api';
                document.getElementById('enableMangaDexMetadata').checked = !!settingsData.enable_mangadex_metadata;
                document.getElementById('mangaDexBaseUrl').value = settingsData.mangadex_base_url || 'https://api.mangadex.org';
                document.getElementById('enableAniListMetadata').checked = !!settingsData.enable_anilist_metadata;
                document.getElementById('aniListBaseUrl').value = settingsData.anilist_base_url || 'https://graphql.anilist.co';

                // Load Suwayomi download-server settings
                const enableSuwayomi = document.getElementById('enableSuwayomiCheckbox');
                const suwayomiBaseUrl = document.getElementById('suwayomiBaseUrl');
                const suwayomiUsername = document.getElementById('suwayomiUsername');
                const suwayomiPassword = document.getElementById('suwayomiPassword');
                const suwayomiPasswordHint = document.getElementById('suwayomiPasswordHint');
                const suwayomiClearPassword = document.getElementById('suwayomiClearPasswordCheckbox');
                if (enableSuwayomi) enableSuwayomi.checked = !!settingsData.enable_suwayomi;
                if (suwayomiBaseUrl) suwayomiBaseUrl.value = settingsData.suwayomi_base_url || '';
                if (suwayomiUsername) suwayomiUsername.value = settingsData.suwayomi_username || '';
                if (suwayomiPassword) suwayomiPassword.value = '';
                if (suwayomiPasswordHint) {
                    suwayomiPasswordHint.textContent = settingsData.suwayomi_password_set
                        ? 'Leave blank to keep the stored password.'
                        : 'Enter a password only if your Suwayomi server requires one.';
                }
                if (suwayomiClearPassword) suwayomiClearPassword.checked = false;

                // Load default library view
                const defaultLibraryViewSelect = document.getElementById('defaultLibraryViewSelect');
                if (defaultLibraryViewSelect) {
                    const defaultView = settingsData.default_library_view === 'series' ? 'series' : 'files';
                    defaultLibraryViewSelect.value = defaultView;
                }

                // Load default preferred-language fallback. Server returns
                // null/empty when no global default is configured.
                const defaultPreferredLanguageSelect = document.getElementById('defaultPreferredLanguageSelect');
                if (defaultPreferredLanguageSelect) {
                    defaultPreferredLanguageSelect.value = settingsData.default_preferred_language || '';
                }

                const smtpHost = document.getElementById('smtpHost');
                const smtpPort = document.getElementById('smtpPort');
                const smtpUsername = document.getElementById('smtpUsername');
                const smtpPassword = document.getElementById('smtpPassword');
                const smtpPasswordHint = document.getElementById('smtpPasswordHint');
                const smtpClearPassword = document.getElementById('smtpClearPasswordCheckbox');
                const smtpUseSsl = document.getElementById('smtpUseSslCheckbox');
                const smtpAllowInsecure = document.getElementById('smtpAllowInsecureCheckbox');
                const emailFromAddress = document.getElementById('emailFromAddress');
                const emailFromName = document.getElementById('emailFromName');
                const emailMaxAttachmentMb = document.getElementById('emailMaxAttachmentMb');
                if (smtpHost) smtpHost.value = settingsData.smtp_host || '';
                if (smtpPort) smtpPort.value = settingsData.smtp_port || '';
                if (smtpUsername) smtpUsername.value = settingsData.smtp_username || '';
                if (smtpPassword) smtpPassword.value = '';
                if (smtpPasswordHint) {
                   smtpPasswordHint.textContent = settingsData.smtp_password_set
                       ? 'Leave blank to keep the stored password.'
                       : 'Enter a password if your SMTP server requires one.';
                }
                if (smtpClearPassword) smtpClearPassword.checked = false;
                if (smtpUseSsl) smtpUseSsl.checked = !!settingsData.smtp_use_ssl;
                if (smtpAllowInsecure) smtpAllowInsecure.checked = !!settingsData.smtp_allow_insecure;
                if (emailFromAddress) emailFromAddress.value = settingsData.email_from_address || '';
                if (emailFromName) emailFromName.value = settingsData.email_from_name || '';
                if (emailMaxAttachmentMb) emailMaxAttachmentMb.value = settingsData.email_max_attachment_mb || '';
                loadEmailStatus();

                const errorReportingEnabled = document.getElementById('errorReportingEnabledCheckbox');
                const errorReportingDestination = document.getElementById('errorReportingDestination');
                const errorReportingToken = document.getElementById('errorReportingToken');
                const errorReportingTokenHint = document.getElementById('errorReportingTokenHint');
                const errorReportingClearToken = document.getElementById('errorReportingClearTokenCheckbox');
                const errorReportingAssignee = document.getElementById('errorReportingAssignee');
                const errorReportingMaxPerDay = document.getElementById('errorReportingMaxPerDay');
                const errorReportingDedupeHours = document.getElementById('errorReportingDedupeHours');
                const errorReportingComment = document.getElementById('errorReportingCommentCheckbox');
                if (errorReportingEnabled) errorReportingEnabled.checked = !!settingsData.error_reporting_enabled;
                // The destination is fixed server-side; it is shown, never edited.
                if (errorReportingDestination) {
                    const destinationOwner = settingsData.error_reporting_github_owner || '';
                    const destinationRepo = settingsData.error_reporting_github_repo || '';
                    errorReportingDestination.textContent = destinationOwner && destinationRepo
                        ? `${destinationOwner}/${destinationRepo}`
                        : 'the ComicMaintainer project repository';
                }
                // The token is never returned by the API; only whether one is stored.
                if (errorReportingToken) errorReportingToken.value = '';
                if (errorReportingTokenHint) {
                    errorReportingTokenHint.textContent = settingsData.error_reporting_github_token_set
                        ? 'Leave blank to keep the stored token.'
                        : 'Use a fine-grained token with issues: write on that repository only.';
                }
                if (errorReportingClearToken) errorReportingClearToken.checked = false;
                if (errorReportingAssignee) errorReportingAssignee.value = settingsData.error_reporting_assignee || '';
                if (errorReportingMaxPerDay) errorReportingMaxPerDay.value = settingsData.error_reporting_max_issues_per_day ?? 10;
                if (errorReportingDedupeHours) errorReportingDedupeHours.value = settingsData.error_reporting_dedupe_window_hours ?? 24;
                if (errorReportingComment) errorReportingComment.checked = settingsData.error_reporting_comment_on_recurrence !== false;
                 
                console.log('[SETTINGS] All settings loaded successfully');
                updateSettingsNavActive();
                settingsLoaded = true;
                setSettingsLoadStatus('loaded');
                applyCapabilities();
            } catch (error) {
                if (loadRequest !== settingsLoadRequest) return;
                console.error('[SETTINGS] Error in loadSettings():', error);
                setSettingsLoadStatus('error', 'Settings could not be loaded. Retry before making changes.');
                showMessage('Failed to load settings: ' + error.message, 'error');
            }
        }
        
        function closeSettings() {
            const target = settingsReturnHash && settingsReturnHash !== '#/settings' ? settingsReturnHash : '#/home';
            navigate(target);
        }

        function scrollToSettingsSection(id) {
            const section = document.getElementById(id);
            if (!section) return;
            const reduceMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
            section.scrollIntoView({ behavior: reduceMotion ? 'auto' : 'smooth', block: 'start' });
            setSettingsNavActive(id);
            const heading = section.querySelector('.settings-card-title');
            if (heading) {
                heading.setAttribute('tabindex', '-1');
                heading.focus({ preventScroll: true });
            }
        }

        function setSettingsNavActive(id) {
            document.querySelectorAll('.settings-nav-link').forEach(link => {
                const active = link.dataset.settingsTarget === id;
                link.classList.toggle('active', active);
                if (active) {
                    link.setAttribute('aria-current', 'true');
                } else {
                    link.removeAttribute('aria-current');
                }
            });
        }

        // Highlight the section-nav entry for the card nearest the top of the
        // viewport while the settings page scrolls.
        let settingsSectionObserver = null;
        let settingsSectionObserverHeight = null;
        function updateSettingsNavActive() {
            if (typeof IntersectionObserver === 'undefined') return;
            const rootHeight = document.documentElement.clientHeight;
            if (settingsSectionObserver && settingsSectionObserverHeight === rootHeight) return;
            if (settingsSectionObserver) {
                settingsSectionObserver.disconnect();
                settingsSectionObserver = null;
            }
            const cards = Array.from(document.querySelectorAll('#settingsPageBody .settings-card'));
            if (!cards.length) return;
            const visible = new Map();
            settingsSectionObserverHeight = rootHeight;
            settingsSectionObserver = new IntersectionObserver(entries => {
                entries.forEach(entry => {
                    if (entry.isIntersecting) {
                        visible.set(entry.target.id, entry.boundingClientRect.top);
                    } else {
                        visible.delete(entry.target.id);
                    }
                });
                if (!visible.size) return;
                const topmost = [...visible.entries()].sort((a, b) => a[1] - b[1])[0][0];
                setSettingsNavActive(topmost);
            }, { rootMargin: `${-Math.round(rootHeight * 0.15)}px 0px ${-Math.round(rootHeight * 0.70)}px 0px` });
            cards.forEach(card => settingsSectionObserver.observe(card));
            if (!document.querySelector('.settings-nav-link[aria-current="true"]')) {
                setSettingsNavActive(cards[0].id);
            }
        }
        
        function showResetConfirmation() {
            document.getElementById('resetConfirmationModal').classList.add('active');
        }
        
        function closeResetConfirmation() {
            document.getElementById('resetConfirmationModal').classList.remove('active');
        }
        
        async function confirmReset() {
            try {
                closeResetConfirmation();
                
                // Show a loading message
                showMessage('Resetting database... This may take a moment.', 'info');
                
                const response = await fetch(apiUrl('/api/settings/reset'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    }
                });
                
                if (handleAuthError(response)) return;
                
                if (!response.ok) {
                    const errorData = await response.json();
                    throw new Error(errorData.error || `HTTP error! status: ${response.status}`);
                }
                
                const result = await response.json();
                
                if (result.success) {
                    showMessage('Database reset completed successfully', 'success');
                    // Clear in-memory selection state — the file list is now empty.
                    if (typeof selectedFiles !== 'undefined' && selectedFiles && typeof selectedFiles.clear === 'function') {
                        selectedFiles.clear();
                        resetSeriesSelectionState();
                    }
                    // The server emits file_list_updated on completion, which
                    // triggers handleFileListUpdatedEvent → in-place refresh.
                    // Refresh counts and active library view defensively in
                    // case the SSE connection is briefly disconnected.
                    if (typeof loadLibraryHealth === 'function') {
                        try { await loadLibraryHealth(); } catch (_e) { /* ignore */ }
                    }
                    if (typeof loadActiveLibraryView === 'function') {
                        try { await loadActiveLibraryView(1, true); } catch (_e) { /* ignore */ }
                    }
                } else {
                    showMessage(result.error || 'Failed to reset database', 'error');
                }
            } catch (error) {
                showMessage('Failed to reset database: ' + error.message, 'error');
            }
        }
        
        async function openLogsModal() {
            document.getElementById('logsModal').classList.add('active');
            await loadLogFiles();
        }
        
        function closeLogsModal() {
            document.getElementById('logsModal').classList.remove('active');
        }
        
        async function openProcessingHistoryModal() {
            document.getElementById('processingHistoryModal').classList.add('active');
            historyCurrentPage = 1;
            await loadProcessingHistory();
        }
        
        function closeProcessingHistoryModal() {
            document.getElementById('processingHistoryModal').classList.remove('active');
        }
        
        async function loadProcessingHistory() {
            const loadingIndicator = document.getElementById('historyLoadingIndicator');
            const contentDiv = document.getElementById('historyContent');
            const pageInfo = document.getElementById('historyPageInfo');
            const prevBtn = document.getElementById('historyPrevBtn');
            const nextBtn = document.getElementById('historyNextBtn');
            
            try {
                loadingIndicator.style.display = 'block';
                clearChildren(contentDiv);
                
                const offset = (historyCurrentPage - 1) * historyPerPage;
                const response = await fetch(apiUrl(`/api/processing-history?limit=${historyPerPage}&offset=${offset}`), {
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) return;
                
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                const data = await response.json();
                historyTotal = data.total;
                const totalPages = Math.ceil(historyTotal / historyPerPage);
                
                loadingIndicator.style.display = 'none';
                
                if (data.history.length === 0) {
                    renderHtml(contentDiv, html`<div style="padding: 40px; text-align: center; color: var(--text-muted);">No processing history found</div>`);
                    pageInfo.textContent = 'No results';
                    prevBtn.disabled = true;
                    nextBtn.disabled = true;
                    return;
                }
                
                // Render history items
                renderHtml(contentDiv, html`
                    <div style="display: flex; flex-direction: column; gap: 15px;">
                        ${data.history.map(item => rawHtml(renderHistoryItem(item)))}
                    </div>
                `);
                
                // Update pagination controls
                pageInfo.textContent = `Page ${historyCurrentPage} of ${totalPages} (${historyTotal} total)`;
                prevBtn.disabled = historyCurrentPage <= 1;
                nextBtn.disabled = historyCurrentPage >= totalPages;
                
            } catch (error) {
                loadingIndicator.style.display = 'none';
                renderHtml(contentDiv, html`<div style="padding: 20px; color: red;">Error loading history: ${error.message}</div>`);
            }
        }
        
        function renderHistoryItem(item) {
            const timestamp = new Date(item.timestamp * 1000).toLocaleString();
            const filepath = item.after_filename || item.before_filename || item.filepath;
            
            // Determine status styling
            const statusIcon = item.success ? '✅' : '❌';
            const statusText = item.success ? 'Success' : 'Failed';
            const statusColor = item.success ? '#10b981' : '#ef4444';
            const borderColor = item.success ? 'var(--border-secondary)' : '#ef4444';
            
            let html = `
                <div style="border: 1px solid ${borderColor}; border-radius: 8px; padding: 15px; background: var(--bg-secondary);">
                    <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 12px; border-bottom: 1px solid var(--border-primary); padding-bottom: 10px;">
                        <div style="flex: 1;">
                            <div style="display: flex; align-items: center; gap: 8px; margin-bottom: 4px;">
                                <span style="font-size: 18px;" title="${statusText}">${statusIcon}</span>
                                <strong style="font-size: 15px; color: var(--text-primary);">${escapeHtml(filepath)}</strong>
                            </div>
                            <div style="font-size: 12px; color: var(--text-muted);">${timestamp}</div>
                        </div>
                        <span style="background: var(--bg-hover); padding: 4px 12px; border-radius: 4px; font-size: 12px; color: var(--text-secondary);">${escapeHtml(item.operation_type)}</span>
                    </div>
            `;
            
            // Show error message if operation failed
            if (!item.success && item.error_message) {
                html += `
                    <div style="background: #fef2f2; border-left: 3px solid #ef4444; padding: 10px; margin-bottom: 12px; border-radius: 4px;">
                        <div style="font-weight: 500; color: #dc2626; margin-bottom: 4px;">Error</div>
                        <div style="color: #991b1b; font-size: 13px;">${escapeHtml(item.error_message)}</div>
                    </div>
                `;
            }
            
            html += `<div style="display: grid; grid-template-columns: 1fr 1fr; gap: 10px; font-size: 13px;">
            `;
            
            // Show changes
            const fields = [
                { label: 'Filename', before: item.before_filename, after: item.after_filename },
                { label: 'Title', before: item.before_title, after: item.after_title },
                { label: 'Series', before: item.before_series, after: item.after_series },
                { label: 'Issue', before: item.before_issue, after: item.after_issue },
                { label: 'Publisher', before: item.before_publisher, after: item.after_publisher },
                { label: 'Year', before: item.before_year, after: item.after_year },
                { label: 'Volume', before: item.before_volume, after: item.after_volume }
            ];
            
            // Check if there are any changes to display
            const hasChanges = fields.some(field => field.before !== field.after && (field.before || field.after));
            
            if (hasChanges) {
                fields.forEach(field => {
                    if (field.before !== field.after && (field.before || field.after)) {
                        html += `
                            <div style="grid-column: 1 / -1; border-left: 3px solid var(--border-secondary); padding-left: 10px; margin: 5px 0;">
                                <div style="font-weight: 500; color: var(--text-secondary); margin-bottom: 5px;">${field.label}</div>
                                <div style="display: flex; gap: 10px; align-items: center;">
                                    <div style="flex: 1; padding: 6px 10px; background: var(--bg-hover); border-radius: 4px; color: var(--text-muted);">
                                        <span style="font-size: 11px; text-transform: uppercase; opacity: 0.7;">Before:</span>
                                        <div style="margin-top: 3px; color: var(--text-primary);">${field.before ? escapeHtml(field.before) : '<em style="opacity: 0.5;">(empty)</em>'}</div>
                                    </div>
                                    <span style="color: var(--text-muted);">→</span>
                                    <div style="flex: 1; padding: 6px 10px; background: var(--bg-hover); border-radius: 4px; color: var(--text-muted);">
                                        <span style="font-size: 11px; text-transform: uppercase; opacity: 0.7;">After:</span>
                                        <div style="margin-top: 3px; color: var(--text-primary);">${field.after ? escapeHtml(field.after) : '<em style="opacity: 0.5;">(empty)</em>'}</div>
                                    </div>
                                </div>
                            </div>
                        `;
                    }
                });
            } else if (item.success) {
                // No changes but operation was successful (e.g., "already normalized", "rename disabled")
                html += `
                    <div style="grid-column: 1 / -1; padding: 10px; color: var(--text-muted); font-size: 13px; font-style: italic; text-align: center;">
                        No changes made
                    </div>
                `;
            }
            
            html += `
                    </div>
                </div>
            `;
            
            return html;
        }
        
        async function loadPreviousHistoryPage() {
            if (historyCurrentPage > 1) {
                historyCurrentPage--;
                await loadProcessingHistory();
            }
        }
        
        async function loadNextHistoryPage() {
            const totalPages = Math.ceil(historyTotal / historyPerPage);
            if (historyCurrentPage < totalPages) {
                historyCurrentPage++;
                await loadProcessingHistory();
            }
        }
        
        async function openAboutModal() {
            try {
                // Load version
                const response = await fetch(apiUrl('/api/version'), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                const data = await response.json();
                const aboutVersionElement = document.getElementById('aboutVersion');
                if (aboutVersionElement && data.version) {
                    aboutVersionElement.textContent = `v${data.version}`;
                }
                
                document.getElementById('aboutModal').classList.add('active');
            } catch (error) {
                showMessage('Failed to load version information: ' + error.message, 'error');
            }
        }
        
        function closeAboutModal() {
            document.getElementById('aboutModal').classList.remove('active');
        }

        // =================================================================
        // System diagnostics
        // =================================================================
        //
        // Triaging "it stopped working" used to mean opening four separate
        // panels (watcher pill, providers, logs, about) and still not being
        // able to see uptime, free disk space, health-check results or whether
        // a mount had gone read-only. /api/diagnostics returns all of it at
        // once; this renders it and can copy the whole snapshot as text for a
        // bug report.

        let lastDiagnostics = null;

        async function openDiagnosticsModal() {
            const modal = document.getElementById('diagnosticsModal');
            if (!modal) return;
            modal.classList.add('active');
            await loadDiagnostics();
        }

        function closeDiagnosticsModal() {
            const modal = document.getElementById('diagnosticsModal');
            if (modal) modal.classList.remove('active');
        }

        async function loadDiagnostics() {
            const container = document.getElementById('diagnosticsContent');
            if (!container) return;
            renderHtml(container, html`<div class="loading"><div class="spinner"></div><p>Loading diagnostics...</p></div>`);

            try {
                const response = await fetch(apiUrl('/api/diagnostics'), { headers: getAuthHeaders() });
                if (handleAuthError(response)) return;
                if (response.status === 403) {
                    lastDiagnostics = null;
                    renderHtml(container, html`<p class="empty-state">System diagnostics are available to administrators only.</p>`);
                    return;
                }
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                lastDiagnostics = await response.json();
                renderDiagnostics(lastDiagnostics);
            } catch (error) {
                lastDiagnostics = null;
                // Rendered inline rather than as a toast: the panel is the only
                // thing on screen the user is looking at. It still carries the
                // correlation reference so a failed snapshot is itself traceable.
                renderHtml(container, html`<p class="empty-state">${withApiFailureReference('Could not load diagnostics: ' + error.message, true)}</p>`);
            }
        }

        function formatUptime(totalSeconds) {
            if (typeof totalSeconds !== 'number' || !isFinite(totalSeconds) || totalSeconds < 0) return 'Unknown';
            const days = Math.floor(totalSeconds / 86400);
            const hours = Math.floor((totalSeconds % 86400) / 3600);
            const minutes = Math.floor((totalSeconds % 3600) / 60);
            if (days > 0) return `${days}d ${hours}h ${minutes}m`;
            if (hours > 0) return `${hours}h ${minutes}m`;
            return `${minutes}m`;
        }

        function diagnosticsStatusClass(status) {
            if (status === 'Healthy') return 'ok';
            if (status === 'Degraded') return 'warn';
            return 'bad';
        }

        function diagnosticsSection(title, rows) {
            return html`
                <div class="diagnostics-section">
                    <h3>${title}</h3>
                    <dl class="diagnostics-grid">
                        ${rows.map(([label, value]) => html`<dt>${label}</dt><dd>${value}</dd>`)}
                    </dl>
                </div>`;
        }

        function renderDiagnostics(data) {
            const container = document.getElementById('diagnosticsContent');
            if (!container) return;

            const app = data.application || {};
            const runtime = data.runtime || {};
            const health = data.health || {};
            const watcher = data.watcher || {};
            const library = data.library || {};
            const jobs = data.jobs || {};
            const storage = Array.isArray(data.storage) ? data.storage : [];
            const logs = data.logs || {};
            const logFiles = Array.isArray(logs.files) ? logs.files : [];

            const healthPill = html`<span class="diagnostics-status ${diagnosticsStatusClass(health.status)}">${health.status || 'Unknown'}</span>`;

            renderHtml(container, html`
                ${diagnosticsSection('Application', [
                    ['Version', app.version || 'Unknown'],
                    ['Environment', app.environment || 'Unknown'],
                    ['Uptime', formatUptime(app.uptimeSeconds)],
                    ['Health', healthPill]
                ])}
                ${health.entries && health.entries.length
                    ? diagnosticsSection('Health checks', health.entries.map(e => [e.name, `${e.status}${e.description ? ' — ' + e.description : ''}`]))
                    : ''}
                ${diagnosticsSection('Runtime', [
                    ['Framework', runtime.framework || 'Unknown'],
                    ['Operating system', runtime.operatingSystem || 'Unknown'],
                    ['Architecture', runtime.architecture || 'Unknown'],
                    ['Processors', runtime.processorCount ?? 'Unknown'],
                    ['Memory in use', typeof runtime.workingSetBytes === 'number' ? formatFileSize(runtime.workingSetBytes) : 'Unknown']
                ])}
                ${diagnosticsSection('Watcher', watcher.error
                    ? [['Status', watcher.error]]
                    : [
                        ['Running', watcher.running ? 'Yes' : 'No'],
                        ['Rename on arrival', watcher.renameEnabled ? 'Enabled' : 'Disabled'],
                        ['Normalize on arrival', watcher.normalizeEnabled ? 'Enabled' : 'Disabled']
                    ])}
                ${diagnosticsSection('Library', library.error
                    ? [['Status', library.error]]
                    : [
                        ['Files tracked', library.total ?? 0],
                        ['Processed', library.processed ?? 0],
                        ['Unprocessed', library.unprocessed ?? 0],
                        ['Duplicates', library.duplicates ?? 0]
                    ])}
                ${diagnosticsSection('Jobs', jobs.error
                    ? [['Status', jobs.error]]
                    : [
                        ['Running or queued', jobs.running ?? 0],
                        ['Interrupted', jobs.interrupted ?? 0],
                        ['Failed', jobs.failed ?? 0]
                    ])}
                ${diagnosticsSection('Storage', storage.map(entry => [
                    entry.name,
                    `${entry.path || 'not configured'} — ${entry.exists ? 'present' : 'missing'}, ${entry.writable ? 'writable' : 'not writable'}${typeof entry.freeBytes === 'number' ? ', ' + formatFileSize(entry.freeBytes) + ' free' : ''}`
                ]))}
                ${diagnosticsSection('Logs', logFiles.length
                    ? logFiles.map(f => [f.name, formatFileSize(f.sizeBytes)])
                    : [['Directory', logs.directory || 'Unknown']])}
            `);
        }

        /**
         * Flatten the snapshot into plain text. Copying a rendered table out of
         * the browser loses the labels, so the report is rebuilt from the data
         * rather than scraped from the DOM.
         */
        function buildDiagnosticsReport(data) {
            if (!data) return '';
            const app = data.application || {};
            const runtime = data.runtime || {};
            const health = data.health || {};
            const watcher = data.watcher || {};
            const library = data.library || {};
            const jobs = data.jobs || {};
            const storage = Array.isArray(data.storage) ? data.storage : [];
            const logs = data.logs || {};
            const logFiles = Array.isArray(logs.files) ? logs.files : [];

            const lines = [
                'ComicMaintainer diagnostics',
                `Generated: ${data.generatedAtUtc || 'unknown'}`,
                `Reference: ${data.correlationId || 'unknown'}`,
                '',
                `Version: ${app.version || 'unknown'} (${app.environment || 'unknown'})`,
                `Uptime: ${formatUptime(app.uptimeSeconds)}`,
                `Health: ${health.status || 'unknown'}`
            ];

            // The failing health check is usually the answer to "why is it
            // broken", so it has to survive the copy.
            (Array.isArray(health.entries) ? health.entries : []).forEach(entry => {
                lines.push(`  ${entry.name}: ${entry.status}${entry.description ? ' — ' + entry.description : ''}`);
            });

            lines.push(
                `Runtime: ${runtime.framework || 'unknown'} on ${runtime.operatingSystem || 'unknown'} (${runtime.architecture || 'unknown'}, ${runtime.processorCount ?? '?'} cpu)`,
                `Memory: ${typeof runtime.workingSetBytes === 'number' ? formatFileSize(runtime.workingSetBytes) : 'unknown'}`,
                watcher.error
                    ? `Watcher: ${watcher.error}`
                    : `Watcher: ${watcher.running ? 'running' : 'stopped'} (rename ${watcher.renameEnabled ? 'on' : 'off'}, normalize ${watcher.normalizeEnabled ? 'on' : 'off'})`,
                library.error
                    ? `Library: ${library.error}`
                    : `Library: ${library.total ?? '?'} files, ${library.processed ?? '?'} processed, ${library.unprocessed ?? '?'} unprocessed, ${library.duplicates ?? '?'} duplicates`,
                jobs.error
                    ? `Jobs: ${jobs.error}`
                    : `Jobs: ${jobs.total ?? '?'} total, ${jobs.running ?? '?'} active, ${jobs.interrupted ?? '?'} interrupted, ${jobs.failed ?? '?'} failed, last started ${jobs.lastStartedUtc || 'never'}`
            );

            storage.forEach(entry => {
                lines.push(`Storage/${entry.name}: ${entry.path || 'not configured'} — ${entry.exists ? 'present' : 'missing'}, ${entry.writable ? 'writable' : 'not writable'}${typeof entry.freeBytes === 'number' ? ', ' + formatFileSize(entry.freeBytes) + ' free' : ''}${entry.error ? ', ' + entry.error : ''}`);
            });

            // Log recency tells an operator whether logging stopped before or
            // after the failure being reported, so the listing is part of the
            // snapshot rather than something they have to go and read.
            lines.push(`Logs: ${logs.error || logs.directory || 'unknown'}`);
            logFiles.forEach(file => {
                lines.push(`  ${file.name}: ${typeof file.sizeBytes === 'number' ? formatFileSize(file.sizeBytes) : '?'}, last written ${file.lastWriteUtc || 'unknown'}`);
            });

            return lines.join('\n');
        }

        async function copyDiagnosticsReport() {
            if (!lastDiagnostics) {
                showMessage('Diagnostics have not loaded yet.', 'error');
                return;
            }

            const report = buildDiagnosticsReport(lastDiagnostics);
            try {
                // navigator.clipboard is unavailable on insecure origins, which
                // is a supported way to run this app on a LAN.
                if (!navigator.clipboard || !navigator.clipboard.writeText) {
                    throw new Error('Clipboard access is not available');
                }
                await navigator.clipboard.writeText(report);
                showMessage('Diagnostics report copied to the clipboard.', 'success');
            } catch {
                showMessage('Could not copy automatically — select the text in the panel and copy it manually.', 'error');
            }
        }
        
        function openChangePasswordModal() {
            const modal = document.getElementById('changePasswordModal');
            const form = document.getElementById('changePasswordForm');
            const errorEl = document.getElementById('changePasswordError');
            const successEl = document.getElementById('changePasswordSuccess');
            
            // Reset form and messages
            form.reset();
            errorEl.textContent = '';
            errorEl.classList.remove('show');
            successEl.textContent = '';
            successEl.classList.remove('show');
            
            modal.classList.add('active');
        }
        
        function closeChangePasswordModal() {
            document.getElementById('changePasswordModal').classList.remove('active');
        }
        
        async function changePassword() {
            const currentPassword = document.getElementById('currentPassword').value;
            const newPassword = document.getElementById('newPassword').value;
            const confirmNewPassword = document.getElementById('confirmNewPassword').value;
            const errorEl = document.getElementById('changePasswordError');
            const successEl = document.getElementById('changePasswordSuccess');
            const btn = document.getElementById('changePasswordBtn');
            
            // Helper to reset button state
            const resetButtonState = () => {
                btn.disabled = false;
                btn.textContent = 'Change Password';
            };
            
            // Clear previous messages
            errorEl.textContent = '';
            errorEl.classList.remove('show');
            successEl.textContent = '';
            successEl.classList.remove('show');
            
            // Validation
            if (!currentPassword || !newPassword || !confirmNewPassword) {
                errorEl.textContent = 'All fields are required';
                errorEl.classList.add('show');
                return;
            }
            
            if (newPassword.length < 8) {
                errorEl.textContent = 'New password must be at least 8 characters long';
                errorEl.classList.add('show');
                return;
            }
            
            if (newPassword !== confirmNewPassword) {
                errorEl.textContent = 'New passwords do not match';
                errorEl.classList.add('show');
                return;
            }
            
            // Disable button during request
            btn.disabled = true;
            btn.textContent = 'Changing...';
            
            try {
                const response = await fetch(apiUrl('/api/auth/change-password'), {
                    method: 'POST',
                    headers: getAuthHeaders(),
                    body: JSON.stringify({
                        currentPassword: currentPassword,
                        newPassword: newPassword
                    })
                });
                
                if (handleAuthError(response)) {
                    resetButtonState();
                    return;
                }
                
                const data = await response.json();
                
                if (response.ok) {
                    successEl.textContent = 'Password changed successfully!';
                    successEl.classList.add('show');
                    
                    // Clear form
                    document.getElementById('changePasswordForm').reset();
                    
                    // Close modal after a short delay
                    setTimeout(() => {
                        closeChangePasswordModal();
                    }, 2000);
                } else {
                    errorEl.textContent = data.error || 'Failed to change password';
                    errorEl.classList.add('show');
                }
            } catch (error) {
                console.error('Error changing password:', error);
                errorEl.textContent = 'An error occurred. Please try again.';
                errorEl.classList.add('show');
            } finally {
                // Re-enable button
                resetButtonState();
            }
        }
        
        async function loadLogFiles() {
            const logFileSelect = document.getElementById('logFile');
            const logType = document.getElementById('logType').value;
            
            try {
                renderHtml(logFileSelect, html`<option value="">Loading...</option>`);
                
                const response = await fetch(apiUrl(`/api/logs/files?type=${logType}`), {
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) return;
                
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                const data = await response.json();
                
                // Clear and populate the dropdown
                clearChildren(logFileSelect);
                
                if (data.files && data.files.length > 0) {
                    // Add "Most Recent" option
                    const mostRecentOption = document.createElement('option');
                    mostRecentOption.value = '';
                    mostRecentOption.textContent = 'Most Recent';
                    logFileSelect.appendChild(mostRecentOption);
                    
                    // Add each log file as an option
                    data.files.forEach(file => {
                        const option = document.createElement('option');
                        option.value = file.filename;
                        option.textContent = `${file.filename} (${file.size_mb} MB, ${file.last_modified})`;
                        logFileSelect.appendChild(option);
                    });
                } else {
                    const noFilesOption = document.createElement('option');
                    noFilesOption.value = '';
                    noFilesOption.textContent = 'No log files available';
                    logFileSelect.appendChild(noFilesOption);
                }
                
                // Load logs for the selected file (most recent by default)
                await loadLogs();
            } catch (error) {
                renderHtml(logFileSelect, html`<option value="">Error loading files</option>`);
                console.error('Failed to load log files:', error);
            }
        }
        
        async function onLogTypeChange() {
            await loadLogFiles();
        }
        
        async function loadLogs() {
            const logsContent = document.getElementById('logsContent');
            const logsLoadingIndicator = document.getElementById('logsLoadingIndicator');
            const logStats = document.getElementById('logStats');
            const lines = document.getElementById('logLines').value;
            const logType = document.getElementById('logType').value;
            const logFile = document.getElementById('logFile').value;
            
            try {
                logsLoadingIndicator.style.display = 'block';
                logsContent.textContent = '';
                logStats.textContent = '';
                
                let url = `/api/logs?lines=${lines}&type=${logType}`;
                if (logFile) {
                    url += `&filename=${encodeURIComponent(logFile)}`;
                }
                
                const response = await fetch(apiUrl(url), {
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) return;
                
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                const data = await response.json();
                
                if (data.error) {
                    logsContent.textContent = 'Error: ' + data.error;
                } else {
                    logsContent.textContent = data.content || 'No logs available';
                    const fileInfo = data.filename ? ` from ${data.filename}` : '';
                    logStats.textContent = `Showing ${data.shown_lines} of ${data.total_lines} total lines${fileInfo}`;
                }
            } catch (error) {
                logsContent.textContent = 'Failed to load logs: ' + error.message;
            } finally {
                logsLoadingIndicator.style.display = 'none';
            }
        }

        async function saveFilenameFormat() {
            if (!settingsLoaded || !userCapabilities.canAdminister) return;
            const format = document.getElementById('filenameFormat').value.trim();
            const logMaxSize = parseFloat(document.getElementById('logMaxSize').value);
            const issueNumberPadding = parseInt(document.getElementById('issueNumberPadding').value);
            const dbCleanupInterval = parseInt(document.getElementById('dbCleanupInterval').value);
            const enableExternalSeriesMetadata = document.getElementById('enableExternalSeriesMetadata').checked;
            const comicVineApiKey = document.getElementById('comicVineApiKey').value.trim();
            const comicVineClearApiKey = document.getElementById('comicVineClearApiKeyCheckbox').checked;
            const comicVineBaseUrl = document.getElementById('comicVineBaseUrl').value.trim();
            const enableMangaDexMetadata = document.getElementById('enableMangaDexMetadata').checked;
            const mangaDexBaseUrl = document.getElementById('mangaDexBaseUrl').value.trim();
            const enableAniListMetadata = document.getElementById('enableAniListMetadata').checked;
            const aniListBaseUrl = document.getElementById('aniListBaseUrl').value.trim();
            const seriesImageMaxMB = parseFloat(document.getElementById('seriesImageMaxMB').value);
            const seriesImageMaxDownloadMB = parseFloat(document.getElementById('seriesImageMaxDownloadMB').value);
            const seriesImageMaxDimension = parseInt(document.getElementById('seriesImageMaxDimension').value);
            const smtpHost = document.getElementById('smtpHost').value.trim();
            const smtpPortValue = document.getElementById('smtpPort').value.trim();
            const smtpPort = smtpPortValue ? parseInt(smtpPortValue, 10) : null;
            const smtpUsername = document.getElementById('smtpUsername').value.trim();
            const smtpPasswordInput = document.getElementById('smtpPassword').value;
            const smtpClearPassword = document.getElementById('smtpClearPasswordCheckbox').checked;
            const smtpUseSsl = document.getElementById('smtpUseSslCheckbox').checked;
            const smtpAllowInsecure = document.getElementById('smtpAllowInsecureCheckbox').checked;
            const emailFromAddress = document.getElementById('emailFromAddress').value.trim();
            const emailFromName = document.getElementById('emailFromName').value.trim();
            const emailMaxAttachmentValue = document.getElementById('emailMaxAttachmentMb').value.trim();
            const emailMaxAttachmentMb = emailMaxAttachmentValue ? parseFloat(emailMaxAttachmentValue) : null;
            
            if (!format) {
                showMessage('Filename format cannot be empty', 'error');
                return;
            }
            
            if (isNaN(logMaxSize) || logMaxSize <= 0) {
                showMessage('Log max size must be a positive number', 'error');
                return;
            }
            
            if (isNaN(issueNumberPadding) || issueNumberPadding < 0) {
                showMessage('Issue number padding must be 0 or greater', 'error');
                return;
            }
            
            if (isNaN(dbCleanupInterval) || dbCleanupInterval < 0) {
                showMessage('Database cleanup interval must be 0 or greater', 'error');
                return;
            }
            
            if (isNaN(seriesImageMaxMB) || seriesImageMaxMB <= 0) {
                showMessage('Max stored cover size must be a positive number', 'error');
                return;
            }
            
            if (isNaN(seriesImageMaxDownloadMB) || seriesImageMaxDownloadMB <= 0) {
                showMessage('Max download size must be a positive number', 'error');
                return;
            }
            
            if (seriesImageMaxDownloadMB < seriesImageMaxMB) {
                showMessage('Max download size must be greater than or equal to max stored cover size', 'error');
                return;
            }
            
            if (isNaN(seriesImageMaxDimension) || seriesImageMaxDimension <= 0) {
                showMessage('Max cover dimension must be a positive number', 'error');
                return;
            }

            if (smtpPortValue && (isNaN(smtpPort) || smtpPort < 1 || smtpPort > 65535)) {
                showMessage('SMTP port must be between 1 and 65535', 'error');
                return;
            }

            if (emailMaxAttachmentValue && (isNaN(emailMaxAttachmentMb) || emailMaxAttachmentMb <= 0)) {
                showMessage('Max email attachment size must be a positive number', 'error');
                return;
            }
            
            try {
                // Save filename format
                const formatResponse = await fetch(apiUrl('/api/settings/filename-format'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ format: format })
                });
                
                if (!formatResponse.ok) {
                    throw new Error(`HTTP error! status: ${formatResponse.status}`);
                }
                const formatResult = await formatResponse.json();
                
                if (formatResult.success === false) {
                    showMessage(formatResult.error || 'Failed to save filename format', 'error');
                    return;
                }
                
                // Save log max size
                const logResponse = await fetch(apiUrl('/api/settings/log-max-bytes'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ maxMB: logMaxSize })
                });
                
                if (!logResponse.ok) {
                    throw new Error(`HTTP error! status: ${logResponse.status}`);
                }
                const logResult = await logResponse.json();
                
                if (logResult.success === false) {
                    showMessage(logResult.error || 'Failed to save log max size', 'error');
                    return;
                }
                
                // Save issue number padding
                const paddingResponse = await fetch(apiUrl('/api/settings/issue-number-padding'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ padding: issueNumberPadding })
                });
                
                if (!paddingResponse.ok) {
                    throw new Error(`HTTP error! status: ${paddingResponse.status}`);
                }
                const paddingResult = await paddingResponse.json();
                
                if (paddingResult.success === false) {
                    showMessage(paddingResult.error || 'Failed to save issue number padding', 'error');
                    return;
                }
                
                // Save database cleanup interval
                const cleanupResponse = await fetch(apiUrl('/api/settings/database-cleanup-interval-hours'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ hours: dbCleanupInterval })
                });
                
                if (!cleanupResponse.ok) {
                    throw new Error(`HTTP error! status: ${cleanupResponse.status}`);
                }
                const cleanupResult = await cleanupResponse.json();
                
                if (cleanupResult.success === false) {
                    showMessage(cleanupResult.error || 'Failed to save database cleanup interval', 'error');
                    return;
                }

                // Save external series metadata settings
                const metadataResponse = await fetch(apiUrl('/api/settings/external-series-metadata'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({
                        enabled: enableExternalSeriesMetadata,
                        // null keeps the stored key; '' clears it.
                        comicVineApiKey: comicVineClearApiKey ? '' : (comicVineApiKey || null),
                        comicVineBaseUrl: comicVineBaseUrl || 'https://comicvine.gamespot.com/api',
                        enableMangaDex: enableMangaDexMetadata,
                        mangaDexBaseUrl: mangaDexBaseUrl || 'https://api.mangadex.org',
                        enableAniList: enableAniListMetadata,
                        aniListBaseUrl: aniListBaseUrl || 'https://graphql.anilist.co'
                    })
                });

                if (!metadataResponse.ok) {
                    throw new Error(`HTTP error! status: ${metadataResponse.status}`);
                }

                const metadataResult = await metadataResponse.json();
                if (metadataResult.success === false) {
                    showMessage(metadataResult.error || 'Failed to save external metadata settings', 'error');
                    return;
                }

                // Save series image limits
                const seriesImageResponse = await fetch(apiUrl('/api/settings/series-image-limits'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({
                        maxMB: seriesImageMaxMB,
                        maxDownloadMB: seriesImageMaxDownloadMB,
                        maxDimension: seriesImageMaxDimension
                    })
                });

                if (!seriesImageResponse.ok) {
                    let errorMsg = `HTTP error! status: ${seriesImageResponse.status}`;
                    try {
                        const errBody = await seriesImageResponse.json();
                        if (errBody && errBody.error) {
                            errorMsg = errBody.error;
                        }
                    } catch (e) { /* ignore parse errors */ }
                    showMessage('Failed to save series image limits: ' + errorMsg, 'error');
                    return;
                }

                const seriesImageResult = await seriesImageResponse.json();
                if (seriesImageResult.success === false) {
                    showMessage(seriesImageResult.error || 'Failed to save series image limits', 'error');
                    return;
                }

                const emailSettingsBody = {
                   smtpHost: smtpHost || null,
                   smtpPort: smtpPort,
                   smtpUsername: smtpUsername || null,
                   smtpPassword: smtpClearPassword ? '' : (smtpPasswordInput ? smtpPasswordInput : null),
                   smtpUseSsl,
                   smtpAllowInsecure,
                   emailFromAddress: emailFromAddress || null,
                   emailFromName: emailFromName || null,
                   emailMaxAttachmentMb: emailMaxAttachmentMb
                };
                const emailSettingsResponse = await fetch(apiUrl('/api/settings/email'), {
                   method: 'PUT',
                   headers: {
                       'Content-Type': 'application/json',
                       ...getAuthHeaders()
                   },
                   body: JSON.stringify(emailSettingsBody)
                });
                if (handleAuthError(emailSettingsResponse)) return;
                if (!emailSettingsResponse.ok) {
                   let errorMsg = `HTTP error! status: ${emailSettingsResponse.status}`;
                   try {
                       const errBody = await emailSettingsResponse.json();
                       if (errBody && (errBody.error || errBody.message)) {
                           errorMsg = errBody.error || errBody.message;
                       }
                   } catch (e) { /* ignore parse errors */ }
                   showMessage('Failed to save email settings: ' + errorMsg, 'error');
                   return;
                }
                emailStatusCache = null;

                const suwayomiEnabledEl = document.getElementById('enableSuwayomiCheckbox');
                if (suwayomiEnabledEl) {
                    const suwayomiPasswordInput = document.getElementById('suwayomiPassword').value;
                    const suwayomiClearPasswordChecked = document.getElementById('suwayomiClearPasswordCheckbox').checked;
                    const suwayomiBody = {
                        enabled: suwayomiEnabledEl.checked,
                        baseUrl: document.getElementById('suwayomiBaseUrl').value.trim() || null,
                        username: document.getElementById('suwayomiUsername').value.trim() || null,
                        // null keeps the stored password; '' clears it.
                        password: suwayomiClearPasswordChecked ? '' : (suwayomiPasswordInput || null)
                    };
                    const suwayomiResponse = await fetch(apiUrl('/api/settings/suwayomi'), {
                        method: 'PUT',
                        headers: {
                            'Content-Type': 'application/json',
                            ...getAuthHeaders()
                        },
                        body: JSON.stringify(suwayomiBody)
                    });
                    if (handleAuthError(suwayomiResponse)) return;
                    if (!suwayomiResponse.ok) {
                        let errorMsg = `HTTP error! status: ${suwayomiResponse.status}`;
                        try {
                            const errBody = await suwayomiResponse.json();
                            if (errBody && (errBody.error || errBody.message)) {
                                errorMsg = errBody.error || errBody.message;
                            }
                        } catch (e) { /* ignore parse errors */ }
                        showMessage('Failed to save Suwayomi settings: ' + errorMsg, 'error');
                        return;
                    }

                    suwayomiStatusPromise = null;
                    await refreshSuwayomiAvailability();
                }

                const errorReportingEnabledEl = document.getElementById('errorReportingEnabledCheckbox');
                if (errorReportingEnabledEl) {
                    const errorReportingTokenInput = document.getElementById('errorReportingToken').value;
                    const errorReportingClearTokenChecked = document.getElementById('errorReportingClearTokenCheckbox').checked;
                    const errorReportingBody = {
                        enabled: errorReportingEnabledEl.checked,
                        // null keeps the stored token; '' clears it.
                        gitHubToken: errorReportingClearTokenChecked ? '' : (errorReportingTokenInput || null),
                        assignee: document.getElementById('errorReportingAssignee').value.trim() || null,
                        maxIssuesPerDay: parseInt(document.getElementById('errorReportingMaxPerDay').value, 10) || 0,
                        dedupeWindowHours: parseInt(document.getElementById('errorReportingDedupeHours').value, 10) || 24,
                        commentOnRecurrence: document.getElementById('errorReportingCommentCheckbox').checked
                    };
                    const errorReportingResponse = await fetch(apiUrl('/api/settings/error-reporting'), {
                        method: 'PUT',
                        headers: {
                            'Content-Type': 'application/json',
                            ...getAuthHeaders()
                        },
                        body: JSON.stringify(errorReportingBody)
                    });
                    if (handleAuthError(errorReportingResponse)) return;
                    if (!errorReportingResponse.ok) {
                        let errorMsg = `HTTP error! status: ${errorReportingResponse.status}`;
                        try {
                            const errBody = await errorReportingResponse.json();
                            if (errBody && (errBody.error || errBody.message)) {
                                errorMsg = errBody.error || errBody.message;
                            }
                        } catch (e) { /* ignore parse errors */ }
                        showMessage('Failed to save error reporting settings: ' + errorMsg, 'error');
                        return;
                    }
                }

                showMessage('Settings saved successfully! Changes to log rotation, external metadata, and database cleanup will take effect on restart.', 'success');
                loadSettings();
            } catch (error) {
                showMessage('Failed to save settings: ' + error.message, 'error');
            }
        }
        
        async function sendErrorReportingTest() {
            try {
                const response = await fetch(apiUrl('/api/error-reports/test'), {
                    method: 'POST',
                    headers: getAuthHeaders()
                });
                if (handleAuthError(response)) return;
                const result = await response.json().catch(() => null);
                if (!response.ok) {
                    showMessage((result && result.error) || `Test report failed (HTTP ${response.status})`, 'error');
                    return;
                }
                showMessage((result && result.message) || 'Test report sent', 'success');
            } catch (error) {
                showMessage('Failed to send test report: ' + error.message, 'error');
            }
        }

        async function resetFilenameFormat() {
            if (!settingsLoaded || !userCapabilities.canAdminister) return;
            try {
                const response = await fetch(apiUrl('/api/settings/filename-format'), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                const data = await response.json();
                
                document.getElementById('filenameFormat').value = data.default;
                showMessage('Reset to default format', 'info');
            } catch (error) {
                showMessage('Failed to reset format: ' + error.message, 'error');
            }
        }
        
        async function cleanupDatabaseNow() {
            try {
                showMessage('Starting database cleanup...', 'info');
                
                const response = await fetch(apiUrl('/api/settings/cleanup-database'), {
                    method: 'POST',
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                const result = await response.json();
                
                if (result.success) {
                    showMessage(`Database cleanup completed. Removed ${result.removedCount} stale entries.`, 'success');
                } else {
                    showMessage(result.error || 'Failed to cleanup database', 'error');
                }
            } catch (error) {
                showMessage('Failed to cleanup database: ' + error.message, 'error');
            }
        }

        // =================================================================
        // Backups (Settings → Backups)
        // =================================================================
        //
        // The whole /api/backups API is administrator-only: a backup is a
        // complete copy of the database, password hashes and stored
        // credentials included. The section is hidden for other users, and
        // also if the server answers 403 (e.g. before /api/auth/user has
        // resolved the user's capabilities).

        let backupsForbidden = false;
        let backupsLoadRequest = 0;

        function updateBackupsSectionVisibility() {
            const hidden = !userCapabilities.canAdminister || backupsForbidden;
            ['settings-backups', 'settingsNavBackups'].forEach(id => {
                const el = document.getElementById(id);
                if (el) el.hidden = hidden;
            });
        }

        /** Fetch a /api/backups endpoint; throws an Error carrying `status` on failure. */
        async function fetchBackupsApi(path, options = {}) {
            const response = await fetch(apiUrl(path), {
                credentials: 'include',
                ...options,
                headers: options.headers || getAuthHeaders()
            });
            if (handleAuthError(response)) {
                const error = new Error('Authentication required');
                error.status = response.status;
                error.authHandled = true;
                throw error;
            }
            if (!response.ok) {
                let message = `HTTP error! status: ${response.status}`;
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

        /** True when the failure needs no toast: a 401 (already redirecting to login) or a 403 (section hidden). */
        function handleBackupsForbidden(error) {
            if (error && error.authHandled) return true;
            if (error && error.status === 403) {
                backupsForbidden = true;
                updateBackupsSectionVisibility();
                return true;
            }
            return false;
        }

        function formatBackupDate(value) {
            if (!value) return '—';
            const date = new Date(value);
            return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString();
        }

        async function loadBackups() {
            updateBackupsSectionVisibility();
            if (!userCapabilities.canAdminister || backupsForbidden) return;
            const list = document.getElementById('backupsList');
            if (!list) return;
            const loadRequest = ++backupsLoadRequest;
            renderHtml(list, html`<div class="loading"><div class="spinner"></div><p>Loading backups...</p></div>`);
            try {
                const response = await fetchBackupsApi('/api/backups');
                const data = await response.json();
                if (loadRequest !== backupsLoadRequest) return;
                renderRestorePending(!!data.restore_pending);
                renderBackupsList(Array.isArray(data.backups) ? data.backups : []);
            } catch (error) {
                if (loadRequest !== backupsLoadRequest) return;
                if (handleBackupsForbidden(error)) return;
                console.error('Failed to load backups:', error);
                renderHtml(list, html`<div class="empty-state"><p>Backups could not be loaded: ${error.message}</p></div>`);
            }
        }

        function renderRestorePending(pending) {
            const banner = document.getElementById('backupsRestorePending');
            if (banner) banner.hidden = !pending;
        }

        function renderBackupsList(backups) {
            const list = document.getElementById('backupsList');
            if (!list) return;
            if (!backups.length) {
                renderHtml(list, html`<div class="empty-state"><p>No backups yet.</p></div>`);
                return;
            }
            renderHtml(list, html`
                <ul class="backups-items">
                    ${backups.map(backup => html`
                        <li class="backups-item">
                            <div class="backups-item-info">
                                <strong class="backups-item-name">${backup.name}</strong>
                                <span class="backups-item-meta">${formatFileSize(Number(backup.size_bytes) || 0)} · ${formatBackupDate(backup.created_utc)}</span>
                            </div>
                            <div class="backups-item-actions">
                                <button type="button" class="btn btn-small" aria-label="Download ${backup.name}" onclick="downloadBackup('${jsArg(backup.name)}')">${uiIcon('download')} Download</button>
                                <button type="button" class="btn btn-small btn-warning" aria-label="Restore ${backup.name}" onclick="restoreBackup('${jsArg(backup.name)}')">${uiIcon('rotate-ccw')} Restore</button>
                                <button type="button" class="btn btn-small btn-danger" aria-label="Delete ${backup.name}" onclick="deleteBackup('${jsArg(backup.name)}')">${uiIcon('trash')} Delete</button>
                            </div>
                        </li>
                    `)}
                </ul>
            `);
        }

        async function createBackup() {
            const button = document.getElementById('createBackupBtn');
            if (button) button.disabled = true;
            try {
                showMessage('Creating backup...', 'info');
                const response = await fetchBackupsApi('/api/backups', { method: 'POST' });
                const backup = await response.json();
                showMessage(`Backup ${backup.name} created.`, 'success');
            } catch (error) {
                if (!handleBackupsForbidden(error)) {
                    showMessage('Failed to create backup: ' + error.message, 'error', { reference: true });
                }
            } finally {
                if (button) button.disabled = false;
                loadBackups();
            }
        }

        // Same authenticated blob download as the condensed-book download:
        // a plain link could not carry the Authorization header.
        async function downloadBackup(name) {
            try {
                const response = await fetchBackupsApi(`/api/backups/${encodeURIComponent(name)}/download`);
                const blob = await response.blob();
                const url = URL.createObjectURL(blob);
                const link = document.createElement('a');
                link.href = url;
                link.download = name;
                document.body.appendChild(link);
                link.click();
                link.remove();
                // Revoked later: revoking in the same tick as the click can
                // cancel the save in some browsers.
                setTimeout(() => URL.revokeObjectURL(url), 60000);
            } catch (error) {
                if (!handleBackupsForbidden(error)) {
                    showMessage('Failed to download backup: ' + error.message, 'error', { reference: true });
                }
            }
        }

        async function deleteBackup(name) {
            if (!confirm(`Delete backup ${name}?\n\nThis cannot be undone.`)) return;
            try {
                await fetchBackupsApi(`/api/backups/${encodeURIComponent(name)}`, { method: 'DELETE' });
                showMessage(`Backup ${name} deleted.`, 'success');
            } catch (error) {
                if (!handleBackupsForbidden(error)) {
                    showMessage('Failed to delete backup: ' + error.message, 'error', { reference: true });
                }
            } finally {
                loadBackups();
            }
        }

        function confirmRestore(source) {
            return confirm(
                `Restore the database from ${source}?\n\n` +
                'A safety backup of the current database is taken first. The restore is applied the next time ' +
                'ComicMaintainer restarts, replacing all current data (settings, users, read status and history).');
        }

        function reportRestoreStaged(result) {
            const safety = result && result.safety_backup && result.safety_backup.name;
            const message = (result && result.message) || 'Restore staged. Restart ComicMaintainer to apply it.';
            showMessage(safety ? `${message} Safety backup: ${safety}.` : message, 'warning');
        }

        async function restoreBackup(name) {
            if (!confirmRestore(`backup ${name}`)) return;
            try {
                showMessage('Validating backup...', 'info');
                const response = await fetchBackupsApi(`/api/backups/${encodeURIComponent(name)}/restore`, { method: 'POST' });
                reportRestoreStaged(await response.json());
            } catch (error) {
                if (!handleBackupsForbidden(error)) {
                    showMessage('Failed to restore backup: ' + error.message, 'error', { reference: true });
                }
            } finally {
                loadBackups();
            }
        }

        async function restoreBackupFromFile(input) {
            const file = input && input.files && input.files[0];
            if (!file) return;
            // Reset so choosing the same file again still fires `change`.
            input.value = '';
            if (!/\.db$/i.test(file.name)) {
                showMessage('Choose a ComicMaintainer database backup (.db file).', 'error');
                return;
            }
            if (!confirmRestore(`the uploaded file ${file.name}`)) return;

            const formData = new FormData();
            formData.append('file', file);
            // Let the browser set the multipart Content-Type (with boundary);
            // getAuthHeaders() defaults it to JSON.
            const headers = getAuthHeaders();
            delete headers['Content-Type'];
            try {
                showMessage('Uploading and validating backup...', 'info');
                const response = await fetchBackupsApi('/api/backups/upload-restore', {
                    method: 'POST',
                    body: formData,
                    headers
                });
                reportRestoreStaged(await response.json());
            } catch (error) {
                if (!handleBackupsForbidden(error)) {
                    showMessage('Failed to restore from file: ' + error.message, 'error', { reference: true });
                }
            } finally {
                loadBackups();
            }
        }

        async function cancelPendingRestore() {
            try {
                await fetchBackupsApi('/api/backups/restore-pending', { method: 'DELETE' });
                showMessage('Pending restore cancelled. The current database will be kept.', 'success');
            } catch (error) {
                if (!handleBackupsForbidden(error)) {
                    showMessage('Failed to cancel the pending restore: ' + error.message, 'error', { reference: true });
                }
            } finally {
                loadBackups();
            }
        }
