'use strict';

// js/app/file-actions.js and series.js: re-checking duplicates and comparing
// a duplicate with the file that has its name. Re-check All (in the Duplicate
// Review) and the series page's Re-check Duplicates start
// POST /api/jobs/recheck-duplicates-{all,selected}; when the job ends its
// summary (GET /api/jobs/{id}/duplicate-recheck) is shown as one message.
// Compare (GET /api/duplicates/compare) shows both files side by side, with
// Keep Both and Swap (POST /api/duplicates/swap).

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const {
    bootPage,
    bootSeriesPage,
    seriesIssue,
    messageTexts,
    isModalOpen,
    pressEscape,
    buttonByText,
    clickIssueAction,
    clickSeriesAction,
    answerConfirms,
    callsTo
} = require('./support/email-helpers');

const A = '/comics/Saga/Saga 1.cbz';
const B = '/comics/Saga/Saga 2.cbz';
const PROPER = '/comics/Saga/Saga - Chapter 0001.cbz';
const ASIDE = '/comics/Saga/Saga - Chapter 0001 (duplicate).cbz';
const COMPARE_A = `GET /api/duplicates/compare?filePath=${encodeURIComponent(A)}`;
const RECHECK_ALL = 'POST /api/jobs/recheck-duplicates-all';
const RECHECK_SELECTED = 'POST /api/jobs/recheck-duplicates-selected';
const SWAP = 'POST /api/duplicates/swap';
// Noon UTC, so the local date is 2020-01-15 in any test time zone.
const OLD = Date.UTC(2020, 0, 15, 12) / 1000;

// A FileDto of GET /api/files?filter=duplicates.
function duplicateFile(path, name) {
    return { relative_path: path, name, size: 1024, modified: OLD, processed: false, renamed: false, normalized: true, duplicate: true, read: false };
}

function filesPage(files) {
    return { files, page: 1, total_pages: 1, total_files: files.length, unmarked_count: 0 };
}

// One side of GET /api/duplicates/compare.
function side(overrides = {}) {
    return {
        file_path: A,
        name: 'Saga 1.cbz',
        size: 2048,
        modified: OLD,
        page_count: 22,
        duplicate: true,
        processed_name: 'Saga - Chapter 0001.cbz',
        tags: { series: 'Saga', number: '1', volume: '1', volume_year: 2012, format: null, title: 'Chapter 1', year: 2012 },
        ...overrides
    };
}

const CLASH = {
    status: 'clashes',
    reason: null,
    file: side(),
    other: side({
        file_path: PROPER,
        name: 'Saga - Chapter 0001.cbz',
        page_count: null,
        duplicate: false,
        processed_name: 'Saga Annual - Chapter 0001.cbz',
        tags: { series: 'Saga Annual', number: '1', volume: '', volume_year: null, format: 'Annual', title: 'Chapter 1', year: 2012 }
    })
};

function job(id, status = 'completed') {
    return { job_id: id, status, total_items: 3, processed_items: 2, failed_items: 1, results: [] };
}

function boot({ files = [duplicateFile(A, 'Saga 1.cbz'), duplicateFile(B, 'Saga 2.cbz')], routes = {}, ...options } = {}) {
    return bootPage({ hash: '#/library', ...options, routes: { 'GET /api/files': filesPage(files), ...routes } });
}

async function openReview(page) {
    page.click(buttonByText(page, '.sidebar-nav', 'Review Duplicates'));
    await page.settle();
}

async function openCompare(page) {
    await openReview(page);
    page.click(buttonByText(page, '#duplicateReviewContent', 'Compare'));
    await page.settle();
}

// Each side of the compare view: its badge, name, path, cover and fields as
// [label, value, differs].
function compared(page) {
    return page.$$('#duplicateCompareContent .duplicate-compare-side').map(element => ({
        badge: element.querySelector('.duplicate-review-badge').textContent,
        name: element.querySelector('h3').textContent,
        path: element.querySelector('.duplicate-compare-path').textContent,
        cover: element.querySelector('img').getAttribute('data-protected-image'),
        fields: [...element.querySelectorAll('.duplicate-compare-fields div')].map(row => [
            row.querySelector('dt').textContent,
            row.querySelector('dd').textContent,
            row.classList.contains('duplicate-compare-differs')
        ])
    }));
}

function summary(page) {
    return page.$('#duplicateCompareSummary').textContent;
}

test('Re-check All re-checks every duplicate in a background job and sums up what it found', async t => {
    const page = await boot({
        routes: {
            [RECHECK_ALL]: { job_id: 'job-1', total_items: 3 },
            'GET /api/jobs/job-1': job('job-1'),
            'GET /api/jobs/job-1/duplicate-recheck': { cleared: 2, still_duplicate: 1, failed: 0 }
        }
    });
    t.after(() => page.close());

    await openReview(page);
    page.click('#duplicateReviewRecheckAllBtn');
    await page.settle(4);

    assert.ok(!isModalOpen(page, 'duplicateReviewModal'));
    const [call] = callsTo(page, 'POST', '/api/jobs/recheck-duplicates-all');
    assert.equal(call.body, undefined);
    assert.deepEqual(messageTexts(page, 'info'), ['Re-checking 3 duplicate(s) in background']);
    assert.equal(callsTo(page, 'GET', '/api/jobs/job-1/duplicate-recheck').length, 1);
    assert.deepEqual(messageTexts(page, 'success'), ['Re-check finished: 2 cleared, 1 still duplicate, 0 failed']);
});

test('a re-check with failures is reported as a warning', async t => {
    const page = await boot({
        routes: {
            [RECHECK_ALL]: { job_id: 'job-2', total_items: 3 },
            'GET /api/jobs/job-2': job('job-2'),
            'GET /api/jobs/job-2/duplicate-recheck': { cleared: 1, still_duplicate: 1, failed: 1 }
        }
    });
    t.after(() => page.close());

    await page.run('startDuplicateRecheck()');
    await page.settle(4);

    assert.deepEqual(messageTexts(page, 'warning'), ['Re-check finished: 1 cleared, 1 still duplicate, 1 failed']);
});

test('a re-check whose results cannot be read still ends quietly', async t => {
    const page = await boot({
        routes: {
            [RECHECK_ALL]: { job_id: 'job-3', total_items: 1 },
            'GET /api/jobs/job-3': job('job-3'),
            'GET /api/jobs/job-3/duplicate-recheck': 500
        }
    });
    t.after(() => page.close());

    await page.run('startDuplicateRecheck()');
    await page.settle(4);

    assert.ok(page.consoleMessages.some(entry => entry.type === 'warn'
        && entry.args[0] === '[DUPLICATES] Could not read the re-check results:'));
    assert.deepEqual(messageTexts(page, 'success'), []);

    // An expired session goes to login without a warning.
    page.route('GET /api/jobs/job-3/duplicate-recheck', 401);
    const warnings = page.consoleMessages.length;
    await page.run('reportDuplicateRecheck("job-3")');
    assert.ok(page.navigations.some(nav => nav.url.includes('/login.html')));
    assert.equal(page.consoleMessages.slice(warnings).filter(entry => entry.type === 'warn').length, 0);
});

test('Re-check All with no duplicates left says so and starts nothing', async t => {
    const page = await boot({ routes: { [RECHECK_ALL]: { job_id: '00000000-0000-0000-0000-000000000000', total_items: 0 } } });
    t.after(() => page.close());

    await page.run('recheckAllDuplicates()');

    assert.deepEqual(messageTexts(page, 'info'), ['No duplicates to re-check']);
    assert.ok(!isModalOpen(page, 'progressModal'));
});

test('a re-check the server did not start is reported, and an expired session goes to login', async t => {
    const page = await boot({ routes: { [RECHECK_ALL]: respond(500, { error: 'boom' }) } });
    t.after(() => page.close());

    await page.run('startDuplicateRecheck()');
    assert.deepEqual(messageTexts(page, 'error'), ['Failed to start the re-check: boom']);
    assert.ok(!isModalOpen(page, 'progressModal'));

    page.route(RECHECK_ALL, 401);
    await page.run('startDuplicateRecheck()');
    assert.ok(page.navigations.some(nav => nav.url.includes('/login.html')));
    assert.deepEqual(messageTexts(page, 'error'), ['Failed to start the re-check: boom']);
    assert.ok(!isModalOpen(page, 'progressModal'));
});

test('no re-check starts while another job runs', async t => {
    const page = await boot({
        routes: {
            [RECHECK_ALL]: { job_id: 'job-4', total_items: 3 },
            'GET /api/jobs/job-4': job('job-4', 'running')
        }
    });
    t.after(() => page.close());

    await page.run('startDuplicateRecheck()');
    await page.run('startDuplicateRecheck(["x"])');

    assert.equal(callsTo(page, 'POST', '/api/jobs/recheck-duplicates-all').length, 1);
    assert.equal(callsTo(page, 'POST', '/api/jobs/recheck-duplicates-selected').length, 0);
    assert.match(messageTexts(page, 'warning')[0], /Another job is already in progress/);
});

test('the series page re-checks the duplicate issues of the series', async t => {
    const page = await bootSeriesPage({
        issues: [
            seriesIssue({ file_path: A, duplicate: true }),
            seriesIssue({ file_path: 'Saga/Saga 003.cbz', file_name: 'Saga 003.cbz', issue: '3' }),
            seriesIssue({ file_path: B, file_name: 'Saga 2.cbz', issue: '2', duplicate: true })
        ],
        routes: {
            [RECHECK_SELECTED]: { job_id: 'job-5', total_items: 2 },
            'GET /api/jobs/job-5': job('job-5', 'running')
        }
    });
    t.after(() => page.close());

    clickSeriesAction(page, 'Re-check Duplicates');
    await page.settle();

    const [call] = callsTo(page, 'POST', '/api/jobs/recheck-duplicates-selected');
    assert.deepEqual(JSON.parse(JSON.stringify(call.body)), { Files: [A, B] });
    assert.deepEqual(messageTexts(page, 'info'), ['Re-checking 2 duplicate(s) in background']);
    assert.match(page.$('#progressTitle').textContent, /^Re-checking 2 duplicate\(s\)\.\.\./);
});

test('the series page says when the series has no duplicate issue', async t => {
    const page = await bootSeriesPage({ issues: [seriesIssue()] });
    t.after(() => page.close());

    clickSeriesAction(page, 'Re-check Duplicates');
    await page.settle();
    // A series whose issues are not loaded has none either.
    await page.run('recheckSeriesDuplicates("unknown", "Unknown")');

    assert.deepEqual(messageTexts(page, 'info'), [
        'No issue of Saga is flagged as a duplicate',
        'No issue of Unknown is flagged as a duplicate'
    ]);
    assert.equal(page.fetchCalls.filter(entry => entry.path.startsWith('/api/jobs/recheck-duplicates')).length, 0);
});

test('Compare shows the duplicate and the file that has its name side by side', async t => {
    const answer = deferred();
    const page = await boot({ routes: { [COMPARE_A]: () => answer.promise } });
    t.after(() => page.close());

    await openCompare(page);
    assert.ok(!isModalOpen(page, 'duplicateReviewModal'));
    assert.ok(isModalOpen(page, 'duplicateCompareModal'));
    assert.equal(page.$('#duplicateCompareContent').textContent.trim(), 'Comparing...');
    assert.ok(page.$('#duplicateCompareSwapBtn').disabled);

    answer.resolve(CLASH);
    await page.settle();

    assert.equal(summary(page),
        'Processing would name this file "Saga - Chapter 0001.cbz", and "Saga - Chapter 0001.cbz" already has that name.');
    const [duplicate, holder] = compared(page);
    assert.deepEqual({ ...duplicate, fields: undefined }, {
        badge: 'Flagged as duplicate', name: 'Saga 1.cbz', path: A, cover: A, fields: undefined
    });
    assert.deepEqual(duplicate.fields, [
        ['Pages', '22', true],
        ['Size', '2.0 KB', false],
        ['Modified', '2020-01-15', false],
        ['Series', 'Saga', true],
        ['Number', '1', false],
        ['Volume', '1', true],
        ['Volume start year', '2012', true],
        ['Format', '—', true],
        ['Title', 'Chapter 1', false],
        ['Year', '2012', false],
        ['Processed name', 'Saga - Chapter 0001.cbz', true]
    ]);
    assert.equal(holder.badge, 'Has the name');
    assert.equal(holder.path, PROPER);
    assert.deepEqual(holder.fields.map(([label, value]) => [label, value]), [
        ['Pages', '—'],
        ['Size', '2.0 KB'],
        ['Modified', '2020-01-15'],
        ['Series', 'Saga Annual'],
        ['Number', '1'],
        ['Volume', '—'],
        ['Volume start year', '—'],
        ['Format', 'Annual'],
        ['Title', 'Chapter 1'],
        ['Year', '2012'],
        ['Processed name', 'Saga Annual - Chapter 0001.cbz']
    ]);
    assert.ok(!page.$('#duplicateCompareSwapBtn').disabled);
    assert.ok(page.$('#duplicateCompareRecheckBtn').hidden);
});

test('Compare on a file whose name is free offers to re-check it', async t => {
    const page = await boot({
        routes: {
            [COMPARE_A]: {
                status: 'free',
                reason: null,
                file: side({ size: null, modified: null, page_count: null, tags: null }),
                other: null
            },
            [RECHECK_SELECTED]: { job_id: 'job-6', total_items: 1 },
            'GET /api/jobs/job-6': job('job-6', 'running')
        }
    });
    t.after(() => page.close());

    await openCompare(page);

    assert.equal(summary(page), 'No other file has the name "Saga - Chapter 0001.cbz" now. Re-check this file to clear its flag and give it that name.');
    const [only] = compared(page);
    assert.equal(compared(page).length, 1);
    assert.deepEqual(only.fields.map(([, value, differs]) => [value, differs]),
        [...Array(10).fill(['—', false]), ['Saga - Chapter 0001.cbz', false]]);
    assert.ok(page.$('#duplicateCompareSwapBtn').disabled);
    assert.ok(!page.$('#duplicateCompareRecheckBtn').hidden);

    page.click('#duplicateCompareRecheckBtn');
    await page.settle();

    assert.ok(!isModalOpen(page, 'duplicateCompareModal'));
    const [call] = callsTo(page, 'POST', '/api/jobs/recheck-duplicates-selected');
    assert.deepEqual(JSON.parse(JSON.stringify(call.body)), { Files: [A] });
});

test('Compare says why a processed name could not be worked out', async t => {
    const page = await boot({
        routes: { [COMPARE_A]: { status: 'unknown', reason: 'No metadata or series information', file: side({ processed_name: null }), other: null } }
    });
    t.after(() => page.close());

    await openCompare(page);

    assert.equal(summary(page), 'The name processing would give this file could not be worked out: No metadata or series information');
    assert.ok(page.$('#duplicateCompareSwapBtn').disabled);
    assert.ok(page.$('#duplicateCompareRecheckBtn').hidden);
});

test('Compare reports a failure, goes to login on an expired session, and drops late answers', async t => {
    const page = await boot({ routes: { [COMPARE_A]: respond(500, { error: 'Error comparing the files' }) } });
    t.after(() => page.close());

    await openCompare(page);
    assert.equal(summary(page), 'Could not compare the files: Error comparing the files');
    assert.equal(page.$('#duplicateCompareContent').children.length, 0);

    page.route(COMPARE_A, 401);
    await page.run(`openDuplicateCompareModal(${JSON.stringify(A)})`);
    assert.ok(page.navigations.some(nav => nav.url.includes('/login.html')));
    assert.equal(summary(page), '');

    // Closed before the answer (or the failure) arrived.
    const answer = deferred();
    page.route(COMPARE_A, () => answer.promise);
    page.run(`openDuplicateCompareModal(${JSON.stringify(A)})`);
    page.click(buttonByText(page, '#duplicateCompareModal .modal-footer', 'Keep Both'));
    answer.resolve(CLASH);
    await page.settle();
    assert.ok(!isModalOpen(page, 'duplicateCompareModal'));
    assert.equal(compared(page).length, 0);

    const failure = deferred();
    page.route(COMPARE_A, () => failure.promise);
    page.run(`openDuplicateCompareModal(${JSON.stringify(A)})`);
    await page.run('closeDuplicateCompareModal()');
    failure.reject(new TypeError('Failed to fetch'));
    await page.settle();
    assert.equal(summary(page), '');
});

test('Compare Duplicate on a duplicate issue opens the comparison, and Escape closes it', async t => {
    const page = await bootSeriesPage({
        issues: [seriesIssue({ file_path: A, duplicate: true })],
        routes: { [COMPARE_A]: CLASH }
    });
    t.after(() => page.close());

    clickIssueAction(page, A, 'Compare Duplicate');
    await page.settle();

    assert.ok(isModalOpen(page, 'duplicateCompareModal'));
    assert.equal(compared(page).length, 2);
    await pressEscape(page);
    assert.ok(!isModalOpen(page, 'duplicateCompareModal'));
});

test('Swap asks first, then gives the duplicate the name and reopens the review on the other file', async t => {
    const page = await boot({
        files: [duplicateFile(A, 'Saga 1.cbz'), duplicateFile(ASIDE, 'Saga - Chapter 0001 (duplicate).cbz')],
        routes: {
            [COMPARE_A]: CLASH,
            [SWAP]: { success: true, file_path: PROPER, duplicate_path: ASIDE }
        }
    });
    t.after(() => page.close());
    answerConfirms(page, false, true);

    await openCompare(page);
    page.click('#duplicateCompareSwapBtn');
    await page.settle();
    assert.equal(page.dialogs.length, 1);
    assert.match(page.dialogs[0].message, /^Swap these files\?\n\n"Saga 1\.cbz" takes the name "Saga - Chapter 0001\.cbz"\./);
    assert.match(page.dialogs[0].message, /No file is deleted\.$/);
    assert.equal(callsTo(page, 'POST', '/api/duplicates/swap').length, 0);
    assert.ok(isModalOpen(page, 'duplicateCompareModal'));

    page.click('#duplicateCompareSwapBtn');
    await page.settle(3);

    const [call] = callsTo(page, 'POST', '/api/duplicates/swap');
    assert.deepEqual(call.body, { filePath: A });
    assert.ok(!isModalOpen(page, 'duplicateCompareModal'));
    assert.deepEqual(messageTexts(page, 'success'),
        ['Swapped: Saga - Chapter 0001.cbz has the name now, and Saga - Chapter 0001 (duplicate).cbz is the duplicate']);
    assert.ok(isModalOpen(page, 'duplicateReviewModal'));
    assert.equal(page.$('#duplicateReviewPath').textContent, ASIDE);
});

test('a failed Swap is reported and can be tried again; an expired session goes to login', async t => {
    const page = await boot({ routes: { [COMPARE_A]: CLASH, [SWAP]: respond(409, { error: 'Another file has the name now. Compare the files again.' }) } });
    t.after(() => page.close());
    answerConfirms(page, true);

    await openCompare(page);
    page.click('#duplicateCompareSwapBtn');
    await page.settle();

    assert.deepEqual(messageTexts(page, 'error'), ['Failed to swap the files: Another file has the name now. Compare the files again.']);
    assert.ok(isModalOpen(page, 'duplicateCompareModal'));
    assert.ok(!page.$('#duplicateCompareSwapBtn').disabled);

    // A failure after the view was closed leaves the button as it is.
    const failure = deferred();
    page.route(SWAP, () => failure.promise);
    page.click('#duplicateCompareSwapBtn');
    await page.run('closeDuplicateCompareModal()');
    failure.resolve(respond(400, { error: 'could not process' }));
    await page.settle();
    assert.ok(page.$('#duplicateCompareSwapBtn').disabled);

    page.route(SWAP, 401);
    await openCompare(page);
    page.click('#duplicateCompareSwapBtn');
    await page.settle();
    assert.ok(page.navigations.some(nav => nav.url.includes('/login.html')));
    assert.equal(messageTexts(page, 'error').length, 2);
});

test('the compare actions do nothing without a comparison to act on', async t => {
    const page = await boot({ files: [], routes: { [COMPARE_A]: { ...CLASH, status: 'free', other: null } } });
    t.after(() => page.close());
    answerConfirms(page, true);

    await page.run('swapComparedDuplicate()');
    await page.run('recheckComparedDuplicate()');
    // Nothing to compare in an empty review.
    await openReview(page);
    await page.run('compareCurrentDuplicate()');
    assert.ok(!isModalOpen(page, 'duplicateCompareModal'));

    // Still loading, and then not clashing.
    const answer = deferred();
    page.route(COMPARE_A, () => answer.promise);
    page.run(`openDuplicateCompareModal(${JSON.stringify(A)})`);
    await page.run('swapComparedDuplicate()');
    answer.resolve({ ...CLASH, status: 'free', other: null });
    await page.settle();
    await page.run('swapComparedDuplicate()');

    assert.deepEqual(page.dialogs, []);
    assert.equal(page.fetchCalls.filter(entry => entry.path === '/api/duplicates/swap'
        || entry.path.startsWith('/api/jobs/recheck-duplicates')).length, 0);
});

test('read-only users can compare but not swap or re-check', async t => {
    const page = await boot({
        user: { canModifyLibrary: false, canAdminister: false, roles: ['ReadOnly'] },
        routes: { [COMPARE_A]: CLASH },
        beforeScripts(window) {
            // css/main.css: the read-only gate (the harness loads no stylesheets).
            const style = window.document.createElement('style');
            style.textContent = 'body.user-read-only [data-requires-write] { display: none !important; }';
            window.document.head.appendChild(style);
        }
    });
    t.after(() => page.close());
    const visible = element => page.window.getComputedStyle(element).display !== 'none';

    await openReview(page);
    assert.ok(!visible(page.$('#duplicateReviewRecheckAllBtn')));
    page.click(buttonByText(page, '#duplicateReviewContent', 'Compare'));
    await page.settle();

    assert.equal(compared(page).length, 2);
    assert.ok(!visible(page.$('#duplicateCompareSwapBtn')));
    assert.ok(!visible(page.$('#duplicateCompareRecheckBtn')));
});
