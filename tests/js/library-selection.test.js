'use strict';

// Selecting series in the library (js/app/library.js toggleSeriesSelection
// and friends, plus the shared selection status in js/app/file-list.js):
// the select boxes, the selection toolbar, the bulk-action menu state and
// the asynchronous resolution of each series' files.

const test = require('node:test');
const assert = require('node:assert/strict');
const { deferred } = require('./support/app');
const {
    makeSeriesList,
    bootLibrary,
    seriesCalls,
    seriesPages,
    scrollObserver,
    renderedTitles,
    dispatch
} = require('./support/library-helpers');

const ISSUES_PATH = /^\/api\/files\/series\/([^/]+)\/issues$/;
const isIssuesCall = call => call.method === 'GET' && ISSUES_PATH.test(call.path);
const issuesCalls = page => page.fetchCalls.filter(isIssuesCall);

/** Two issue files per series, as GET /api/files/series/{id}/issues lists them. */
function issuesFor(call) {
    const id = ISSUES_PATH.exec(call.path)[1];
    return {
        issues: [1, 2].map(n => ({ file_path: `${id}/${id} #${n}.cbz`, file_name: `${id} #${n}.cbz`, issue: String(n) })),
        issue_count: 2
    };
}

/** Boot the library with `count` series whose issue lists answer through `issues`. */
async function bootSelectable(count = 3, issues = issuesFor, options = {}) {
    const page = await bootLibrary({ series: makeSeriesList(count), ...options });
    page.route(isIssuesCall, issues);
    return page;
}

const box = (page, id) => page.$(`.series-select-box[data-series-id="${id}"]`);
const selectedFiles = page => page.evaluate('[...selectedFiles]').sort();
const toolbar = page => ({
    count: page.$('#seriesSelectionCount').textContent.trim(),
    all: page.$('#seriesSelectAll').checked,
    some: page.$('#seriesSelectAll').indeterminate,
    clearHidden: page.$('#seriesSelectionClearBtn').hidden,
    emailHidden: page.$('#seriesSelectionEmailBtn').hidden,
    emailDisabled: page.$('#seriesSelectionEmailBtn').disabled
});
const bulkState = page => ({
    info: page.$('#selectInfo').textContent,
    batch: !page.$('#batchUpdateBtn').disabled,
    process: !page.$('#processSelectedItem').disabled,
    email: !page.$('#emailSelectedItem').disabled,
    covers: !page.$('#updateSeriesCoversSelectedItem').disabled
});

const pressKey = (page, element, key) => dispatch(page, element, 'keydown', { key });

test('a card\'s select box selects the whole series, fetching all its files, without opening it', async () => {
    const answer = deferred();
    const page = await bootSelectable(3, () => answer.promise);
    assert.deepEqual(bulkState(page), { info: 'No files selected', batch: false, process: false, email: false, covers: false });

    page.click(box(page, 'series-1'));

    // Selected at once; the series' files are still being listed.
    const selectBox = box(page, 'series-1');
    assert.equal(selectBox.getAttribute('aria-checked'), 'true');
    assert.equal(selectBox.textContent, '✓');
    assert.ok(selectBox.classList.contains('series-select-box--checked'));
    assert.ok(selectBox.closest('.series-card').classList.contains('series-card--selected'));
    assert.deepEqual(toolbar(page), {
        count: '1 series selected', all: false, some: true, clearHidden: false, emailHidden: false, emailDisabled: true
    });
    const [call] = issuesCalls(page);
    assert.equal(call.path, '/api/files/series/series-1/issues');
    assert.deepEqual(call.query, { per_page: '-1', filter: 'all' });
    assert.equal(call.init.credentials, 'same-origin');
    assert.match(call.headers.authorization, /^Bearer /);

    answer.resolve(issuesFor(call));
    await page.settle();

    assert.deepEqual(selectedFiles(page), ['series-1/series-1 #1.cbz', 'series-1/series-1 #2.cbz']);
    assert.equal(toolbar(page).emailDisabled, false);
    assert.deepEqual(bulkState(page), { info: '1 series · 2 files selected', batch: true, process: true, email: true, covers: true });
    // Selecting is not opening.
    assert.equal(page.window.location.hash, '#/library');
    assert.equal(page.$('#seriesDetailPanel'), null);
    assert.ok(!box(page, 'series-2').classList.contains('series-select-box--checked'));
});

test('clicking a selected box again deselects the series and releases its files', async () => {
    const page = await bootSelectable();
    page.click(box(page, 'series-1'));
    page.click(box(page, 'series-2'));
    await page.settle();
    assert.equal(page.$('#selectInfo').textContent, '2 series · 4 files selected');

    page.click(box(page, 'series-1'));
    await page.settle();

    assert.deepEqual(selectedFiles(page), ['series-2/series-2 #1.cbz', 'series-2/series-2 #2.cbz']);
    assert.equal(box(page, 'series-1').getAttribute('aria-checked'), 'false');
    assert.equal(box(page, 'series-1').textContent, '');
    assert.ok(!box(page, 'series-1').closest('.series-card').classList.contains('series-card--selected'));
    assert.equal(page.$('#selectInfo').textContent, '1 series · 2 files selected');

    page.click(box(page, 'series-2'));
    await page.settle();
    assert.deepEqual(bulkState(page), { info: 'No files selected', batch: false, process: false, email: false, covers: false });
    assert.deepEqual(toolbar(page), {
        count: 'No series selected', all: false, some: false, clearHidden: true, emailHidden: true, emailDisabled: false
    });
    // Each series' files were listed once.
    assert.equal(issuesCalls(page).length, 2);
});

test('Space and Enter toggle a focused select box; other keys do nothing', async () => {
    const page = await bootSelectable(1);
    const selectBox = box(page, 'series-1');

    const other = pressKey(page, selectBox, 'a');
    await page.settle();
    assert.equal(other.defaultPrevented, false);
    assert.equal(selectBox.getAttribute('aria-checked'), 'false');

    const space = pressKey(page, selectBox, ' ');
    await page.settle();
    assert.equal(space.defaultPrevented, true);
    assert.equal(selectBox.getAttribute('aria-checked'), 'true');
    assert.equal(page.$('#selectInfo').textContent, '1 series · 2 files selected');

    pressKey(page, selectBox, 'Enter');
    await page.settle();
    assert.equal(selectBox.getAttribute('aria-checked'), 'false');
    assert.equal(page.$('#selectInfo').textContent, 'No files selected');
});

test('"Select all series" selects every loaded series and Clear empties the selection', async () => {
    const page = await bootSelectable(3);

    page.input('#seriesSelectAll', true);
    await page.settle();

    assert.equal(issuesCalls(page).length, 3);
    assert.equal(selectedFiles(page).length, 6);
    assert.equal(page.$('#selectInfo').textContent, '3 series · 6 files selected');
    assert.deepEqual(toolbar(page), {
        count: '3 series selected', all: true, some: false, clearHidden: false, emailHidden: false, emailDisabled: false
    });
    assert.equal(page.$$('#fileList .series-card--selected').length, 3);

    // Selecting all again does not re-fetch series already selected.
    page.input('#seriesSelectAll', true);
    await page.settle();
    assert.equal(issuesCalls(page).length, 3);

    page.click('#seriesSelectionClearBtn');
    assert.deepEqual(selectedFiles(page), []);
    assert.equal(page.$$('#fileList .series-card--selected').length, 0);
    assert.deepEqual(toolbar(page), {
        count: 'No series selected', all: false, some: false, clearHidden: true, emailHidden: true, emailDisabled: false
    });
    assert.equal(page.$('#selectInfo').textContent, 'No files selected');
});

test('unticking "Select all series" clears the selection', async () => {
    const page = await bootSelectable(2);
    page.input('#seriesSelectAll', true);
    await page.settle();

    page.input('#seriesSelectAll', false);
    await page.settle();

    assert.equal(page.evaluate('selectedSeries.size'), 0);
    assert.deepEqual(selectedFiles(page), []);
    assert.equal(page.$('#seriesSelectionCount').textContent, 'No series selected');
});

test('a series deselected while its files are loading stays deselected', async () => {
    const answer = deferred();
    const page = await bootSelectable(2, () => answer.promise);

    page.click(box(page, 'series-1'));
    page.click(box(page, 'series-1'));
    answer.resolve(issuesFor(issuesCalls(page)[0]));
    await page.settle();

    assert.equal(page.evaluate('selectedSeries.size'), 0);
    assert.deepEqual(selectedFiles(page), []);
    assert.equal(box(page, 'series-1').getAttribute('aria-checked'), 'false');
    assert.equal(page.$('#selectInfo').textContent, 'No files selected');
});

test('reselecting while an earlier file listing is pending keeps only the latest listing', async () => {
    const answers = [deferred(), deferred()];
    let next = 0;
    const page = await bootSelectable(1, () => answers[next++].promise);

    page.click(box(page, 'series-1'));
    page.click(box(page, 'series-1'));
    page.click(box(page, 'series-1'));
    assert.equal(issuesCalls(page).length, 2);

    answers[0].resolve({ issues: [{ file_path: 'stale.cbz' }] });
    await page.settle();
    assert.deepEqual(selectedFiles(page), []);
    assert.equal(page.$('#seriesSelectionEmailBtn').disabled, true);

    answers[1].resolve({ issues: [{ file_path: 'fresh.cbz' }] });
    await page.settle();
    assert.deepEqual(selectedFiles(page), ['fresh.cbz']);
    assert.equal(page.$('#selectInfo').textContent, '1 series · 1 file selected');
    assert.equal(page.$('#seriesSelectionEmailBtn').disabled, false);
});

test('a series whose files cannot be listed is selected without files', async () => {
    const page = await bootSelectable(3, call => {
        if (call.path.includes('series-1')) return 500;
        if (call.path.includes('series-2')) throw new TypeError('Failed to fetch');
        // Malformed answers contribute no files either.
        return { issues: 'nope' };
    });

    page.input('#seriesSelectAll', true);
    await page.settle();

    assert.equal(page.evaluate('selectedSeries.size'), 3);
    assert.deepEqual(selectedFiles(page), []);
    assert.equal(page.$('#seriesSelectionCount').textContent, '3 series selected');
    assert.equal(page.$('#selectInfo').textContent, 'No files selected');
    const failures = page.consoleMessages.filter(message => message.type === 'error' && message.args[0] === 'fetchSeriesFilePaths failed');
    assert.equal(failures.length, 1);
    assert.match(String(failures[0].args[1]), /Failed to fetch/);
});

test('issues without a usable file path are skipped', async () => {
    const page = await bootSelectable(1, () => ({
        issues: [{ file_path: 'a.cbz' }, { file_path: '' }, { file_path: null }, { file_name: 'b.cbz' }, { file_path: 'c.cbz' }]
    }));

    page.click(box(page, 'series-1'));
    await page.settle();

    assert.deepEqual(selectedFiles(page), ['a.cbz', 'c.cbz']);
});

test('a filter change drops selected series that are no longer listed', async () => {
    const all = makeSeriesList(3);
    const page = await bootSelectable(3, issuesFor, {
        routes: { 'GET /api/files/series': call => seriesPages(call.query.filter === 'unread' ? [all[1], all[2]] : all)(call) }
    });
    page.click(box(page, 'series-1'));
    page.click(box(page, 'series-2'));
    await page.settle();

    page.click('#headerFilterMenu [data-filter="unread"]');
    await page.settle();

    assert.deepEqual(renderedTitles(page), ['Series 002', 'Series 003']);
    assert.deepEqual(page.evaluate('[...selectedSeries]'), ['series-2']);
    assert.deepEqual(selectedFiles(page), ['series-2/series-2 #1.cbz', 'series-2/series-2 #2.cbz']);
    // The remaining selection is rendered as such.
    assert.ok(page.$('#fileList .series-card').classList.contains('series-card--selected'));
    assert.equal(box(page, 'series-2').getAttribute('aria-checked'), 'true');
    assert.equal(box(page, 'series-2').textContent, '✓');
    assert.equal(page.$('#seriesSelectionCount').textContent.trim(), '1 series selected');
    assert.equal(page.$('#seriesSelectionClearBtn').hidden, false);
    assert.equal(page.$('#seriesSelectAll').checked, false);
    assert.equal(page.$('#selectInfo').textContent, '1 series · 2 files selected');
});

test('a series dropped by a filter change while its files are loading adds none of them', async () => {
    const answer = deferred();
    const all = makeSeriesList(2);
    const page = await bootSelectable(2, () => answer.promise, {
        routes: { 'GET /api/files/series': call => seriesPages(call.query.filter === 'unread' ? [all[1]] : all)(call) }
    });

    page.click(box(page, 'series-1'));
    assert.equal(page.$('#seriesSelectionCount').textContent.trim(), '1 series selected');
    page.click('#headerFilterMenu [data-filter="unread"]');
    await page.settle();
    assert.deepEqual(renderedTitles(page), ['Series 002']);
    assert.equal(page.$('#seriesSelectionCount').textContent.trim(), 'No series selected');

    answer.resolve(issuesFor(issuesCalls(page)[0]));
    await page.settle();
    assert.deepEqual(page.evaluate('[...selectedSeries]'), []);
    assert.deepEqual(selectedFiles(page), []);
    assert.equal(page.$('#selectInfo').textContent, 'No files selected');
});

test('a fully selected list is rendered with "Select all series" ticked', async () => {
    const page = await bootSelectable(2);
    page.input('#seriesSelectAll', true);
    await page.settle();

    page.click('#seriesLayoutGridBtn');

    assert.equal(page.$('#seriesSelectAll').checked, true);
    assert.equal(page.$$('#fileList .series-card--titled.series-card--selected').length, 2);
    assert.equal(page.$('#seriesSelectionEmailBtn').hidden, false);
});

test('the selection is kept, and shown, as more series are loaded', async () => {
    const page = await bootSelectable(70);
    page.input('#seriesSelectAll', true);
    await page.settle();
    assert.equal(page.$('#seriesSelectAll').checked, true);

    scrollObserver(page).trigger(true);
    await page.settle();

    assert.equal(seriesCalls(page).at(-1).query.offset, '60');
    assert.equal(page.$$('#fileList .series-card').length, 70);
    assert.equal(page.$$('#fileList .series-card--selected').length, 60);
    assert.deepEqual(toolbar(page), {
        count: '60 series selected', all: false, some: true, clearHidden: false, emailHidden: false, emailDisabled: false
    });
    assert.equal(page.$('#selectInfo').textContent, '60 series · 120 files selected');
});

test('in the list layout selected rows are highlighted', async () => {
    const page = await bootSelectable(2, issuesFor, { localStorage: { seriesLayout: 'list' } });

    page.click(box(page, 'series-2'));
    await page.settle();

    const rows = page.$$('#fileList .series-list-row');
    assert.deepEqual(rows.map(row => row.classList.contains('series-list-row--selected')), [false, true]);

    page.click('#seriesLayoutCompactBtn');
    page.click('#seriesLayoutListBtn');
    assert.deepEqual(page.$$('#fileList .series-list-row').map(row => row.classList.contains('series-list-row--selected')), [false, true]);
    assert.equal(box(page, 'series-2').getAttribute('aria-checked'), 'true');
});

test('files resolved after the list was replaced or a series opened are still recorded', async () => {
    const answers = [deferred(), deferred()];
    let next = 0;
    const page = await bootSelectable(2, () => answers[next++].promise);
    page.route('GET /api/files/series/series-2/issues', issuesFor);

    page.click(box(page, 'series-1'));
    // Change the filter before series-1's files arrive: the list is replaced
    // by a loading state while the new page is requested.
    const reload = deferred();
    page.route('GET /api/files/series', () => reload.promise);
    page.click('#headerFilterMenu [data-filter="unread"]');
    await page.settle();
    assert.equal(page.$('#fileList .loading p').textContent, 'Loading series...');

    answers[0].resolve({ issues: [{ file_path: 'one.cbz' }] });
    await page.settle();
    assert.deepEqual(selectedFiles(page), ['one.cbz']);

    reload.resolve(seriesPages(makeSeriesList(2))({ query: {} }));
    await page.settle();
    assert.equal(box(page, 'series-1').getAttribute('aria-checked'), 'true');
    assert.equal(page.$('#selectInfo').textContent, '1 series · 1 file selected');
});

test('a series page opened while a selection is resolving leaves the page alone', async () => {
    const answer = deferred();
    const page = await bootSelectable(2, issuesFor);
    page.route('GET /api/files/series/series-1/issues', call => (call.query.filter === 'all' ? answer.promise : issuesFor(call)));

    page.click(box(page, 'series-1'));
    page.click('#fileList .series-card');
    await page.settle();
    assert.equal(page.window.location.hash, '#/series/series-1');
    const detailGrid = page.$('#seriesDetailPanel .series-issues-grid');
    assert.ok(detailGrid);

    answer.resolve({ issues: [{ file_path: 'series-1/series-1 #1.cbz' }] });
    await page.settle();

    assert.deepEqual(selectedFiles(page), ['series-1/series-1 #1.cbz']);
    assert.equal(page.$('#seriesDetailPanel .series-issues-grid'), detailGrid);
    // On a series page the status counts files only.
    assert.equal(page.$('#selectInfo').textContent, '1 file selected');
});

test('resetting the series selection leaves the selected files to the caller', async () => {
    const page = await bootSelectable(1);
    page.click(box(page, 'series-1'));
    await page.settle();

    page.run('resetSeriesSelectionState()');

    assert.equal(page.evaluate('selectedSeries.size'), 0);
    assert.equal(page.evaluate('selectedSeriesFilePaths.size'), 0);
    assert.equal(page.evaluate('selectedSeriesPathLoads.size'), 0);
    assert.equal(selectedFiles(page).length, 2);
});

test('deleting the selected series clears the selection on the page at once', async () => {
    const page = await bootSelectable(2, issuesFor, {
        routes: {
            'POST /api/jobs/delete-selected': { job_id: 'delete-job', total_items: 4 },
            'GET /api/jobs/delete-job': { job_id: 'delete-job', status: 'running', total_items: 4, processed_items: 0, failed_items: 0, results: [] }
        }
    });
    page.input('#seriesSelectAll', true);
    await page.settle();
    assert.equal(page.$('#selectInfo').textContent, '2 series · 4 files selected');
    const grid = page.$('#fileList .series-grid');

    page.click('#deleteSelectedBtn');
    await page.settle();

    const [deletion] = page.fetchCalls.filter(call => call.method === 'POST' && call.path === '/api/jobs/delete-selected');
    assert.deepEqual([...deletion.body.Files].sort(), [
        'series-1/series-1 #1.cbz', 'series-1/series-1 #2.cbz', 'series-2/series-2 #1.cbz', 'series-2/series-2 #2.cbz'
    ]);
    // Shown before the job reports anything, and in place: the list is not
    // redrawn, so it keeps its scroll position.
    assert.deepEqual(selectedFiles(page), []);
    assert.deepEqual(bulkState(page), { info: 'No files selected', batch: false, process: false, email: false, covers: false });
    assert.equal(page.$('#deleteSelectedBtn').disabled, true);
    assert.deepEqual(toolbar(page), {
        count: 'No series selected', all: false, some: false, clearHidden: true, emailHidden: true, emailDisabled: false
    });
    assert.deepEqual(['series-1', 'series-2'].map(id => box(page, id).getAttribute('aria-checked')), ['false', 'false']);
    assert.equal(page.$$('#fileList .series-card--selected').length, 0);
    assert.equal(page.$('#fileList .series-grid'), grid);
});

test('read-only users can select series; write-only actions are marked for hiding', async () => {
    const page = await bootSelectable(1, issuesFor, { user: { canModifyLibrary: false, canAdminister: false } });
    assert.ok(page.document.body.classList.contains('user-read-only'));

    page.click(box(page, 'series-1'));
    await page.settle();

    assert.equal(page.$('#seriesSelectionCount').textContent, '1 series selected');
    assert.ok(page.$('#seriesSelectionEmailBtn').hasAttribute('data-requires-write'));
    assert.equal(page.$('#selectInfo').textContent, '1 series · 2 files selected');
});
