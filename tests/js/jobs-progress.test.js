'use strict';

// The batch-job progress modal (js/app/jobs.js) as server events drive it:
// job_updated counts, file_processed rows, the four ways a job ends, the
// minimized header indicator, catching up after the event stream reconnects
// and the leave-page warning.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const h = require('./support/jobs-helpers');

const { JOB_ID, TRACKED_TITLE, LIBRARY_RELOAD, ISSUE_A, ISSUE_B } = h;
const STICKY = 'progress-indicator-btn--mobile-sticky';

const finished = (file, success, error = null) => ({ file, success, error });
const warnings = page => page.consoleMessages.filter(message => message.type === 'warn').map(message => message.args[0]);

test('job_updated events move the progress modal of the tracked job only', async (t) => {
    const job = h.trackedJob();
    const page = await h.bootJobsApp(t, { routes: job.routes });

    // Nothing is tracked yet.
    h.jobUpdate(page, 'running', h.progress(1, 4));
    h.assertProgress(page, { open: false, text: '0 / 0 files' });

    await h.startTrackedJob(page);
    h.assertProgress(page, {
        open: true,
        title: TRACKED_TITLE,
        text: '0 / 4 files',
        percent: '0%',
        currentFile: 'Waiting for the next completed file...',
        emptyResults: true,
        resultsCount: '0 results'
    });

    h.jobUpdate(page, 'running', h.progress(3, 4), h.OTHER_JOB_ID);
    h.assertProgress(page, { text: '0 / 4 files', processed: '0' });

    h.jobUpdate(page, 'running', h.progress(1, 4));
    h.assertProgress(page, { title: TRACKED_TITLE, text: '1 / 4 files', percent: '25%', bar: '25%', processed: '1', succeeded: '1', failed: '0' });

    h.jobUpdate(page, 'running', h.progress(3, 4, 2, 1));
    h.assertProgress(page, {
        title: `${TRACKED_TITLE} - 2 succeeded, 1 failed`,
        text: '3 / 4 files',
        percent: '75%',
        bar: '75%',
        processed: '3',
        succeeded: '2',
        failed: '1'
    });

    // An update without progress counts reads as nothing done.
    h.serverEvent(page, 'job_updated', { job_id: JOB_ID, status: 'queued' });
    h.assertProgress(page, { title: TRACKED_TITLE, text: '0 / 0 files', percent: '0%', bar: '0%', processed: '0', succeeded: '0', failed: '0' });
    assert.equal(h.progressView(page).cancelVisible, true);
    assert.deepEqual(page.errors, []);
});

test('file_processed events list each finished file, newest first, up to 200', async (t) => {
    const job = h.trackedJob();
    const page = await h.bootJobsApp(t, { routes: job.routes });

    h.fileProcessed(page, 'Comics/Batman/Batman 001.cbz', true);
    assert.deepEqual(h.progressView(page).results, [], 'files processed outside a tracked job are not listed');

    await h.startTrackedJob(page);
    h.fileProcessed(page, 'Comics/Batman/Batman 001.cbz', true);
    h.assertProgress(page, {
        currentFile: 'Completed: Batman 001.cbz',
        resultsCount: '1 result',
        emptyResults: false,
        results: [{ success: true, file: 'Batman 001.cbz', detail: 'Completed successfully' }]
    });

    h.fileProcessed(page, 'Comics/Batman/Batman 002.cbz', false, 'Corrupt archive');
    h.fileProcessed(page, 'Comics/Batman/Batman 003.cbz', false);
    h.assertProgress(page, {
        currentFile: 'Failed: Batman 003.cbz',
        resultsCount: '3 results',
        results: [
            { success: false, file: 'Batman 003.cbz', detail: 'Failed' },
            { success: false, file: 'Batman 002.cbz', detail: 'Corrupt archive' },
            { success: true, file: 'Batman 001.cbz', detail: 'Completed successfully' }
        ]
    });

    // A file reported again moves to the top instead of being listed twice.
    h.fileProcessed(page, 'Comics/Batman/Batman 001.cbz', false, 'Rename target exists');
    h.assertProgress(page, {
        currentFile: 'Failed: Batman 001.cbz',
        resultsCount: '3 results',
        results: [
            { success: false, file: 'Batman 001.cbz', detail: 'Rename target exists' },
            { success: false, file: 'Batman 003.cbz', detail: 'Failed' },
            { success: false, file: 'Batman 002.cbz', detail: 'Corrupt archive' }
        ]
    });

    // Only the latest 200 results are kept; the oldest ones drop off the end.
    const bulk = n => `Comics/Bulk/Bulk ${String(n).padStart(3, '0')}.cbz`;
    for (let n = 1; n <= 200; n++) h.fileProcessed(page, bulk(n), true);
    let view = h.progressView(page);
    assert.equal(view.resultsCount, '200 results');
    assert.equal(view.results.length, 200);
    assert.deepEqual(view.results[0], { success: true, file: 'Bulk 200.cbz', detail: 'Completed successfully' });
    assert.equal(view.results.at(-1).file, 'Bulk 001.cbz');
    assert.ok(!view.results.some(result => result.file.startsWith('Batman')));

    // A dropped file reported again is listed again.
    h.fileProcessed(page, 'Comics/Batman/Batman 002.cbz', true);
    view = h.progressView(page);
    assert.equal(view.resultsCount, '200 results');
    assert.deepEqual(view.results[0], { success: true, file: 'Batman 002.cbz', detail: 'Completed successfully' });
    assert.equal(view.results.at(-1).file, 'Bulk 002.cbz');
    assert.deepEqual(page.errors, []);
});

const OUTCOMES = [
    {
        status: 'completed',
        progress: h.progress(4, 4, 3, 1),
        title: 'Completed! All 4 items processed (3 succeeded, 1 failed)',
        closesAfter: 1000,
        reloads: 1
    },
    { status: 'failed', progress: h.progress(2, 4, 1, 1), title: 'Failed - Job processing failed', closesAfter: 3000, reloads: 0 },
    { status: 'cancelled', progress: h.progress(1, 4), title: 'Cancelled - Job was cancelled', closesAfter: 2000, reloads: 0 },
    {
        status: 'interrupted',
        progress: h.progress(2, 4),
        title: 'Interrupted - the server restarted after 2 of 4 items',
        closesAfter: 5000,
        reloads: 0
    }
];

for (const outcome of OUTCOMES) {
    test(`a ${outcome.status} job shows its outcome, then closes the progress modal`, async (t) => {
        const job = h.trackedJob();
        const page = await h.bootJobsApp(t, { routes: job.routes });
        const reloads = () => h.requests(page, 'GET', LIBRARY_RELOAD).length;
        await h.startTrackedJob(page);
        const reloadsBefore = reloads();

        // The counts update at once; the outcome is shown after a short pause.
        h.jobUpdate(page, outcome.status, outcome.progress);
        const { processed, total } = outcome.progress;
        h.assertProgress(page, { text: `${processed} / ${total} files`, cancelVisible: true, closeVisible: false });
        await h.tick(t, page, 499);
        h.assertProgress(page, { cancelVisible: true, closeVisible: false });

        await h.tick(t, page, 1);
        h.assertProgress(page, {
            open: true,
            title: outcome.title,
            currentFile: `Finished: ${TRACKED_TITLE}`,
            cancelVisible: false,
            closeVisible: true,
            indicatorVisible: false
        });
        assert.equal(reloads(), reloadsBefore + outcome.reloads, 'only a completed job reloads the library');

        // The job is over: late events no longer reach the modal.
        h.fileProcessed(page, 'Comics/Late.cbz', true);
        h.jobUpdate(page, 'running', h.progress(1, 4));
        h.assertProgress(page, { title: outcome.title, resultsCount: '0 results', text: `${processed} / ${total} files` });

        await h.tick(t, page, outcome.closesAfter - 1);
        assert.equal(h.progressView(page).open, true);
        await h.tick(t, page, 1);
        h.assertProgress(page, { open: false, indicatorVisible: false, cancelVisible: false });
        assert.deepEqual(h.messages(page), []);
        assert.deepEqual(page.errors, []);
    });
}

test('Close dismisses a finished job straight away', async (t) => {
    const job = h.trackedJob();
    const page = await h.bootJobsApp(t, { routes: job.routes });
    await h.startTrackedJob(page);

    h.jobUpdate(page, 'failed', h.progress(1, 4, 0, 1));
    await h.tick(t, page, 500);
    assert.equal(h.progressView(page).closeVisible, true);
    page.click('#progressCloseBtn');
    h.assertProgress(page, { open: false, indicatorVisible: false, cancelVisible: false });

    // The pending auto-close finds it already closed.
    await h.tick(t, page, 3000);
    h.assertProgress(page, { open: false, indicatorVisible: false });
    assert.deepEqual(page.errors, []);
});

test('a job completed in an open series reloads just that series and clears the selection', async (t) => {
    const page = await h.bootSeriesDetail(t, {
        routes: {
            'POST /api/jobs/process-selected': { job_id: JOB_ID, total_items: 2 },
            [`GET /api/jobs/${JOB_ID}`]: h.jobStatus({ total_items: 2 })
        }
    });
    const issueLoads = () => h.requests(page, 'GET', '/api/files/series/s1/issues').length;
    h.selectIssue(page, ISSUE_A);
    h.selectIssue(page, ISSUE_B);
    h.chooseMenuItem(page, 'processDropdown', 'Process Selected');
    await page.settle();
    h.takeConfirms(page);
    h.assertProgress(page, { open: true, title: 'Processing Selected Files...', text: '0 / 2 files' });
    const loadsBefore = issueLoads();

    h.jobUpdate(page, 'completed', h.progress(2, 2));
    await h.tick(t, page, 500);
    h.assertProgress(page, { title: 'Completed! All 2 items processed (2 succeeded, 0 failed)', closeVisible: true });
    assert.equal(issueLoads(), loadsBefore + 1, 'the open series reloads its issues in place');
    assert.equal(h.requests(page, 'GET', LIBRARY_RELOAD).length, 0, 'the series library is not rebuilt');
    assert.ok(page.$('#seriesDetailPanel'), 'the series stays open');
    assert.equal(page.run('selectedFiles.size'), 0);
    assert.deepEqual(page.$$('.series-issue-select input:checked'), [], 'the reloaded issues are not selected');
    assert.equal(page.$('#selectInfo').textContent, 'No files selected');
    assert.equal(page.$('#processSelectedItem').disabled, true);

    await h.tick(t, page, 1000);
    assert.equal(h.progressView(page).open, false);
    assert.deepEqual(page.errors, []);
});

test('minimizing the progress modal follows the job in the header indicator', async (t) => {
    const job = h.trackedJob();
    const page = await h.bootJobsApp(t, { routes: job.routes });
    const indicator = page.$('#progressIndicator');
    const minimized = () => page.window.localStorage.getItem('progressModalMinimized');
    await h.startTrackedJob(page);
    h.jobUpdate(page, 'running', h.progress(1, 4));

    page.click('#progressModal .close-btn');
    h.assertProgress(page, { open: false, indicatorVisible: true, indicatorText: '1 / 4 files (25%)' });
    assert.equal(indicator.style.display, 'flex');
    assert.equal(indicator.classList.contains(STICKY), false, 'desktop keeps the indicator in the header');
    assert.equal(minimized(), 'true');

    h.jobUpdate(page, 'running', h.progress(2, 4));
    h.assertProgress(page, { open: false, indicatorText: '2 / 4 files (50%)' });

    page.click('#progressIndicator');
    h.assertProgress(page, { open: true, indicatorVisible: false, text: '2 / 4 files' });
    assert.equal(minimized(), null);

    // A job finishing while minimized says so in the indicator, which then goes away.
    page.click('#progressModal .close-btn');
    h.jobUpdate(page, 'completed', h.progress(4, 4));
    h.assertProgress(page, { open: false, indicatorText: '4 / 4 files (100%)' });
    await h.tick(t, page, 500);
    h.assertProgress(page, { open: false, indicatorVisible: true, indicatorText: '✅ Processing Complete' });
    await h.tick(t, page, 1000);
    h.assertProgress(page, { open: false, indicatorVisible: false });
    assert.equal(minimized(), null);
    assert.deepEqual(page.errors, []);
});

test('a job started or resumed while minimized opens in the header indicator', async (t) => {
    // The resume checks the job's status, then polls it again to fill in the modal.
    const pendingPoll = { resolve: null };
    const job = h.trackedJob();
    let checks = 0;
    job.poll.answer = () => (++checks === 1 ? h.jobStatus() : new Promise(resolve => { pendingPoll.resolve = resolve; }));
    const page = await h.bootJobsApp(t, {
        routes: { ...job.routes, 'GET /api/active-job': { job_id: JOB_ID, job_title: 'Renaming All Files...' } },
        localStorage: { progressModalMinimized: 'true' }
    });

    // The resumed job reports first under its own title...
    assert.deepEqual(h.messages(page), [{ type: 'info', text: 'Resuming active job...' }]);
    h.assertProgress(page, { open: false, indicatorVisible: true, indicatorText: 'Renaming All Files...', title: 'Renaming All Files...' });

    // ...then with its counts.
    pendingPoll.resolve(h.jobStatus({ processed_items: 1, results: [finished('Comics/A.cbz', true)] }));
    await page.settle();
    h.assertProgress(page, { open: false, indicatorText: '1 / 4 files (25%)', title: 'Renaming All Files...' });

    // A new job started once it has ended, before its indicator goes away,
    // stays minimized too.
    await h.endTrackedJob(t, page);
    job.poll.answer = () => h.jobStatus();
    h.chooseMenuItem(page, 'processDropdown', 'Process Unmarked');
    h.assertProgress(page, { open: false, indicatorVisible: true, indicatorText: 'Starting async processing...', title: 'Starting async processing...' });
    await page.settle();
    h.assertProgress(page, { open: false, indicatorText: '0 / 4 files (0%)', title: TRACKED_TITLE });
    assert.deepEqual(page.errors, []);
});

test('on a phone the minimized indicator sticks to the bottom of the screen', async (t) => {
    const job = h.trackedJob();
    const page = await h.bootJobsApp(t, { routes: job.routes, media: { '(max-width: 768px)': true } });
    const indicator = page.$('#progressIndicator');
    await h.startTrackedJob(page);

    page.click('#progressModal .close-btn');
    assert.equal(indicator.style.display, 'flex');
    assert.equal(indicator.classList.contains(STICKY), true);

    page.click('#progressIndicator');
    assert.equal(indicator.style.display, 'none');
    assert.equal(indicator.classList.contains(STICKY), false);
    assert.equal(h.progressView(page).open, true);
    assert.deepEqual(page.errors, []);
});

test('the progress modal still works when localStorage refuses access', async (t) => {
    const job = h.trackedJob();
    const page = await h.bootJobsApp(t, { routes: job.routes });
    const storage = page.window.Storage.prototype;
    // Only the minimized flag fails, so the sign-in token stays readable.
    const refusing = (method, action) => {
        const original = storage[method];
        storage[method] = function refused(key, ...rest) {
            if (key === 'progressModalMinimized') throw new page.window.DOMException('The operation is insecure.', 'SecurityError');
            return original.call(this, key, ...rest);
        };
        try {
            return action();
        } finally {
            storage[method] = original;
        }
    };

    const warnedSince = () => {
        const seen = warnings(page).length;
        return () => warnings(page).slice(seen);
    };

    let newWarnings = warnedSince();
    await refusing('getItem', () => h.startTrackedJob(page));
    h.assertProgress(page, { open: true, indicatorVisible: false, title: TRACKED_TITLE });
    assert.deepEqual(newWarnings(), ['Could not access localStorage:']);

    newWarnings = warnedSince();
    refusing('setItem', () => page.click('#progressModal .close-btn'));
    h.assertProgress(page, { open: false, indicatorVisible: true, indicatorText: '0 / 4 files (0%)' });
    assert.deepEqual(newWarnings(), ['Could not save to localStorage:']);

    newWarnings = warnedSince();
    refusing('removeItem', () => page.click('#progressIndicator'));
    h.assertProgress(page, { open: true, indicatorVisible: false });
    assert.deepEqual(newWarnings(), ['Could not clear localStorage:']);

    h.jobUpdate(page, 'cancelled', h.progress(1, 4));
    await h.tick(t, page, 500);
    newWarnings = warnedSince();
    refusing('removeItem', () => page.click('#progressCloseBtn'));
    h.assertProgress(page, { open: false, indicatorVisible: false });
    assert.deepEqual(newWarnings(), ['Could not clear localStorage:']);
    assert.deepEqual(page.errors, []);
});

test('renderProgressDetails redraws the stored per-file results', async (t) => {
    const page = await h.bootJobsApp(t);

    page.run(`progressResults = [
        { key: 'Comics/A.cbz', filename: 'A.cbz', success: true, error: null },
        { key: 'Comics/B.cbz', filename: 'B.cbz', success: false, error: 'Bad zip' },
        { key: 'Comics/C.cbz', filename: 'C.cbz', success: false, error: null }
    ]; renderProgressDetails();`);
    h.assertProgress(page, {
        resultsCount: '3 results',
        emptyResults: false,
        results: [
            { success: true, file: 'A.cbz', detail: 'Completed successfully' },
            { success: false, file: 'B.cbz', detail: 'Bad zip' },
            { success: false, file: 'C.cbz', detail: 'Failed' }
        ]
    });

    page.run(`progressResults = [{ key: 'Comics/D.cbz', filename: 'D.cbz', success: true, error: null }]; renderProgressDetails();`);
    h.assertProgress(page, { resultsCount: '1 result', results: [{ success: true, file: 'D.cbz', detail: 'Completed successfully' }] });

    page.run('progressResults = []; renderProgressDetails();');
    h.assertProgress(page, { resultsCount: '0 results', results: [], emptyResults: true });
    assert.deepEqual(page.errors, []);
});

test('a reconnected event stream catches up on the tracked job', async (t) => {
    const job = h.trackedJob();
    const page = await h.bootJobsApp(t, { routes: job.routes });
    const polls = () => h.requests(page, 'GET', `/api/jobs/${JOB_ID}`).length;
    const reconnect = async () => {
        page.eventSources.at(-1).emit('open');
        await page.settle();
    };

    await reconnect();
    assert.equal(polls(), 0, 'without a job there is nothing to catch up on');

    await h.startTrackedJob(page);
    assert.equal(polls(), 1);

    // The first file is still being worked on.
    job.poll.answer = () => h.jobStatus({ current_file: 'Comics/Batman/Batman 001.cbz' });
    await reconnect();
    assert.equal(polls(), 2);
    h.assertProgress(page, { text: '0 / 4 files', currentFile: 'Latest update: Batman 001.cbz', emptyResults: true });

    job.poll.answer = () => h.jobStatus({
        processed_items: 1,
        failed_items: 1,
        current_file: 'Comics/Batman/Batman 003.cbz',
        results: [finished('Comics/Batman/Batman 001.cbz', true), finished('Comics/Batman/Batman 002.cbz', false, 'Corrupt archive')]
    });
    await reconnect();
    h.assertProgress(page, {
        title: `${TRACKED_TITLE} - 1 succeeded, 1 failed`,
        text: '2 / 4 files',
        percent: '50%',
        processed: '2',
        succeeded: '1',
        failed: '1',
        currentFile: 'Failed: Batman 002.cbz',
        resultsCount: '2 results',
        results: [
            { success: false, file: 'Batman 002.cbz', detail: 'Corrupt archive' },
            { success: true, file: 'Batman 001.cbz', detail: 'Completed successfully' }
        ]
    });

    // A status the server can't give leaves the modal as it was.
    const before = h.progressView(page);
    job.poll.answer = () => respond(500, 'Error');
    await reconnect();
    assert.deepEqual(h.progressView(page), before);
    assert.ok(warnings(page).includes(`[JOB ${JOB_ID}] Could not fetch job status: 500`));

    job.poll.answer = () => { throw new TypeError('Failed to fetch'); };
    await reconnect();
    assert.deepEqual(h.progressView(page), before);
    assert.ok(page.consoleMessages.some(message => message.type === 'error' && message.args[0] === `[JOB ${JOB_ID}] Error polling job status:`));

    // The job finished while the stream was down: the poll completes it.
    job.poll.answer = () => h.jobStatus({
        status: 'completed',
        processed_items: 3,
        failed_items: 1,
        results: [
            finished('Comics/Batman/Batman 001.cbz', true),
            finished('Comics/Batman/Batman 002.cbz', false, 'Corrupt archive'),
            finished('Comics/Batman/Batman 003.cbz', true),
            finished('Comics/Batman/Batman 004.cbz', true)
        ]
    });
    await reconnect();
    h.assertProgress(page, { text: '4 / 4 files', resultsCount: '4 results', closeVisible: false });
    await h.tick(t, page, 500);
    h.assertProgress(page, { title: 'Completed! All 4 items processed (3 succeeded, 1 failed)', closeVisible: true, cancelVisible: false });
    await h.tick(t, page, 1000);
    assert.equal(h.progressView(page).open, false);

    await reconnect();
    assert.equal(polls(), 6, 'a finished job is not polled again');
    assert.deepEqual(page.errors, []);
});

test('a bare job status still updates the modal, and a signed-out poll goes to the login page', async (t) => {
    const job = h.trackedJob({ job_id: JOB_ID, status: 'running' });
    const page = await h.bootJobsApp(t, { routes: job.routes });

    await h.startTrackedJob(page);
    h.assertProgress(page, { open: true, title: TRACKED_TITLE, text: '0 / 0 files', percent: '0%', currentFile: 'Waiting for the next completed file...' });

    // Cancelled with no counts at all.
    job.poll.answer = () => ({ job_id: JOB_ID, status: 'cancelled' });
    page.eventSources.at(-1).emit('open');
    await page.settle();
    await h.tick(t, page, 500);
    h.assertProgress(page, { title: 'Cancelled - Job was cancelled', text: '0 / 0 files', closeVisible: true });
    await h.tick(t, page, 2000);
    assert.equal(h.progressView(page).open, false);

    job.poll.answer = () => 401;
    await h.startTrackedJob(page);
    assert.equal(h.loginRedirects(page), 1);
    h.assertProgress(page, { open: true, title: 'Starting async processing...', text: '0 / 0 files' });
    assert.deepEqual(page.errors, []);
});

test('leaving the page closes the event stream and warns only while a job runs', async (t) => {
    const job = h.trackedJob();
    const page = await h.bootJobsApp(t, { routes: job.routes });
    const leave = () => {
        const event = new page.window.Event('beforeunload', { cancelable: true });
        page.window.dispatchEvent(event);
        return event;
    };
    const stream = page.eventSources.at(-1);

    assert.equal(leave().defaultPrevented, false);
    assert.equal(stream.readyState, 2, 'the event stream is closed');

    await h.startTrackedJob(page);
    assert.equal(leave().defaultPrevented, true, 'the browser asks before leaving a running job');

    h.jobUpdate(page, 'completed', h.progress(4, 4));
    await h.tick(t, page, 500);
    assert.equal(leave().defaultPrevented, false);
    assert.deepEqual(page.errors, []);
});

test('a completion reported twice still leaves one finished, then closed, modal', async (t) => {
    const job = h.trackedJob();
    const page = await h.bootJobsApp(t, { routes: job.routes });
    await h.startTrackedJob(page);

    // e.g. the job_updated event and a reconnect poll both report the end.
    h.jobUpdate(page, 'completed', h.progress(4, 4));
    h.jobUpdate(page, 'completed', h.progress(4, 4));
    await h.tick(t, page, 500);
    h.assertProgress(page, { open: true, title: 'Completed! All 4 items processed (4 succeeded, 0 failed)', closeVisible: true, cancelVisible: false });
    await h.tick(t, page, 1000);
    h.assertProgress(page, { open: false, indicatorVisible: false });
    assert.deepEqual(page.errors, []);
});

test('the progress helpers do nothing when their markup is missing', async (t) => {
    const page = await h.bootJobsApp(t);
    page.$('#progressIndicator').remove();
    page.$('#progressCurrentFile').remove();

    page.run("setProgressIndicatorVisible(true); setProgressIndicatorVisible(false); setProgressCurrentFile('Comics/A.cbz');");
    assert.deepEqual(page.errors, []);
});
