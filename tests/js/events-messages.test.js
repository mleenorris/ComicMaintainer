'use strict';

// js/app/events.js — what each server event does to the page: watcher status,
// processed files, file list changes and job progress / completion.

const test = require('node:test');
const assert = require('node:assert/strict');
const {
    boot, consoleLines, requests, serverEvent, seriesCard, seriesIssue, seriesIssues, seriesPage, wentToLogin
} = require('./support/core-helpers');

const JOB_ID = '6c0f8f2e-4a8b-4d55-9a4e-2f1d3c5b7a90';
const SERIES = seriesCard();
const ISSUES_PATH = `/api/files/series/${SERIES.id}/issues`;
const useMockTimers = t => t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });

const RUNNING = { text: 'Watcher Running', title: 'File watcher is running and monitoring for changes', classes: ['running'] };
const STOPPED = { text: 'Watcher Stopped', title: 'File watcher is enabled but not running', classes: ['stopped'] };
const DISABLED = { text: 'Watcher Stopped', title: 'File watcher is disabled', classes: ['stopped'] };
const UNKNOWN = { text: 'Status Unknown', title: 'Unable to determine watcher status', classes: [] };

function watcherIndicators(page) {
    return ['#watcherStatus', '#watcherStatusMenu', '#watcherStatusSidebar'].map(selector => {
        const element = page.$(selector);
        return {
            text: element.querySelector('.watcher-text').textContent,
            title: element.title,
            classes: ['running', 'stopped'].filter(name => element.classList.contains(name))
        };
    });
}

const everywhere = state => [state, state, state];
const handlerErrors = page => consoleLines(page, 'error').filter(line => line.startsWith('SSE: Error parsing event data:'));

// GET /api/jobs/{id} (JobsController) for the job resumed at boot.
function jobStatus(overrides = {}) {
    return {
        job_id: JOB_ID,
        status: 'running',
        total_items: 4,
        processed_items: 1,
        failed_items: 0,
        current_file: 'Saga/Saga 001.cbz',
        start_time: 1700000000,
        end_time: null,
        results: [{ file: 'Saga/Saga 001.cbz', success: true, error: null }],
        ...overrides
    };
}

/** Boot while a job is running on the server, so the app resumes tracking it. */
function bootWithRunningJob(options = {}) {
    return boot({
        ...options,
        routes: {
            'GET /api/active-job': {
                job_id: JOB_ID,
                job_title: 'Processing 4 files...',
                status: 'running',
                total_items: 4,
                processed_items: 1,
                failed_items: 0,
                current_file: 'Saga/Saga 001.cbz',
                start_time: 1700000000,
                end_time: null
            },
            [`GET /api/jobs/${JOB_ID}`]: jobStatus(),
            ...options.routes
        }
    });
}

const seriesRoutes = {
    'GET /api/files/series': seriesPage([SERIES]),
    [`GET ${ISSUES_PATH}`]: seriesIssues(SERIES, [seriesIssue(), seriesIssue({ file_path: 'Saga/Saga 002.cbz', file_name: 'Saga 002.cbz', issue: '2' })])
};

const progressModalOpen = page => page.$('#progressModal').classList.contains('active');

test('the watcher indicators show the status fetched at startup', async () => {
    const page = await boot();
    assert.equal(requests(page, 'GET', '/api/watcher/status').length, 1);
    assert.deepEqual(watcherIndicators(page), everywhere(RUNNING));
});

test('watcher_status events update every indicator', async () => {
    const page = await boot();
    serverEvent(page, 'watcher_status', { running: false, enabled: true });
    assert.deepEqual(watcherIndicators(page), everywhere(STOPPED));
    serverEvent(page, 'watcher_status', { running: false, enabled: false });
    assert.deepEqual(watcherIndicators(page), everywhere(DISABLED));
    serverEvent(page, 'watcher_status', { running: true, enabled: true });
    assert.deepEqual(watcherIndicators(page), everywhere(RUNNING));
    assert.ok(consoleLines(page, 'log').some(line => line.startsWith('SSE: Watcher status updated:')));
});

for (const [description, handler, logged] of [
    ['fails', 500, 'Error fetching initial watcher status: HTTP error! status: 500'],
    ['is unreachable', () => { throw new TypeError('Failed to fetch'); }, 'Error fetching initial watcher status: Failed to fetch']
]) {
    test(`the watcher status shows as unknown when the startup check ${description}`, async () => {
        const page = await boot({ routes: { 'GET /api/watcher/status': handler } });
        assert.deepEqual(watcherIndicators(page), everywhere(UNKNOWN));
        assert.ok(consoleLines(page, 'error').includes(logged));
        serverEvent(page, 'watcher_status', { running: true, enabled: true });
        assert.deepEqual(watcherIndicators(page), everywhere(RUNNING), 'a later event still updates it');
    });
}

test('an expired session on the watcher status check goes to the login page', async () => {
    const page = await boot({ routes: { 'GET /api/watcher/status': 401 } });
    assert.ok(wentToLogin(page));
    assert.deepEqual(watcherIndicators(page).map(indicator => indicator.text), ['Checking...', 'Checking...', 'Checking...']);
});

test('watcher updates tolerate missing indicators', async () => {
    const page = await boot();
    page.$('#watcherStatusMenu .watcher-text').remove();
    serverEvent(page, 'watcher_status', { running: false, enabled: false });
    assert.equal(page.$('#watcherStatusMenu').title, 'File watcher is disabled');
    assert.ok(page.$('#watcherStatusMenu').classList.contains('stopped'));
    for (const id of ['watcherStatus', 'watcherStatusMenu', 'watcherStatusSidebar']) page.$(`#${id}`).remove();
    serverEvent(page, 'watcher_status', { running: true, enabled: true });
    assert.deepEqual(handlerErrors(page), []);
});

test('processed files refresh the library health once, without rebuilding the series list', async t => {
    useMockTimers(t);
    const page = await boot({ hash: '#/library', routes: { 'GET /api/files/series': seriesPage([SERIES]) } });
    const seriesLoads = requests(page, 'GET', '/api/files/series').length;
    const healthLoads = requests(page, 'GET', '/api/files/counts').length;
    serverEvent(page, 'file_processed', { filename: 'Saga/Saga 001.cbz', success: true, error: null });
    t.mock.timers.tick(400);
    serverEvent(page, 'file_processed', { filename: 'Saga/Saga 002.cbz', success: true, error: null });
    t.mock.timers.tick(749);
    await page.settle(2);
    assert.equal(requests(page, 'GET', '/api/files/counts').length, healthLoads);
    t.mock.timers.tick(1);
    await page.settle(2);
    assert.equal(requests(page, 'GET', '/api/files/counts').length, healthLoads + 1);
    assert.equal(requests(page, 'GET', '/api/files/series').length, seriesLoads);
    assert.ok(consoleLines(page, 'log').includes('SSE: File processed: Saga/Saga 002.cbz Success: true'));
});

test('a processed file reloads the issues of the open series in place', async () => {
    const page = await boot({ hash: `#/series/${SERIES.id}`, routes: seriesRoutes });
    const issueLoads = requests(page, 'GET', ISSUES_PATH).length;
    assert.ok(issueLoads >= 1);
    serverEvent(page, 'file_processed', { filename: 'Saga/Saga 002.cbz', success: true, error: null });
    await page.settle(3);
    assert.equal(requests(page, 'GET', ISSUES_PATH).length, issueLoads + 1);
    assert.equal(page.window.location.hash, `#/series/${SERIES.id}`);
});

test('in the legacy files view a processed file only invalidates its folder cache', async () => {
    const page = await boot({ hash: '#/library' });
    page.run(`libraryViewMode = 'files'; folderFiles.set('Saga', { status: 'error', files: [] });`);
    const fetches = page.fetchCalls.length;
    serverEvent(page, 'file_processed', { filename: 'Saga/Saga 004.cbz', success: true, error: null });
    assert.equal(page.run("folderFiles.has('Saga')"), false);
    serverEvent(page, 'file_processed', { success: false, error: 'No file name' });
    await page.settle(2);
    assert.equal(page.fetchCalls.length, fetches);
    assert.deepEqual(handlerErrors(page), []);
});

test('processed files are listed in the progress dialog of the running job', async () => {
    const page = await bootWithRunningJob();
    assert.ok(progressModalOpen(page));
    serverEvent(page, 'file_processed', { filename: 'Saga/Saga 002.cbz', success: true, error: null });
    serverEvent(page, 'file_processed', { filename: 'Saga/Saga 003.cbz', success: false, error: 'Corrupt archive' });
    const rows = page.$$('#progressDetails .progress-result-item').map(row => ({
        status: row.classList.contains('success') ? 'success' : 'error',
        name: row.querySelector('strong').textContent,
        detail: row.querySelector('span').textContent
    }));
    assert.deepEqual(rows, [
        { status: 'error', name: 'Saga 003.cbz', detail: 'Corrupt archive' },
        { status: 'success', name: 'Saga 002.cbz', detail: 'Completed successfully' },
        { status: 'success', name: 'Saga 001.cbz', detail: 'Completed successfully' }
    ]);
    assert.equal(page.$('#progressCurrentFile').textContent, 'Failed: Saga 003.cbz');
    assert.equal(page.$('#progressResultsCount').textContent, '3 results');
});

test('bursts of file list updates reload the library once', async t => {
    useMockTimers(t);
    const page = await boot({ hash: '#/library', routes: { 'GET /api/files/series': seriesPage([SERIES]) } });
    const seriesLoads = requests(page, 'GET', '/api/files/series').length;
    const healthLoads = requests(page, 'GET', '/api/files/counts').length;
    for (let i = 0; i < 3; i++) {
        serverEvent(page, 'file_list_updated', {});
        t.mock.timers.tick(200);
    }
    t.mock.timers.tick(299);
    await page.settle(2);
    assert.equal(requests(page, 'GET', '/api/files/series').length, seriesLoads);
    t.mock.timers.tick(1);
    await page.settle(3);
    const reloads = requests(page, 'GET', '/api/files/series').slice(seriesLoads);
    assert.equal(reloads.length, 1);
    assert.equal(reloads[0].query.refresh, undefined, 'a quiet reload, not a cache-busting refresh');
    t.mock.timers.tick(250);
    await page.settle(2);
    assert.equal(requests(page, 'GET', '/api/files/counts').length, healthLoads + 1);
    assert.equal(page.$$('#fileList .series-card').length, 1);
});

test('a file list update with a series open reloads only its issues', async t => {
    useMockTimers(t);
    const page = await boot({ hash: `#/series/${SERIES.id}`, routes: seriesRoutes });
    const issueLoads = requests(page, 'GET', ISSUES_PATH).length;
    const seriesLoads = requests(page, 'GET', '/api/files/series').length;
    serverEvent(page, 'file_list_updated', {});
    t.mock.timers.tick(500);
    await page.settle(3);
    assert.equal(requests(page, 'GET', ISSUES_PATH).length, issueLoads + 1);
    assert.equal(requests(page, 'GET', '/api/files/series').length, seriesLoads);
});

test('job updates for other jobs, or with no job running, are ignored', async () => {
    const idle = await boot();
    serverEvent(idle, 'job_updated', { job_id: JOB_ID, status: 'running', progress: { processed: 1, total: 2, success: 1, errors: 0, percentage: 50 } });
    assert.ok(consoleLines(idle, 'log').includes(`SSE: Ignoring job update for ${JOB_ID} (not current job)`));
    assert.equal(idle.$('#progressText').textContent, '0 / 0 files');

    const page = await bootWithRunningJob();
    assert.equal(page.$('#progressText').textContent, '1 / 4 files');
    serverEvent(page, 'job_updated', { job_id: 'another-job', status: 'running', progress: { processed: 7, total: 9, success: 7, errors: 0, percentage: 77 } });
    assert.equal(page.$('#progressText').textContent, '1 / 4 files');
});

test('job progress updates move the bar, counters and title', async () => {
    const page = await bootWithRunningJob();
    serverEvent(page, 'job_updated', { job_id: JOB_ID, status: 'running', progress: { processed: 2, total: 4, success: 1, errors: 1, percentage: 50 } });
    assert.equal(page.$('#progressBarFill').style.width, '50%');
    assert.equal(page.$('#progressText').textContent, '2 / 4 files');
    assert.equal(page.$('#progressPercent').textContent, '50%');
    assert.deepEqual(['#progressProcessedCount', '#progressSuccessCount', '#progressErrorCount'].map(s => page.$(s).textContent), ['2', '1', '1']);
    assert.equal(page.$('#progressTitle').textContent, 'Processing 4 files... - 1 succeeded, 1 failed');
    assert.ok(consoleLines(page, 'log').includes(`SSE: Job ${JOB_ID} updated - status: running, progress: 2/4`));

    serverEvent(page, 'job_updated', { job_id: JOB_ID, status: 'queued' });
    assert.equal(page.$('#progressText').textContent, '0 / 0 files');
    assert.equal(page.$('#progressTitle').textContent, 'Processing 4 files...');
    assert.ok(progressModalOpen(page));
});

test('a completed job reports its totals, refreshes the library and closes the dialog', async t => {
    useMockTimers(t);
    const page = await bootWithRunningJob({ hash: '#/library' });
    page.run("selectedFiles.add('Saga/Saga 001.cbz'); selectedSeries.add('series-1')");
    const seriesLoads = requests(page, 'GET', '/api/files/series').length;
    const healthLoads = requests(page, 'GET', '/api/files/counts').length;
    serverEvent(page, 'job_updated', { job_id: JOB_ID, status: 'completed', progress: { processed: 4, total: 4, success: 3, errors: 1, percentage: 100 } });
    assert.equal(page.$('#progressText').textContent, '4 / 4 files');
    t.mock.timers.tick(250);
    await page.settle(2);
    assert.equal(requests(page, 'GET', '/api/files/counts').length, healthLoads + 1, 'library health refreshed quickly');
    t.mock.timers.tick(250);
    await page.settle(3);

    assert.equal(page.$('#progressTitle').textContent, 'Completed! All 4 items processed (3 succeeded, 1 failed)');
    assert.equal(page.$('#progressCloseBtn').style.display, 'block');
    assert.equal(page.$('#progressCancelBtn').style.display, 'none');
    assert.equal(page.$('#progressCurrentFile').textContent, 'Finished: Processing 4 files...');
    assert.equal(page.run('hasActiveJob'), false);
    assert.equal(page.run('currentJobTitle'), null);
    assert.equal(page.run('selectedFiles.size + selectedSeries.size'), 0);
    const reloads = requests(page, 'GET', '/api/files/series').slice(seriesLoads);
    assert.deepEqual(reloads.map(call => call.query.refresh), ['true']);

    assert.ok(progressModalOpen(page));
    t.mock.timers.tick(999);
    assert.ok(progressModalOpen(page));
    t.mock.timers.tick(1);
    assert.equal(progressModalOpen(page), false);
});

test('a completed job with a series open refreshes that series instead of the library', async t => {
    useMockTimers(t);
    const page = await bootWithRunningJob({ hash: `#/series/${SERIES.id}`, routes: seriesRoutes });
    const issueLoads = requests(page, 'GET', ISSUES_PATH).length;
    const seriesLoads = requests(page, 'GET', '/api/files/series').length;
    const healthLoads = requests(page, 'GET', '/api/files/counts').length;
    serverEvent(page, 'job_updated', { job_id: JOB_ID, status: 'completed', progress: { processed: 4, total: 4, success: 4, errors: 0, percentage: 100 } });
    t.mock.timers.tick(500);
    await page.settle(3);
    assert.equal(requests(page, 'GET', ISSUES_PATH).length, issueLoads + 1);
    assert.equal(requests(page, 'GET', '/api/files/series').length, seriesLoads);
    assert.equal(page.$('#progressTitle').textContent, 'Completed! All 4 items processed (4 succeeded, 0 failed)');
    t.mock.timers.tick(1000);
    await page.settle(2);
    assert.equal(progressModalOpen(page), false);
    assert.equal(requests(page, 'GET', '/api/files/counts').length, healthLoads + 2, 'the 250 ms refresh and the post-reload refresh');
});

for (const { status, title, closeAfter } of [
    { status: 'failed', title: 'Failed - Job processing failed', closeAfter: 3000 },
    { status: 'cancelled', title: 'Cancelled - Job was cancelled', closeAfter: 2000 },
    { status: 'interrupted', title: 'Interrupted - the server restarted after 2 of 4 items', closeAfter: 5000 }
]) {
    test(`a ${status} job is reported and its dialog closes after ${closeAfter / 1000}s`, async t => {
        useMockTimers(t);
        const page = await bootWithRunningJob();
        const seriesLoads = requests(page, 'GET', '/api/files/series').length;
        serverEvent(page, 'job_updated', { job_id: JOB_ID, status, progress: { processed: 2, total: 4, success: 2, errors: 0, percentage: 50 } });
        t.mock.timers.tick(499);
        assert.equal(page.$('#progressTitle').textContent, 'Processing 4 files...');
        t.mock.timers.tick(1);
        assert.equal(page.$('#progressTitle').textContent, title);
        assert.equal(page.$('#progressCloseBtn').style.display, 'block');
        assert.equal(page.run('hasActiveJob'), false);
        assert.equal(page.run('currentJobTitle'), null);
        t.mock.timers.tick(closeAfter - 1);
        assert.ok(progressModalOpen(page));
        t.mock.timers.tick(1);
        assert.equal(progressModalOpen(page), false);
        await page.settle(2);
        assert.equal(requests(page, 'GET', '/api/files/series').length, seriesLoads, 'the library is not reloaded');
    });
}
