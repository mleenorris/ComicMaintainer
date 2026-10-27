'use strict';

// Re-applying cached series images to cover sidecars / first-issue archives
// (js/app/series-metadata.js): the Metadata menu's Update Covers All Series and
// Update Covers Selected Series, the series page's Update Covers action, and
// the background job each of them starts.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const {
    bootSeries, seriesCard, issue, issuesBody, lastMessage, callsTo, text, seriesAction, libraryRefreshes
} = require('./support/series-helpers');

const JOB = '0b9d4c51-7e2a-4f38-9c61-5a8e2d7f3b40';
const EMPTY_JOB = '00000000-0000-0000-0000-000000000000';
const ALL = '/api/series-images/apply-current/all';
const SELECTED = '/api/series-images/apply-current/selected';
const ALL_PROMPT = 'Re-apply the current cached series image to cover sidecars and first-issue archives for ALL series in the library? This may rewrite archive files and can take a while.';

const bearer = page => `Bearer ${page.window.localStorage.getItem('jwt_token')}`;
const progressOpen = page => page.$('#progressModal').classList.contains('active');
const selectedItem = page => page.$('#updateSeriesCoversSelectedItem');

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

// GET /api/jobs/{id} for a cover job that has just started.
const coverJob = total => ({
    job_id: JOB,
    status: 'running',
    total_items: total,
    processed_items: 0,
    failed_items: 0,
    current_file: null,
    results: []
});

test('Update Covers All Series re-applies every cached image as a tracked background job', async () => {
    const page = await bootSeries({
        hash: '#/library',
        routes: {
            [`POST ${ALL}`]: { job_id: JOB, total_items: 3, totalSeries: 5, skippedSeries: 2 },
            [`GET /api/jobs/${JOB}`]: coverJob(3)
        }
    });
    const refreshesBefore = libraryRefreshes(page);

    await choose(page, 'Update Covers All Series');

    assert.deepEqual(page.dialogs.map(dialog => dialog.message), [ALL_PROMPT]);
    const [post] = callsTo(page, ALL, 'POST');
    assert.equal(post.headers['content-type'], 'application/json');
    assert.equal(post.headers.authorization, bearer(page));
    assert.deepEqual(lastMessage(page), {
        text: 'Embedding covers for 3 series in background (2 skipped — no cached image)',
        type: 'info'
    });
    assert.equal(callsTo(page, `/api/jobs/${JOB}`, 'GET').length, 1, 'the job is tracked');
    assert.ok(progressOpen(page));
    assert.equal(text(page.$('#progressTitle')), 'Embedding Covers (All Series)...');
    assert.equal(text(page.$('#progressText')), '0 / 3 files');
    assert.equal(libraryRefreshes(page), refreshesBefore + 1, 'the library is reloaded with the new covers');
});

test('Update Covers in the series actions re-applies the image of that series', async () => {
    const page = await bootSeries({
        routes: {
            [`POST ${SELECTED}`]: { jobId: JOB, total_items: 1, totalSeries: 1 },
            [`GET /api/jobs/${JOB}`]: coverJob(1)
        }
    });

    await action(page, 'Update Covers');

    assert.deepEqual(page.dialogs.map(dialog => dialog.message), [
        'Re-apply the current cached series image to "Saga"? This may rewrite the first-issue archive file.'
    ]);
    const [post] = callsTo(page, SELECTED, 'POST');
    assert.deepEqual(post.body, { series: ['Saga'] });
    assert.equal(post.headers['content-type'], 'application/json');
    assert.equal(post.headers.authorization, bearer(page));
    assert.deepEqual(lastMessage(page), { text: 'Embedding covers for 1 series in background', type: 'info' });
    assert.equal(text(page.$('#progressTitle')), 'Embedding Cover ("Saga")...');
});

test('declining a cover update sends nothing and opens no progress window', async () => {
    const page = await bootSeries({ confirm: false });

    await choose(page, 'Update Covers All Series');
    await action(page, 'Update Covers');
    page.click(selectedItem(page));
    await page.settle();

    assert.equal(page.dialogs.length, 3);
    assert.equal(callsTo(page, ALL).length + callsTo(page, SELECTED).length, 0);
    assert.equal(progressOpen(page), false);

    // Nothing to ask about without a series title.
    await page.run("updateSeriesCoversDirect('')");
    await page.settle();
    assert.equal(page.dialogs.length, 3);
});

test('Update Covers Selected Series targets the open series by its canonical title', async () => {
    const page = await bootSeries({
        series: seriesCard({ title: 'Saga', canonical_title: '  Saga (Image Comics)  ' }),
        routes: { [`POST ${SELECTED}`]: { job_id: EMPTY_JOB, total_items: 0, totalSeries: 1, skippedSeries: 1 } }
    });

    assert.equal(selectedItem(page).disabled, false, 'an open series counts as selected');
    page.click(selectedItem(page));
    await page.settle();

    assert.deepEqual(page.dialogs.map(dialog => dialog.message), [
        'Re-apply the current cached series image to 1 selected series? This may rewrite archive files.'
    ]);
    const [post] = callsTo(page, SELECTED, 'POST');
    assert.deepEqual(post.body, { series: ['Saga (Image Comics)'] });
    assert.equal(post.headers.authorization, bearer(page));
    assert.equal(progressOpen(page), false, 'an empty job closes the progress window');
    assert.deepEqual(lastMessage(page), {
        text: 'No covers embedded: none of the 1 series have a cached image to apply.',
        type: 'info'
    });
    assert.equal(page.fetchCalls.filter(call => call.path.startsWith('/api/jobs/')).length, 0, 'nothing to track');
});

test('Update Covers Selected Series on the library sends each selected title once', async () => {
    const library = [
        seriesCard({ id: 'saga', title: 'Saga', canonical_title: null }),
        seriesCard({ id: 'saga-2012', title: 'Saga (2012)', canonical_title: 'SAGA' }),
        seriesCard({ id: 'untitled', title: '', canonical_title: null }),
        seriesCard({ id: 'blank', title: '   ', canonical_title: '' }),
        seriesCard({ id: 'paper-girls', title: 'Paper Girls', canonical_title: 'Paper Girls' }),
        seriesCard({ id: 'monstress', title: 'Monstress', canonical_title: 'Monstress' })
    ];
    const page = await bootSeries({
        hash: '#/library',
        library,
        routes: {
            [`POST ${SELECTED}`]: { job_id: JOB, total_items: 0, totalSeries: 2, skippedSeries: 0 },
            [/^GET \/api\/files\/series\/[^/]+\/issues\?/]: call => issuesBody(
                library.find(series => call.path.endsWith(`/${encodeURIComponent(series.id)}/issues`)),
                [issue(1)])
        }
    });
    assert.equal(selectedItem(page).disabled, true, 'nothing is selected yet');

    for (const id of ['saga', 'saga-2012', 'untitled', 'blank', 'paper-girls']) {
        page.click(`#fileList .series-select-box[data-series-id="${id}"]`);
        await page.settle();
    }
    assert.deepEqual(
        page.$$('#fileList .series-select-box[aria-checked="true"]').map(box => box.dataset.seriesId),
        ['saga', 'saga-2012', 'untitled', 'blank', 'paper-girls']);
    assert.equal(selectedItem(page).disabled, false);
    page.click(selectedItem(page));
    await page.settle();

    assert.equal(page.dialogs.length, 1);
    assert.match(page.dialogs[0].message, /^Re-apply the current cached series image to 2 selected series/);
    const [post] = callsTo(page, SELECTED, 'POST');
    assert.deepEqual(post.body, { series: ['Saga', 'Paper Girls'] }, 'case-insensitive duplicates and blank titles are dropped');
    assert.deepEqual(lastMessage(page), { text: 'No covers embedded: no series have a cached image to apply.', type: 'info' });
    assert.equal(progressOpen(page), false);
});

test('Update Covers Selected Series needs a selected or open series with a title', async () => {
    const page = await bootSeries({ hash: '#/library' });

    assert.equal(selectedItem(page).disabled, true);
    await page.run('updateSelectedSeriesCovers()');
    await page.settle();
    assert.deepEqual(lastMessage(page), { text: 'Select at least one series from the library view', type: 'error' });

    // A series page whose series is unknown (no title) has nothing to target either.
    page.route('GET /api/files/series/ghost/issues', respond(404, { error: 'Series not found' }));
    page.window.location.hash = '#/series/ghost';
    await page.settle();
    assert.ok(page.$('#seriesDetailPanel'));
    assert.equal(selectedItem(page).disabled, true);
    await page.run('updateSelectedSeriesCovers()');
    await page.settle();
    assert.deepEqual(lastMessage(page), { text: 'Select at least one series from the library view', type: 'error' });

    assert.deepEqual(page.dialogs, []);
    assert.equal(callsTo(page, SELECTED).length, 0);
});

test('an expired session sends every cover update to the login page', async () => {
    const page = await bootSeries({ routes: { [`POST ${ALL}`]: 401, [`POST ${SELECTED}`]: 401 } });

    await choose(page, 'Update Covers All Series');
    assert.equal(page.navigations.at(-1).url, 'http://localhost/login.html');
    assert.equal(page.window.localStorage.getItem('jwt_token'), null);
    assert.equal(progressOpen(page), false);

    const redirects = () => page.navigations.filter(nav => nav.url === 'http://localhost/login.html').length;
    const before = redirects();
    page.click(selectedItem(page));
    await page.settle();
    assert.equal(redirects(), before + 1);
    assert.equal(progressOpen(page), false);

    await action(page, 'Update Covers');
    assert.equal(redirects(), before + 2);
    assert.equal(progressOpen(page), false);
    assert.equal(page.fetchCalls.filter(call => call.path.startsWith('/api/jobs/')).length, 0);
});

test('cover update errors close the progress window and are reported', async () => {
    const page = await bootSeries({
        routes: {
            [`POST ${ALL}`]: respond(500, 'Error updating series covers'),
            [`POST ${SELECTED}`]: respond(400, 'No series specified')
        }
    });

    await choose(page, 'Update Covers All Series');
    assert.deepEqual(lastMessage(page), { text: 'Failed to update series covers: Error updating series covers', type: 'error' });
    assert.equal(progressOpen(page), false);

    page.click(selectedItem(page));
    await page.settle();
    assert.deepEqual(lastMessage(page), { text: 'Failed to update selected series covers: No series specified', type: 'error' });
    assert.equal(progressOpen(page), false);

    await action(page, 'Update Covers');
    assert.deepEqual(lastMessage(page), { text: 'Failed to update series covers: No series specified', type: 'error' });
    assert.equal(progressOpen(page), false);
});

test('cover update network errors close the progress window and are reported', async () => {
    const offline = () => { throw new TypeError('Failed to fetch'); };
    const page = await bootSeries({ routes: { [`POST ${ALL}`]: offline, [`POST ${SELECTED}`]: offline } });

    await choose(page, 'Update Covers All Series');
    assert.deepEqual(lastMessage(page), { text: 'Failed to update series covers', type: 'error' });
    assert.equal(progressOpen(page), false);

    page.click(selectedItem(page));
    await page.settle();
    assert.deepEqual(lastMessage(page), { text: 'Failed to update selected series covers', type: 'error' });

    await action(page, 'Update Covers');
    assert.deepEqual(lastMessage(page), { text: 'Failed to update series covers', type: 'error' });
    assert.equal(progressOpen(page), false);

    const logged = page.consoleMessages.filter(entry => entry.type === 'error').map(entry => entry.args[0]);
    for (const name of ['updateAllSeriesCovers', 'updateSelectedSeriesCovers', 'updateSeriesCoversDirect']) {
        assert.ok(logged.includes(`${name} failed`), name);
    }
});

test('a cover response without a body is treated as nothing to embed', async () => {
    const page = await bootSeries({ routes: { [`POST ${ALL}`]: null } });

    await choose(page, 'Update Covers All Series');

    assert.deepEqual(lastMessage(page), { text: 'No covers embedded: no series have a cached image to apply.', type: 'info' });
    assert.equal(progressOpen(page), false);
});
