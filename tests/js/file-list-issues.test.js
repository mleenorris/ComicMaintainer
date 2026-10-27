'use strict';

// js/app/file-list.js: selecting the issues of an open series page — single
// issues, Shift-click ranges (which follow the grid's display order and skip
// missing-issue placeholders), "Select all issues", how the shared selection
// status follows along, and how the selection follows a reload of the page's
// issues (js/app/series.js loadSeriesIssues).

const test = require('node:test');
const assert = require('node:assert/strict');
const { deferred } = require('./support/app');
const {
    makeSeriesList,
    bootLibrary,
    callsTo,
    dispatch
} = require('./support/library-helpers');

const ISSUES_PATH = '/api/files/series/series-1/issues';
const filePath = n => `Saga/Saga #${String(n).padStart(3, '0')}.cbz`;

/** SeriesIssueDto, as GET /api/files/series/{id}/issues lists it. */
function makeIssue(n, overrides = {}) {
    return {
        file_path: filePath(n),
        file_name: filePath(n).split('/').pop(),
        title: `Saga #${n}`,
        issue: String(n),
        volume: null,
        year: 2012,
        size: 1024,
        read: false,
        duplicate: false,
        ...overrides
    };
}

/** GET /api/files/series/{id}/issues listing `issues` (per_page=-1: one page). */
function issuesResponse(issues) {
    return {
        issues,
        issue_count: issues.length,
        page: 1,
        per_page: -1,
        total_pages: 1
    };
}

/**
 * Open series-1, whose issues #1, #2, #4 and #5 are present (#3 is shown as
 * a missing-issue placeholder between #2 and #4). `server.issues` can be
 * replaced to change what the next issues request returns.
 */
async function bootSeriesPage(issues = [1, 2, 4, 5].map(n => makeIssue(n))) {
    const server = { issues };
    const page = await bootLibrary({
        hash: '#/series/series-1',
        series: makeSeriesList(2),
        routes: {
            [`GET ${ISSUES_PATH}`]: () => issuesResponse(server.issues)
        }
    });
    return { page, server };
}

const card = (page, n) => page.$$('#seriesDetailPanel .series-issue-card[data-file-path]')
    .find(element => element.dataset.filePath === filePath(n));
const checkbox = (page, n) => card(page, n).querySelector('.series-issue-select input[type="checkbox"]');
const selected = page => page.evaluate('[...selectedFiles].sort()');
const selectedCards = page => page.$$('#seriesDetailPanel .series-issue-card--selected').map(element => element.dataset.filePath);
const checkedBoxes = page => page.$$('#seriesDetailPanel .series-issue-select input[type="checkbox"]')
    .filter(element => element.checked)
    .map(element => element.closest('.series-issue-card').dataset.filePath);
const selectAllState = page => ({ checked: page.$('#selectAll').checked, indeterminate: page.$('#selectAll').indeterminate });

/**
 * Tick or untick issue `n` the way a browser delivers it: the press on the
 * checkbox's label (mousedown, or keydown for the keyboard) carries the Shift
 * state, the click that follows toggles the box and fires its change event.
 */
function toggleIssue(page, n, { shift = false, keyboard = false } = {}) {
    const box = checkbox(page, n);
    if (keyboard) {
        dispatch(page, box, 'keydown', { key: ' ', shiftKey: shift });
    } else {
        dispatch(page, box.closest('.series-issue-select'), 'mousedown', { shiftKey: shift });
    }
    page.click(box);
}

// The watcher reports `filename` processed, which reloads the open series' issues.
function fileProcessed(page, filename) {
    page.eventSources.at(-1).emit('message', { type: 'file_processed', data: { filename, success: true } });
}

test('ticking an issue selects its file and marks its card; unticking releases it', async () => {
    const { page } = await bootSeriesPage();
    assert.equal(page.$('#seriesDetailPanel .series-issue-card--missing').getAttribute('aria-label'), 'Missing issue #3');
    assert.equal(page.$('#selectInfo').textContent, 'No files selected');

    toggleIssue(page, 2);
    assert.deepEqual(selected(page), [filePath(2)]);
    assert.deepEqual(selectedCards(page), [filePath(2)]);
    assert.equal(page.$('#selectInfo').textContent, '1 file selected');
    assert.equal(page.$('#batchUpdateBtn').disabled, false);
    assert.equal(page.$('#processSelectedItem').disabled, false);
    assert.deepEqual(selectAllState(page), { checked: false, indeterminate: true });

    toggleIssue(page, 2);
    assert.deepEqual(selected(page), []);
    assert.deepEqual(selectedCards(page), []);
    assert.equal(page.$('#selectInfo').textContent, 'No files selected');
    assert.equal(page.$('#batchUpdateBtn').disabled, true);
    assert.deepEqual(selectAllState(page), { checked: false, indeterminate: false });
});

test('a Shift-click selects every issue between the last one clicked and this one, skipping placeholders', async () => {
    const { page } = await bootSeriesPage();
    toggleIssue(page, 2);

    // Shift-clicking extends the page's text selection in a browser; the
    // range selection clears it again.
    page.window.getSelection().selectAllChildren(page.document.body);
    assert.equal(page.window.getSelection().rangeCount, 1);

    toggleIssue(page, 5, { shift: true });

    const range = [filePath(2), filePath(4), filePath(5)];
    assert.deepEqual(selected(page), range);
    assert.deepEqual(selectedCards(page), range);
    assert.deepEqual(checkedBoxes(page), range, 'boxes in the range are ticked along with the clicked one');
    assert.equal(page.$('#selectInfo').textContent, '3 files selected');
    assert.deepEqual(selectAllState(page), { checked: false, indeterminate: true });
    assert.equal(page.window.getSelection().rangeCount, 0);
});

test('a range can be made upwards, and a Shift-click on a ticked issue unticks the range', async () => {
    const { page } = await bootSeriesPage();
    toggleIssue(page, 5);
    toggleIssue(page, 1, { shift: true });

    const all = [1, 2, 4, 5].map(filePath);
    assert.deepEqual(selected(page), all);
    assert.deepEqual(checkedBoxes(page), all);
    assert.deepEqual(selectAllState(page), { checked: true, indeterminate: false });

    // The anchor is now #1, so unticking #4 with Shift releases #1 to #4.
    toggleIssue(page, 4, { shift: true });
    assert.deepEqual(selected(page), [filePath(5)]);
    assert.deepEqual(selectedCards(page), [filePath(5)]);
    assert.deepEqual(checkedBoxes(page), [filePath(5)]);
    assert.equal(page.$('#selectInfo').textContent, '1 file selected');
    assert.deepEqual(selectAllState(page), { checked: false, indeterminate: true });
});

test('Shift held while ticking from the keyboard selects a range too', async () => {
    const { page } = await bootSeriesPage();
    toggleIssue(page, 1, { keyboard: true });
    toggleIssue(page, 4, { keyboard: true, shift: true });

    assert.deepEqual(selected(page), [filePath(1), filePath(2), filePath(4)]);
    assert.deepEqual(checkedBoxes(page), [filePath(1), filePath(2), filePath(4)]);
});

test('ranges work in browsers without the Selection API too', async () => {
    const { page } = await bootSeriesPage();
    page.run('window.getSelection = undefined');

    toggleIssue(page, 1);
    toggleIssue(page, 2, { shift: true });

    assert.deepEqual(selected(page), [filePath(1), filePath(2)]);
    assert.deepEqual(page.errors, []);
});

test('Shift-clicking with no earlier click, or on the last clicked issue, toggles just that issue', async () => {
    const { page } = await bootSeriesPage();

    toggleIssue(page, 4, { shift: true });
    assert.deepEqual(selected(page), [filePath(4)], 'no anchor yet: a plain toggle');

    toggleIssue(page, 4, { shift: true });
    assert.deepEqual(selected(page), [], 'the anchor itself: a plain toggle');

    // Each click still sets the anchor for the next Shift-click.
    toggleIssue(page, 2, { shift: true });
    assert.deepEqual(selected(page), [filePath(2), filePath(4)]);
});

test('a plain click after a Shift-press that never ticked a box is not a range', async () => {
    const { page } = await bootSeriesPage();
    toggleIssue(page, 1);

    // Shift pressed on #2's label without a toggle (e.g. the press was
    // dragged off), then a plain click on #5: the stale Shift state from
    // #2 is replaced by the plain press on #5.
    dispatch(page, checkbox(page, 2).closest('.series-issue-select'), 'mousedown', { shiftKey: true });
    toggleIssue(page, 5);

    assert.deepEqual(selected(page), [filePath(1), filePath(5)]);
});

test('when the last clicked issue was renamed away, a Shift-click toggles just the clicked issue', async () => {
    const { page, server } = await bootSeriesPage();
    toggleIssue(page, 2);

    // #2 is renamed on the server; the processed-file event reloads the page.
    server.issues = [makeIssue(1), makeIssue(2, { file_path: 'Saga/Saga 002 (2012).cbz' }), makeIssue(4), makeIssue(5)];
    page.eventSources.at(-1).emit('message', { type: 'file_processed', data: { filename: filePath(2), success: true } });
    await page.settle();
    assert.equal(callsTo(page, ISSUES_PATH).length, 2);
    assert.ok(page.$('#seriesDetailPanel .series-issue-card[data-file-path="Saga/Saga 002 (2012).cbz"]'));

    toggleIssue(page, 5, { shift: true });

    assert.ok(selected(page).includes(filePath(5)));
    assert.equal(selected(page).includes(filePath(4)), false, 'no range without a rendered anchor');
    assert.deepEqual(selectedCards(page), [filePath(5)]);
});

test('"Select all issues" ticks every issue of the series and unticking it clears them', async () => {
    const { page } = await bootSeriesPage();
    toggleIssue(page, 2);
    assert.equal(page.$('#selectAll').indeterminate, true);

    page.input('#selectAll', true);
    const all = [1, 2, 4, 5].map(filePath);
    assert.deepEqual(selected(page), all);
    assert.deepEqual(selectedCards(page), all);
    assert.deepEqual(checkedBoxes(page), all);
    assert.deepEqual(selectAllState(page), { checked: true, indeterminate: false });
    assert.equal(page.$('#selectInfo').textContent, '4 files selected');

    page.input('#selectAll', false);
    assert.deepEqual(selected(page), []);
    assert.deepEqual(checkedBoxes(page), []);
    assert.deepEqual(selectAllState(page), { checked: false, indeterminate: false });
    assert.equal(page.$('#selectInfo').textContent, 'No files selected');
});

// Regression tests for #849: when the page's issues reload, the selection
// keeps only the issues the reloaded list still has.
test('a reload unselects the ticked issues it no longer lists and keeps the rest', async () => {
    const { page, server } = await bootSeriesPage();
    toggleIssue(page, 2);
    toggleIssue(page, 4);
    toggleIssue(page, 5);
    assert.equal(page.$('#selectInfo').textContent, '3 files selected');

    // Elsewhere #2 is renamed and #5 deleted; the processed-file event
    // reloads the page.
    const renamed = 'Saga/Saga 002 (2012).cbz';
    server.issues = [makeIssue(1), makeIssue(2, { file_path: renamed }), makeIssue(4)];
    fileProcessed(page, renamed);
    await page.settle();
    assert.equal(callsTo(page, ISSUES_PATH).length, 2);

    assert.deepEqual(selected(page), [filePath(4)]);
    assert.deepEqual(checkedBoxes(page), [filePath(4)]);
    assert.equal(page.$('#selectInfo').textContent, '1 file selected');
    assert.deepEqual(selectAllState(page), { checked: false, indeterminate: true });

    // "Select all issues" brings back nothing that is gone, and unticking it
    // leaves nothing behind.
    page.input('#selectAll', true);
    assert.deepEqual(selected(page), [filePath(1), renamed, filePath(4)].sort());
    assert.equal(page.$('#selectInfo').textContent, '3 files selected');
    page.input('#selectAll', false);
    assert.deepEqual(selected(page), []);
    assert.equal(page.$('#selectInfo').textContent, 'No files selected');
    assert.equal(page.$('#batchUpdateBtn').disabled, true);
});

test('a reload leaves the files selected in another series alone', async () => {
    const { page, server } = await bootSeriesPage();
    const other = 'Other/Other #001.cbz';
    page.route('GET /api/files/series/series-2/issues',
        issuesResponse([makeIssue(1, { file_path: other, file_name: 'Other #001.cbz' })]));
    page.window.location.hash = '#/series/series-2';
    await page.settle();
    page.click(page.$(`#seriesDetailPanel .series-issue-card[data-file-path="${other}"] .series-issue-select input`));
    page.window.location.hash = '#/series/series-1';
    await page.settle();
    toggleIssue(page, 4);
    assert.deepEqual(selected(page), [other, filePath(4)].sort());

    // #4 is deleted elsewhere.
    server.issues = [1, 2, 5].map(n => makeIssue(n));
    fileProcessed(page, filePath(5));
    await page.settle();

    assert.deepEqual(selected(page), [other]);
    assert.equal(page.$('#selectInfo').textContent, '1 file selected');
});

test('a reload that finishes after the series page was left still corrects the selection', async () => {
    const { page } = await bootSeriesPage();
    toggleIssue(page, 2);
    toggleIssue(page, 5);
    const reload = deferred();
    page.route(`GET ${ISSUES_PATH}`, () => reload.promise);
    fileProcessed(page, filePath(5));
    await page.settle();

    page.click('#seriesDetailPanel .series-detail-back');
    await page.settle();
    assert.equal(page.window.location.hash, '#/library');
    assert.equal(page.$('#selectInfo').textContent, '2 files selected');

    // #5 has been deleted.
    reload.resolve(issuesResponse([1, 2, 4].map(n => makeIssue(n))));
    await page.settle();
    assert.deepEqual(selected(page), [filePath(2)]);
    assert.equal(page.$('#selectInfo').textContent, '1 file selected');
});

test('a reload with another filter than the list it replaces unselects nothing that filter hides', async () => {
    // The page's first request goes out before the saved "unmarked" filter
    // is applied; that filter hides #4 and #5, which are still there.
    const server = { unmarked: [1, 2] };
    const page = await bootLibrary({
        hash: '#/series/series-1',
        series: makeSeriesList(2),
        prefs: { filterMode: 'unmarked' },
        routes: {
            [`GET ${ISSUES_PATH}`]: call => issuesResponse(
                (call.query.filter === 'unmarked' ? server.unmarked : [1, 2, 4, 5]).map(n => makeIssue(n)))
        }
    });
    assert.equal(callsTo(page, ISSUES_PATH)[0].query.filter, undefined);
    toggleIssue(page, 4);
    toggleIssue(page, 5);

    fileProcessed(page, filePath(1));
    await page.settle();
    assert.equal(callsTo(page, ISSUES_PATH).at(-1).query.filter, 'unmarked');
    assert.deepEqual(selected(page), [filePath(4), filePath(5)]);
    assert.equal(page.$('#selectInfo').textContent, '2 files selected');

    // Under the filter of the list it replaces, a reload does unselect the
    // issues it no longer lists.
    toggleIssue(page, 2);
    server.unmarked = [1];
    fileProcessed(page, filePath(2));
    await page.settle();
    assert.deepEqual(selected(page), [filePath(4), filePath(5)]);
});

test('overlapping reloads unselect by the newest list, whichever answer arrives last', async () => {
    const { page } = await bootSeriesPage();
    toggleIssue(page, 2);
    toggleIssue(page, 5);
    const first = deferred();
    page.route(`GET ${ISSUES_PATH}`, () => first.promise);
    fileProcessed(page, filePath(2));
    await page.settle();

    // Another change reloads the page before the first answer arrives; by
    // now #5 has been deleted.
    page.route(`GET ${ISSUES_PATH}`, issuesResponse([1, 2, 4].map(n => makeIssue(n))));
    fileProcessed(page, filePath(5));
    await page.settle();
    assert.equal(callsTo(page, ISSUES_PATH).length, 3);
    assert.deepEqual(selected(page), [filePath(2)]);
    assert.equal(page.$('#selectInfo').textContent, '1 file selected');

    // The older answer, taken while #2 was being moved, unselects nothing.
    first.resolve(issuesResponse([1, 4, 5].map(n => makeIssue(n))));
    await page.settle();
    assert.deepEqual(selected(page), [filePath(2)]);
    assert.deepEqual(checkedBoxes(page), [filePath(2)]);
    assert.equal(page.$('#selectInfo').textContent, '1 file selected');
});
