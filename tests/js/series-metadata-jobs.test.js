'use strict';

// External metadata jobs (js/app/series-metadata.js): the Metadata menu's
// Refresh All / Match All Unmatched, the series page's Refresh Metadata /
// Refresh Folder / Reset Processed Status actions, and the progress toast
// that follows a queued metadata refresh job.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const {
    bootSeries, seriesCard, metadataRecord, lastMessage, callsTo, text, seriesAction, spyOn, libraryRefreshes, inTurn,
    answerConfirm
} = require('./support/series-helpers');

const JOB = '6f1c2b8e-0d4a-4c1e-9a57-3b2d8f4e7a10';
const JOB2 = 'a3e5c7d9-1b2f-4a6c-8e0d-2f4b6d8a0c1e';
const POLL_MS = 1500;
const REFRESH_ALL_PROMPT = 'Queue an external metadata refresh for every series in the library? Lookups happen in the background and progress is reported as a job.';
const MATCH_PROMPT = 'Queue an external metadata lookup for every series that is not yet matched? Lookups happen in the background and progress is reported as a job.';

const jobPath = id => `/api/metadata/refresh/job/${id}`;
const polls = (page, id = JOB) => callsTo(page, jobPath(id), 'GET').length;
const anyPolls = page => page.fetchCalls.filter(call => call.path.startsWith('/api/metadata/refresh/job/')).length;
const bearer = page => `Bearer ${page.window.localStorage.getItem('jwt_token')}`;
const toast = (page, id = JOB) => page.$(`#metadataRefreshToasts > #mdrToast-${id}`);

function menuItem(page, label) {
    const item = page.$$('#metadataDropdown .action-dropdown-item').find(el => text(el) === label);
    if (!item) throw new Error(`No Metadata menu item "${label}"`);
    return item;
}

async function choose(page, label) {
    page.click(menuItem(page, label));
    await page.settle();
}

async function action(page, label) {
    seriesAction(page, label);
    await page.settle();
}

async function advance(t, page, ms = POLL_MS) {
    t.mock.timers.tick(ms);
    await page.settle();
}

function toastState(page, id = JOB) {
    const el = toast(page, id);
    if (!el) return null;
    const status = el.querySelector('.metadata-refresh-toast-status');
    return {
        title: text(el.querySelector('.metadata-refresh-toast-header strong')),
        status: text(status),
        statusClass: [...status.classList].find(name => name.startsWith('metadata-refresh-toast-status--')),
        width: el.querySelector('.metadata-refresh-toast-bar-fill').style.width,
        meta: text(el.querySelector('.metadata-refresh-toast-meta')),
        done: el.classList.contains('metadata-refresh-toast--done')
    };
}

test('the Metadata menu asks before queueing a refresh or a match and does nothing when declined', async () => {
    const page = await bootSeries({ hash: '#/library', confirm: false });

    await choose(page, 'Refresh All External Metadata');
    await choose(page, 'Match All Unmatched Series');

    assert.deepEqual(page.dialogs.map(dialog => [dialog.kind, dialog.message]), [
        ['confirm', REFRESH_ALL_PROMPT],
        ['confirm', MATCH_PROMPT]
    ]);
    assert.equal(callsTo(page, '/api/metadata/refresh-all').length, 0);
    assert.equal(callsTo(page, '/api/metadata/match-unmatched').length, 0);
});

test('Refresh All External Metadata queues a job and follows it in a toast until it completes', async t => {
    t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
    const page = await bootSeries({
        hash: '#/library',
        routes: {
            'POST /api/metadata/refresh-all': { jobId: JOB, totalSeries: 4 },
            [`GET ${jobPath(JOB)}`]: inTurn(
                { jobId: JOB, status: 'running', totalSeries: 4, processedSeries: 1, successes: 1, notFound: 0, failures: 0, currentSeries: 'Paper Girls' },
                { jobId: JOB, status: 'completed', totalSeries: 4, processedSeries: 4, successes: 3, notFound: 0, failures: 1, currentSeries: null })
        }
    });
    const providerHealth = spyOn(page, 'loadProviderHealth');
    const refreshesBefore = libraryRefreshes(page);

    await choose(page, 'Refresh All External Metadata');

    assert.deepEqual(page.dialogs.map(dialog => dialog.message), [REFRESH_ALL_PROMPT]);
    const [post] = callsTo(page, '/api/metadata/refresh-all', 'POST');
    assert.equal(post.headers['content-type'], 'application/json');
    assert.deepEqual(lastMessage(page), { text: 'Metadata refresh job queued for 4 series.', type: 'success' });
    assert.equal(polls(page), 0, 'the first status poll waits for the poll interval');
    assert.equal(toast(page), null);

    await advance(t, page);
    assert.equal(polls(page), 1);
    assert.deepEqual(toastState(page), {
        title: 'Refreshing All Series (4)',
        status: 'running',
        statusClass: 'metadata-refresh-toast-status--running',
        width: '25%',
        meta: '1/4 · ✓ 1 · ✗ 0 · now: Paper Girls',
        done: false
    });
    assert.equal(libraryRefreshes(page), refreshesBefore);
    assert.equal(providerHealth.length, 0);

    await advance(t, page);
    assert.equal(polls(page), 2);
    assert.deepEqual(toastState(page), {
        title: 'Refreshing All Series (4)',
        status: 'completed',
        statusClass: 'metadata-refresh-toast-status--completed',
        width: '100%',
        meta: '4/4 · ✓ 3 · ✗ 1',
        done: true
    });
    assert.equal(page.$$('#metadataRefreshToasts > .metadata-refresh-toast').length, 1, 'the toast is updated in place');
    assert.equal(libraryRefreshes(page), refreshesBefore + 1, 'the library is reloaded once the job is done');
    assert.equal(providerHealth.length, 1, 'and the provider health is refreshed');

    await advance(t, page);
    assert.equal(polls(page), 2, 'a finished job is not polled again');
    assert.ok(toast(page));

    await advance(t, page, 6000 - POLL_MS);
    assert.equal(toast(page), null, 'the finished toast goes away 6 s after the job ends');
});

test('Match All Unmatched Series runs alongside another job, each in its own toast', async t => {
    t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
    const page = await bootSeries({
        hash: '#/library',
        routes: {
            'POST /api/metadata/refresh-all': { jobId: JOB, totalSeries: 2 },
            'POST /api/metadata/match-unmatched': { jobId: JOB2, totalSeries: 3 },
            [`GET ${jobPath(JOB)}`]: { jobId: JOB, status: 'running', totalSeries: 2, processedSeries: 0, successes: 0, failures: 0 },
            // PascalCase payloads are understood as well.
            [`GET ${jobPath(JOB2)}`]: inTurn(
                { JobId: JOB2, Status: 'Running', TotalSeries: 3, ProcessedSeries: 2, Successes: 1, Failures: 1, CurrentSeries: 'Monstress' },
                { jobId: JOB2, status: 'Failed', TotalSeries: 3, ProcessedSeries: 3, Successes: 1, Failures: 2 })
        }
    });
    const refreshesBefore = libraryRefreshes(page);

    await choose(page, 'Refresh All External Metadata');
    await choose(page, 'Match All Unmatched Series');
    assert.deepEqual(page.dialogs.map(dialog => dialog.message), [REFRESH_ALL_PROMPT, MATCH_PROMPT]);
    assert.deepEqual(lastMessage(page), { text: 'Match job queued for 3 unmatched series.', type: 'success' });

    await advance(t, page);
    assert.deepEqual(page.$$('#metadataRefreshToasts > .metadata-refresh-toast').map(el => el.id), [`mdrToast-${JOB}`, `mdrToast-${JOB2}`]);
    assert.equal(toastState(page).meta, '0/2 · ✓ 0 · ✗ 0');
    assert.equal(toastState(page).width, '0%');
    assert.deepEqual(toastState(page, JOB2), {
        title: 'Refreshing Unmatched Series (3)',
        status: 'running',
        statusClass: 'metadata-refresh-toast-status--running',
        width: '67%',
        meta: '2/3 · ✓ 1 · ✗ 1 · now: Monstress',
        done: false
    });

    await advance(t, page);
    assert.deepEqual(toastState(page, JOB2), {
        title: 'Refreshing Unmatched Series (3)',
        status: 'failed',
        statusClass: 'metadata-refresh-toast-status--failed',
        width: '100%',
        meta: '3/3 · ✓ 1 · ✗ 2',
        done: true
    });
    assert.equal(toastState(page).done, false, 'the other job is still running');
    assert.equal(libraryRefreshes(page), refreshesBefore + 1);

    await advance(t, page);
    assert.equal(polls(page, JOB2), 2, 'the failed job is not polled again');
    assert.equal(polls(page), 3, 'the running job keeps being polled');
});

test('a job without progress yet shows an empty toast, keeps polling and is tracked only once', async t => {
    t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
    const page = await bootSeries({
        hash: '#/library',
        routes: {
            'POST /api/metadata/refresh-all': { jobId: JOB, totalSeries: 5 },
            [`GET ${jobPath(JOB)}`]: {}
        }
    });

    // Queued twice, e.g. a double click: the server hands back the same job.
    await choose(page, 'Refresh All External Metadata');
    await choose(page, 'Refresh All External Metadata');
    assert.equal(callsTo(page, '/api/metadata/refresh-all', 'POST').length, 2);

    await advance(t, page);
    assert.equal(polls(page), 1, 'one poller per job');
    assert.deepEqual(toastState(page), {
        title: 'Refreshing All Series (5)',
        status: 'queued',
        statusClass: 'metadata-refresh-toast-status--queued',
        width: '0%',
        meta: '0/0 · ✓ 0 · ✗ 0',
        done: false
    });

    await advance(t, page);
    assert.equal(polls(page), 2);
    assert.equal(page.$$('#metadataRefreshToasts > .metadata-refresh-toast').length, 1);
});

test('polling stops when the job status cannot be fetched or the request fails', async t => {
    t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
    const page = await bootSeries({
        hash: '#/library',
        routes: {
            'POST /api/metadata/refresh-all': { jobId: JOB, totalSeries: 1 },
            'POST /api/metadata/match-unmatched': { jobId: JOB2, totalSeries: 1 },
            [`GET ${jobPath(JOB)}`]: respond(404),
            [`GET ${jobPath(JOB2)}`]: () => { throw new TypeError('Failed to fetch'); }
        }
    });

    await choose(page, 'Refresh All External Metadata');
    await choose(page, 'Match All Unmatched Series');
    await advance(t, page);
    await advance(t, page);
    await advance(t, page);

    assert.equal(polls(page), 1);
    assert.equal(polls(page, JOB2), 1);
    assert.equal(page.$('#metadataRefreshToasts'), null, 'no toast without a job status');
    const warning = page.consoleMessages.find(entry => entry.type === 'warn' && entry.args[0] === 'metadata refresh poll failed');
    assert.ok(warning, 'the failed poll is logged');
    assert.equal(warning.args[1].message, 'Failed to fetch');

    // The job was forgotten, so queueing it again starts a new poller.
    await choose(page, 'Refresh All External Metadata');
    await advance(t, page);
    assert.equal(polls(page), 2);
});

test('queue responses without a job or with nothing to match are reported without a toast', async t => {
    t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
    const page = await bootSeries({
        hash: '#/library',
        routes: {
            'POST /api/metadata/refresh-all': {},
            'POST /api/metadata/match-unmatched': { jobId: null, totalSeries: 0 }
        }
    });

    await choose(page, 'Refresh All External Metadata');
    assert.deepEqual(lastMessage(page), { text: 'Metadata refresh job queued for 0 series.', type: 'success' });

    await choose(page, 'Match All Unmatched Series');
    assert.deepEqual(lastMessage(page), { text: 'No unmatched series to process.', type: 'info' });

    page.route('POST /api/metadata/match-unmatched', { totalSeries: 2 });
    await choose(page, 'Match All Unmatched Series');
    assert.deepEqual(lastMessage(page), { text: 'Match job queued for 2 unmatched series.', type: 'success' });

    await advance(t, page);
    assert.equal(anyPolls(page), 0);
    assert.equal(page.$('#metadataRefreshToasts'), null);
});

test('a refresh-all job without a series count is still tracked, as All Series (0)', async t => {
    t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
    const page = await bootSeries({
        hash: '#/library',
        routes: {
            'POST /api/metadata/refresh-all': { jobId: JOB },
            [`GET ${jobPath(JOB)}`]: { status: 'running', totalSeries: 3, processedSeries: 1, successes: 1, failures: 0 }
        }
    });

    await choose(page, 'Refresh All External Metadata');
    assert.deepEqual(lastMessage(page), { text: 'Metadata refresh job queued for 0 series.', type: 'success' });

    await advance(t, page);
    assert.equal(polls(page), 1);
    const state = toastState(page);
    assert.equal(state.title, 'Refreshing All Series (0)');
    assert.equal(state.status, 'running');
    assert.equal(state.meta, '1/3 · ✓ 1 · ✗ 0');
});

test('Refresh All and Match All Unmatched report server and network errors', async () => {
    const page = await bootSeries({
        hash: '#/library',
        routes: {
            'POST /api/metadata/refresh-all': respond(500, 'Error queuing metadata refresh'),
            'POST /api/metadata/match-unmatched': respond(500, 'Error queuing metadata match')
        }
    });

    await choose(page, 'Refresh All External Metadata');
    assert.deepEqual(lastMessage(page), { text: 'Failed to queue metadata refresh: Error queuing metadata refresh', type: 'error' });
    await choose(page, 'Match All Unmatched Series');
    assert.deepEqual(lastMessage(page), { text: 'Failed to queue match-unmatched: Error queuing metadata match', type: 'error' });

    const offline = () => { throw new TypeError('Failed to fetch'); };
    page.route('POST /api/metadata/refresh-all', offline);
    page.route('POST /api/metadata/match-unmatched', offline);
    await choose(page, 'Refresh All External Metadata');
    assert.deepEqual(lastMessage(page), { text: 'Failed to queue metadata refresh', type: 'error' });
    await choose(page, 'Match All Unmatched Series');
    assert.deepEqual(lastMessage(page), { text: 'Failed to queue match-unmatched', type: 'error' });

    const logged = page.consoleMessages.filter(entry => entry.type === 'error').map(entry => entry.args[0]);
    assert.ok(logged.includes('refreshAllExternalMetadata failed'));
    assert.ok(logged.includes('matchAllUnmatchedSeries failed'));
});

test('Refresh Metadata in the series actions queues a background refresh of that series', async t => {
    t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
    const title = "Fate/Zero: Kiritsugu's War";
    const refreshPath = `/api/metadata/refresh/${encodeURIComponent(title)}`;
    const page = await bootSeries({
        series: seriesCard({ id: 'fate-zero', title, canonical_title: title }),
        routes: {
            [`POST ${refreshPath}`]: respond(202, { jobId: JOB, totalSeries: 1 }),
            [`GET ${jobPath(JOB)}`]: { jobId: JOB, status: 'running', totalSeries: 1, processedSeries: 0, successes: 0, failures: 0, currentSeries: title }
        }
    });

    await action(page, 'Refresh Metadata');

    const [post] = callsTo(page, refreshPath, 'POST');
    assert.deepEqual(post.query, { queue: 'true' }, 'always queued as a background job');
    assert.equal(post.headers.authorization, bearer(page));
    assert.deepEqual(lastMessage(page), { text: `Queued metadata refresh for "${title}"`, type: 'info' });

    await advance(t, page);
    assert.equal(toastState(page).title, `Refreshing ${title}`);
    assert.equal(toastState(page).meta, `0/1 · ✓ 0 · ✗ 0 · now: ${title}`);
});

test('Refresh Metadata reports the outcome when the server refreshes synchronously', async () => {
    const page = await bootSeries();
    const providerHealth = spyOn(page, 'loadProviderHealth');
    const refreshesBefore = libraryRefreshes(page);
    const outcomes = [
        [metadataRecord({ lookup_status: 'not_found' }), { text: 'No external metadata found for "Saga"', type: 'info' }],
        [metadataRecord({ lookup_status: 'error' }), { text: 'External lookup failed for "Saga"', type: 'error' }],
        [metadataRecord({ lookup_status: 'success', source: 'AniList' }), { text: 'Metadata refreshed for "Saga" from AniList', type: 'success' }],
        [metadataRecord(), { text: 'Metadata refreshed for "Saga"', type: 'success' }]
    ];

    for (const [record, message] of outcomes) {
        page.route('POST /api/metadata/refresh/Saga', record);
        await action(page, 'Refresh Metadata');
        assert.deepEqual(lastMessage(page), message);
    }

    assert.equal(callsTo(page, '/api/metadata/refresh/Saga', 'POST').length, 4);
    assert.equal(providerHealth.length, 4, 'the provider health is refreshed after each lookup');
    assert.equal(libraryRefreshes(page), refreshesBefore + 4);
    assert.equal(anyPolls(page), 0);
});

test('Refresh Metadata reports server and network errors', async () => {
    const page = await bootSeries({ routes: { 'POST /api/metadata/refresh/Saga': respond(500, 'Error refreshing metadata') } });

    await action(page, 'Refresh Metadata');
    assert.deepEqual(lastMessage(page), { text: 'Failed to refresh metadata for Saga', type: 'error' });

    page.route('POST /api/metadata/refresh/Saga', () => { throw new TypeError('Failed to fetch'); });
    await action(page, 'Refresh Metadata');
    assert.deepEqual(lastMessage(page), { text: 'Failed to refresh metadata', type: 'error' });
    assert.ok(page.consoleMessages.some(entry => entry.type === 'error' && entry.args[0] === 'refreshSeriesMetadataDirect failed'));
});

test('Refresh Folder queues a refresh of every title grouped under the series', async t => {
    t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
    const page = await bootSeries({
        routes: {
            'POST /api/metadata/refresh/folder': inTurn(
                respond(202, { jobId: JOB, totalSeries: 3, titles: ['Saga', 'Saga (2012)', 'Saga Deluxe'] }),
                respond(202, { jobId: JOB2, totalSeries: 1, titles: ['Saga'] })),
            [`GET ${jobPath(JOB)}`]: { status: 'running', totalSeries: 3, processedSeries: 2, successes: 2, failures: 0 },
            [`GET ${jobPath(JOB2)}`]: { status: 'running', totalSeries: 1, processedSeries: 0, successes: 0, failures: 0 }
        }
    });

    await action(page, 'Refresh Folder');

    const [post] = callsTo(page, '/api/metadata/refresh/folder', 'POST');
    assert.deepEqual(post.body, { seriesId: 'saga' });
    assert.equal(post.headers['content-type'], 'application/json');
    assert.equal(post.headers.authorization, bearer(page));
    assert.deepEqual(lastMessage(page), { text: 'Queued metadata refresh for 3 title(s) in "Saga"', type: 'info' });

    // Without a title the series id stands in for it.
    await page.run("refreshSeriesFolder('saga')");
    await page.settle();
    assert.deepEqual(lastMessage(page), { text: 'Queued metadata refresh for 1 title(s) in "saga"', type: 'info' });

    await advance(t, page);
    assert.equal(toastState(page).title, 'Refreshing Saga');
    assert.equal(toastState(page).meta, '2/3 · ✓ 2 · ✗ 0');
    assert.equal(toastState(page, JOB2).title, 'Refreshing saga');
});

test('Refresh Folder reports an unknown series, server errors and network errors', async () => {
    const page = await bootSeries({ routes: { 'POST /api/metadata/refresh/folder': respond(404, { error: 'Series not found' }) } });

    await action(page, 'Refresh Folder');
    assert.deepEqual(lastMessage(page), { text: 'Series "Saga" not found', type: 'error' });
    await page.run("refreshSeriesFolder('saga')");
    await page.settle();
    assert.deepEqual(lastMessage(page), { text: 'Series "saga" not found', type: 'error' });

    page.route('POST /api/metadata/refresh/folder', respond(500, 'Error queuing metadata refresh'));
    await action(page, 'Refresh Folder');
    assert.deepEqual(lastMessage(page), { text: 'Failed to queue folder metadata refresh', type: 'error' });

    page.route('POST /api/metadata/refresh/folder', () => { throw new TypeError('Failed to fetch'); });
    await action(page, 'Refresh Folder');
    assert.deepEqual(lastMessage(page), { text: 'Failed to queue folder metadata refresh', type: 'error' });
    assert.ok(page.consoleMessages.some(entry => entry.type === 'error' && entry.args[0] === 'refreshSeriesFolder failed'));
});

test('Reset Processed Status clears the flags of the series after confirming', async () => {
    const page = await bootSeries({ routes: { 'POST /api/status/clear-series/saga': { cleared: 2, requested: 3 } } });
    const refreshesBefore = libraryRefreshes(page);

    answerConfirm(page, false);
    await action(page, 'Reset Processed Status');
    assert.deepEqual(page.dialogs.map(dialog => dialog.message), [
        'Reset the renamed/normalized flags on every file in "Saga"?\n\n' +
        'The files will then be re-processed on the next Process / Rename / Normalize run. ' +
        'Use this when a series metadata or filename change refuses to apply.'
    ]);
    assert.equal(callsTo(page, '/api/status/clear-series/saga').length, 0, 'declining sends nothing');

    answerConfirm(page, true);
    await action(page, 'Reset Processed Status');
    const [post] = callsTo(page, '/api/status/clear-series/saga', 'POST');
    assert.equal(post.headers['content-type'], 'application/json');
    assert.equal(post.headers.authorization, bearer(page));
    assert.deepEqual(lastMessage(page), {
        text: 'Cleared processed status on 2 of 3 file(s) in "Saga". Run Process / Rename / Normalize to reapply.',
        type: 'success'
    });
    assert.equal(libraryRefreshes(page), refreshesBefore + 1);
});

test('Reset Processed Status reports an unknown series, server errors and network errors', async () => {
    const page = await bootSeries({ routes: { 'POST /api/status/clear-series/saga': respond(404, { error: 'Series not found' }) } });

    await action(page, 'Reset Processed Status');
    assert.deepEqual(lastMessage(page), { text: 'Series "Saga" not found', type: 'error' });

    // Without a title the series id labels the prompt and the message.
    await page.run("resetSeriesProcessedStatus('saga')");
    await page.settle();
    assert.match(page.dialogs.at(-1).message, /^Reset the renamed\/normalized flags on every file in "saga"\?/);
    assert.deepEqual(lastMessage(page), { text: 'Series "saga" not found', type: 'error' });

    page.route('POST /api/status/clear-series/saga', respond(500, { error: 'boom' }));
    await action(page, 'Reset Processed Status');
    assert.deepEqual(lastMessage(page), { text: 'Failed to reset processed status', type: 'error' });

    page.route('POST /api/status/clear-series/saga', () => { throw new TypeError('Failed to fetch'); });
    await action(page, 'Reset Processed Status');
    assert.deepEqual(lastMessage(page), { text: 'Failed to reset processed status', type: 'error' });
    assert.ok(page.consoleMessages.some(entry => entry.type === 'error' && entry.args[0] === 'resetSeriesProcessedStatus failed'));
});

test('metadata actions without a series or a job do nothing', async t => {
    t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
    const page = await bootSeries();
    const metadataRequests = () => page.fetchCalls.filter(call => /^\/api\/(metadata|status)\//.test(call.path)).length;

    await page.run(`Promise.all([
        refreshSeriesMetadataDirect(''),
        refreshSeriesFolder(''),
        resetSeriesProcessedStatus(''),
        trackMetadataRefreshJob(null, 'Nothing')
    ])`);
    await advance(t, page);

    assert.equal(metadataRequests(), 0);
    assert.deepEqual(page.dialogs, []);
    assert.equal(page.$('#metadataRefreshToasts'), null);
});
