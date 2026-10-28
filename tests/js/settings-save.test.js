'use strict';

// Settings page (js/app/settings.js): "Save Settings" — client-side
// validation, the sequence of per-group save requests and their exact bodies,
// how stored secrets are kept/replaced/cleared, and the failure reported for
// each step; plus "Reset Filename Format" and "Send Test Report".

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const {
    bootSettings, messages, lastMessage, clearMessages, callsTo, requestLog
} = require('./support/settings-helpers');

const networkError = () => {
    throw new TypeError('Failed to fetch');
};

const SAVE_STEPS = {
    format: 'POST /api/settings/filename-format',
    log: 'POST /api/settings/log-max-bytes',
    padding: 'POST /api/settings/issue-number-padding',
    cleanup: 'POST /api/settings/database-cleanup-interval-hours',
    metadata: 'POST /api/settings/external-series-metadata',
    images: 'POST /api/settings/series-image-limits',
    email: 'PUT /api/settings/email',
    emailRateLimit: 'PUT /api/settings/email-rate-limit',
    suwayomi: 'PUT /api/settings/suwayomi',
    errorReporting: 'PUT /api/settings/error-reporting'
};

const SAVED_MESSAGE = 'Settings saved successfully! Changes to log rotation, external metadata, and database cleanup will take effect on restart.';

// How a failure message names each group, in SAVE_STEPS order.
const GROUP_NAMES = [
    'filename format', 'log max size', 'issue number padding', 'database cleanup interval',
    'external metadata settings', 'series image limits', 'email settings', 'email rate limit', 'Suwayomi settings',
    'error reporting settings'
];

// The end of a failure message once `attempted` steps were sent (the last one failed).
function savedBefore(attempted) {
    const saved = GROUP_NAMES.slice(0, attempted - 1);
    return saved.length ? `Only these settings were saved: ${saved.join(', ')}.` : 'No other settings were saved.';
}

// Route every save step to success (as SettingsController answers), then apply
// per-step overrides; routes added later win.
function routeSaveSteps(page, overrides = {}) {
    for (const route of Object.values(SAVE_STEPS)) {
        page.route(route, { message: 'Settings updated successfully' });
    }
    for (const [step, handler] of Object.entries(overrides)) {
        page.route(SAVE_STEPS[step], handler);
    }
}

async function save(page) {
    const since = page.fetchCalls.length;
    clearMessages(page);
    page.click('#saveSettingsBtn');
    await page.settle();
    return since;
}

// Requests made by the save itself (the reload that follows a successful save excluded).
function saveRequests(page, since) {
    const routes = new Set(Object.values(SAVE_STEPS));
    return page.fetchCalls.slice(since)
        .map(call => `${call.method} ${call.path}`)
        .filter(route => routes.has(route));
}

function body(page, step, since = 0) {
    const calls = callsTo(page, SAVE_STEPS[step], since);
    assert.equal(calls.length, 1, `one ${SAVE_STEPS[step]} request`);
    return calls[0].body;
}

test('saving sends each settings group in order with the edited values', async () => {
    const page = await bootSettings();
    routeSaveSteps(page);
    page.input('#filenameFormat', '  {series} #{issue}  ');
    page.input('#logMaxSize', '7.5');
    page.input('#issueNumberPadding', '2');
    page.input('#dbCleanupInterval', '0');
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

    // Hold the Suwayomi step to observe the state between steps.
    const suwayomiGate = deferred();
    page.route(SAVE_STEPS.suwayomi, () => suwayomiGate.promise);
    const since = await save(page);
    assert.equal(page.evaluate('emailStatusCache'), null, 'the cached email status is dropped once SMTP settings change');
    assert.deepEqual(messages(page), []);
    suwayomiGate.resolve({ message: 'Suwayomi settings updated successfully' });
    await page.settle();

    assert.deepEqual(requestLog(page, since), [
        'POST /api/settings/filename-format',
        'POST /api/settings/log-max-bytes',
        'POST /api/settings/issue-number-padding',
        'POST /api/settings/database-cleanup-interval-hours',
        'POST /api/settings/external-series-metadata',
        'POST /api/settings/series-image-limits',
        'PUT /api/settings/email',
        'PUT /api/settings/email-rate-limit',
        'PUT /api/settings/suwayomi',
        'GET /api/suwayomi/status',
        'PUT /api/settings/error-reporting',
        // The saved settings are then reloaded from the server.
        'GET /api/backups',
        'GET /api/settings',
        'GET /api/email/status'
    ]);
    assert.deepEqual(body(page, 'format', since), { format: '{series} #{issue}' });
    assert.deepEqual(body(page, 'log', since), { maxMB: 7.5 });
    assert.deepEqual(body(page, 'padding', since), { padding: 2 });
    assert.deepEqual(body(page, 'cleanup', since), { hours: 0 });
    assert.deepEqual(body(page, 'metadata', since), {
        enabled: true,
        comicVineApiKey: 'new-key',
        comicVineBaseUrl: 'https://comicvine.example/api',
        enableMangaDex: true,
        mangaDexBaseUrl: 'https://api.mangadex.org',
        enableAniList: true,
        aniListBaseUrl: 'https://anilist.example/graphql'
    });
    assert.deepEqual(body(page, 'images', since), { maxMB: 4, maxDownloadMB: 4, maxDimension: 1024 });
    assert.deepEqual(body(page, 'email', since), {
        smtpHost: 'mail.example.org',
        smtpPort: 465,
        smtpUsername: null,
        smtpPassword: 'smtp secret ',
        smtpUseSsl: true,
        smtpAllowInsecure: true,
        emailFromAddress: null,
        emailFromName: 'Comics',
        emailMaxAttachmentMb: null
    });
    // A blank rate-limit field is 0: no limit.
    assert.deepEqual(body(page, 'emailRateLimit', since), { maxSendsPerHour: 12, maxSendsPerDay: 0, minSecondsBetweenSends: 90 });
    // A blank password keeps the stored one (null); blank fields are unset,
    // and a blank import folder is sent empty to clear it.
    assert.deepEqual(body(page, 'suwayomi', since), {
        enabled: true,
        baseUrl: null,
        username: null,
        password: null,
        downloadsDirectory: '/suwayomi/downloads/mangas',
        importDirectory: ''
    });
    // "Clear token" wins over a typed token; blank numbers fall back.
    assert.deepEqual(body(page, 'errorReporting', since), {
        enabled: true,
        gitHubToken: '',
        assignee: null,
        maxIssuesPerDay: 0,
        dedupeWindowHours: 24,
        commentOnRecurrence: true
    });
    for (const call of page.fetchCalls.slice(since).filter(c => c.method !== 'GET')) {
        assert.equal(call.headers['content-type'], 'application/json');
        assert.match(call.headers.authorization, /^Bearer /);
    }

    assert.deepEqual(messages(page), [{ type: 'success', text: SAVED_MESSAGE }]);
    // The reload shows what the server now holds and clears the secret inputs.
    assert.equal(page.$('#filenameFormat').value, '{series} v{volume} #{issue}');
    assert.equal(page.$('#smtpPassword').value, '');
    assert.equal(page.$('#errorReportingClearTokenCheckbox').checked, false);
    assert.equal(page.evaluate('settingsLoaded'), true);
});

test('new secrets are sent, cleared secrets are sent empty', async () => {
    const page = await bootSettings({ settings: { smtp_port: null, email_max_attachment_mb: 12.5 } });
    routeSaveSteps(page);
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

    assert.deepEqual(body(page, 'email', since), {
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
    assert.deepEqual(body(page, 'emailRateLimit', since), { maxSendsPerHour: 20, maxSendsPerDay: 200, minSecondsBetweenSends: 30 });
    assert.deepEqual(body(page, 'suwayomi', since), {
        enabled: false,
        baseUrl: 'http://suwayomi:4567',
        username: 'reader',
        password: 'new-suwayomi-password',
        downloadsDirectory: '/downloads/mangas',
        importDirectory: 'Manga'
    });
    assert.deepEqual(body(page, 'errorReporting', since), {
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
    const metadata = body(page, 'metadata', second);
    assert.equal(metadata.comicVineBaseUrl, 'https://comicvine.gamespot.com/api');
    assert.equal(metadata.aniListBaseUrl, 'https://graphql.anilist.co');
    assert.equal(lastMessage(page).text, SAVED_MESSAGE);
});

test('a blank SMTP host and sender name are unset, and "clear password" clears the Suwayomi password', async () => {
    const page = await bootSettings();
    routeSaveSteps(page);
    page.input('#smtpHost', '   ');
    page.input('#emailFromName', '');
    page.input('#suwayomiPassword', 'typed but cleared');
    page.input('#suwayomiClearPasswordCheckbox', true);

    const since = await save(page);

    assert.deepEqual(body(page, 'email', since), {
        smtpHost: null,
        smtpPort: 2525,
        smtpUsername: 'mailer',
        smtpPassword: null,
        smtpUseSsl: false,
        smtpAllowInsecure: true,
        emailFromAddress: 'comics@example.com',
        emailFromName: null,
        emailMaxAttachmentMb: 30
    });
    assert.deepEqual(body(page, 'suwayomi', since), {
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
    routeSaveSteps(page);

    // Untouched (or only whitespace), the key is sent as null: keep the stored one.
    let since = await save(page);
    assert.deepEqual(body(page, 'metadata', since), {
        enabled: true,
        comicVineApiKey: null,
        comicVineBaseUrl: 'https://comicvine.example/api',
        enableMangaDex: true,
        mangaDexBaseUrl: 'https://mangadex.example',
        enableAniList: false,
        aniListBaseUrl: 'https://anilist.example/graphql'
    });
    page.input('#comicVineApiKey', '   ');
    since = await save(page);
    assert.equal(body(page, 'metadata', since).comicVineApiKey, null);

    page.input('#comicVineApiKey', ' cv-new-key ');
    since = await save(page);
    assert.equal(body(page, 'metadata', since).comicVineApiKey, 'cv-new-key');
    assert.equal(page.$('#comicVineApiKey').value, '', 'the reload empties the input again');

    // "Clear" wins over a typed key.
    page.input('#comicVineApiKey', 'typed but cleared');
    page.input('#comicVineClearApiKeyCheckbox', true);
    since = await save(page);
    assert.equal(body(page, 'metadata', since).comicVineApiKey, '');
    assert.deepEqual(messages(page), [{ type: 'success', text: SAVED_MESSAGE }]);
    assert.equal(page.$('#comicVineClearApiKeyCheckbox').checked, false, 'the tick is reset by the reload');
    assert.equal(page.$('#comicVineApiKey').value, '');
});

test('groups whose controls are missing are not saved', async () => {
    const page = await bootSettings();
    routeSaveSteps(page);
    page.$('#enableSuwayomiCheckbox').remove();
    page.$('#errorReportingEnabledCheckbox').remove();

    const since = await save(page);

    assert.deepEqual(saveRequests(page, since), [
        SAVE_STEPS.format, SAVE_STEPS.log, SAVE_STEPS.padding, SAVE_STEPS.cleanup,
        SAVE_STEPS.metadata, SAVE_STEPS.images, SAVE_STEPS.email, SAVE_STEPS.emailRateLimit
    ]);
    assert.equal(callsTo(page, 'GET /api/suwayomi/status', since).length, 0);
    assert.deepEqual(messages(page), [{ type: 'success', text: SAVED_MESSAGE }]);
});

test('the email rate limit is not saved when its controls are missing', async () => {
    const page = await bootSettings();
    routeSaveSteps(page);
    for (const id of ['emailMaxSendsPerHour', 'emailMaxSendsPerDay', 'emailMinSecondsBetweenSends']) {
        page.$(`#${id}`).closest('.form-group').remove();
    }

    const since = await save(page);

    assert.deepEqual(saveRequests(page, since), Object.values(SAVE_STEPS).filter(step => step !== SAVE_STEPS.emailRateLimit));
    assert.deepEqual(messages(page), [{ type: 'success', text: SAVED_MESSAGE }]);
});

// [description, edits, expected message]
const INVALID_FORMS = [
    ['blank filename format', { filenameFormat: '   ' }, 'Filename format cannot be empty'],
    ['missing log size', { logMaxSize: '' }, 'Log max size must be a positive number'],
    ['zero log size', { logMaxSize: '0' }, 'Log max size must be a positive number'],
    ['missing padding', { issueNumberPadding: '' }, 'Issue number padding must be between 0 and 10'],
    ['negative padding', { issueNumberPadding: '-1' }, 'Issue number padding must be between 0 and 10'],
    ['padding above 10', { issueNumberPadding: '11' }, 'Issue number padding must be between 0 and 10'],
    ['log size above the server limit', { logMaxSize: '2048' }, 'Log max size must be 2047 MB or less'],
    ['missing cleanup interval', { dbCleanupInterval: '' }, 'Database cleanup interval must be 0 or greater'],
    ['negative cleanup interval', { dbCleanupInterval: '-2' }, 'Database cleanup interval must be 0 or greater'],
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
    routeSaveSteps(page);

    for (const [description, edits, expected] of INVALID_FORMS) {
        await t.test(description, async () => {
            await page.run('loadSettings()');
            for (const [id, value] of Object.entries(edits)) page.input(`#${id}`, value);

            const since = await save(page);

            assert.deepEqual(messages(page), [{ type: 'error', text: expected }]);
            assert.deepEqual(saveRequests(page, since), []);
        });
    }
});

// [description, failing step, response, start of the expected message, steps attempted]
// The message then says which groups were saved before the failure (savedBefore).
const FAILED_SAVES = [
    ['filename format rejected', 'format', respond(500, { error: 'Failed to update filename format' }),
        'Failed to save filename format: Failed to update filename format.', 1],
    ['filename format refused with a reason', 'format', { success: false, error: 'Unknown placeholder {foo}' },
        'Failed to save filename format: Unknown placeholder {foo}.', 1],
    ['filename format refused', 'format', { success: false }, 'Failed to save filename format.', 1],
    ['log size rejected', 'log', respond(400, { error: 'MaxMB must be between 1 and 2047' }),
        'Failed to save log max size: MaxMB must be between 1 and 2047.', 2],
    ['log size refused with a reason', 'log', { success: false, error: 'Too large' }, 'Failed to save log max size: Too large.', 2],
    ['log size refused', 'log', { success: false }, 'Failed to save log max size.', 2],
    ['padding rejected', 'padding', 503, 'Failed to save issue number padding: HTTP error! status: 503.', 3],
    ['padding refused with a reason', 'padding', { success: false, error: 'Padding too wide' },
        'Failed to save issue number padding: Padding too wide.', 3],
    ['padding refused', 'padding', { success: false }, 'Failed to save issue number padding.', 3],
    ['cleanup interval rejected', 'cleanup', 500, 'Failed to save database cleanup interval: HTTP error! status: 500.', 4],
    ['cleanup interval failed with a non-JSON body', 'cleanup', respond(504, 'Gateway Timeout'),
        'Failed to save database cleanup interval: HTTP error! status: 504.', 4],
    ['cleanup interval refused with a reason', 'cleanup', { success: false, error: 'Interval too long' },
        'Failed to save database cleanup interval: Interval too long.', 4],
    ['cleanup interval refused', 'cleanup', { success: false }, 'Failed to save database cleanup interval.', 4],
    ['metadata rejected', 'metadata', 500, 'Failed to save external metadata settings: HTTP error! status: 500.', 5],
    ['metadata failed with an HTML error page', 'metadata',
        respond(502, '<html><body><h1>502 Bad Gateway</h1></body></html>', { 'Content-Type': 'text/html' }),
        'Failed to save external metadata settings: HTTP error! status: 502.', 5],
    ['metadata refused with a reason', 'metadata', { success: false, error: 'Bad ComicVine URL' },
        'Failed to save external metadata settings: Bad ComicVine URL.', 5],
    ['metadata refused', 'metadata', { success: false }, 'Failed to save external metadata settings.', 5],
    ['image limits rejected with a reason', 'images',
        respond(400, { error: 'Max download size must be greater than or equal to max stored size' }),
        'Failed to save series image limits: Max download size must be greater than or equal to max stored size.', 6],
    ['image limits rejected without a reason', 'images', respond(400, {}),
        'Failed to save series image limits: HTTP error! status: 400.', 6],
    ['image limits failed with a non-JSON body', 'images', respond(500, 'Internal Server Error'),
        'Failed to save series image limits: HTTP error! status: 500.', 6],
    ['image limits refused with a reason', 'images', { success: false, error: 'Dimension too large' },
        'Failed to save series image limits: Dimension too large.', 6],
    ['image limits refused', 'images', { success: false }, 'Failed to save series image limits.', 6],
    ['email rejected with an error', 'email', respond(400, { error: 'SmtpPort must be between 1 and 65535' }),
        'Failed to save email settings: SmtpPort must be between 1 and 65535.', 7],
    ['email rejected with a message', 'email', respond(500, { message: 'Could not persist settings' }),
        'Failed to save email settings: Could not persist settings.', 7],
    ['email failed with a non-JSON body', 'email', respond(502, 'Bad gateway'),
        'Failed to save email settings: HTTP error! status: 502.', 7],
    ['email rejected without a reason', 'email', respond(500, {}),
        'Failed to save email settings: HTTP error! status: 500.', 7],
    ['email rate limit rejected with an error', 'emailRateLimit',
        respond(400, { error: 'Maximum emails per hour must be between 0 and 10000' }),
        'Failed to save email rate limit: Maximum emails per hour must be between 0 and 10000.', 8],
    ['email rate limit rejected with a message', 'emailRateLimit', respond(500, { message: 'Storage failure' }),
        'Failed to save email rate limit: Storage failure.', 8],
    ['email rate limit failed with a non-JSON body', 'emailRateLimit', respond(502, 'Bad gateway'),
        'Failed to save email rate limit: HTTP error! status: 502.', 8],
    ['Suwayomi rejected with an error', 'suwayomi', respond(400, { error: 'Base URL must be an absolute http(s) URL' }),
        'Failed to save Suwayomi settings: Base URL must be an absolute http(s) URL.', 9],
    ['Suwayomi rejected with a full sentence', 'suwayomi', respond(400, { error: 'A base URL is required when Suwayomi is enabled.' }),
        'Failed to save Suwayomi settings: A base URL is required when Suwayomi is enabled.', 9],
    ['Suwayomi rejected with a message', 'suwayomi', respond(500, { message: 'Storage failure' }),
        'Failed to save Suwayomi settings: Storage failure.', 9],
    ['Suwayomi failed with a non-JSON body', 'suwayomi', respond(502, 'Bad gateway'),
        'Failed to save Suwayomi settings: HTTP error! status: 502.', 9],
    ['error reporting rejected with an error', 'errorReporting', respond(400, { error: 'MaxIssuesPerDay must be between 0 and 100' }),
        'Failed to save error reporting settings: MaxIssuesPerDay must be between 0 and 100.', 10],
    ['error reporting rejected with a message', 'errorReporting', respond(500, { message: 'Storage failure' }),
        'Failed to save error reporting settings: Storage failure.', 10],
    ['error reporting failed with a non-JSON body', 'errorReporting', respond(500, 'oops'),
        'Failed to save error reporting settings: HTTP error! status: 500.', 10],
    ['network failure', 'format', networkError, 'Failed to save filename format: Failed to fetch.', 1],
    ['network failure part-way', 'images', networkError, 'Failed to save series image limits: Failed to fetch.', 6]
];

test('a failed step stops the save and says which group failed and which were saved', async t => {
    const page = await bootSettings();
    const steps = Object.values(SAVE_STEPS);

    for (const [description, step, response, expected, attempted] of FAILED_SAVES) {
        await t.test(description, async () => {
            routeSaveSteps(page, { [step]: response });

            const since = await save(page);

            assert.deepEqual(messages(page), [{ type: 'error', text: `${expected} ${savedBefore(attempted)}` }]);
            assert.deepEqual(saveRequests(page, since), steps.slice(0, attempted));
            assert.equal(callsTo(page, 'GET /api/settings', since).length, 0, 'the form is not reloaded');
        });
    }
});

test('after a partial save the page shows what was saved and keeps the edits for another try', async () => {
    const page = await bootSettings();
    page.input('#filenameFormat', '{series} - {issue}');
    page.input('#issueNumberPadding', '4');
    page.input('#smtpPassword', 'new smtp secret');
    page.input('#emailFromAddress', 'comics@');

    // A rejected format is not shown as the current one.
    routeSaveSteps(page, { format: respond(400, { error: 'Unknown placeholder {foo}' }) });
    await save(page);
    assert.equal(page.$('#currentFormat').textContent, '{series} v{volume} #{issue}');

    // Only the server knows the address is invalid, so the groups before it are saved.
    routeSaveSteps(page, { email: respond(400, { error: "'comics@' is not a valid email address (Parameter 'fromAddress')" }) });
    const since = await save(page);

    assert.deepEqual(messages(page), [{
        type: 'error',
        text: "Failed to save email settings: 'comics@' is not a valid email address (Parameter 'fromAddress'). " +
            'Only these settings were saved: filename format, log max size, issue number padding, ' +
            'database cleanup interval, external metadata settings, series image limits.'
    }]);
    assert.deepEqual(saveRequests(page, since), Object.values(SAVE_STEPS).slice(0, 7));
    assert.equal(page.$('#currentFormat').textContent, '{series} - {issue}', 'the saved format is now the current one');
    assert.equal(callsTo(page, 'GET /api/settings', since).length, 0, 'the form is not reloaded');
    assert.equal(page.$('#filenameFormat').value, '{series} - {issue}');
    assert.equal(page.$('#issueNumberPadding').value, '4');
    assert.equal(page.$('#smtpPassword').value, 'new smtp secret');
    assert.equal(page.$('#emailFromAddress').value, 'comics@');

    // Fixing the address and saving again sends everything, the typed secret included.
    routeSaveSteps(page);
    page.input('#emailFromAddress', 'comics@example.org');
    const retry = await save(page);

    assert.deepEqual(saveRequests(page, retry), Object.values(SAVE_STEPS));
    assert.equal(body(page, 'email', retry).emailFromAddress, 'comics@example.org');
    assert.equal(body(page, 'email', retry).smtpPassword, 'new smtp secret');
    assert.deepEqual(messages(page), [{ type: 'success', text: SAVED_MESSAGE }]);
    assert.equal(callsTo(page, 'GET /api/settings', retry).length, 1, 'the saved settings are reloaded');
});

test('values at the server limits are saved', async () => {
    const page = await bootSettings();
    routeSaveSteps(page);
    assert.equal(page.$('#logMaxSize').max, '2047', "the input's limit matches the server's");
    assert.equal(page.$('#emailMaxAttachmentMb').max, '200', "the input's limit matches the server's");
    page.input('#logMaxSize', '2047');
    page.input('#emailMaxAttachmentMb', '200');
    page.input('#errorReportingMaxPerDay', '100');
    page.input('#errorReportingDedupeHours', '720');

    const since = await save(page);

    assert.deepEqual(messages(page), [{ type: 'success', text: SAVED_MESSAGE }]);
    assert.equal(body(page, 'log', since).maxMB, 2047);
    assert.equal(body(page, 'email', since).emailMaxAttachmentMb, 200);
    assert.equal(body(page, 'errorReporting', since).maxIssuesPerDay, 100);
    assert.equal(body(page, 'errorReporting', since).dedupeWindowHours, 720);
});

test('an expired session while saving any group redirects to login', async t => {
    const page = await bootSettings();
    const token = page.window.localStorage.getItem('jwt_token');
    const steps = Object.values(SAVE_STEPS);

    for (const [index, step] of Object.keys(SAVE_STEPS).entries()) {
        await t.test(step, async () => {
            page.window.localStorage.setItem('jwt_token', token);
            routeSaveSteps(page, { [step]: respond(401, { error: 'Unauthorized' }) });
            const navigations = page.navigations.length;

            const since = await save(page);

            assert.deepEqual(saveRequests(page, since), steps.slice(0, index + 1));
            assert.deepEqual(messages(page), []);
            assert.equal(page.navigations.length, navigations + 1);
            assert.equal(page.navigations.at(-1).url, 'http://localhost/login.html');
            assert.equal(page.window.localStorage.getItem('jwt_token'), null);
        });
    }
});

test('Save Settings does nothing until the settings have loaded', async () => {
    const page = await bootSettings({ routes: { 'GET /api/settings': respond(500, 'unavailable') } });
    routeSaveSteps(page);
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
    assert.equal(callsTo(page, 'POST /api/settings/filename-format').length, 0);

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
    assert.deepEqual(lastMessage(page), { type: 'error', text: 'Failed to reset format: HTTP error! status: 500' });

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
    assert.equal(callsTo(page, route).length, 5);
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
