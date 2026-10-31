'use strict';

// Suwayomi on the series page (js/app/series.js): the status probe that
// decides whether the missing-issues banner offers "Download from Suwayomi",
// the download request itself with every way it can end, and the Settings
// page's Test Connection action.

const test = require('node:test');
const assert = require('node:assert/strict');
const { deferred } = require('./support/app');
const { respond } = require('./support/dom');
const {
    bootSeries, seriesCard, issue, issuesBody, issuesPath, messages, lastMessage, callsTo, text
} = require('./support/series-helpers');

const CONNECTED = { enabled: true, configured: true, connected: true, serverName: 'Suwayomi', version: 'v1.1.1', error: null };
const ISSUES = issuesPath('saga');
const DOWNLOAD = 'POST /api/suwayomi/download-missing';
const downloadButton = page => page.$('#seriesDetailPanel .series-detail-missing-banner button');
const statusCalls = page => callsTo(page, '/api/suwayomi/status').length;
const downloadCalls = page => callsTo(page, '/api/suwayomi/download-missing', 'POST');
const chapter = (number, name = `Chapter ${number}`) => ({ id: 100 + number, name, chapterNumber: number, isDownloaded: false });

// A series with #2 and #3 missing, on a page that offers the download.
async function bootMissing(options = {}) {
    return bootSeries({ issues: [issue(1), issue(4)], suwayomi: CONNECTED, ...options });
}

async function download(page) {
    page.click(downloadButton(page));
    await page.settle();
}

test('a reachable Suwayomi server adds a download action to the missing-issues banner', async () => {
    const page = await bootMissing();

    const button = downloadButton(page);
    assert.equal(text(button), 'Download from Suwayomi');
    assert.equal(button.title, 'Ask the configured Suwayomi server to download these issues');
    assert.ok(button.hasAttribute('data-requires-write'), 'hidden from read-only users');
    assert.equal(statusCalls(page), 1);

    // The answer is remembered for the rest of the session.
    page.click('#seriesDetailPanel .series-detail-back');
    await page.settle();
    page.click('#fileList .series-card');
    await page.settle();
    assert.equal(page.window.location.hash, '#/series/saga');
    assert.ok(downloadButton(page));
    assert.equal(statusCalls(page), 1);
});

test('a complete series offers no download even with Suwayomi connected', async () => {
    const page = await bootSeries({ suwayomi: CONNECTED });

    assert.equal(page.$('#seriesDetailPanel .series-detail-missing-banner'), null);
    assert.equal(statusCalls(page), 1);
});

test('no download action is offered when Suwayomi is disabled, unreachable or its status fails', async () => {
    const answers = [
        { enabled: false, configured: true, connected: false },
        { enabled: true, configured: true, connected: false, error: 'Connection refused' },
        respond(500, { error: 'boom' }),
        () => { throw new TypeError('Failed to fetch'); }
    ];
    for (const answer of answers) {
        const page = await bootMissing({ routes: { 'GET /api/suwayomi/status': answer } });
        assert.equal(statusCalls(page), 1);
        assert.ok(page.$('#seriesDetailPanel .series-detail-missing-banner'), 'the banner is still shown');
        assert.equal(downloadButton(page), null);
        assert.deepEqual(messages(page), [], 'the probe is silent');
    }
});

test('a failing status probe is logged as a warning', async () => {
    const page = await bootMissing({ routes: { 'GET /api/suwayomi/status': 503 } });

    const warning = page.consoleMessages.find(entry => entry.type === 'warn' && entry.args[0] === 'Suwayomi status check failed');
    assert.ok(warning);
    assert.equal(warning.args[1].message, 'suwayomi status 503');
});

test('downloading asks Suwayomi for the missing issues of the freshly loaded list', async () => {
    const page = await bootMissing();
    // The list has changed since the page was rendered: #3 has arrived.
    page.route(`GET ${ISSUES}`, issuesBody(seriesCard(), [issue(1), issue(3), issue(5)]));
    page.route(DOWNLOAD, {
        series: { id: 7, title: 'Saga', inLibrary: true, source: 'MangaDex', thumbnailUrl: null },
        queuedChapters: [chapter(2), chapter(4)],
        unmatchedIssueNumbers: [],
        alreadyDownloadedIssueNumbers: []
    });

    await download(page);

    const [refetch] = callsTo(page, ISSUES).slice(1);
    assert.deepEqual(refetch.query, { per_page: '-1', page: '1' });
    const [call] = downloadCalls(page);
    assert.deepEqual(call.body, { seriesTitle: 'Saga', alternateTitles: [], issueNumbers: [2, 4] });
    assert.equal(call.headers['content-type'], 'application/json');
    assert.match(call.headers.authorization, /^Bearer /);
    assert.deepEqual(messages(page), [
        { text: 'Asking Suwayomi for 2 missing issues...', type: 'info' },
        { text: 'Queued 2 chapters on Suwayomi from MangaDex.', type: 'success' }
    ]);
});

test('every distinct title of the series is offered, and a match under another title is named', async () => {
    const series = seriesCard({ title: 'Saga', canonical_title: 'Saga: Deluxe', aliases: ['Saga Comic'] });
    const page = await bootMissing({ series });
    page.route(`GET ${ISSUES}`, issuesBody(
        { ...series, title: ' saga ', aliases: ['SAGA COMIC', ' Saga (2012) ', '', null] },
        [issue(1), issue(3)]
    ));
    page.route(DOWNLOAD, { series: { id: 9, title: 'Saga (2012)' }, queued_chapters: [chapter(2)], unmatched_issue_numbers: [7] });

    await download(page);

    assert.deepEqual(downloadCalls(page)[0].body, {
        seriesTitle: 'Saga',
        alternateTitles: ['Saga: Deluxe', 'SAGA COMIC', 'Saga (2012)'],
        issueNumbers: [2]
    });
    assert.deepEqual(lastMessage(page), {
        text: 'Queued 1 chapter on Suwayomi (listed there as "Saga (2012)"). 1 issue has no matching chapter.',
        type: 'success'
    });
    assert.equal(messages(page)[0].text, 'Asking Suwayomi for 1 missing issue...');
});

test('nothing queued is reported as information, with the unmatched issues counted', async () => {
    const page = await bootMissing();
    page.route(DOWNLOAD, { series: { id: 7, title: 'SAGA' }, queuedChapters: [], unmatchedIssueNumbers: [2, 3] });

    await download(page);
    assert.deepEqual(lastMessage(page), { text: 'Nothing was queued on Suwayomi. 2 issues have no matching chapter.', type: 'info' });

    page.route(DOWNLOAD, {});
    await download(page);
    assert.deepEqual(lastMessage(page), { text: 'Nothing was queued on Suwayomi.', type: 'info' });
});

test('partial source failures are reported without hiding successful downloads', async () => {
    const page = await bootMissing();
    page.route(DOWNLOAD, {
        series: { id: 8, title: 'Saga', inLibrary: false, source: 'Working (EN)' },
        queuedChapters: [chapter(2)],
        unmatchedIssueNumbers: [3],
        discoveryFailures: [{ target: 'Broken (EN)', error: 'site unavailable' }]
    });

    await download(page);

    assert.deepEqual(lastMessage(page), {
        text: 'Queued 1 chapter on Suwayomi from Working (EN). 1 issue has no matching chapter. 1 source check failed. First failure: Broken (EN): site unavailable',
        type: 'success'
    });
});

test('a series that has no missing issues any more asks for nothing', async () => {
    const page = await bootMissing();
    page.route(`GET ${ISSUES}`, issuesBody(seriesCard(), [issue(1), issue(2), issue(3), issue(4)]));

    await download(page);

    assert.deepEqual(messages(page), [{ text: 'This series has no missing issues.', type: 'info' }]);
    assert.deepEqual(downloadCalls(page), []);
});

test('a series without any title asks with an empty title', async () => {
    const series = seriesCard({ title: '', canonical_title: '', aliases: null });
    const page = await bootMissing({ series });
    page.route(`GET ${ISSUES}`, { issues: [issue(1), issue(3)] });
    page.route(DOWNLOAD, { queuedChapters: [chapter(2)] });

    await download(page);

    assert.deepEqual(downloadCalls(page)[0].body, { seriesTitle: '', alternateTitles: [], issueNumbers: [2] });
    assert.deepEqual(lastMessage(page), { text: 'Queued 1 chapter on Suwayomi.', type: 'success' });
});

test('a list response without issues has nothing missing', async () => {
    const page = await bootMissing();
    page.route(`GET ${ISSUES}`, { issues: null });

    await download(page);
    assert.deepEqual(messages(page), [{ text: 'This series has no missing issues.', type: 'info' }]);
});

test('an expired session while loading the issue list goes back to the login page', async () => {
    const page = await bootMissing();
    page.route(`GET ${ISSUES}`, 401);

    await download(page);

    assert.equal(page.navigations.at(-1).url, 'http://localhost/login.html');
    assert.equal(page.window.localStorage.getItem('jwt_token'), null);
    assert.deepEqual(downloadCalls(page), []);
    assert.deepEqual(messages(page), []);
});

test('a failed issue list request is reported and nothing is downloaded', async () => {
    const page = await bootMissing();
    page.route(`GET ${ISSUES}`, respond(500, { error: 'boom' }));

    await download(page);
    assert.deepEqual(messages(page), [{ text: 'Could not load the full issue list (HTTP 500).', type: 'error' }]);

    page.route(`GET ${ISSUES}`, () => { throw new TypeError('Failed to fetch'); });
    await download(page);
    assert.deepEqual(lastMessage(page), { text: 'Could not load the full issue list for Suwayomi.', type: 'error' });
    assert.ok(page.consoleMessages.some(entry => entry.type === 'error'
        && entry.args[0] === 'Could not load the full issue list for Suwayomi'));
    assert.deepEqual(downloadCalls(page), []);
});

test('an expired session while downloading goes back to the login page', async () => {
    const page = await bootMissing();
    page.route(DOWNLOAD, 401);

    await download(page);

    assert.equal(page.navigations.at(-1).url, 'http://localhost/login.html');
    assert.deepEqual(messages(page), [{ text: 'Asking Suwayomi for 2 missing issues...', type: 'info' }]);
});

test('download errors show the server message or the HTTP status', async () => {
    const page = await bootMissing();
    const cases = [
        [respond(400, { error: 'No Suwayomi series matches "Saga".' }), 'Suwayomi download failed: No Suwayomi series matches "Saga".'],
        [respond(502, { message: 'Bad gateway' }), 'Suwayomi download failed: Bad gateway'],
        [respond(500, {}), 'Suwayomi download failed: HTTP error! status: 500'],
        [respond(503, 'Service Unavailable'), 'Suwayomi download failed: HTTP error! status: 503'],
        [504, 'Suwayomi download failed: HTTP error! status: 504']
    ];
    for (const [answer, expected] of cases) {
        page.route(DOWNLOAD, answer);
        await download(page);
        assert.deepEqual(lastMessage(page), { text: expected, type: 'error' });
    }
});

test('a download request that cannot reach the server is reported', async () => {
    const page = await bootMissing();
    page.route(DOWNLOAD, () => { throw new TypeError('Failed to fetch'); });

    await download(page);

    assert.deepEqual(lastMessage(page), { text: 'Suwayomi download failed.', type: 'error' });
    assert.ok(page.consoleMessages.some(entry => entry.type === 'error' && entry.args[0] === 'Suwayomi download failed'));
});

test('the download action is marked write-only for read-only users', async () => {
    const page = await bootMissing({ user: { canModifyLibrary: false, canAdminister: false } });

    assert.ok(page.document.body.classList.contains('user-read-only'));
    assert.ok(downloadButton(page).hasAttribute('data-requires-write'));
});

// ── Settings: Test Connection ───────────────────────────────────────────────

const testConnectionButton = '#settings-suwayomi .form-group button.btn';
const hint = page => page.$('#suwayomiStatusHint').textContent;

// The settings form (and its Test Connection button) is enabled once the
// settings have loaded.
async function openSettings(page) {
    page.route('GET /api/settings', {});
    page.window.location.hash = '#/settings';
    await page.settle();
    assert.equal(page.$(testConnectionButton).disabled, false);
}


test('Test Connection reports the connected server and the series page then offers downloads', async () => {
    const page = await bootSeries({ issues: [issue(1), issue(3)] });
    assert.equal(downloadButton(page), null);
    await openSettings(page);
    assert.equal(hint(page), 'Save your settings before testing.');

    const gate = deferred();
    page.route('GET /api/suwayomi/status', () => gate.promise);
    page.click(testConnectionButton);
    assert.equal(hint(page), 'Testing connection...');

    gate.resolve({ ...CONNECTED, serverName: 'Tachidesk', version: 'v2.0' });
    await page.settle();
    assert.equal(hint(page), 'Connected to Tachidesk v2.0.');
    assert.deepEqual(messages(page), [{ text: 'Connected to Tachidesk v2.0.', type: 'success' }]);
    assert.equal(statusCalls(page), 3, 'the boot probe, the test and a fresh probe for the series page');

    page.window.location.hash = '#/series/saga';
    await page.settle();
    assert.ok(downloadButton(page));
    assert.equal(statusCalls(page), 3);
});

test('Test Connection names the server from either field, or generically', async () => {
    const page = await bootSeries();
    await openSettings(page);
    const cases = [
        [{ ...CONNECTED, server_name: 'Komf', serverName: 'Ignored', version: null }, 'Connected to Komf.'],
        [{ ...CONNECTED, serverName: null, version: '1.0' }, 'Connected to Suwayomi 1.0.']
    ];
    for (const [status, expected] of cases) {
        page.route('GET /api/suwayomi/status', status);
        page.click(testConnectionButton);
        await page.settle();
        assert.equal(hint(page), expected);
        assert.deepEqual(messages(page).at(-1), { text: expected, type: 'success' });
    }
});

test('Test Connection reports a disabled or unreachable server as an error', async () => {
    const page = await bootSeries();
    await openSettings(page);
    const cases = [
        [{ enabled: false, configured: false, connected: false }, 'The Suwayomi integration is disabled.'],
        [{ enabled: true, configured: true, connected: false, error: 'Connection refused (localhost:4567)' }, 'Connection refused (localhost:4567)'],
        [{ enabled: true, configured: true, connected: false, error: null }, 'Could not reach the Suwayomi server.'],
        [respond(500, { error: 'boom' }), 'Could not check the Suwayomi server.'],
        [() => { throw new TypeError('Failed to fetch'); }, 'Could not check the Suwayomi server.']
    ];
    for (const [status, expected] of cases) {
        page.route('GET /api/suwayomi/status', status);
        page.click(testConnectionButton);
        await page.settle();
        assert.equal(hint(page), expected);
        assert.deepEqual(messages(page).at(-1), { text: expected, type: 'error' });
    }
});

test('a server that stops answering removes the download action', async () => {
    const page = await bootMissing();
    assert.ok(downloadButton(page));
    await openSettings(page);

    page.route('GET /api/suwayomi/status', { enabled: true, configured: true, connected: false, error: 'Connection refused' });
    page.click(testConnectionButton);
    await page.settle();
    page.window.location.hash = '#/series/saga';
    await page.settle();

    assert.ok(page.$('#seriesDetailPanel .series-detail-missing-banner'));
    assert.equal(downloadButton(page), null);
});
