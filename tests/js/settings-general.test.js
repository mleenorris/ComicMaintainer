'use strict';

// Settings page (js/app/settings.js): loading every settings group into the
// form, load failures and retries, the read-only (non-admin) variant, the
// controls that save immediately on change, and the page's section navigation.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond, plain } = require('./support/dom');
const { bootApp, deferred } = require('./support/app');
const {
    settingsResponse, settingsRoutes, bootSettings, messages, lastMessage, clearMessages, callsTo
} = require('./support/settings-helpers');

const networkError = () => {
    throw new TypeError('Failed to fetch');
};

function formValues(page) {
    const value = id => page.$(`#${id}`).value;
    const checked = id => page.$(`#${id}`).checked;
    const text = id => page.$(`#${id}`).textContent;
    return {
        filenameFormat: value('filenameFormat'),
        currentFormat: text('currentFormat'),
        theme: value('themeSelect'),
        rename: checked('watcherEnableRenameCheckbox'),
        normalize: checked('watcherEnableNormalizeCheckbox'),
        coverToArchive: checked('writeCoverToFirstArchiveCheckbox'),
        coverToFolder: checked('writeCoverToSeriesFolderCheckbox'),
        allowRegistration: checked('allowRegistrationCheckbox'),
        downloadSeriesImages: checked('downloadExternalSeriesImagesCheckbox'),
        seriesImageMaxMB: value('seriesImageMaxMB'),
        seriesImageMaxDownloadMB: value('seriesImageMaxDownloadMB'),
        seriesImageMaxDimension: value('seriesImageMaxDimension'),
        logMaxSize: value('logMaxSize'),
        padding: value('issueNumberPadding'),
        cleanupHours: value('dbCleanupInterval'),
        externalMetadata: checked('enableExternalSeriesMetadata'),
        comicVineApiKey: value('comicVineApiKey'),
        comicVineApiKeyHint: text('comicVineApiKeyHint'),
        comicVineClearApiKey: checked('comicVineClearApiKeyCheckbox'),
        comicVineBaseUrl: value('comicVineBaseUrl'),
        mangaDex: checked('enableMangaDexMetadata'),
        mangaDexBaseUrl: value('mangaDexBaseUrl'),
        aniList: checked('enableAniListMetadata'),
        aniListBaseUrl: value('aniListBaseUrl'),
        suwayomi: checked('enableSuwayomiCheckbox'),
        suwayomiBaseUrl: value('suwayomiBaseUrl'),
        suwayomiUsername: value('suwayomiUsername'),
        suwayomiPassword: value('suwayomiPassword'),
        suwayomiPasswordHint: text('suwayomiPasswordHint'),
        suwayomiClearPassword: checked('suwayomiClearPasswordCheckbox'),
        suwayomiDownloadsDirectory: value('suwayomiDownloadsDirectory'),
        suwayomiImportDirectory: value('suwayomiImportDirectory'),
        defaultView: value('defaultLibraryViewSelect'),
        preferredLanguage: value('defaultPreferredLanguageSelect'),
        smtpHost: value('smtpHost'),
        smtpPort: value('smtpPort'),
        smtpUsername: value('smtpUsername'),
        smtpPassword: value('smtpPassword'),
        smtpPasswordHint: text('smtpPasswordHint'),
        smtpClearPassword: checked('smtpClearPasswordCheckbox'),
        smtpUseSsl: checked('smtpUseSslCheckbox'),
        smtpAllowInsecure: checked('smtpAllowInsecureCheckbox'),
        emailFromAddress: value('emailFromAddress'),
        emailFromName: value('emailFromName'),
        emailMaxAttachmentMb: value('emailMaxAttachmentMb'),
        errorReporting: checked('errorReportingEnabledCheckbox'),
        errorReportingDestination: text('errorReportingDestination'),
        errorReportingToken: value('errorReportingToken'),
        errorReportingTokenHint: text('errorReportingTokenHint'),
        errorReportingClearToken: checked('errorReportingClearTokenCheckbox'),
        errorReportingAssignee: value('errorReportingAssignee'),
        errorReportingMaxPerDay: value('errorReportingMaxPerDay'),
        errorReportingDedupeHours: value('errorReportingDedupeHours'),
        errorReportingComment: checked('errorReportingCommentCheckbox')
    };
}

function loadStatus(page) {
    return {
        hidden: page.$('#settingsLoadStatus').hidden,
        message: page.$('#settingsLoadMessage').textContent,
        retryHidden: page.$('#settingsRetryButton').hidden
    };
}

test('the settings page fills every group from GET /api/settings', async () => {
    const page = await bootSettings();

    assert.equal(callsTo(page, 'GET /api/settings').length, 1);
    assert.match(callsTo(page, 'GET /api/settings')[0].headers.authorization, /^Bearer /);
    assert.equal(callsTo(page, 'GET /api/email/status').length, 1, 'the email status summary is refreshed');
    assert.equal(page.evaluate('settingsLoaded'), true);
    assert.deepEqual(loadStatus(page), { hidden: true, message: '', retryHidden: true });
    assert.deepEqual(formValues(page), {
        filenameFormat: '{series} v{volume} #{issue}',
        currentFormat: '{series} v{volume} #{issue}',
        theme: 'light',
        rename: true,
        normalize: false,
        coverToArchive: true,
        coverToFolder: false,
        allowRegistration: true,
        downloadSeriesImages: true,
        seriesImageMaxMB: '5',
        seriesImageMaxDownloadMB: '20',
        seriesImageMaxDimension: '1600',
        logMaxSize: '10',
        padding: '3',
        cleanupHours: '12',
        externalMetadata: true,
        comicVineApiKey: '',
        comicVineApiKeyHint: 'Leave blank to keep the stored key.',
        comicVineClearApiKey: false,
        comicVineBaseUrl: 'https://comicvine.example/api',
        mangaDex: true,
        mangaDexBaseUrl: 'https://mangadex.example',
        aniList: false,
        aniListBaseUrl: 'https://anilist.example/graphql',
        suwayomi: true,
        suwayomiBaseUrl: 'http://suwayomi.local:4567',
        suwayomiUsername: 'reader',
        suwayomiPassword: '',
        suwayomiPasswordHint: 'Leave blank to keep the stored password.',
        suwayomiClearPassword: false,
        suwayomiDownloadsDirectory: '/suwayomi/downloads/mangas',
        suwayomiImportDirectory: 'Manga',
        defaultView: 'series',
        preferredLanguage: 'ja',
        smtpHost: 'smtp.example.com',
        smtpPort: '2525',
        smtpUsername: 'mailer',
        smtpPassword: '',
        smtpPasswordHint: 'Leave blank to keep the stored password.',
        smtpClearPassword: false,
        smtpUseSsl: false,
        smtpAllowInsecure: true,
        emailFromAddress: 'comics@example.com',
        emailFromName: 'Comic Maintainer',
        emailMaxAttachmentMb: '30',
        errorReporting: true,
        errorReportingDestination: 'mleenorris/ComicMaintainer',
        errorReportingToken: '',
        errorReportingTokenHint: 'Leave blank to keep the stored token.',
        errorReportingClearToken: false,
        errorReportingAssignee: 'octocat',
        errorReportingMaxPerDay: '5',
        errorReportingDedupeHours: '48',
        errorReportingComment: false
    });

    // Administrators can edit everything once the settings have loaded.
    assert.equal(page.$('#settingsReadOnlyNotice').hidden, true);
    assert.equal(page.$('#filenameFormat').disabled, false);
    assert.equal(page.$('#saveSettingsBtn').disabled, false);
    assert.equal(page.$('#resetFilenameFormatBtn').disabled, false);
    assert.equal(page.$('#settings-backups').hidden, false);
    assert.deepEqual(messages(page), []);
    assert.deepEqual(page.errors, []);
});

test('reloading the settings discards typed secrets and clear-secret ticks', async () => {
    const page = await bootSettings();
    page.$('#smtpPassword').value = 'typed smtp password';
    page.$('#suwayomiPassword').value = 'typed suwayomi password';
    page.$('#errorReportingToken').value = 'ghp_typed';
    page.$('#comicVineApiKey').value = 'typed comicvine key';
    for (const id of ['smtpClearPasswordCheckbox', 'suwayomiClearPasswordCheckbox', 'errorReportingClearTokenCheckbox',
        'comicVineClearApiKeyCheckbox']) {
        page.$(`#${id}`).checked = true;
    }

    await page.run('loadSettings()');

    const values = formValues(page);
    assert.equal(values.smtpPassword, '');
    assert.equal(values.suwayomiPassword, '');
    assert.equal(values.errorReportingToken, '');
    assert.equal(values.comicVineApiKey, '');
    assert.equal(values.smtpClearPassword, false);
    assert.equal(values.suwayomiClearPassword, false);
    assert.equal(values.errorReportingClearToken, false);
    assert.equal(values.comicVineClearApiKey, false);
    assert.equal(callsTo(page, 'GET /api/settings').length, 2);
});

test('a ComicVine API key in the settings response is never put into the form', async () => {
    // GET /api/settings only reports whether a key is stored; should a server
    // still return the key itself, it must not end up in the page.
    const page = await bootSettings({ settings: { comicvine_api_key: 'cv-key-123', comicvine_api_key_set: true } });

    assert.equal(page.$('#comicVineApiKey').value, '');
    assert.equal(page.$('#comicVineApiKeyHint').textContent, 'Leave blank to keep the stored key.');
});

test('unset optional settings fall back to defaults and first-time hints', async () => {
    const page = await bootSettings({
        settings: {
            filename_format: '',
            enable_external_series_metadata: false,
            comicvine_api_key_set: false,
            comicvine_base_url: '',
            enable_mangadex_metadata: false,
            mangadex_base_url: null,
            anilist_base_url: '',
            enable_suwayomi: false,
            suwayomi_base_url: null,
            suwayomi_username: null,
            suwayomi_password_set: false,
            suwayomi_downloads_directory: null,
            default_library_view: 'files',
            allow_registration: false,
            default_preferred_language: null,
            write_cover_to_first_archive: false,
            download_external_series_images: false,
            series_image_max_bytes: null,
            series_image_max_download_bytes: undefined,
            series_image_max_dimension: '2048',
            smtp_host: null,
            smtp_port: null,
            smtp_username: null,
            smtp_password_set: false,
            smtp_use_ssl: true,
            smtp_allow_insecure: false,
            email_from_address: null,
            email_from_name: null,
            email_max_attachment_mb: null,
            error_reporting_enabled: false,
            error_reporting_github_owner: '',
            error_reporting_github_repo: 'ComicMaintainer',
            error_reporting_github_token_set: false,
            error_reporting_assignee: null,
            error_reporting_max_issues_per_day: null,
            error_reporting_dedupe_window_hours: undefined,
            error_reporting_comment_on_recurrence: null
        }
    });

    const values = formValues(page);
    assert.equal(values.filenameFormat, '');
    assert.equal(values.currentFormat, '{series} - Chapter {issue}', 'the built-in default format is shown');
    assert.equal(values.comicVineApiKey, '');
    assert.equal(values.comicVineApiKeyHint,
        'Leave blank to disable ComicVine lookups. The key is stored in user settings on this instance.');
    assert.equal(values.comicVineBaseUrl, 'https://comicvine.gamespot.com/api');
    assert.equal(values.mangaDexBaseUrl, 'https://api.mangadex.org');
    assert.equal(values.aniListBaseUrl, 'https://graphql.anilist.co');
    assert.equal(values.suwayomiBaseUrl, '');
    assert.equal(values.suwayomiUsername, '');
    assert.equal(values.suwayomiPasswordHint, 'Enter a password only if your Suwayomi server requires one.');
    assert.equal(values.suwayomiDownloadsDirectory, '');
    assert.equal(values.defaultView, 'files');
    assert.equal(values.preferredLanguage, '');
    // Image limits that are not numbers leave the inputs untouched (blank).
    assert.equal(values.seriesImageMaxMB, '');
    assert.equal(values.seriesImageMaxDownloadMB, '');
    assert.equal(values.seriesImageMaxDimension, '');
    assert.equal(values.smtpHost, '');
    assert.equal(values.smtpPort, '');
    assert.equal(values.smtpPasswordHint, 'Enter a password if your SMTP server requires one.');
    assert.equal(values.smtpUseSsl, true);
    assert.equal(values.smtpAllowInsecure, false);
    assert.equal(values.emailFromAddress, '');
    assert.equal(values.emailFromName, '');
    assert.equal(values.emailMaxAttachmentMb, '');
    assert.equal(values.errorReporting, false);
    assert.equal(values.errorReportingDestination, 'the ComicMaintainer project repository',
        'an incomplete owner/repo pair is not shown');
    assert.equal(values.errorReportingTokenHint, 'Use a fine-grained token with issues: write on that repository only.');
    assert.equal(values.errorReportingAssignee, '');
    assert.equal(values.errorReportingMaxPerDay, '10');
    assert.equal(values.errorReportingDedupeHours, '24');
    assert.equal(values.errorReportingComment, true, 'commenting on recurrences defaults to on');

    // Nor is an owner without a repository.
    page.route('GET /api/settings', settingsResponse({ error_reporting_github_owner: 'mleenorris', error_reporting_github_repo: null }));
    await page.run('loadSettings()');
    assert.equal(formValues(page).errorReportingDestination, 'the ComicMaintainer project repository');
});

test('the theme select shows the theme currently applied to the page', async () => {
    const page = await bootSettings();

    page.document.documentElement.setAttribute('data-theme', 'dark');
    await page.run('loadSettings()');
    assert.equal(page.$('#themeSelect').value, 'dark');

    page.document.documentElement.removeAttribute('data-theme');
    await page.run('loadSettings()');
    assert.equal(page.$('#themeSelect').value, 'light');
});

test('a failed load shows a retry prompt, and retrying loads the settings', async () => {
    const page = await bootSettings({ routes: { 'GET /api/settings': respond(500, 'database is locked') } });

    assert.equal(page.evaluate('settingsLoaded'), false);
    assert.deepEqual(loadStatus(page), {
        hidden: false,
        message: 'Settings could not be loaded. Retry before making changes.',
        retryHidden: false
    });
    assert.deepEqual(messages(page), [
        { type: 'error', text: 'Failed to load settings: HTTP error! status: 500 - database is locked' }
    ]);
    assert.equal(page.$('#messageContainer .message').getAttribute('role'), 'alert');
    // Nothing can be edited or saved until the settings are known.
    assert.equal(page.$('#filenameFormat').disabled, true);
    assert.equal(page.$('#themeSelect').disabled, true);
    assert.equal(page.$('#saveSettingsBtn').disabled, true);
    assert.equal(page.$('#resetFilenameFormatBtn').disabled, true);

    page.route('GET /api/settings', settingsResponse({ filename_format: '{series} #{issue}' }));
    page.click('#settingsRetryButton');
    assert.deepEqual(loadStatus(page), { hidden: false, message: 'Loading settings…', retryHidden: true });
    await page.settle();

    assert.equal(page.evaluate('settingsLoaded'), true);
    assert.deepEqual(loadStatus(page), { hidden: true, message: '', retryHidden: true });
    assert.equal(page.$('#filenameFormat').value, '{series} #{issue}');
    assert.equal(page.$('#filenameFormat').disabled, false);
    assert.equal(page.$('#saveSettingsBtn').disabled, false);
});

test('a network failure while loading is reported', async () => {
    const page = await bootSettings({ routes: { 'GET /api/settings': networkError } });

    assert.equal(page.evaluate('settingsLoaded'), false);
    assert.equal(loadStatus(page).message, 'Settings could not be loaded. Retry before making changes.');
    assert.deepEqual(messages(page), [{ type: 'error', text: 'Failed to load settings: Failed to fetch' }]);
});

test('an expired session while loading redirects to the login page', async () => {
    const page = await bootSettings({ routes: { 'GET /api/settings': 401 } });

    assert.ok(page.navigations.some(navigation => navigation.url === 'http://localhost/login.html'));
    assert.equal(page.window.localStorage.getItem('jwt_token'), null);
    assert.equal(page.evaluate('settingsLoaded'), false);
    assert.deepEqual(messages(page), []);
});

test('a slower, superseded settings load is ignored', async () => {
    const page = await bootSettings();
    let request = 0;
    const slow = deferred();
    page.route('GET /api/settings', () => (++request === 1
        ? slow.promise
        : settingsResponse({ filename_format: 'newest {issue}' })));

    page.run('loadSettings()');
    await page.run('loadSettings()');
    assert.equal(page.$('#filenameFormat').value, 'newest {issue}');

    slow.resolve(settingsResponse({ filename_format: 'stale {issue}' }));
    await page.settle();
    assert.equal(page.$('#filenameFormat').value, 'newest {issue}');
    assert.equal(page.evaluate('settingsLoaded'), true);
});

test('a superseded settings load that fails is ignored', async () => {
    const page = await bootSettings();
    let request = 0;
    const slow = deferred();
    page.route('GET /api/settings', () => (++request === 1 ? slow.promise : settingsResponse()));

    page.run('loadSettings()');
    await page.run('loadSettings()');
    slow.reject(new TypeError('Failed to fetch'));
    await page.settle();

    assert.equal(page.evaluate('settingsLoaded'), true);
    assert.equal(page.$('#settingsLoadStatus').hidden, true);
    assert.deepEqual(messages(page), []);
});

test('settings still load and report failures on a page without the load-status banner', async () => {
    const page = await bootSettings();
    page.$('#settingsLoadStatus').remove();

    page.route('GET /api/settings', networkError);
    clearMessages(page);
    await page.run('loadSettings()');
    assert.equal(page.evaluate('settingsLoaded'), false);
    assert.deepEqual(messages(page), [{ type: 'error', text: 'Failed to load settings: Failed to fetch' }]);

    page.route('GET /api/settings', settingsResponse({ filename_format: 'banner-free {issue}' }));
    await page.run('loadSettings()');
    assert.equal(page.evaluate('settingsLoaded'), true);
    assert.equal(page.$('#filenameFormat').value, 'banner-free {issue}');
    assert.deepEqual(page.errors, []);
});

test('non-administrators see server-wide settings read-only', async () => {
    const page = await bootSettings({
        user: { canModifyLibrary: false, canAdminister: false },
        routes: { 'GET /api/backups': respond(403, { error: 'Forbidden' }) }
    });

    assert.equal(page.evaluate('settingsLoaded'), true);
    assert.equal(page.document.body.classList.contains('user-read-only'), true);
    assert.equal(page.$('#settingsReadOnlyNotice').hidden, false);
    // The personal theme preference stays editable; nothing server-wide does.
    assert.equal(page.$('#themeSelect').disabled, false);
    for (const id of ['filenameFormat', 'defaultLibraryViewSelect', 'watcherEnableRenameCheckbox', 'smtpHost',
        'errorReportingToken', 'saveSettingsBtn', 'resetFilenameFormatBtn']) {
        assert.equal(page.$(`#${id}`).disabled, true, `#${id} is disabled`);
    }
    assert.equal(page.$('#settings-backups').hidden, true);
    assert.equal(page.$('#settingsNavBackups').hidden, true);
    assert.equal(page.$('#createBackupBtn').disabled, true);
    assert.equal(page.$('#diagnosticsMenuItem').hidden, true);
    assert.deepEqual(messages(page), [], 'a 403 from the backups API is not an error toast');

    // Saving or resetting is refused client-side, without a request.
    const before = page.fetchCalls.length;
    await page.run('saveFilenameFormat()');
    await page.run('resetFilenameFormat()');
    assert.equal(page.fetchCalls.length, before);
});

// Checkboxes that save on change: [element, method, endpoint, success label, error prefix].
const TOGGLES = [
    ['watcherEnableRenameCheckbox', 'POST', '/api/settings/watcher-enable-rename', 'File rename', 'Failed to update rename setting: '],
    ['watcherEnableNormalizeCheckbox', 'POST', '/api/settings/watcher-enable-normalize', 'Metadata normalize', 'Failed to update normalize setting: '],
    ['writeCoverToFirstArchiveCheckbox', 'POST', '/api/settings/write-cover-to-first-archive', 'Embed cover into first issue archive', 'Failed to update cover embed setting: '],
    ['writeCoverToSeriesFolderCheckbox', 'POST', '/api/settings/write-cover-to-series-folder', 'Write cover into series folder', 'Failed to update cover folder setting: '],
    ['allowRegistrationCheckbox', 'PUT', '/api/settings/allow-registration', 'Self-service registration', 'Failed to update registration setting: '],
    ['downloadExternalSeriesImagesCheckbox', 'POST', '/api/settings/download-external-series-images', 'Download external series images', 'Failed to update series image download setting: ']
];

test('settings checkboxes save as soon as they change', async t => {
    const page = await bootSettings();
    const token = page.window.localStorage.getItem('jwt_token');

    for (const [id, method, path, label, errorPrefix] of TOGGLES) {
        // Each case ends with a 401, which signs the page out.
        page.window.localStorage.setItem('jwt_token', token);
        await t.test(label, async () => {
            const checkbox = page.$(`#${id}`);
            const route = `${method} ${path}`;
            const initial = checkbox.checked;
            page.route(route, { message: 'ok' });
            clearMessages(page);

            page.input(checkbox, !initial);
            await page.settle();
            let calls = callsTo(page, route);
            assert.equal(calls.length, 1);
            assert.deepEqual(calls[0].body, { enabled: !initial });
            assert.equal(calls[0].headers['content-type'], 'application/json');
            assert.match(calls[0].headers.authorization, /^Bearer /);
            assert.deepEqual(lastMessage(page), {
                type: 'success',
                text: `${label} ${!initial ? 'enabled' : 'disabled'} successfully!`
            });
            assert.equal(checkbox.checked, !initial);

            page.input(checkbox, initial);
            await page.settle();
            calls = callsTo(page, route);
            assert.deepEqual(calls.at(-1).body, { enabled: initial });
            assert.equal(lastMessage(page).text, `${label} ${initial ? 'enabled' : 'disabled'} successfully!`);

            // A rejected change is rolled back.
            page.route(route, respond(500, { error: 'Failed to save setting' }));
            page.input(checkbox, !initial);
            await page.settle();
            assert.deepEqual(lastMessage(page), { type: 'error', text: `${errorPrefix}HTTP error! status: 500` });
            assert.equal(checkbox.checked, initial);

            page.route(route, networkError);
            page.input(checkbox, !initial);
            await page.settle();
            assert.deepEqual(lastMessage(page), { type: 'error', text: `${errorPrefix}Failed to fetch` });
            assert.equal(checkbox.checked, initial);

            // An expired session also rolls back, without a toast.
            const shown = messages(page).length;
            const navigations = page.navigations.length;
            page.route(route, 401);
            page.input(checkbox, !initial);
            await page.settle();
            assert.equal(checkbox.checked, initial);
            assert.equal(messages(page).length, shown);
            assert.equal(page.navigations.at(-1).url, 'http://localhost/login.html');
            assert.ok(page.navigations.length > navigations);
            assert.equal(callsTo(page, route).length, 5);
        });
    }
});

test('changing the theme applies it and saves the preference', async () => {
    const page = await bootSettings({ routes: { 'PUT /api/preferences': {} } });

    page.input('#themeSelect', 'dark');
    await page.settle();

    assert.equal(page.document.documentElement.getAttribute('data-theme'), 'dark');
    const calls = callsTo(page, 'PUT /api/preferences');
    assert.equal(calls.length, 1);
    assert.deepEqual(calls[0].body, { theme: 'dark' });
});

test('the default library view saves on change', async () => {
    const page = await bootSettings({ routes: { 'PUT /api/settings/default-library-view': { message: 'ok' } } });
    const route = 'PUT /api/settings/default-library-view';

    page.input('#defaultLibraryViewSelect', 'files');
    await page.settle();
    assert.deepEqual(callsTo(page, route).at(-1).body, { view: 'files' });
    assert.deepEqual(lastMessage(page), { type: 'success', text: 'Default library view set to Files (folder view)' });

    page.input('#defaultLibraryViewSelect', 'series');
    await page.settle();
    assert.deepEqual(callsTo(page, route).at(-1).body, { view: 'series' });
    assert.deepEqual(lastMessage(page), { type: 'success', text: 'Default library view set to Series' });

    page.route(route, respond(500, { error: 'nope' }));
    page.input('#defaultLibraryViewSelect', 'files');
    await page.settle();
    assert.deepEqual(lastMessage(page), { type: 'error', text: 'Failed to update default library view: HTTP error! status: 500' });

    const shown = messages(page).length;
    page.route(route, 401);
    page.input('#defaultLibraryViewSelect', 'series');
    await page.settle();
    assert.equal(messages(page).length, shown);
    assert.equal(page.navigations.at(-1).url, 'http://localhost/login.html');

    // Without the select (e.g. markup from an older page) there is nothing to save.
    const before = page.fetchCalls.length;
    page.$('#defaultLibraryViewSelect').remove();
    await page.run('updateDefaultLibraryViewFromSettings()');
    assert.equal(page.fetchCalls.length, before);
});

test('the default preferred language saves on change and refreshes the series list', async () => {
    const route = 'PUT /api/settings/default-preferred-language';
    const page = await bootSettings({ routes: { [route]: { message: 'ok' } } });

    let since = page.fetchCalls.length;
    page.input('#defaultPreferredLanguageSelect', 'ko');
    await page.settle();
    assert.deepEqual(callsTo(page, route).at(-1).body, { language: 'ko' });
    assert.deepEqual(lastMessage(page), { type: 'success', text: 'Default preferred language updated' });
    const refreshes = callsTo(page, 'GET /api/files/series', since);
    assert.equal(refreshes.length, 1, 'series names are re-resolved');
    assert.equal(refreshes[0].query.refresh, 'true');

    // "None" clears the default.
    page.input('#defaultPreferredLanguageSelect', '');
    await page.settle();
    assert.deepEqual(callsTo(page, route).at(-1).body, { language: null });

    // The server's own error text is shown when there is one.
    page.route(route, respond(400, { error: 'Unsupported language code' }));
    page.input('#defaultPreferredLanguageSelect', 'zh');
    await page.settle();
    assert.deepEqual(lastMessage(page), {
        type: 'error',
        text: 'Failed to update default preferred language: Unsupported language code'
    });

    page.route(route, respond(500, {}));
    page.input('#defaultPreferredLanguageSelect', 'en');
    await page.settle();
    assert.equal(lastMessage(page).text, 'Failed to update default preferred language: HTTP error! status: 500');

    page.route(route, respond(502, '<html>Bad gateway</html>'));
    page.input('#defaultPreferredLanguageSelect', 'ja');
    await page.settle();
    assert.equal(lastMessage(page).text, 'Failed to update default preferred language: HTTP error! status: 502');

    page.route(route, networkError);
    page.input('#defaultPreferredLanguageSelect', 'ko');
    await page.settle();
    assert.equal(lastMessage(page).text, 'Failed to update default preferred language: Failed to fetch');

    since = page.fetchCalls.length;
    const shown = messages(page).length;
    page.route(route, 401);
    page.input('#defaultPreferredLanguageSelect', 'en');
    await page.settle();
    assert.equal(messages(page).length, shown);
    assert.equal(callsTo(page, 'GET /api/files/series', since).length, 0);
    assert.equal(page.navigations.at(-1).url, 'http://localhost/login.html');

    const before = page.fetchCalls.length;
    page.$('#defaultPreferredLanguageSelect').remove();
    await page.run('updateDefaultPreferredLanguageFromSettings()');
    assert.equal(page.fetchCalls.length, before);
});

test('settings opens from the sidebar and Cancel returns to the previous page', async () => {
    const page = await bootApp({ hash: '#/library', routes: settingsRoutes() });
    assert.equal(callsTo(page, 'GET /api/settings').length, 0);

    page.click('#sidebarSettingsBtn');
    await page.waitFor(() => page.evaluate('settingsLoaded'));
    assert.equal(page.window.location.hash, '#/settings');
    assert.equal(page.$('#settingsView').hidden, false);
    assert.equal(callsTo(page, 'GET /api/settings').length, 1);

    // Opening Settings again while it is shown reloads it.
    page.run('openSettings()');
    await page.settle();
    assert.equal(callsTo(page, 'GET /api/settings').length, 2);

    page.click('button[onclick="closeSettings()"]');
    await page.settle();
    assert.equal(page.window.location.hash, '#/library');
    assert.equal(page.$('#settingsView').hidden, true);
});

test('leaving settings without a page to return to goes home', async () => {
    const page = await bootSettings();
    assert.equal(page.evaluate('settingsReturnHash'), '#/home');

    page.run("settingsReturnHash = '#/settings'; closeSettings()");
    await page.settle();
    assert.equal(page.window.location.hash, '#/home');

    page.window.location.hash = '#/settings';
    await page.settle();
    page.run("settingsReturnHash = ''; closeSettings()");
    await page.settle();
    assert.equal(page.window.location.hash, '#/home');
});

test('the section navigation scrolls to a card and focuses its heading', async () => {
    const media = {};
    const page = await bootSettings({ media });
    const scrolls = [];
    page.window.Element.prototype.scrollIntoView = function scrollIntoView(options) {
        scrolls.push({ id: this.id, options: plain(options) });
    };
    const activeLinks = () => page.$$('.settings-nav-link[aria-current="true"]').map(link => link.dataset.settingsTarget);

    page.click('.settings-nav-link[data-settings-target="settings-watcher"]');
    assert.deepEqual(scrolls, [{ id: 'settings-watcher', options: { behavior: 'smooth', block: 'start' } }]);
    assert.deepEqual(activeLinks(), ['settings-watcher']);
    assert.deepEqual(page.$$('.settings-nav-link.active').map(link => link.dataset.settingsTarget), ['settings-watcher']);
    const heading = page.$('#settings-watcher-title');
    assert.equal(heading.getAttribute('tabindex'), '-1');
    assert.equal(page.document.activeElement, heading);

    // Reduced motion jumps instead of animating.
    media['(prefers-reduced-motion: reduce)'] = true;
    page.click('.settings-nav-link[data-settings-target="settings-danger"]');
    assert.deepEqual(scrolls.at(-1), { id: 'settings-danger', options: { behavior: 'auto', block: 'start' } });
    assert.deepEqual(activeLinks(), ['settings-danger']);
    assert.equal(page.document.activeElement, page.$('#settings-danger-title'));

    // A card without a title heading is still scrolled to.
    page.$('#settings-access .settings-card-title').remove();
    page.run("scrollToSettingsSection('settings-access')");
    assert.equal(scrolls.at(-1).id, 'settings-access');
    assert.deepEqual(activeLinks(), ['settings-access']);

    // Unknown sections are ignored.
    page.run("scrollToSettingsSection('settings-nope')");
    assert.equal(scrolls.length, 3);
    assert.deepEqual(activeLinks(), ['settings-access']);
});

test('the section navigation follows the card nearest the top while scrolling', async () => {
    const page = await bootSettings();
    const card = id => page.$(`#${id}`);
    const cardObservers = () => page.intersectionObservers.filter(observer => observer.targets.has(card('settings-appearance')));
    const activeLinks = () => page.$$('.settings-nav-link[aria-current="true"]').map(link => link.dataset.settingsTarget);

    assert.equal(cardObservers().length, 1);
    const observer = cardObservers()[0];
    assert.equal(observer.targets.size, page.$$('#settingsPageBody .settings-card').length);
    assert.equal(observer.options.rootMargin, '0px 0px 0px 0px');
    assert.deepEqual(activeLinks(), ['settings-appearance'], 'the first section starts active');

    const entry = (id, isIntersecting, top = 0) => ({ target: card(id), isIntersecting, boundingClientRect: { top } });
    observer.callback([entry('settings-watcher', true, 220), entry('settings-access', true, 40)]);
    assert.deepEqual(activeLinks(), ['settings-access']);

    observer.callback([entry('settings-access', false)]);
    assert.deepEqual(activeLinks(), ['settings-watcher']);

    // Nothing visible (between cards): the last highlight stays.
    observer.callback([entry('settings-watcher', false)]);
    assert.deepEqual(activeLinks(), ['settings-watcher']);

    // Reloading with the same viewport keeps the observer...
    await page.run('loadSettings()');
    assert.equal(page.intersectionObservers.filter(o => o.callback === observer.callback).length, 1);
    assert.equal(cardObservers().length, 1);

    // ...a resized viewport replaces it, with margins scaled to the new height.
    Object.defineProperty(page.document.documentElement, 'clientHeight', { value: 1000, configurable: true });
    await page.run('loadSettings()');
    assert.equal(observer.targets.size, 0, 'the old observer is disconnected');
    const [replacement] = cardObservers();
    assert.notEqual(replacement, observer);
    assert.equal(replacement.options.rootMargin, '-150px 0px -700px 0px');
    assert.deepEqual(activeLinks(), ['settings-watcher'], 'the current highlight is kept');

    // Without any cards there is nothing to observe.
    Object.defineProperty(page.document.documentElement, 'clientHeight', { value: 800, configurable: true });
    const observers = page.intersectionObservers.length;
    page.$$('#settingsPageBody .settings-card').forEach(element => element.classList.remove('settings-card'));
    page.run('updateSettingsNavActive()');
    assert.equal(replacement.targets.size, 0);
    assert.equal(page.intersectionObservers.length, observers);
});

test('the section highlight is skipped where IntersectionObserver is unavailable', async () => {
    const page = await bootSettings();
    const observers = page.intersectionObservers.length;
    delete page.window.IntersectionObserver;
    Object.defineProperty(page.document.documentElement, 'clientHeight', { value: 900, configurable: true });

    await page.run('loadSettings()');

    assert.equal(page.evaluate('settingsLoaded'), true);
    assert.equal(page.intersectionObservers.length, observers);
    assert.deepEqual(page.errors, []);
});
