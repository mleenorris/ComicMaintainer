'use strict';

// js/app/email.js: server-side builds of condensed books — starting one from
// the plan, following its progress by polling (also with the dialog closed),
// the automatic download when it finishes, builds found on a later visit,
// cancel / dismiss, and the failures of each step. Mock timers drive polling.

const test = require('node:test');
const assert = require('node:assert/strict');
const { deferred } = require('./support/app');
const { respond } = require('./support/dom');
const {
    emailDevice,
    emailStatus,
    condensePlan,
    condenseBuild,
    seriesIssue,
    bootSeriesPage,
    messages,
    recordMessages,
    captureDownloads,
    isModalOpen,
    clickUi,
    clickSeriesAction,
    buttonByText,
    callsTo
} = require('./support/email-helpers');

const MB = 1024 * 1024;
const BUILDS = 'GET /api/email/condense-builds';
const POLL_MS = 2000;

const EMAIL_ROUTES = {
    'GET /api/email/status': emailStatus(),
    'GET /api/email/devices': { devices: [emailDevice()] },
    [BUILDS]: { builds: [] },
    'POST /api/email/condense-plan': condensePlan(),
    'POST /api/client-errors': undefined
};

/** Boots a series page with mock timers and opens Email All Issues. */
async function openDialog(t, { routes = {} } = {}) {
    t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
    const page = await bootSeriesPage({ issues: [seriesIssue()], routes: { ...EMAIL_ROUTES, ...routes } });
    t.after(() => page.close());
    const shown = recordMessages(page);
    await openSend(page);
    return { page, shown };
}

async function openSend(page) {
    clickSeriesAction(page, 'Email All Issues');
    await page.settle();
    assert.ok(isModalOpen(page, 'emailSendModal'));
}

async function closeSend(page) {
    page.click(buttonByText(page, '#emailSendModal .modal-footer', 'Cancel'));
    await page.settle();
}

async function tick(t, page, ms = POLL_MS) {
    t.mock.timers.tick(ms);
    await page.settle();
}

/** Starts condensing and presses Download on the first planned book. */
async function startBuild(page) {
    page.input('#emailCondenseModeSelect', 'count');
    await page.settle();
    clickUi(page, buttonByText(page, '#emailCondenseBookList', 'Download'));
    await page.settle();
}

function polls(page) {
    return callsTo(page, 'GET', '/api/email/condense-builds').length;
}

/** The builds panel: null while hidden, else one entry per build. */
function buildRows(page) {
    const panel = page.$('#emailCondenseBuilds');
    if (panel.hidden) return null;
    const [title, ...rows] = panel.children;
    assert.equal(title.textContent, 'Condensed book builds');
    return rows.map(row => ({
        name: row.querySelector('span').textContent,
        buttons: [...row.querySelectorAll('button')].map(button => button.textContent.trim()),
        progress: row.querySelector('.progress-bar-fill')?.style.width ?? null,
        text: row.lastElementChild.textContent.trim()
    }));
}

function rowButton(page, buildName, label) {
    const row = [...page.$('#emailCondenseBuilds').children]
        .find(element => element.querySelector('span')?.textContent.startsWith(buildName));
    if (!row) throw new Error(`No build row for ${buildName}`);
    return buttonByText(page, row, label);
}

function clientErrorReports(page) {
    return callsTo(page, 'POST', '/api/client-errors').map(call => call.body);
}

function book(body = 'EPUB-BYTES') {
    return () => new Response(body, { headers: { 'Content-Type': 'application/epub+zip' } });
}

test('Download builds the book on the server, follows it and saves it when it is done', async t => {
    const { page, shown } = await openDialog(t, {
        routes: { 'POST /api/email/condense-builds': condenseBuild({ status: 'queued', progress: null }) }
    });
    const downloads = captureDownloads(page);

    await startBuild(page);

    assert.deepEqual(callsTo(page, 'POST', '/api/email/condense-builds').map(call => call.body), [{
        seriesId: 'saga',
        condenseMode: 'count',
        issuesPerBook: 5,
        deliveryFormat: 'epub',
        deviceId: 1,
        skipAlreadyDelivered: true,
        bookIndex: 0
    }]);
    assert.deepEqual(shown.at(-1), {
        text: 'Building the condensed EPUB. Progress is shown in the send dialog; you can close it and come back.',
        type: 'info'
    });
    assert.deepEqual(buildRows(page), [{
        name: 'Saga #1-5 · 5 issues',
        buttons: ['Cancel'],
        progress: '0%',
        text: 'Waiting for another book to finish building...'
    }]);

    const opened = polls(page);
    page.route(BUILDS, { builds: [condenseBuild({ progress: { phase: 'reading', completed_issues: 1, percentage: 20 } })] });
    await tick(t, page, POLL_MS - 1);
    assert.equal(polls(page), opened, 'polls every two seconds');
    await tick(t, page, 1);
    assert.equal(polls(page), opened + 1);
    assert.deepEqual(buildRows(page), [{
        name: 'Saga #1-5 · 5 issues',
        buttons: ['Cancel'],
        progress: '20%',
        text: 'Reading issue 2 of 5...'
    }]);

    // Closing the dialog does not abandon the build.
    await closeSend(page);
    page.route('GET /api/email/condense-builds/build-1/download', book());
    page.route(BUILDS, {
        builds: [condenseBuild({
            status: 'completed',
            completed_at: '2024-03-01T10:05:00Z',
            file_size_bytes: 5 * MB,
            progress: { percentage: 100 }
        })]
    });
    await tick(t, page);

    assert.equal(downloads.length, 1, 'a build started here is saved as soon as it is ready');
    assert.equal(downloads[0].download, 'Saga #1-5.epub');
    assert.ok(downloads[0].connected, 'the link is in the document when clicked');
    assert.equal(await page.objectUrls.get(downloads[0].href).text(), 'EPUB-BYTES');
    assert.equal(page.$$('a[download]').length, 0, 'the temporary link is removed');
    assert.deepEqual(shown.at(-1), { text: 'Condensed EPUB downloaded.', type: 'success' });
    assert.equal(shown.filter(message => message.text.includes('is ready to download')).length, 0);
    const download = callsTo(page, 'GET', '/api/email/condense-builds/build-1/download')[0];
    assert.match(download.headers.authorization, /^Bearer /);

    await openSend(page);
    assert.deepEqual(buildRows(page), [{
        name: 'Saga #1-5 · 5 issues',
        buttons: ['Download', 'Dismiss'],
        progress: null,
        text: 'Ready · 5.0 MB'
    }]);

    // Nothing is left to follow, so polling has stopped.
    const settled = polls(page);
    await tick(t, page);
    assert.equal(polls(page), settled);

    // The object URL outlives the click and is released a minute later.
    await tick(t, page, 60000 - POLL_MS - 1);
    assert.ok(page.objectUrls.has(downloads[0].href));
    await tick(t, page, 1);
    assert.ok(!page.objectUrls.has(downloads[0].href));
});

test('builds found when the dialog opens show their progress, outcome and actions', async t => {
    const found = [
        condenseBuild({ build_id: 'q', status: 'queued', display_name: 'Queued book', issue_count: 1, progress: null }),
        condenseBuild({ build_id: 'r1', display_name: 'Reading book', progress: { phase: 'reading', completed_issues: 1, percentage: 20 } }),
        condenseBuild({
            build_id: 'r2',
            display_name: 'Paged book',
            progress: { completed_pages: 30, total_pages: 120, current_issue: 'Saga 002', pass: 1, total_passes: 2, percentage: 25 }
        }),
        condenseBuild({ build_id: 'r3', display_name: 'Plain book', progress: { completed_pages: 3, total_pages: 10, percentage: 30 } }),
        condenseBuild({ build_id: 'r4', display_name: 'Checked book', progress: { phase: 'validating', pass: 2, total_passes: 2, percentage: 90 } }),
        condenseBuild({ build_id: 'r5', display_name: 'Squeezed book', progress: { phase: 'recompressing', percentage: 95 } }),
        condenseBuild({ build_id: 'r6', display_name: 'Fresh book', progress: { phase: 'queued' } }),
        condenseBuild({ build_id: 'c', status: 'completed', display_name: 'Done book', file_size_bytes: 5 * MB }),
        condenseBuild({ build_id: 'c0', status: 'completed', display_name: 'Empty book' }),
        condenseBuild({ build_id: 'f1', status: 'failed', display_name: 'Broken book', error: 'Corrupt archive' }),
        condenseBuild({ build_id: 'f2', status: 'failed', display_name: 'Mystery book' }),
        condenseBuild({ build_id: 'x', status: 'cancelled', display_name: 'Stopped book' }),
        condenseBuild({ build_id: 'o', status: 'expired', display_name: 'Odd book' })
    ];
    const { page, shown } = await openDialog(t, { routes: { [BUILDS]: { builds: found } } });

    const running = ['Cancel'];
    assert.deepEqual(buildRows(page), [
        { name: 'Queued book · 1 issue', buttons: running, progress: '0%', text: 'Waiting for another book to finish building...' },
        { name: 'Reading book · 5 issues', buttons: running, progress: '20%', text: 'Reading issue 2 of 5...' },
        { name: 'Paged book · 5 issues', buttons: running, progress: '25%', text: 'Page 30 of 120 · Saga 002 · pass 1/2' },
        { name: 'Plain book · 5 issues', buttons: running, progress: '30%', text: 'Page 3 of 10' },
        { name: 'Checked book · 5 issues', buttons: running, progress: '90%', text: 'Checking the finished book... · pass 2/2' },
        { name: 'Squeezed book · 5 issues', buttons: running, progress: '95%', text: 'Too large; rebuilding with stronger compression' },
        { name: 'Fresh book · 5 issues', buttons: running, progress: '0%', text: 'Starting...' },
        { name: 'Done book · 5 issues', buttons: ['Download', 'Dismiss'], progress: null, text: 'Ready · 5.0 MB' },
        { name: 'Empty book · 5 issues', buttons: ['Download', 'Dismiss'], progress: null, text: 'Ready · 0 B' },
        { name: 'Broken book · 5 issues', buttons: ['Dismiss'], progress: null, text: 'Failed: Corrupt archive' },
        { name: 'Mystery book · 5 issues', buttons: ['Dismiss'], progress: null, text: 'Failed: unknown error' },
        { name: 'Stopped book · 5 issues', buttons: ['Dismiss'], progress: null, text: 'Cancelled' },
        { name: 'Odd book · 5 issues', buttons: ['Dismiss'], progress: null, text: 'expired' }
    ]);
    const failedRow = rowButton(page, 'Broken book', 'Dismiss').closest('#emailCondenseBuilds > div');
    assert.match(failedRow.lastElementChild.getAttribute('style'), /color: var\(--text-warning/);

    // Builds from an earlier visit are announced, but never saved unasked.
    assert.deepEqual(shown, [
        { text: '"Done book" is ready to download.', type: 'success' },
        { text: '"Empty book" is ready to download.', type: 'success' },
        { text: 'Building "Broken book" failed: Corrupt archive', type: 'error' },
        { text: 'Building "Mystery book" failed: unknown error', type: 'error' }
    ]);
    assert.equal(callsTo(page, 'GET', '/api/email/condense-builds/c/download').length, 0);

    // Seeing the same builds again announces nothing new.
    await closeSend(page);
    await openSend(page);
    assert.equal(shown.length, 4);

    // Active builds are followed.
    const before = polls(page);
    await tick(t, page);
    assert.equal(polls(page), before + 1);
});

test('a completed AZW3 build found later downloads on request under its own extension', async t => {
    const { page, shown } = await openDialog(t, {
        routes: {
            [BUILDS]: { builds: [condenseBuild({ build_id: 'k', status: 'completed', display_name: 'Saga Omnibus', delivery_format: 'azw3', file_size_bytes: 2 * MB })] },
            'GET /api/email/condense-builds/k/download': book('AZW3-BYTES')
        }
    });
    const downloads = captureDownloads(page);

    clickUi(page, rowButton(page, 'Saga Omnibus', 'Download'));
    await page.settle();

    assert.deepEqual(downloads.map(entry => entry.download), ['Saga Omnibus.azw3']);
    assert.equal(await page.objectUrls.get(downloads[0].href).text(), 'AZW3-BYTES');
    assert.deepEqual(shown.at(-1), { text: 'Condensed AZW3 downloaded.', type: 'success' });
});

test('download failures are shown, and only those without a response are reported', async t => {
    const { page, shown } = await openDialog(t, {
        routes: { [BUILDS]: { builds: [condenseBuild({ build_id: 'b', status: 'completed', display_name: 'Saga', file_size_bytes: MB })] } }
    });
    const downloads = captureDownloads(page);
    const download = 'GET /api/email/condense-builds/b/download';

    page.route(download, respond(409, { error: 'The built book is no longer available; build it again.' }));
    clickUi(page, rowButton(page, 'Saga', 'Download'));
    await page.settle();
    page.route(download, respond(500, 'Internal Server Error'));
    clickUi(page, rowButton(page, 'Saga', 'Download'));
    await page.settle();
    page.route(download, respond(404, { message: 'Build not found' }));
    clickUi(page, rowButton(page, 'Saga', 'Download'));
    await page.settle();
    assert.deepEqual(clientErrorReports(page), [], 'the server already knows about HTTP errors');

    page.route(download, () => { throw new TypeError('Failed to fetch'); });
    clickUi(page, rowButton(page, 'Saga', 'Download'));
    await page.settle();

    assert.deepEqual(shown.slice(-4), [
        { text: 'Failed to download the condensed book: The built book is no longer available; build it again.', type: 'error' },
        { text: 'Failed to download the condensed book: HTTP error! status: 500', type: 'error' },
        { text: 'Failed to download the condensed book: Build not found', type: 'error' },
        { text: 'Failed to download the condensed book: Failed to fetch', type: 'error' }
    ]);
    const reports = clientErrorReports(page);
    assert.equal(reports.length, 1);
    assert.equal(reports[0].source, 'condensed book: download');
    assert.equal(reports[0].message, 'Failed to fetch');
    assert.equal(downloads.length, 0);

    // An expired session goes to the login page instead.
    page.route(download, 401);
    const before = shown.length;
    clickUi(page, rowButton(page, 'Saga', 'Download'));
    await page.settle();
    assert.ok(page.navigations.some(navigation => navigation.url.endsWith('/login.html')));
    assert.equal(shown.length, before);
    assert.equal(clientErrorReports(page).length, 1);
});

test('a build that cannot be started is reported, unless the server answered', async t => {
    const { page, shown } = await openDialog(t, { routes: { 'POST /api/email/condense-builds': {} } });

    await startBuild(page);
    assert.deepEqual(shown.at(-1), {
        text: 'Failed to start the condensed book build: The server did not return a build id.',
        type: 'error'
    });

    page.route('POST /api/email/condense-builds', () => { throw new TypeError('Failed to fetch'); });
    clickUi(page, buttonByText(page, '#emailCondenseBookList', 'Download'));
    await page.settle();
    assert.deepEqual(shown.at(-1), { text: 'Failed to start the condensed book build: Failed to fetch', type: 'error' });

    page.route('POST /api/email/condense-builds', respond(409, { error: 'A build of this book is already running.' }));
    clickUi(page, buttonByText(page, '#emailCondenseBookList', 'Download'));
    await page.settle();
    assert.deepEqual(shown.at(-1), {
        text: 'Failed to start the condensed book build: A build of this book is already running.',
        type: 'error'
    });

    assert.deepEqual(clientErrorReports(page).map(report => [report.source, report.message, report.kind]), [
        ['condensed book: start build', 'The server did not return a build id.', 'Error'],
        ['condensed book: start build', 'Failed to fetch', 'Error']
    ]);
    assert.equal(buildRows(page), null, 'no build is tracked');
    const before = polls(page);
    await tick(t, page);
    assert.equal(polls(page), before, 'nothing to poll');
});

test('a failed build started here is announced and not downloaded', async t => {
    const { page, shown } = await openDialog(t, { routes: { 'POST /api/email/condense-builds': condenseBuild() } });
    const downloads = captureDownloads(page);
    await startBuild(page);

    page.route(BUILDS, { builds: [condenseBuild({ status: 'failed', error: 'Out of disk space' })] });
    await tick(t, page);

    assert.deepEqual(shown.at(-1), { text: 'Building "Saga #1-5" failed: Out of disk space', type: 'error' });
    assert.deepEqual(buildRows(page), [{ name: 'Saga #1-5 · 5 issues', buttons: ['Dismiss'], progress: null, text: 'Failed: Out of disk space' }]);

    // Should the server later report it complete, it is no longer saved unasked.
    page.route(BUILDS, { builds: [condenseBuild({ status: 'completed', file_size_bytes: MB })] });
    await closeSend(page);
    await openSend(page);
    assert.deepEqual(shown.at(-1), { text: '"Saga #1-5" is ready to download.', type: 'success' });
    assert.equal(downloads.length, 0);
});

test('Cancel stops a build at once and Dismiss forgets it', async t => {
    const { page, shown } = await openDialog(t, {
        routes: {
            [BUILDS]: { builds: [condenseBuild({ build_id: 'run/1', progress: { completed_pages: 3, total_pages: 10, percentage: 30 } })] },
            'POST /api/email/condense-builds/run%2F1/cancel': { status: 'cancelling' },
            'DELETE /api/email/condense-builds/run%2F1': undefined
        }
    });
    const opened = polls(page);

    page.route(BUILDS, { builds: [condenseBuild({ build_id: 'run/1', status: 'cancelled' })] });
    clickUi(page, rowButton(page, 'Saga #1-5', 'Cancel'));
    await page.settle();

    assert.equal(callsTo(page, 'POST', '/api/email/condense-builds/run%2F1/cancel').length, 1);
    assert.deepEqual(shown.at(-1), { text: 'Cancelling the condensed book build...', type: 'info' });
    assert.equal(polls(page), opened + 1, 'the outcome is fetched straight away');
    assert.deepEqual(buildRows(page), [{ name: 'Saga #1-5 · 5 issues', buttons: ['Dismiss'], progress: null, text: 'Cancelled' }]);
    await tick(t, page);
    assert.equal(polls(page), opened + 1, 'the pending poll was replaced and nothing is left to follow');

    clickUi(page, rowButton(page, 'Saga #1-5', 'Dismiss'));
    await page.settle();
    assert.equal(callsTo(page, 'DELETE', '/api/email/condense-builds/run%2F1').length, 1);
    assert.equal(buildRows(page), null);
    assert.equal(page.$('#emailCondenseBuilds').childElementCount, 0);
});

test('Cancel during a poll does not start a second one', async t => {
    const { page } = await openDialog(t, {
        routes: {
            [BUILDS]: { builds: [condenseBuild()] },
            'POST /api/email/condense-builds/build-1/cancel': { status: 'cancelling' }
        }
    });
    const opened = polls(page);
    const poll = deferred();
    page.route(BUILDS, () => poll.promise);
    await tick(t, page);
    assert.equal(polls(page), opened + 1);

    clickUi(page, rowButton(page, 'Saga #1-5', 'Cancel'));
    await page.settle();
    assert.equal(callsTo(page, 'POST', '/api/email/condense-builds/build-1/cancel').length, 1);
    assert.equal(polls(page), opened + 1, 'the poll in flight reports the outcome');

    poll.resolve({ builds: [condenseBuild({ status: 'cancelled' })] });
    await page.settle();
    assert.equal(buildRows(page)[0].text, 'Cancelled');
});

test('cancel and dismiss failures leave the build in place', async t => {
    const { page, shown } = await openDialog(t, {
        routes: {
            [BUILDS]: {
                builds: [
                    condenseBuild({ build_id: 'a', display_name: 'Running book' }),
                    condenseBuild({ build_id: 'b', status: 'failed', display_name: 'Failed book', error: 'Corrupt archive' })
                ]
            },
            'POST /api/email/condense-builds/a/cancel': respond(404, { error: 'Build not found' }),
            'DELETE /api/email/condense-builds/b': respond(500, 'Internal Server Error')
        }
    });

    clickUi(page, rowButton(page, 'Running book', 'Cancel'));
    await page.settle();
    clickUi(page, rowButton(page, 'Failed book', 'Dismiss'));
    await page.settle();

    assert.deepEqual(shown.slice(-2), [
        { text: 'Failed to cancel the build: Build not found', type: 'error' },
        { text: 'Failed to remove the build: HTTP error! status: 500', type: 'error' }
    ]);
    assert.deepEqual(buildRows(page).map(row => row.text), ['Starting...', 'Failed: Corrupt archive']);
    assert.deepEqual(clientErrorReports(page), []);
});

test('polling survives failed polls and stops when the server forgets the builds', async t => {
    const { page, shown } = await openDialog(t, { routes: { [BUILDS]: { builds: [condenseBuild()] } } });
    const opened = polls(page);

    page.route(BUILDS, respond(503, { error: 'Restarting' }));
    await tick(t, page);
    assert.equal(polls(page), opened + 1);
    const warning = page.consoleMessages.find(message => message.type === 'warn' && message.args[0] === 'Could not poll condensed book builds');
    assert.ok(warning, 'the failed poll is logged');
    assert.equal(buildRows(page)[0].text, 'Starting...', 'the build is kept');

    page.route(BUILDS, 204);
    await tick(t, page);
    assert.equal(polls(page), opened + 2, 'the next tick polls again');
    assert.equal(buildRows(page), null);

    await tick(t, page);
    assert.equal(polls(page), opened + 2, 'nothing left to follow');
    assert.deepEqual(shown, []);
});

test('pressing Download again for the same book follows the one build the server joined it to', async t => {
    const { page, shown } = await openDialog(t, {
        routes: { 'POST /api/email/condense-builds': condenseBuild({ status: 'queued', progress: null }) }
    });
    const downloads = captureDownloads(page);
    await startBuild(page);

    page.route('POST /api/email/condense-builds', condenseBuild({ progress: { completed_pages: 12, total_pages: 40, percentage: 30 } }));
    clickUi(page, buttonByText(page, '#emailCondenseBookList', 'Download'));
    await page.settle();

    assert.equal(callsTo(page, 'POST', '/api/email/condense-builds').length, 2);
    assert.deepEqual(buildRows(page), [{ name: 'Saga #1-5 · 5 issues', buttons: ['Cancel'], progress: '30%', text: 'Page 12 of 40' }]);

    page.route('GET /api/email/condense-builds/build-1/download', book());
    page.route(BUILDS, { builds: [condenseBuild({ status: 'completed', file_size_bytes: MB })] });
    await tick(t, page);
    assert.equal(downloads.length, 1, 'saved once');
    assert.equal(shown.filter(message => message.text === 'Condensed EPUB downloaded.').length, 1);
});

test('a finished build that reappears as running stays finished', async t => {
    const { page, shown } = await openDialog(t, {
        routes: {
            [BUILDS]: {
                builds: [
                    condenseBuild({ build_id: 'a', display_name: 'First book' }),
                    condenseBuild({ build_id: 'b', display_name: 'Second book' })
                ]
            }
        }
    });

    page.route(BUILDS, {
        builds: [
            condenseBuild({ build_id: 'a', status: 'completed', display_name: 'First book', file_size_bytes: MB }),
            condenseBuild({ build_id: 'b', display_name: 'Second book' })
        ]
    });
    await tick(t, page);
    assert.deepEqual(shown, [{ text: '"First book" is ready to download.', type: 'success' }]);

    // An older snapshot (e.g. from another server instance) still says running.
    page.route(BUILDS, {
        builds: [
            condenseBuild({ build_id: 'a', display_name: 'First book' }),
            condenseBuild({ build_id: 'b', display_name: 'Second book' })
        ]
    });
    await tick(t, page);
    assert.deepEqual(buildRows(page).map(row => [row.name, row.text]), [
        ['First book · 5 issues', 'Ready · 1.0 MB'],
        ['Second book · 5 issues', 'Starting...']
    ]);
    assert.equal(shown.length, 1, 'nothing is announced twice');

    page.route(BUILDS, {
        builds: [
            condenseBuild({ build_id: 'a', status: 'completed', display_name: 'First book', file_size_bytes: MB }),
            condenseBuild({ build_id: 'b', status: 'completed', display_name: 'Second book', file_size_bytes: 2 * MB })
        ]
    });
    await tick(t, page);
    assert.deepEqual(shown.map(message => message.text), ['"First book" is ready to download.', '"Second book" is ready to download.']);
    const finished = polls(page);
    await tick(t, page);
    assert.equal(polls(page), finished, 'polling stops once everything has finished');
});

test('reopening the dialog picks up a build that finished meanwhile and stops polling', async t => {
    const { page, shown } = await openDialog(t, { routes: { [BUILDS]: { builds: [condenseBuild()] } } });
    await closeSend(page);

    page.route(BUILDS, { builds: [condenseBuild({ status: 'completed', file_size_bytes: 3 * MB })] });
    await openSend(page);
    assert.deepEqual(shown, [{ text: '"Saga #1-5" is ready to download.', type: 'success' }]);
    assert.equal(buildRows(page)[0].text, 'Ready · 3.0 MB');

    const reopened = polls(page);
    await tick(t, page);
    assert.equal(polls(page), reopened, 'the poll scheduled for the running build was cancelled');
    assert.deepEqual(messages(page).map(message => message.type), ['success']);
});
