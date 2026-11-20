'use strict';

// Shift Issue Numbers (js/app/series.js): the series page's dialog that moves
// the issue numbers of the selected issues, for a run a downloader numbered
// from the wrong start (issue 3 that is #1).

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const h = require('./support/jobs-helpers');

const PATH = '/api/jobs/shift-issues-selected';
const ISSUE_3 = 'Batman/Batman - Chapter 0003.cbz';
const ISSUE_4 = 'Batman/Batman - Chapter 0004.cbz';
const TIE_IN = 'Batman/Batman - Chapter 0001.MU.cbz';

const issues = [
    h.seriesIssue(ISSUE_3, { issue: '3' }),
    h.seriesIssue(ISSUE_4, { issue: '4' }),
    h.seriesIssue(TIE_IN, { issue: '1.MU' })
];

const previewRows = page => page.$$('#shiftIssuesPreview .shift-issues-row')
    .map(row => [row.querySelector('.shift-issues-name').textContent, row.querySelector('.shift-issues-change').textContent]);

async function openWith(page, selected) {
    for (const path of selected) h.selectIssue(page, path);
    page.click([...page.$$('#seriesDetailPanel button')].find(button => button.textContent.includes('Shift Issue Numbers')));
    await page.settle();
}

test('without a selection the dialog does not open', async (t) => {
    const page = await h.bootSeriesDetail(t, { issues });

    page.run('openShiftIssuesModal()');
    await page.settle();

    assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: 'Select the issues to shift first.' }]);
    assert.equal(page.$('#shiftIssuesModal').classList.contains('active'), false);
});

test('the preview shows each selected issue before and after, and Shift waits for a usable shift', async (t) => {
    const page = await h.bootSeriesDetail(t, { issues });
    await openWith(page, [ISSUE_4, ISSUE_3, TIE_IN]);

    assert.equal(page.$('#shiftIssuesModal').classList.contains('active'), true);
    assert.equal(page.$('#shiftIssuesApply').disabled, true);
    assert.deepEqual(previewRows(page).map(row => row[1]), ['#1.MU', '#3', '#4']);

    for (const typed of ['0', 'abc', '1.5', '20000']) {
        page.input('#shiftIssuesOffset', typed);
        assert.equal(page.$('#shiftIssuesApply').disabled, true, `"${typed}" is no shift`);
    }

    page.input('#shiftIssuesOffset', '-2');
    assert.equal(page.$('#shiftIssuesApply').disabled, false);
    assert.deepEqual(previewRows(page), [
        ['Batman - Chapter 0001.MU.cbz', '#1.MU: left as it is'],
        ['Batman - Chapter 0003.cbz', '#3 → #1'],
        ['Batman - Chapter 0004.cbz', '#4 → #2']
    ]);

    // Below 0 everywhere: nothing to shift.
    page.input('#shiftIssuesOffset', '-5');
    assert.equal(page.$('#shiftIssuesApply').disabled, true);

    page.run('closeShiftIssuesModal()');
    assert.equal(page.$('#shiftIssuesModal').classList.contains('active'), false);
});

test('Shift starts the job for the issues it moves, and follows it', async (t) => {
    const page = await h.bootSeriesDetail(t, {
        issues,
        routes: {
            [`POST ${PATH}`]: { job_id: h.JOB_ID, total_items: 2 },
            [`GET /api/jobs/${h.JOB_ID}`]: h.jobStatus({ total_items: 2 })
        }
    });
    // Selected elsewhere, or a tie-in whose number cannot move: left out of the request.
    page.run("selectedFiles.add('Other/Other 001.cbz')");
    await openWith(page, [ISSUE_3, ISSUE_4, TIE_IN]);
    page.input('#shiftIssuesOffset', '-2');

    page.click('#shiftIssuesApply');
    await page.settle();

    const [start] = h.requests(page, 'POST', PATH);
    assert.deepEqual(start.body, { Files: [ISSUE_3, ISSUE_4], IssueOffset: -2 });
    assert.equal(page.$('#shiftIssuesModal').classList.contains('active'), false);
    assert.deepEqual(h.takeMessages(page)[0], { type: 'info', text: 'Started shifting the issue numbers of 2 issue(s)' });
    assert.equal(h.requests(page, 'GET', `/api/jobs/${h.JOB_ID}`).length > 0, true);
});

test('a refused start is reported and the progress view closes', async (t) => {
    const page = await h.bootSeriesDetail(t, {
        issues,
        routes: { [`POST ${PATH}`]: respond(400, { error: 'No issue number shift specified' }) }
    });
    await openWith(page, [ISSUE_3]);
    page.input('#shiftIssuesOffset', '1');

    page.click('#shiftIssuesApply');
    await page.settle();

    assert.deepEqual(h.takeMessages(page).at(-1), { type: 'error', text: 'Failed to start shifting issue numbers: No issue number shift specified' });
    assert.equal(h.progressView(page).open, false);
});
