'use strict';

// Settings page (js/app/settings.js): "Save Settings" — client-side
// validation, the single save request and its exact body, how stored secrets
// are kept/replaced/cleared, settings pinned by the environment, and the one
// result shown; plus "Reset Filename Format" and "Send Test Report".

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const {
    bootSettings, messages, lastMessage, clearMessages, callsTo, requestLog, settingsResponse, SERVER_FAILURE, serverFailure
} = require('./support/settings-helpers');

const networkError = () => {
    throw new TypeError('Failed to fetch');
};

const SAVE_ROUTE = 'PUT /api/settings';

const SAVED_MESSAGE = 'Settings saved. Changes to log rotation take effect after a restart.';

function routeSave(page, response = { message: 'Settings saved' }) {
    page.route(SAVE_ROUTE, response);
}

async function save(page) {
    const since = page.fetchCalls.length;
    clearMessages(page);
    page.click('#saveSettingsBtn');
    await page.settle();
    return since;
}

function saveBody(page, since = 0) {
    const calls = callsTo(page, SAVE_ROUTE, since);
    assert.equal(calls.length, 1, `one ${SAVE_ROUTE} request`);
    return calls[0].body;
}

test('saving sends every group in one request with the edited values', async () => {
    const page = await bootSettings();
    routeSave(page);
    page.input('#filenameFormat', '  {series} #{issue}  ');
    page.input('#logMaxSize', '7.5');
    page.input('#issueNumberPadding', '2');
    page.input('#dbCleanupInterval', '0');
    page.input('#staleCleanupMaxRemovalPercent', '25');
    page.input('#staleCleanupMissingGraceHours', '0');
    page.input('#staleCleanupSentinelFile', '  .library-mounted  ');
    page.input('#processingHistoryRetentionDays', '0');
    page.input('#processingHistoryMaxEntries', '1000');
    page.input('#enableAniListMetadata', true);
    page.input('#comicVineApiKey', ' new-key ');
    page.input('#mangaDexBaseUrl', '   ');
    page.input('#seriesImageMaxMB', '4');
    page.input('#seriesImageMaxDownloadMB', '4');
    page.input('#seriesImageMaxDimension', '1024');
    page.input('#smtpHost', ' mail.example.org ');
    page.input('#smtpPort', '465');
    page.input('#smtpUsername', '');
    page.input('#smtpPassword', 'smtp secret ');
    page.input('#smtpUseSslCheckbox', true);
    page.input('#emailFromAddress', '');
    page.input('#emailFromName', ' Comics ');
    page.input('#emailMaxAttachmentMb', '');
    page.input('#emailMaxSendsPerHour', '12');
    page.input('#emailMaxSendsPerDay', '');
    page.input('#emailMinSecondsBetweenSends', '90');
    page.input('#suwayomiBaseUrl', '');
    page.input('#suwayomiUsername', '  ');
    page.input('#suwayomiImportDirectory', '  ');
    page.input('#errorReportingClearTokenCheckbox', true);
    page.input('#errorReportingToken', 'ghp_ignored_because_cleared');
    page.input('#errorReportingAssignee', '');
    page.input('#errorReportingMaxPerDay', '0');
    page.input('#errorReportingDedupeHours', '');
    page.input('#errorReportingCommentCheckbox', true);
    // An earlier Suwayomi probe is discarded so the new settings are re-probed.
    page.run('suwayomiStatusPromise = Promise.resolve(false)');

    // Hold the Suwayomi probe to observe the state just after the save.
    const probeGate = deferred();
    page.route('GET /api/suwayomi/status', () => probeGate.promise);
    const since = await save(page);
    assert.equal(page.evaluate('emailStatusCache'), null, 'the cached email status is dropped once SMTP settings change');
    assert.deepEqual(messages(page), [], 'one result, once everything is done');
    probeGate.resolve({ enabled: true, connected: true });
    await page.settle();

    assert.deepEqual(requestLog(page, since), [
        'PUT /api/settings',
        'GET /api/suwayomi/status',
        // The saved settings are then reloaded from the server.
        'GET /api/backups',
        'GET /api/settings',
        'GET /api/auth/api-key',
        'GET /api/email/status'
    ]);
    assert.deepEqual(saveBody(page, since), {
        filenameFormat: '{series} #{issue}',
        logMaxMB: 7.5,
        issueNumberPadding: 2,
        databaseCleanup: {
            intervalHours: 0,
            staleCleanupMaxRemovalPercent: 25,
            staleCleanupMissingGraceHours: 0,
            staleCleanupSentinelFile: '.library-mounted',
            processingHistoryRetentionDays: 0,
            processingHistoryMaxEntries: 1000
        },
        externalSeriesMetadata: {
            enabled: true,
            comicVineApiKey: 'new-key',
            comicVineBaseUrl: 'https://comicvine.example/api',
            enableMangaDex: true,
            mangaDexBaseUrl: 'https://api.mangadex.org',
            enableAniList: true,
            aniListBaseUrl: 'https://anilist.example/graphql',
            fillComicInfoFromProviders: true
        },
        seriesImageLimits: { maxMB: 4, maxDownloadMB: 4, maxDimension: 1024 },
        email: {
            smtpHost: 'mail.example.org',
            smtpPort: 465,
            smtpUsername: '',
            smtpPassword: 'smtp secret ',
            smtpUseSsl: true,
            smtpAllowInsecure: true,
            emailFromAddress: '',
            emailFromName: 'Comics',
            emailMaxAttachmentMb: null
        },
        // A blank rate-limit field is 0: no limit.
        emailRateLimit: { maxSendsPerHour: 12, maxSendsPerDay: 0, minSecondsBetweenSends: 90 },
        // A blank password keeps the stored one (null); blank text fields are sent empty to clear them.
        suwayomi: {
            enabled: true,
            baseUrl: '',
            username: '',
            password: null,
            downloadsDirectory: '/suwayomi/downloads/mangas',
            importDirectory: ''
        },
        // "Clear token" wins over a typed token; blank numbers fall back.
        errorReporting: {
            enabled: true,
            gitHubToken: '',
            assignee: '',
            maxIssuesPerDay: 0,
            dedupeWindowHours: 24,
            commentOnRecurrence: true
        },
        // None of the fixture's settings are notification targets, so each is blank and off.
        notifications: {
            ntfy: { enabled: false, url: '', events: '', token: null, topic: '' },
            gotify: { enabled: false, url: '', events: '', token: null },
            discord: { enabled: false, url: null, events: '' },
            webhook: { enabled: false, url: null, events: '' },
            komga: { enabled: false, url: '', events: '', token: null, libraryId: '' },
            kavita: { enabled: false, url: '', events: '', token: null, libraryId: '' }
        },
        opds: { enabled: true }
    });
    const call = callsTo(page, SAVE_ROUTE, since)[0];
    assert.equal(call.headers['content-type'], 'application/json');
    assert.match(call.headers.authorization, /^Bearer /);

    assert.deepEqual(messages(page), [{ type: 'success', text: SAVED_MESSAGE }]);
    // The reload shows what the server now holds and clears the secret inputs.
    assert.equal(page.$('#filenameFormat').value, '{series} v{volume} #{issue}');
    assert.equal(page.$('#smtpPassword').value, '');
    assert.equal(page.$('#errorReportingClearTokenCheckbox').checked, false);
    assert.equal(page.evaluate('settingsLoaded'), true);
    assert.equal(page.evaluate('suwayomiAvailable'), true, 'the new probe answer is used, not the earlier one');
});

test('new secrets are sent, cleared secrets are sent empty', async () => {
    const page = await bootSettings({ settings: { smtp_port: null, email_max_attachment_mb: 12.5 } });
    routeSave(page);
    page.input('#smtpPassword', 'typed but cleared');
    page.input('#smtpClearPasswordCheckbox', true);
    page.input('#suwayomiPassword', 'new-suwayomi-password');
    page.input('#suwayomiBaseUrl', ' http://suwayomi:4567 ');
    page.input('#enableSuwayomiCheckbox', false);
    page.input('#suwayomiDownloadsDirectory', ' /downloads/mangas ');
    page.input('#errorReportingToken', 'ghp_new_token');
    page.input('#errorReportingMaxPerDay', '');
    page.input('#errorReportingDedupeHours', '6');
    page.input('#errorReportingEnabledCheckbox', false);

    const since = await save(page);

    const body = saveBody(page, since);
    assert.deepEqual(body.email, {
        smtpHost: 'smtp.example.com',
        smtpPort: null,
        smtpUsername: 'mailer',
        smtpPassword: '',
        smtpUseSsl: false,
        smtpAllowInsecure: true,
        emailFromAddress: 'comics@example.com',
        emailFromName: 'Comic Maintainer',
        emailMaxAttachmentMb: 12.5
    });
    assert.deepEqual(body.emailRateLimit, { maxSendsPerHour: 20, maxSendsPerDay: 200, minSecondsBetweenSends: 30 });
    assert.deepEqual(body.suwayomi, {
        enabled: false,
        baseUrl: 'http://suwayomi:4567',
        username: 'reader',
        password: 'new-suwayomi-password',
        downloadsDirectory: '/downloads/mangas',
        importDirectory: 'Manga'
    });
    assert.deepEqual(body.errorReporting, {
        enabled: false,
        gitHubToken: 'ghp_new_token',
        assignee: 'octocat',
        maxIssuesPerDay: 0,
        dedupeWindowHours: 6,
        commentOnRecurrence: false
    });
    // The metadata defaults apply when the base URLs are left blank.
    page.input('#comicVineBaseUrl', '');
    page.input('#aniListBaseUrl', '');
    const second = await save(page);
    const metadata = saveBody(page, second).externalSeriesMetadata;
    assert.equal(metadata.comicVineBaseUrl, 'https://comicvine.gamespot.com/api');
    assert.equal(metadata.aniListBaseUrl, 'https://graphql.anilist.co');
    assert.equal(lastMessage(page).text, SAVED_MESSAGE);
});

test('a blank SMTP host and sender name are sent empty, and "clear password" clears the Suwayomi password', async () => {
    const page = await bootSettings();
    routeSave(page);
    page.input('#smtpHost', '   ');
    page.input('#emailFromName', '');
    page.input('#suwayomiPassword', 'typed but cleared');
    page.input('#suwayomiClearPasswordCheckbox', true);

    const since = await save(page);

    const body = saveBody(page, since);
    assert.deepEqual(body.email, {
        smtpHost: '',
        smtpPort: 2525,
        smtpUsername: 'mailer',
        smtpPassword: null,
        smtpUseSsl: false,
        smtpAllowInsecure: true,
        emailFromAddress: 'comics@example.com',
        emailFromName: '',
        emailMaxAttachmentMb: 30
    });
    assert.deepEqual(body.suwayomi, {
        enabled: true,
        baseUrl: 'http://suwayomi.local:4567',
        username: 'reader',
        password: '',
        downloadsDirectory: '/suwayomi/downloads/mangas',
        importDirectory: 'Manga'
    });
    assert.deepEqual(messages(page), [{ type: 'success', text: SAVED_MESSAGE }]);
    assert.equal(page.$('#suwayomiClearPasswordCheckbox').checked, false, 'the tick is reset by the reload');
    assert.equal(page.$('#suwayomiPassword').value, '');
});

test('the stored ComicVine API key is kept unless a new key is typed or "clear" is ticked', async () => {
    const page = await bootSettings();
    routeSave(page);

    // Untouched (or only whitespace), the key is sent as null: keep the stored one.
    let since = await save(page);
    assert.deepEqual(saveBody(page, since).externalSeriesMetadata, {
        enabled: true,
        comicVineApiKey: null,
        comicVineBaseUrl: 'https://comicvine.example/api',
        enableMangaDex: true,
        mangaDexBaseUrl: 'https://mangadex.example',
        enableAniList: false,
        aniListBaseUrl: 'https://anilist.example/graphql',
        fillComicInfoFromProviders: true
    });
    page.input('#comicVineApiKey', '   ');
    since = await save(page);
    assert.equal(saveBody(page, since).externalSeriesMetadata.comicVineApiKey, null);

    page.input('#comicVineApiKey', ' cv-new-key ');
    since = await save(page);
    assert.equal(saveBody(page, since).externalSeriesMetadata.comicVineApiKey, 'cv-new-key');
    assert.equal(page.$('#comicVineApiKey').value, '', 'the reload empties the input again');

    // "Clear" wins over a typed key.
    page.input('#comicVineApiKey', 'typed but cleared');
    page.input('#comicVineClearApiKeyCheckbox', true);
    since = await save(page);
    assert.equal(saveBody(page, since).externalSeriesMetadata.comicVineApiKey, '');
    assert.deepEqual(messages(page), [{ type: 'success', text: SAVED_MESSAGE }]);
    assert.equal(page.$('#comicVineClearApiKeyCheckbox').checked, false, 'the tick is reset by the reload');
    assert.equal(page.$('#comicVineApiKey').value, '');
});

test('groups whose controls are missing are left out of the request', async () => {
    const page = await bootSettings();
    routeSave(page);
    page.$('#enableSuwayomiCheckbox').remove();
    page.$('#errorReportingEnabledCheckbox').remove();

    const since = await save(page);

    const body = saveBody(page, since);
    assert.equal('suwayomi' in body, false);
    assert.equal('errorReporting' in body, false);
    assert.equal(callsTo(page, 'GET /api/suwayomi/status', since).length, 0);
    assert.deepEqual(messages(page), [{ type: 'success', text: SAVED_MESSAGE }]);
});

test('the email rate limit is left out when its controls are missing', async () => {
    const page = await bootSettings();
    routeSave(page);
    for (const id of ['emailMaxSendsPerHour', 'emailMaxSendsPerDay', 'emailMinSecondsBetweenSends']) {
        page.$(`#${id}`).closest('.form-group').remove();
    }

    const since = await save(page);

    assert.equal('emailRateLimit' in saveBody(page, since), false);
    assert.deepEqual(messages(page), [{ type: 'success', text: SAVED_MESSAGE }]);
});

// [description, edits, expected message]
const INVALID_FORMS = [
    ['blank filename format', { filenameFormat: '   ' }, 'Filename format cannot be empty'],
    ['missing log size', { logMaxSize: '' }, 'Log max size must be a positive number'],
    ['zero log size', { logMaxSize: '0' }, 'Log max size must be a positive number'],
    ['missing padding', { issueNumberPadding: '' }, 'Issue number padding must be between 0 and 10'],
    ['negative padding', { issueNumberPadding: '-1' }, 'Issue number padding must be between 0 and 10'],
    ['fractional padding', { issueNumberPadding: '10.5' }, 'Issue number padding must be between 0 and 10'],
    ['exponent padding', { issueNumberPadding: '1e9' }, 'Issue number padding must be between 0 and 10'],
    ['padding above 10', { issueNumberPadding: '11' }, 'Issue number padding must be between 0 and 10'],
    ['log size above the server limit', { logMaxSize: '2048' }, 'Log max size must be 2047 MB or less'],
    ['missing cleanup interval', { dbCleanupInterval: '' }, 'Database cleanup interval must be 0 or greater'],
    ['negative cleanup interval', { dbCleanupInterval: '-2' }, 'Database cleanup interval must be 0 or greater'],
    ['missing removal share', { staleCleanupMaxRemovalPercent: '' },
        'Largest share removed without confirmation must be a whole number from 0 to 100'],
    ['removal share above 100%', { staleCleanupMaxRemovalPercent: '101' },
        'Largest share removed without confirmation must be a whole number from 0 to 100'],
    ['fractional missing-file grace period', { staleCleanupMissingGraceHours: '1.5' },
        'Remove missing files after must be a whole number of hours from 0 to 720'],
    ['missing-file grace period above 30 days', { staleCleanupMissingGraceHours: '721' },
        'Remove missing files after must be a whole number of hours from 0 to 720'],
    ['negative history retention', { processingHistoryRetentionDays: '-1' },
        'Processing history retention must be a whole number of days from 0 to 3650'],
    ['history entries above the server limit', { processingHistoryMaxEntries: '10000001' },
        'Maximum processing history entries must be a whole number from 0 to 10000000'],
    ['missing stored cover size', { seriesImageMaxMB: '' }, 'Max stored cover size must be a positive number'],
    ['zero stored cover size', { seriesImageMaxMB: '0' }, 'Max stored cover size must be a positive number'],
    ['missing download size', { seriesImageMaxDownloadMB: '' }, 'Max download size must be a positive number'],
    ['negative download size', { seriesImageMaxDownloadMB: '-1' }, 'Max download size must be a positive number'],
    ['download size below stored size', { seriesImageMaxMB: '30', seriesImageMaxDownloadMB: '20' },
        'Max download size must be greater than or equal to max stored cover size'],
    ['missing cover dimension', { seriesImageMaxDimension: '' }, 'Max cover dimension must be a positive number'],
    ['zero cover dimension', { seriesImageMaxDimension: '0' }, 'Max cover dimension must be a positive number'],
    ['SMTP port 0', { smtpPort: '0' }, 'SMTP port must be between 1 and 65535'],
    ['SMTP port above 65535', { smtpPort: '70000' }, 'SMTP port must be between 1 and 65535'],
    ['zero attachment size', { emailMaxAttachmentMb: '0' }, 'Max email attachment size must be a positive number'],
    ['negative attachment size', { emailMaxAttachmentMb: '-5' }, 'Max email attachment size must be a positive number'],
    ['attachment size above the server limit', { emailMaxAttachmentMb: '201' }, 'Max email attachment size must be 200 MB or less'],
    ['negative issues per day', { errorReportingMaxPerDay: '-1' }, 'Maximum issues per day must be between 0 and 100'],
    ['more than 100 issues per day', { errorReportingMaxPerDay: '101' }, 'Maximum issues per day must be between 0 and 100'],
    ['negative recurrence interval', { errorReportingDedupeHours: '-1' }, 'Recurrence comment interval must be between 1 and 720 hours'],
    ['zero recurrence interval', { errorReportingDedupeHours: '0' }, 'Recurrence comment interval must be between 1 and 720 hours'],
    ['recurrence interval above 720 hours', { errorReportingDedupeHours: '721' },
        'Recurrence comment interval must be between 1 and 720 hours'],
    // One bad value among other edits: nothing is half-saved.
    ['log size too large alongside other edits', { filenameFormat: '{series} - {issue}', logMaxSize: '3000', issueNumberPadding: '4' },
        'Log max size must be 2047 MB or less'],
    ['fractional emails per hour', { emailMaxSendsPerHour: '1.5' }, 'Max emails per hour must be a whole number from 0 to 10000'],
    ['negative emails per hour', { emailMaxSendsPerHour: '-1' }, 'Max emails per hour must be a whole number from 0 to 10000'],
    ['too many emails per hour', { emailMaxSendsPerHour: '10001' }, 'Max emails per hour must be a whole number from 0 to 10000'],
    ['negative emails per day', { emailMaxSendsPerDay: '-3' }, 'Max emails per day must be a whole number from 0 to 100000'],
    ['too many emails per day', { emailMaxSendsPerDay: '100001' }, 'Max emails per day must be a whole number from 0 to 100000'],
    ['fractional seconds between emails', { emailMinSecondsBetweenSends: '0.5' },
        'Minimum seconds between emails must be a whole number from 0 to 86400'],
    ['more than a day between emails', { emailMinSecondsBetweenSends: '86401' },
        'Minimum seconds between emails must be a whole number from 0 to 86400']
];

test('invalid values are rejected before anything is saved', async t => {
    const page = await bootSettings();
    routeSave(page);

    for (const [description, edits, expected] of INVALID_FORMS) {
        await t.test(description, async () => {
            await page.run('loadSettings()');
            for (const [id, value] of Object.entries(edits)) page.input(`#${id}`, value);

            const since = await save(page);

            assert.deepEqual(messages(page), [{ type: 'error', text: expected }]);
            assert.equal(callsTo(page, SAVE_ROUTE, since).length, 0);
        });
    }
});

// [description, response, expected message]
const FAILED_SAVES = [
    ['a value rejected by the server', respond(400, { error: 'MaxMB must be between 1 and 2047' }),
        'Failed to save settings: MaxMB must be between 1 and 2047. No settings were saved.'],
    ['a rejection that is already a full sentence', respond(400, { error: 'A base URL is required when Suwayomi is enabled.' }),
        'Failed to save settings: A base URL is required when Suwayomi is enabled. No settings were saved.'],
    ['a rejection with only a title (a malformed field)', respond(400, { title: 'One or more validation errors occurred.' }),
        'Failed to save settings: One or more validation errors occurred. No settings were saved.'],
    ['a setting pinned by the environment', respond(409, { error: 'Set by the environment and cannot be changed here: SMTP_HOST.' }),
        'Failed to save settings: Set by the environment and cannot be changed here: SMTP_HOST. No settings were saved.'],
    // Sent back with the request's reference, the only way to find the log line. A 5xx does
    // not claim that nothing was saved: the file can be replaced before a later step fails.
    ["the global handler's answer", serverFailure('corr-500'),
        `Failed to save settings: ${SERVER_FAILURE} (ref: corr-500)`],
    ['a refusal with a reference', respond(409, { error: 'Set by the environment and cannot be changed here: SMTP_HOST.' }, { 'X-Correlation-Id': 'corr-409' }),
        'Failed to save settings: Set by the environment and cannot be changed here: SMTP_HOST. No settings were saved. (ref: corr-409)'],
    ['a server error with only a message', respond(500, { message: 'Storage failure' }), 'Failed to save settings: Storage failure.'],
    ['a server error without a reason', respond(500, {}), 'Failed to save settings: HTTP error! status: 500.'],
    ['a bare status', 503, 'Failed to save settings: HTTP error! status: 503.'],
    ['a proxy error page', respond(502, '<html><body><h1>502 Bad Gateway</h1></body></html>', { 'Content-Type': 'text/html' }),
        'Failed to save settings: HTTP error! status: 502.'],
    ['a proxy timeout', respond(504, 'Gateway Timeout'), 'Failed to save settings: HTTP error! status: 504.'],
    ['a network failure', networkError, 'Failed to save settings: Failed to fetch.'],
];

test('a failed save shows one error and reloads nothing', async t => {
    const page = await bootSettings();

    for (const [description, response, expected] of FAILED_SAVES) {
        await t.test(description, async () => {
            routeSave(page, response);

            const since = await save(page);

            assert.deepEqual(messages(page), [{ type: 'error', text: expected }]);
            assert.equal(callsTo(page, SAVE_ROUTE, since).length, 1);
            assert.equal(callsTo(page, 'GET /api/settings', since).length, 0, 'the form is not reloaded');
            assert.equal(callsTo(page, 'GET /api/suwayomi/status', since).length, 0);
        });
    }
});

test('after a failed save the form keeps the edits for another try', async () => {
    const page = await bootSettings();
    page.input('#filenameFormat', '{series} - {issue}');
    page.input('#issueNumberPadding', '4');
    page.input('#smtpPassword', 'new smtp secret');
    page.input('#emailFromAddress', 'comics@');

    routeSave(page, respond(400, { error: "'comics@' is not a valid email address" }));
    await save(page);

    assert.deepEqual(messages(page), [{
        type: 'error',
        text: "Failed to save settings: 'comics@' is not a valid email address. No settings were saved."
    }]);
    assert.equal(page.$('#currentFormat').textContent, '{series} v{volume} #{issue}', 'the refused format is not shown as the current one');
    assert.equal(page.$('#filenameFormat').value, '{series} - {issue}');
    assert.equal(page.$('#issueNumberPadding').value, '4');
    assert.equal(page.$('#smtpPassword').value, 'new smtp secret');
    assert.equal(page.$('#emailFromAddress').value, 'comics@');

    // Fixing the address and saving again sends everything, the typed secret included.
    routeSave(page);
    page.route('GET /api/settings', settingsResponse({ filename_format: '{series} - {issue}' }));
    page.input('#emailFromAddress', 'comics@example.org');
    const retry = await save(page);

    assert.equal(saveBody(page, retry).email.emailFromAddress, 'comics@example.org');
    assert.equal(saveBody(page, retry).email.smtpPassword, 'new smtp secret');
    assert.deepEqual(messages(page), [{ type: 'success', text: SAVED_MESSAGE }]);
    assert.equal(callsTo(page, 'GET /api/settings', retry).length, 1, 'the saved settings are reloaded');
    assert.equal(page.$('#currentFormat').textContent, '{series} - {issue}', 'the saved format is now the current one');
});

test('the result is shown only after the page has reloaded the saved settings', async () => {
    const page = await bootSettings();
    routeSave(page);
    const reload = deferred();
    page.route('GET /api/settings', () => reload.promise);

    await save(page);
    assert.deepEqual(messages(page), [], 'still reloading');
    assert.equal(page.evaluate('settingsLoaded'), false);

    reload.resolve(settingsResponse({ filename_format: '{series} reloaded' }));
    await page.settle();

    assert.deepEqual(messages(page), [{ type: 'success', text: SAVED_MESSAGE }]);
    assert.equal(page.$('#filenameFormat').value, '{series} reloaded');
});

test('a save that cannot be reloaded says so once, and does not claim a clean success', async () => {
    const page = await bootSettings();
    routeSave(page);
    page.route('GET /api/settings', networkError);

    await save(page);

    assert.deepEqual(messages(page), [{
        type: 'warning',
        text: 'Settings were saved, but the page could not be reloaded: Failed to fetch'
    }]);
});

test('Settings pinned by the environment are disabled with a hint naming the variable', async () => {
    const page = await bootSettings({
        settings: {
            env_locked: [
                { field: 'smtp_host', variable: 'SMTP_HOST' },
                { field: 'smtp_password_set', variable: 'SMTP_PASSWORD' },
                { field: 'smtp_use_ssl', variable: 'SMTP_USE_SSL' }
            ]
        }
    });

    assert.equal(page.$('#smtpHost').disabled, true);
    assert.equal(page.$('#smtpPassword').disabled, true);
    assert.equal(page.$('#smtpClearPasswordCheckbox').disabled, true, 'both inputs of a secret');
    assert.equal(page.$('#smtpUseSslCheckbox').disabled, true);
    assert.equal(page.$('#smtpPort').disabled, false, 'other settings stay editable');
    assert.equal(
        page.$('[data-env-lock-hint="smtp_host"]').textContent,
        'Set by the SMTP_HOST environment variable, so it cannot be changed here.');
    assert.match(page.$('[data-env-lock-hint="smtp_password_set"]').textContent, /SMTP_PASSWORD/);
    assert.equal(page.$$('[data-env-lock-hint="smtp_password_set"]').length, 1, 'one hint per setting');
    // A checkbox's hint goes after its label, not inside it.
    assert.equal(page.$('[data-env-lock-hint="smtp_use_ssl"]').previousElementSibling.getAttribute('for'), 'smtpUseSslCheckbox');
});

test('a pin on a setting whose input is not on the page is ignored', async () => {
    const page = await bootSettings();
    page.$('#smtpHost').closest('.form-group').remove();

    page.route('GET /api/settings', settingsResponse({ env_locked: [{ field: 'smtp_host', variable: 'SMTP_HOST' }] }));
    await page.run('loadSettings()');

    assert.equal(page.$('[data-env-lock-hint="smtp_host"]'), null);
    assert.equal(page.evaluate('settingsEnvLocks.get("smtp_host")'), 'SMTP_HOST');
});

test('a pinned setting stays disabled however often the capabilities are reapplied', async () => {
    const page = await bootSettings({ settings: { env_locked: [{ field: 'smtp_host', variable: 'SMTP_HOST' }] } });

    page.run('applyCapabilities()');

    assert.equal(page.$('#smtpHost').disabled, true);
    assert.equal(page.$('#smtpPort').disabled, false);
});

test('a setting no longer pinned becomes editable again and loses its hint', async () => {
    const page = await bootSettings({ settings: { env_locked: [{ field: 'smtp_host', variable: 'SMTP_HOST' }] } });

    page.route('GET /api/settings', settingsResponse());
    await page.run('loadSettings()');

    assert.equal(page.$('#smtpHost').disabled, false);
    assert.equal(page.$('[data-env-lock-hint="smtp_host"]'), null);
});

test('a pinned setting is left out of the save, and its value is not validated', async () => {
    const page = await bootSettings({
        settings: {
            smtp_port: 99999,
            email_max_attachment_mb: 500,
            series_image_max_bytes: 0,
            error_reporting_max_issues_per_day: 900,
            stale_cleanup_max_removal_percent: 200,
            env_locked: [
                { field: 'smtp_host', variable: 'SMTP_HOST' },
                { field: 'smtp_port', variable: 'SMTP_PORT' },
                { field: 'smtp_password_set', variable: 'SMTP_PASSWORD' },
                { field: 'email_max_attachment_mb', variable: 'EMAIL_MAX_ATTACHMENT_MB' },
                { field: 'series_image_max_bytes', variable: 'SERIES_IMAGE_MAX_BYTES' },
                { field: 'error_reporting_max_issues_per_day', variable: 'ERROR_REPORTING_MAX_ISSUES_PER_DAY' },
                { field: 'stale_cleanup_max_removal_percent', variable: 'STALE_CLEANUP_MAX_REMOVAL_PERCENT' },
                { field: 'enable_suwayomi', variable: 'ENABLE_SUWAYOMI' }
            ]
        }
    });
    routeSave(page);
    page.input('#smtpUsername', 'new-user');

    const since = await save(page);

    const body = saveBody(page, since);
    assert.deepEqual(body.email, {
        smtpUsername: 'new-user',
        smtpUseSsl: false,
        smtpAllowInsecure: true,
        emailFromAddress: 'comics@example.com',
        emailFromName: 'Comic Maintainer'
    });
    assert.equal('maxMB' in body.seriesImageLimits, false);
    assert.equal('maxIssuesPerDay' in body.errorReporting, false);
    assert.equal('staleCleanupMaxRemovalPercent' in body.databaseCleanup, false);
    assert.equal('enabled' in body.suwayomi, false);
    assert.deepEqual(messages(page), [{ type: 'success', text: SAVED_MESSAGE }]);
});

test('the OPDS switch is saved with the rest of the page, unless the environment pins it', async () => {
    const page = await bootSettings({ settings: { opds_enabled: false } });
    routeSave(page);
    assert.equal(page.$('#opdsEnabledCheckbox').checked, false);
    page.input('#opdsEnabledCheckbox', true);

    let since = await save(page);

    assert.deepEqual(saveBody(page, since).opds, { enabled: true });

    const pinned = await bootSettings({ settings: { env_locked: [{ field: 'opds_enabled', variable: 'OPDS_ENABLED' }] } });
    routeSave(pinned);
    since = await save(pinned);

    assert.equal(pinned.$('#opdsEnabledCheckbox').disabled, true);
    assert.deepEqual(saveBody(pinned, since).opds, {});
});

test('a pinned upload-size pair is not cross-checked', async () => {
    const page = await bootSettings({
        settings: {
            series_image_max_bytes: 30 * 1048576,
            series_image_max_download_bytes: 20 * 1048576,
            env_locked: [{ field: 'series_image_max_bytes', variable: 'SERIES_IMAGE_MAX_BYTES' }]
        }
    });
    routeSave(page);

    await save(page);

    assert.deepEqual(messages(page), [{ type: 'success', text: SAVED_MESSAGE }]);
});

test('the pinned download-images toggle reports the server\'s refusal if the page was stale', async () => {
    const page = await bootSettings();
    page.route('PUT /api/settings/download-external-series-images',
        respond(409, { error: 'Set by the environment and cannot be changed here: DOWNLOAD_EXTERNAL_SERIES_IMAGES.' }));
    clearMessages(page);
    const before = page.$('#downloadExternalSeriesImagesCheckbox').checked;

    page.$('#downloadExternalSeriesImagesCheckbox').checked = !before;
    await page.run('updateDownloadExternalSeriesImages()');

    assert.deepEqual(lastMessage(page), {
        type: 'error',
        text: 'Failed to update series image download setting: Set by the environment and cannot be changed here: DOWNLOAD_EXTERNAL_SERIES_IMAGES.'
    });
    assert.equal(page.$('#downloadExternalSeriesImagesCheckbox').checked, before);
});

test('the download-images toggle quotes the reference of an unexpected failure', async () => {
    const page = await bootSettings();
    page.route('PUT /api/settings/download-external-series-images', serverFailure('corr-images'));
    clearMessages(page);

    page.$('#downloadExternalSeriesImagesCheckbox').checked = !page.$('#downloadExternalSeriesImagesCheckbox').checked;
    await page.run('updateDownloadExternalSeriesImages()');

    assert.deepEqual(lastMessage(page), {
        type: 'error',
        text: `Failed to update series image download setting: ${SERVER_FAILURE} (ref: corr-images)`
    });
});

test('values at the server limits are saved', async () => {
    const page = await bootSettings();
    routeSave(page);
    assert.equal(page.$('#logMaxSize').max, '2047', "the input's limit matches the server's");
    assert.equal(page.$('#emailMaxAttachmentMb').max, '200', "the input's limit matches the server's");
    page.input('#logMaxSize', '2047');
    page.input('#emailMaxAttachmentMb', '200');
    page.input('#errorReportingMaxPerDay', '100');
    page.input('#errorReportingDedupeHours', '720');

    const since = await save(page);

    assert.deepEqual(messages(page), [{ type: 'success', text: SAVED_MESSAGE }]);
    const body = saveBody(page, since);
    assert.equal(body.logMaxMB, 2047);
    assert.equal(body.email.emailMaxAttachmentMb, 200);
    assert.equal(body.errorReporting.maxIssuesPerDay, 100);
    assert.equal(body.errorReporting.dedupeWindowHours, 720);
});

test('an expired session while saving redirects to login', async () => {
    const page = await bootSettings();
    routeSave(page, respond(401, { error: 'Unauthorized' }));
    const navigations = page.navigations.length;

    const since = await save(page);

    assert.equal(callsTo(page, SAVE_ROUTE, since).length, 1);
    assert.deepEqual(messages(page), []);
    assert.equal(page.navigations.length, navigations + 1);
    assert.equal(page.navigations.at(-1).url, 'http://localhost/login.html');
    assert.equal(page.window.localStorage.getItem('jwt_token'), null);
});

test('an expired session while reloading after a save shows no result', async () => {
    const page = await bootSettings();
    routeSave(page);
    page.route('GET /api/settings', 401);

    await save(page);

    assert.deepEqual(messages(page), []);
    assert.equal(page.navigations.at(-1).url, 'http://localhost/login.html');
});

test('Save Settings does nothing until the settings have loaded', async () => {
    const page = await bootSettings({ routes: { 'GET /api/settings': respond(500, 'unavailable') } });
    routeSave(page);
    assert.equal(page.$('#saveSettingsBtn').disabled, true);

    const since = page.fetchCalls.length;
    await page.run('saveFilenameFormat()');
    await page.run('resetFilenameFormat()');

    assert.deepEqual(requestLog(page, since), []);
});

test('Reset Filename Format puts the default format the server reports into the input', async () => {
    const page = await bootSettings({
        routes: {
            'GET /api/settings/filename-format': { format: '{series} v{volume} #{issue}', default: '{series} - Chapter {issue}' }
        }
    });
    const route = 'GET /api/settings/filename-format';
    page.input('#filenameFormat', '{series} edited #{issue}');

    clearMessages(page);
    page.click('#resetFilenameFormatBtn');
    await page.settle();
    assert.equal(callsTo(page, route).length, 1);
    assert.match(callsTo(page, route)[0].headers.authorization, /^Bearer /);
    assert.deepEqual(messages(page), [{ type: 'info', text: 'Reset to default format' }]);
    // The built-in default, not the current format; it is saved with "Save Settings".
    assert.equal(page.$('#filenameFormat').value, '{series} - Chapter {issue}');
    assert.equal(page.$('#currentFormat').textContent, '{series} v{volume} #{issue}');
    assert.equal(callsTo(page, 'PUT /api/settings').length, 0);

    // A response without a default leaves the input alone.
    page.input('#filenameFormat', '{series} edited #{issue}');
    page.route(route, { format: '{series} v{volume} #{issue}' });
    page.click('#resetFilenameFormatBtn');
    await page.settle();
    assert.deepEqual(lastMessage(page), { type: 'error', text: 'Failed to reset format: No default format was returned' });
    assert.equal(page.$('#filenameFormat').value, '{series} edited #{issue}');

    page.route(route, respond(500, { error: 'boom' }));
    page.click('#resetFilenameFormatBtn');
    await page.settle();
    assert.deepEqual(lastMessage(page), { type: 'error', text: 'Failed to reset format: boom' });

    page.route(route, serverFailure('corr-format'));
    page.click('#resetFilenameFormatBtn');
    await page.settle();
    assert.deepEqual(lastMessage(page), { type: 'error', text: `Failed to reset format: ${SERVER_FAILURE} (ref: corr-format)` });

    page.route(route, networkError);
    page.click('#resetFilenameFormatBtn');
    await page.settle();
    assert.deepEqual(lastMessage(page), { type: 'error', text: 'Failed to reset format: Failed to fetch' });

    const shown = messages(page).length;
    page.route(route, 401);
    page.click('#resetFilenameFormatBtn');
    await page.settle();
    assert.equal(messages(page).length, shown);
    assert.equal(page.navigations.at(-1).url, 'http://localhost/login.html');
    assert.equal(callsTo(page, route).length, 6);
});

test('Send Test Report reports what the server said', async () => {
    const page = await bootSettings();
    const route = 'POST /api/error-reports/test';
    const button = 'button[onclick="sendErrorReportingTest()"]';
    const cases = [
        [{ message: 'Test report delivered to GitHub', issueNumber: 12 }, { type: 'success', text: 'Test report delivered to GitHub' }],
        [{}, { type: 'success', text: 'Test report sent' }],
        [undefined, { type: 'success', text: 'Test report sent' }],
        [respond(400, { error: 'Error reporting is disabled' }), { type: 'error', text: 'Error reporting is disabled' }],
        [respond(502, 'Bad gateway'), { type: 'error', text: 'Test report failed (HTTP 502)' }],
        [networkError, { type: 'error', text: 'Failed to send test report: Failed to fetch' }]
    ];

    for (const [response, expected] of cases) {
        page.route(route, response);
        clearMessages(page);
        page.click(button);
        await page.settle();
        assert.deepEqual(messages(page), [expected]);
    }
    const calls = callsTo(page, route);
    assert.equal(calls.length, cases.length);
    assert.match(calls[0].headers.authorization, /^Bearer /);

    page.route(route, 401);
    clearMessages(page);
    page.click(button);
    await page.settle();
    assert.deepEqual(messages(page), []);
    assert.equal(page.navigations.at(-1).url, 'http://localhost/login.html');
});

test('the ComicInfo fill setting is saved with the metadata group', async () => {
    const page = await bootSettings();
    routeSave(page);

    page.input('#fillComicInfoFromProvidersCheckbox', false);
    const since = await save(page);

    assert.equal(saveBody(page, since).externalSeriesMetadata.fillComicInfoFromProviders, false);
});

test('a ComicInfo fill setting pinned by the environment is left out of the save', async () => {
    const page = await bootSettings({
        settings: { env_locked: [{ field: 'fill_comicinfo_from_providers', variable: 'FILL_COMICINFO_FROM_PROVIDERS' }] }
    });
    routeSave(page);

    assert.equal(page.$('#fillComicInfoFromProvidersCheckbox').disabled, true);
    const since = await save(page);

    assert.equal(saveBody(page, since).externalSeriesMetadata.fillComicInfoFromProviders, undefined);
});
