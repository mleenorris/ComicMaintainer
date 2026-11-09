        // ComicMaintainer SPA — settings page, logs, processing history, diagnostics, account and maintenance actions.
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

        // Shared state declared in state.js and reassigned here.
        /* global historyTotal: true */

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
                await apiFetch('/api/settings/default-library-view', {
                    method: 'PUT',
                    body: { view: selectedView }
                });
showMessage(`Default library view set to ${selectedView === 'series' ? 'Series' : 'Files (folder view)'}`, 'success');
            } catch (error) {
                console.error('Failed to update default library view:', error);
                showMessage('Failed to update default library view: ' + error.message, 'error', { reference: error });
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
                await apiFetch('/api/settings/default-preferred-language', {
                    method: 'PUT',
                    body: { language: value }
                });
showMessage('Default preferred language updated', 'success');
                if (typeof loadSeriesLibrary === 'function') {
                    loadSeriesLibrary(1, true);
                }
            } catch (error) {
                console.error('Failed to update default preferred language:', error);
                showMessage('Failed to update default preferred language: ' + error.message, 'error', { reference: error });
            }
        }
        
        // Note: Watcher is now automatically enabled/disabled based on rename and normalize settings
        // No need for explicit watcher toggle
        
        async function updateWatcherEnableRename() {
            const enabled = document.getElementById('watcherEnableRenameCheckbox').checked;
            
            try {
                await apiFetch('/api/settings/watcher-enable-rename', {
                    method: 'PUT',
                    body: { enabled: enabled }
                });
                
                const statusText = enabled ? 'enabled' : 'disabled';
                showMessage(`File rename ${statusText} successfully!`, 'success');
            } catch (error) {
                showMessage('Failed to update rename setting: ' + error.message, 'error', { reference: error });
                document.getElementById('watcherEnableRenameCheckbox').checked = !enabled;
            }
        }
        
        async function updateWatcherEnableNormalize() {
            const enabled = document.getElementById('watcherEnableNormalizeCheckbox').checked;
            
            try {
                await apiFetch('/api/settings/watcher-enable-normalize', {
                    method: 'PUT',
                    body: { enabled: enabled }
                });
                
                const statusText = enabled ? 'enabled' : 'disabled';
                showMessage(`Metadata normalize ${statusText} successfully!`, 'success');
            } catch (error) {
                showMessage('Failed to update normalize setting: ' + error.message, 'error', { reference: error });
                document.getElementById('watcherEnableNormalizeCheckbox').checked = !enabled;
            }
        }

        async function updateConvertRarToCbzOnWrite() {
            const enabled = document.getElementById('convertRarToCbzOnWriteCheckbox').checked;

            try {
                await apiFetch('/api/settings/convert-rar-to-cbz-on-write', {
                    method: 'PUT',
                    body: { enabled: enabled }
                });

                const statusText = enabled ? 'enabled' : 'disabled';
                showMessage(`Convert CBR/RAR to CBZ when writing tags ${statusText} successfully!`, 'success');
            } catch (error) {
                showMessage('Failed to update CBR/RAR conversion setting: ' + error.message, 'error', { reference: error });
                document.getElementById('convertRarToCbzOnWriteCheckbox').checked = !enabled;
            }
        }

        async function updateWriteCoverToFirstArchive() {
            const enabled = document.getElementById('writeCoverToFirstArchiveCheckbox').checked;
            
            try {
                await apiFetch('/api/settings/write-cover-to-first-archive', {
                    method: 'PUT',
                    body: { enabled: enabled }
                });
                
                const statusText = enabled ? 'enabled' : 'disabled';
                showMessage(`Embed cover into first issue archive ${statusText} successfully!`, 'success');
            } catch (error) {
                showMessage('Failed to update cover embed setting: ' + error.message, 'error', { reference: error });
                document.getElementById('writeCoverToFirstArchiveCheckbox').checked = !enabled;
            }
        }
        
        async function updateWriteCoverToSeriesFolder() {
            const enabled = document.getElementById('writeCoverToSeriesFolderCheckbox').checked;
            
            try {
                await apiFetch('/api/settings/write-cover-to-series-folder', {
                    method: 'PUT',
                    body: { enabled: enabled }
                });
                
                const statusText = enabled ? 'enabled' : 'disabled';
                showMessage(`Write cover into series folder ${statusText} successfully!`, 'success');
            } catch (error) {
                showMessage('Failed to update cover folder setting: ' + error.message, 'error', { reference: error });
                document.getElementById('writeCoverToSeriesFolderCheckbox').checked = !enabled;
            }
        }
        
        async function updateAllowRegistration() {
            const checkbox = document.getElementById('allowRegistrationCheckbox');
            const enabled = checkbox.checked;

            try {
                await apiFetch('/api/settings/allow-registration', {
                    method: 'PUT',
                    body: { enabled: enabled }
                });

                showMessage(`Self-service registration ${enabled ? 'enabled' : 'disabled'} successfully!`, 'success');
            } catch (error) {
                checkbox.checked = !enabled;
                showMessage('Failed to update registration setting: ' + error.message, 'error', { reference: error });
            }
        }

        async function updateDownloadExternalSeriesImages() {
            const enabled = document.getElementById('downloadExternalSeriesImagesCheckbox').checked;
            
            try {
                await apiFetch('/api/settings/download-external-series-images', {
                    method: 'PUT',
                    body: { enabled: enabled }
                });

                const statusText = enabled ? 'enabled' : 'disabled';
                showMessage(`Download external series images ${statusText} successfully!`, 'success');
            } catch (error) {
                showMessage('Failed to update series image download setting: ' + error.message, 'error', { reference: error });
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

        // The settings an environment variable pins, as last reported by
        // GET /api/settings: field name → variable name.
        let settingsEnvLocks = new Map();

        // The inputs of each GET /api/settings field the environment can pin.
        // The server's override table (AppSettingsEnvironmentOverrides) is the
        // other half of this list; a test keeps the two in step.
        const SETTINGS_ENV_LOCK_INPUTS = Object.freeze({
            stale_cleanup_max_removal_percent: ['staleCleanupMaxRemovalPercent'],
            stale_cleanup_missing_grace_hours: ['staleCleanupMissingGraceHours'],
            stale_cleanup_sentinel_file: ['staleCleanupSentinelFile'],
            processing_history_retention_days: ['processingHistoryRetentionDays'],
            processing_history_max_entries: ['processingHistoryMaxEntries'],
            enable_external_series_metadata: ['enableExternalSeriesMetadata'],
            comicvine_api_key_set: ['comicVineApiKey', 'comicVineClearApiKeyCheckbox'],
            comicvine_base_url: ['comicVineBaseUrl'],
            enable_mangadex_metadata: ['enableMangaDexMetadata'],
            mangadex_base_url: ['mangaDexBaseUrl'],
            enable_anilist_metadata: ['enableAniListMetadata'],
            anilist_base_url: ['aniListBaseUrl'],
            enable_suwayomi: ['enableSuwayomiCheckbox'],
            suwayomi_base_url: ['suwayomiBaseUrl'],
            suwayomi_username: ['suwayomiUsername'],
            suwayomi_password_set: ['suwayomiPassword', 'suwayomiClearPasswordCheckbox'],
            suwayomi_downloads_directory: ['suwayomiDownloadsDirectory'],
            suwayomi_import_directory: ['suwayomiImportDirectory'],
            download_external_series_images: ['downloadExternalSeriesImagesCheckbox'],
            series_image_max_bytes: ['seriesImageMaxMB'],
            series_image_max_download_bytes: ['seriesImageMaxDownloadMB'],
            series_image_max_dimension: ['seriesImageMaxDimension'],
            smtp_host: ['smtpHost'],
            smtp_port: ['smtpPort'],
            smtp_username: ['smtpUsername'],
            smtp_password_set: ['smtpPassword', 'smtpClearPasswordCheckbox'],
            smtp_use_ssl: ['smtpUseSslCheckbox'],
            smtp_allow_insecure: ['smtpAllowInsecureCheckbox'],
            email_from_address: ['emailFromAddress'],
            email_from_name: ['emailFromName'],
            email_max_attachment_mb: ['emailMaxAttachmentMb'],
            email_max_sends_per_hour: ['emailMaxSendsPerHour'],
            email_max_sends_per_day: ['emailMaxSendsPerDay'],
            email_min_seconds_between_sends: ['emailMinSecondsBetweenSends'],
            error_reporting_enabled: ['errorReportingEnabledCheckbox'],
            error_reporting_github_token_set: ['errorReportingToken', 'errorReportingClearTokenCheckbox'],
            error_reporting_assignee: ['errorReportingAssignee'],
            error_reporting_max_issues_per_day: ['errorReportingMaxPerDay'],
            error_reporting_dedupe_window_hours: ['errorReportingDedupeHours'],
            error_reporting_comment_on_recurrence: ['errorReportingCommentCheckbox']
        });

        /**
         * Marks the inputs of the settings the environment pins: applyCapabilities
         * keeps them disabled, and a hint under each says which variable pins it.
         */
        function applySettingsEnvLocks(locked) {
            settingsEnvLocks = new Map((Array.isArray(locked) ? locked : []).map(entry => [entry.field, entry.variable]));
            for (const [field, inputIds] of Object.entries(SETTINGS_ENV_LOCK_INPUTS)) {
                const variable = settingsEnvLocks.get(field);
                for (const inputId of inputIds) {
                    const input = document.getElementById(inputId);
                    if (!input) continue;
                    if (variable) {
                        input.setAttribute('data-env-locked', variable);
                    } else {
                        input.removeAttribute('data-env-locked');
                    }
                }
                setSettingsEnvLockHint(field, inputIds[0], variable);
            }
        }

        function setSettingsEnvLockHint(field, inputId, variable) {
            document.querySelector(`[data-env-lock-hint="${field}"]`)?.remove();
            const input = document.getElementById(inputId);
            if (!variable || !input) return;
            const hint = document.createElement('small');
            hint.className = 'form-hint';
            hint.dataset.envLockHint = field;
            hint.textContent = `Set by the ${variable} environment variable, so it cannot be changed here.`;
            (input.closest('label') || input).after(hint);
        }

/**
         * Fills the settings page from the server. Resolves to false when it told
         * the user the load failed (or the session expired); `afterSave` words that
         * failure for the save that came before it.
         */
        async function loadSettings(afterSave = false) {
            const loadRequest = ++settingsLoadRequest;
            settingsLoaded = false;
            setSettingsLoadStatus('loading', 'Loading settings…');
            applyCapabilities();
            loadBackups();
            try {
                // Load all settings
                const settingsData = await apiFetch('/api/settings');
                if (loadRequest !== settingsLoadRequest) return undefined;
                apiDebugLog('Settings loaded', settingsData);

                document.getElementById('filenameFormat').value = settingsData.filename_format || '';
                document.getElementById('currentFormat').textContent = settingsData.filename_format || '{series} - Chapter {issue}';
                
                // Load current theme
                const currentTheme = document.documentElement.getAttribute('data-theme') || 'light';
                document.getElementById('themeSelect').value = currentTheme;
                
                // Load watcher enable rename status
                document.getElementById('watcherEnableRenameCheckbox').checked = settingsData.watcher_enable_rename;
                
                // Load watcher enable normalize status
                document.getElementById('watcherEnableNormalizeCheckbox').checked = settingsData.watcher_enable_normalize;

                // Load CBR/RAR to CBZ conversion status (on unless the server says otherwise)
                const convertRarCheckbox = document.getElementById('convertRarToCbzOnWriteCheckbox');
                if (convertRarCheckbox) {
                    convertRarCheckbox.checked = settingsData.convert_rar_to_cbz_on_write !== false;
                }

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
                document.getElementById('staleCleanupMaxRemovalPercent').value = settingsData.stale_cleanup_max_removal_percent ?? 10;
                document.getElementById('staleCleanupMissingGraceHours').value = settingsData.stale_cleanup_missing_grace_hours ?? 6;
                document.getElementById('staleCleanupSentinelFile').value = settingsData.stale_cleanup_sentinel_file || '';
                document.getElementById('processingHistoryRetentionDays').value = settingsData.processing_history_retention_days ?? 90;
                document.getElementById('processingHistoryMaxEntries').value = settingsData.processing_history_max_entries ?? 50000;

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
                const suwayomiDownloadsDirectory = document.getElementById('suwayomiDownloadsDirectory');
                const suwayomiImportDirectory = document.getElementById('suwayomiImportDirectory');
                if (suwayomiDownloadsDirectory) suwayomiDownloadsDirectory.value = settingsData.suwayomi_downloads_directory || '';
                if (suwayomiImportDirectory) suwayomiImportDirectory.value = settingsData.suwayomi_import_directory || '';

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
                const emailMaxSendsPerHour = document.getElementById('emailMaxSendsPerHour');
                const emailMaxSendsPerDay = document.getElementById('emailMaxSendsPerDay');
                const emailMinSecondsBetweenSends = document.getElementById('emailMinSecondsBetweenSends');
                if (emailMaxSendsPerHour) emailMaxSendsPerHour.value = settingsData.email_max_sends_per_hour ?? 0;
                if (emailMaxSendsPerDay) emailMaxSendsPerDay.value = settingsData.email_max_sends_per_day ?? 0;
                if (emailMinSecondsBetweenSends) emailMinSecondsBetweenSends.value = settingsData.email_min_seconds_between_sends ?? 0;
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
                applySettingsEnvLocks(settingsData.env_locked);
                applyCapabilities();
                return true;
            } catch (error) {
                if (error.authHandled) return false;
                if (loadRequest !== settingsLoadRequest) return undefined;
                console.error('[SETTINGS] Error in loadSettings():', error);
                setSettingsLoadStatus('error', 'Settings could not be loaded. Retry before making changes.');
                showMessage(
                    (afterSave ? 'Settings were saved, but the page could not be reloaded: ' : 'Failed to load settings: ') + error.message,
                    afterSave ? 'warning' : 'error',
                    { reference: error });
                return false;
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
                
                const result = await apiFetch('/api/settings/reset', { method: 'POST' });

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
                        try { await loadActiveLibraryView({ refresh: true }); } catch (_e) { /* ignore */ }
                    }
                } else {
                    showMessage(result.error || 'Failed to reset database', 'error');
                }
            } catch (error) {
                showMessage('Failed to reset database: ' + error.message, 'error', { reference: error });
            }
        }
        
        async function openLogsModal() {
            // The logs API is administrator-only; its entry points are hidden
            // for everyone else (see applyCapabilities in shell.js).
            if (!userCapabilities.canAdminister) return;
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
                const data = await apiFetch(`/api/processing-history?limit=${historyPerPage}&offset=${offset}`);
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
                if (error.authHandled) return;
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
                const data = await apiFetch('/api/version');
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
                lastDiagnostics = await apiFetch('/api/diagnostics');
                renderDiagnostics(lastDiagnostics);
            } catch (error) {
                if (error.authHandled) return;
                lastDiagnostics = null;
                if (error.status === 403) {
                    renderHtml(container, html`<p class="empty-state">System diagnostics are available to administrators only.</p>`);
                    return;
                }
// Rendered inline rather than as a toast: the panel is the only
                // thing on screen the user is looking at. It still carries the
                // correlation reference so a failed snapshot is itself traceable.
                renderHtml(container, html`<p class="empty-state">${withApiFailureReference('Could not load diagnostics: ' + error.message, error)}</p>`);
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
                const response = await apiFetch('/api/auth/change-password', {
                    method: 'POST',
                    json: false,
                    body: {
                        currentPassword: currentPassword,
                        newPassword: newPassword
                    }
                });

                const data = await response.json();

                if (response.ok) {
                    // Changing the password signed every session out, this one included;
                    // carry on with the new session token the server returned.
                    if (data.token) {
                        localStorage.setItem('jwt_token', data.token);
                    }
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
                if (error.authHandled) return;
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
                
                const data = await apiFetch(`/api/logs/files?type=${logType}`);

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
                if (error.authHandled) return;
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
                
                const data = await apiFetch(url);

                if (data.error) {
                    logsContent.textContent = 'Error: ' + data.error;
                } else {
                    logsContent.textContent = data.content || 'No logs available';
                    const fileInfo = data.filename ? ` from ${data.filename}` : '';
                    logStats.textContent = `Showing ${data.shown_lines} of ${data.total_lines} total lines${fileInfo}`;
                }
            } catch (error) {
                if (error.authHandled) return;
                logsContent.textContent = 'Failed to load logs: ' + error.message;
            } finally {
                logsLoadingIndicator.style.display = 'none';
            }
        }

        /**
         * Sends one Save Settings request. Throws apiFetch's error on failure, with
         * `authHandled` after a 401 (already redirecting to login), and flagged
         * `nothingSaved` when the server refused the save (a 4xx). A 5xx says nothing
         * about that: the file may already be replaced when a later step fails, and a
         * proxy's 502/504 never saw the outcome. Nor does a request that got no answer.
         */
        async function saveAllSettings(body) {
            try {
                await apiFetch('/api/settings', { method: 'PUT', body });
            } catch (error) {
                error.nothingSaved = error.status < 500;
                throw error;
            }
        }

        // Server-side limits checked before Save Settings sends anything; the
        // matching inputs' max attributes in index.html must agree.
        const SETTINGS_SERVER_LIMITS = Object.freeze({
            logMaxSizeMB: 2047,
            emailMaxAttachmentMB: 200,
            errorReportingMaxPerDay: 100,
            errorReportingDedupeHours: 720,
            staleCleanupMaxRemovalPercent: 100,
            staleCleanupMissingGraceHours: 720,
            processingHistoryRetentionDays: 3650,
            processingHistoryMaxEntries: 10000000
        });

        /** A whole number from a settings input, or NaN when it is blank or not a whole number. */
        function parseWholeNumberInput(id) {
            const value = (document.getElementById(id)?.value ?? '').trim();
            if (!value) return NaN;
            const number = Number(value);
            return Number.isInteger(number) ? number : NaN;
        }

        /** Whether `value` is a whole number from 0 to `max`. */
        function isWholeNumberInRange(value, max) {
            return Number.isInteger(value) && value >= 0 && value <= max;
        }

        /**
         * A whole number from an email rate-limit input. Blank means 0 (no
         * limit); anything that is not a whole number comes back as NaN.
         */
        function parseEmailRateLimitInput(id) {
            const value = (document.getElementById(id)?.value ?? '').trim();
            if (!value) return 0;
            const number = Number(value);
            return Number.isInteger(number) ? number : NaN;
        }

        async function saveFilenameFormat() {
            if (!settingsLoaded || !userCapabilities.canAdminister) return;
            const format = document.getElementById('filenameFormat').value.trim();
            const logMaxSize = parseFloat(document.getElementById('logMaxSize').value);
            const issueNumberPadding = document.getElementById('issueNumberPadding').valueAsNumber;
            const dbCleanupInterval = parseInt(document.getElementById('dbCleanupInterval').value);
            const staleCleanupMaxRemovalPercent = parseWholeNumberInput('staleCleanupMaxRemovalPercent');
            const staleCleanupMissingGraceHours = parseWholeNumberInput('staleCleanupMissingGraceHours');
            const staleCleanupSentinelFile = document.getElementById('staleCleanupSentinelFile').value.trim();
            const processingHistoryRetentionDays = parseWholeNumberInput('processingHistoryRetentionDays');
            const processingHistoryMaxEntries = parseWholeNumberInput('processingHistoryMaxEntries');
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
            // The error reporting section may be missing (its group is then skipped below).
            const errorReportingMaxPerDay = parseInt(document.getElementById('errorReportingMaxPerDay')?.value, 10) || 0;
            const errorReportingDedupeHoursValue = parseInt(document.getElementById('errorReportingDedupeHours')?.value, 10);
            // Only an empty or missing field falls back to the default; 0 must reach validation.
            const errorReportingDedupeHours = isNaN(errorReportingDedupeHoursValue) ? 24 : errorReportingDedupeHoursValue;
            // The rate limit is only saved when its controls are on the page.
            const emailRateLimitShown = !!document.getElementById('emailMaxSendsPerHour');
            const emailRateLimit = emailRateLimitShown
                ? {
                    maxSendsPerHour: parseEmailRateLimitInput('emailMaxSendsPerHour'),
                    maxSendsPerDay: parseEmailRateLimitInput('emailMaxSendsPerDay'),
                    minSecondsBetweenSends: parseEmailRateLimitInput('emailMinSecondsBetweenSends')
                }
                : null;
            
            // A setting the environment pins is left out of the save, which keeps it,
            // and its value (which may be outside what this form accepts) is not checked.
            const pinned = field => settingsEnvLocks.has(field);
            const unlessPinned = (field, value) => (pinned(field) ? undefined : value);

            // Check what the server would reject before sending anything, so the
            // user is told which field to fix.
            const checks = [
                [!format, 'Filename format cannot be empty'],
                [isNaN(logMaxSize) || logMaxSize <= 0, 'Log max size must be a positive number'],
                // The server keeps the size in bytes as a 32-bit integer.
                [logMaxSize > SETTINGS_SERVER_LIMITS.logMaxSizeMB, `Log max size must be ${SETTINGS_SERVER_LIMITS.logMaxSizeMB} MB or less`],
                [!Number.isInteger(issueNumberPadding) || issueNumberPadding < 0 || issueNumberPadding > 10, 'Issue number padding must be between 0 and 10'],
                [isNaN(dbCleanupInterval) || dbCleanupInterval < 0, 'Database cleanup interval must be 0 or greater'],
                [!pinned('stale_cleanup_max_removal_percent') && !isWholeNumberInRange(staleCleanupMaxRemovalPercent, SETTINGS_SERVER_LIMITS.staleCleanupMaxRemovalPercent),
                    `Largest share removed without confirmation must be a whole number from 0 to ${SETTINGS_SERVER_LIMITS.staleCleanupMaxRemovalPercent}`],
                [!pinned('stale_cleanup_missing_grace_hours') && !isWholeNumberInRange(staleCleanupMissingGraceHours, SETTINGS_SERVER_LIMITS.staleCleanupMissingGraceHours),
                    `Remove missing files after must be a whole number of hours from 0 to ${SETTINGS_SERVER_LIMITS.staleCleanupMissingGraceHours}`],
                [!pinned('processing_history_retention_days') && !isWholeNumberInRange(processingHistoryRetentionDays, SETTINGS_SERVER_LIMITS.processingHistoryRetentionDays),
                    `Processing history retention must be a whole number of days from 0 to ${SETTINGS_SERVER_LIMITS.processingHistoryRetentionDays}`],
                [!pinned('processing_history_max_entries') && !isWholeNumberInRange(processingHistoryMaxEntries, SETTINGS_SERVER_LIMITS.processingHistoryMaxEntries),
                    `Maximum processing history entries must be a whole number from 0 to ${SETTINGS_SERVER_LIMITS.processingHistoryMaxEntries}`],
                [!pinned('series_image_max_bytes') && (isNaN(seriesImageMaxMB) || seriesImageMaxMB <= 0), 'Max stored cover size must be a positive number'],
                [!pinned('series_image_max_download_bytes') && (isNaN(seriesImageMaxDownloadMB) || seriesImageMaxDownloadMB <= 0), 'Max download size must be a positive number'],
                [!pinned('series_image_max_bytes') && !pinned('series_image_max_download_bytes') && seriesImageMaxDownloadMB < seriesImageMaxMB,
                    'Max download size must be greater than or equal to max stored cover size'],
                [!pinned('series_image_max_dimension') && (isNaN(seriesImageMaxDimension) || seriesImageMaxDimension <= 0), 'Max cover dimension must be a positive number'],
                [!pinned('smtp_port') && smtpPortValue && (isNaN(smtpPort) || smtpPort < 1 || smtpPort > 65535), 'SMTP port must be between 1 and 65535'],
                [!pinned('email_max_attachment_mb') && emailMaxAttachmentValue && (isNaN(emailMaxAttachmentMb) || emailMaxAttachmentMb <= 0),
                    'Max email attachment size must be a positive number'],
                ...(emailRateLimit
                    ? [
                        ['email_max_sends_per_hour', emailRateLimit.maxSendsPerHour, 'Max emails per hour', 10000],
                        ['email_max_sends_per_day', emailRateLimit.maxSendsPerDay, 'Max emails per day', 100000],
                        ['email_min_seconds_between_sends', emailRateLimit.minSecondsBetweenSends, 'Minimum seconds between emails', 86400]
                    ].map(([field, value, label, max]) => [
                        !pinned(field) && !(value >= 0 && value <= max),
                        `${label} must be a whole number from 0 to ${max}`
                    ])
                    : []),
                [!pinned('email_max_attachment_mb') && emailMaxAttachmentMb > SETTINGS_SERVER_LIMITS.emailMaxAttachmentMB,
                    `Max email attachment size must be ${SETTINGS_SERVER_LIMITS.emailMaxAttachmentMB} MB or less`],
                [!pinned('error_reporting_max_issues_per_day') && (errorReportingMaxPerDay < 0 || errorReportingMaxPerDay > SETTINGS_SERVER_LIMITS.errorReportingMaxPerDay),
                    `Maximum issues per day must be between 0 and ${SETTINGS_SERVER_LIMITS.errorReportingMaxPerDay}`],
                [!pinned('error_reporting_dedupe_window_hours') && (errorReportingDedupeHours < 1 || errorReportingDedupeHours > SETTINGS_SERVER_LIMITS.errorReportingDedupeHours),
                    `Recurrence comment interval must be between 1 and ${SETTINGS_SERVER_LIMITS.errorReportingDedupeHours} hours`]
            ];
            const failedCheck = checks.find(([invalid]) => invalid);
            if (failedCheck) {
                showMessage(failedCheck[1], 'error');
                return;
            }

            // One request for every group: the server applies all of it or none
            // of it. In each group a field left out or sent as null keeps its
            // value and a text field sent empty is cleared.
            const settingsUpdate = {
                filenameFormat: format,
                logMaxMB: logMaxSize,
                issueNumberPadding,
                databaseCleanup: {
                    intervalHours: dbCleanupInterval,
                    staleCleanupMaxRemovalPercent: unlessPinned('stale_cleanup_max_removal_percent', staleCleanupMaxRemovalPercent),
                    staleCleanupMissingGraceHours: unlessPinned('stale_cleanup_missing_grace_hours', staleCleanupMissingGraceHours),
                    staleCleanupSentinelFile: unlessPinned('stale_cleanup_sentinel_file', staleCleanupSentinelFile),
                    processingHistoryRetentionDays: unlessPinned('processing_history_retention_days', processingHistoryRetentionDays),
                    processingHistoryMaxEntries: unlessPinned('processing_history_max_entries', processingHistoryMaxEntries)
                },
                externalSeriesMetadata: {
                    enabled: unlessPinned('enable_external_series_metadata', enableExternalSeriesMetadata),
                    // null keeps the stored key; '' clears it.
                    comicVineApiKey: unlessPinned('comicvine_api_key_set', comicVineClearApiKey ? '' : (comicVineApiKey || null)),
                    comicVineBaseUrl: unlessPinned('comicvine_base_url', comicVineBaseUrl || 'https://comicvine.gamespot.com/api'),
                    enableMangaDex: unlessPinned('enable_mangadex_metadata', enableMangaDexMetadata),
                    mangaDexBaseUrl: unlessPinned('mangadex_base_url', mangaDexBaseUrl || 'https://api.mangadex.org'),
                    enableAniList: unlessPinned('enable_anilist_metadata', enableAniListMetadata),
                    aniListBaseUrl: unlessPinned('anilist_base_url', aniListBaseUrl || 'https://graphql.anilist.co')
                },
                seriesImageLimits: {
                    maxMB: unlessPinned('series_image_max_bytes', seriesImageMaxMB),
                    maxDownloadMB: unlessPinned('series_image_max_download_bytes', seriesImageMaxDownloadMB),
                    maxDimension: unlessPinned('series_image_max_dimension', seriesImageMaxDimension)
                },
                email: {
                    smtpHost: unlessPinned('smtp_host', smtpHost),
                    smtpPort: unlessPinned('smtp_port', smtpPort),
                    smtpUsername: unlessPinned('smtp_username', smtpUsername),
                    smtpPassword: unlessPinned('smtp_password_set', smtpClearPassword ? '' : (smtpPasswordInput || null)),
                    smtpUseSsl: unlessPinned('smtp_use_ssl', smtpUseSsl),
                    smtpAllowInsecure: unlessPinned('smtp_allow_insecure', smtpAllowInsecure),
                    emailFromAddress: unlessPinned('email_from_address', emailFromAddress),
                    emailFromName: unlessPinned('email_from_name', emailFromName),
                    emailMaxAttachmentMb: unlessPinned('email_max_attachment_mb', emailMaxAttachmentMb)
                }
            };

            if (emailRateLimit) {
                settingsUpdate.emailRateLimit = {
                    maxSendsPerHour: unlessPinned('email_max_sends_per_hour', emailRateLimit.maxSendsPerHour),
                    maxSendsPerDay: unlessPinned('email_max_sends_per_day', emailRateLimit.maxSendsPerDay),
                    minSecondsBetweenSends: unlessPinned('email_min_seconds_between_sends', emailRateLimit.minSecondsBetweenSends)
                };
            }

            const suwayomiEnabledEl = document.getElementById('enableSuwayomiCheckbox');
            if (suwayomiEnabledEl) {
                const suwayomiPasswordInput = document.getElementById('suwayomiPassword').value;
                const suwayomiClearPasswordChecked = document.getElementById('suwayomiClearPasswordCheckbox').checked;
                const suwayomiDownloadsDirectoryEl = document.getElementById('suwayomiDownloadsDirectory');
                const suwayomiImportDirectoryEl = document.getElementById('suwayomiImportDirectory');
                settingsUpdate.suwayomi = {
                    enabled: unlessPinned('enable_suwayomi', suwayomiEnabledEl.checked),
                    baseUrl: unlessPinned('suwayomi_base_url', document.getElementById('suwayomiBaseUrl').value.trim()),
                    username: unlessPinned('suwayomi_username', document.getElementById('suwayomiUsername').value.trim()),
                    // null keeps the stored password; '' clears it.
                    password: unlessPinned('suwayomi_password_set', suwayomiClearPasswordChecked ? '' : (suwayomiPasswordInput || null)),
                    // Import folders: '' clears (the downloads folder turns importing off).
                    downloadsDirectory: unlessPinned('suwayomi_downloads_directory', suwayomiDownloadsDirectoryEl ? suwayomiDownloadsDirectoryEl.value.trim() : null),
                    importDirectory: unlessPinned('suwayomi_import_directory', suwayomiImportDirectoryEl ? suwayomiImportDirectoryEl.value.trim() : null)
                };
            }

            const errorReportingEnabledEl = document.getElementById('errorReportingEnabledCheckbox');
            if (errorReportingEnabledEl) {
                const errorReportingTokenInput = document.getElementById('errorReportingToken').value;
                const errorReportingClearTokenChecked = document.getElementById('errorReportingClearTokenCheckbox').checked;
                settingsUpdate.errorReporting = {
                    enabled: unlessPinned('error_reporting_enabled', errorReportingEnabledEl.checked),
                    // null keeps the stored token; '' clears it.
                    gitHubToken: unlessPinned('error_reporting_github_token_set', errorReportingClearTokenChecked ? '' : (errorReportingTokenInput || null)),
                    assignee: unlessPinned('error_reporting_assignee', document.getElementById('errorReportingAssignee').value.trim()),
                    maxIssuesPerDay: unlessPinned('error_reporting_max_issues_per_day', errorReportingMaxPerDay),
                    dedupeWindowHours: unlessPinned('error_reporting_dedupe_window_hours', errorReportingDedupeHours),
                    commentOnRecurrence: unlessPinned('error_reporting_comment_on_recurrence', document.getElementById('errorReportingCommentCheckbox').checked)
                };
            }

            try {
                await saveAllSettings(settingsUpdate);
            } catch (error) {
                if (error.authHandled) return;
                let message = `Failed to save settings: ${error.message}`;
                if (!/[.!?]$/.test(message)) message += '.';
                // A refused save is all or nothing, and the form keeps the edits for a retry.
                if (error.nothingSaved) message += ' No settings were saved.';
                showMessage(message, 'error', { reference: error });
                return;
            }

            // The new values are in effect once the request returns, so a status
            // read from here on sees them.
            emailStatusCache = null;
            if (settingsUpdate.suwayomi) {
                suwayomiStatusPromise = null;
                await refreshSuwayomiAvailability();
            }

            if (await loadSettings(true) !== false) {
                showMessage('Settings saved. Changes to log rotation take effect after a restart.', 'success');
            }
        }

        async function sendErrorReportingTest() {
            try {
                const response = await apiFetch('/api/error-reports/test', { method: 'POST', json: false });
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
                const data = await apiFetch('/api/settings/filename-format');
                if (!data.default) {
                    throw new Error('No default format was returned');
                }
                
                document.getElementById('filenameFormat').value = data.default;
                showMessage('Reset to default format', 'info');
            } catch (error) {
                showMessage('Failed to reset format: ' + error.message, 'error', { reference: error });
            }
        }
        
        /**
         * "Clean Database Now". The server refuses (409) while the library looks
         * unmounted, and asks for confirmation (409 with requiresConfirmation)
         * before removing more than the configured share of the library, files
         * that have not stayed missing for the grace period, or when the watched
         * folder holds no comics; the request is then repeated with ?confirm=true
         * only if the administrator agrees.
         */
        async function cleanupDatabaseNow() {
            try {
                showMessage('Starting database cleanup...', 'info');

                const post = confirmed => apiFetch(
                    '/api/settings/cleanup-database' + (confirmed ? '?confirm=true' : ''),
                    { method: 'POST', json: false });

                let response = await post(false);
                let result = await response.json().catch(() => ({}));

                if (response.status === 409 && result.requiresConfirmation) {
                    if (!confirm(`${result.message}\n\nRemove these entries anyway?`)) {
                        showMessage('Database cleanup cancelled. No entries were removed.', 'info');
                        return;
                    }
                    response = await post(true);
                    result = await response.json().catch(() => ({}));
                }

                if (response.ok) {
                    if (result.success) {
                        showMessage(result.message || `Database cleanup completed. Removed ${result.removedCount} stale entries.`, 'success');
                    } else {
                        showMessage(result.error || 'Failed to cleanup database', 'error');
                    }
                } else if (response.status === 409) {
                    // Refused (the library looks unmounted); nothing was removed.
                    showMessage(result.message || 'Database cleanup was skipped. No entries were removed.', 'warning');
                } else {
                    throw apiResponseError(response, result.error || undefined);
                }
            } catch (error) {
                showMessage('Failed to cleanup database: ' + error.message, 'error', { reference: error });
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

        /** Fetch a /api/backups endpoint; throws an Error carrying `status` (and the response's `correlationId`) on failure. */
        async function fetchBackupsApi(path, options = {}) {
            // The caller reads the body: a download is a blob, not JSON.
            const response = await apiFetch(path, { ...options, json: false });
            if (!response.ok) {
                throw apiResponseError(response, await apiErrorMessage(response));
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
                    showMessage('Failed to create backup: ' + error.message, 'error', { reference: error });
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
                    showMessage('Failed to download backup: ' + error.message, 'error', { reference: error });
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
                    showMessage('Failed to delete backup: ' + error.message, 'error', { reference: error });
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
                    showMessage('Failed to restore backup: ' + error.message, 'error', { reference: error });
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
            try {
                showMessage('Uploading and validating backup...', 'info');
                const response = await fetchBackupsApi('/api/backups/upload-restore', {
                    method: 'POST',
                    body: formData
                });
                reportRestoreStaged(await response.json());
            } catch (error) {
                if (!handleBackupsForbidden(error)) {
                    showMessage('Failed to restore from file: ' + error.message, 'error', { reference: error });
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
                    showMessage('Failed to cancel the pending restore: ' + error.message, 'error', { reference: error });
                }
            } finally {
                loadBackups();
            }
        }
