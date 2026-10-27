'use strict';

// Picking up a batch job that was running when the page was last closed
// (checkAndResumeActiveJob() at boot, js/app/jobs.js) and cancelling the
// tracked job from the progress modal (cancelCurrentJob()).

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { validToken } = require('./support/app');
const h = require('./support/jobs-helpers');

const { JOB_ID, LIBRARY_RELOAD } = h;
const JOB_PATH = `/api/jobs/${JOB_ID}`;
const ACTIVE_JOB = { job_id: JOB_ID, job_title: 'Renaming All Files...' };
const finished = (file, success, error = null) => ({ file, success, error });

function bootResuming(t, status, activeJob = ACTIVE_JOB) {
    return h.bootJobsApp(t, { routes: { 'GET /api/active-job': activeJob, [`GET ${JOB_PATH}`]: status } });
}

test('a job still running at boot reopens the progress modal and keeps being tracked', async (t) => {
    const page = await bootResuming(t, h.jobStatus({
        processed_items: 1,
        current_file: 'Comics/Batman/Batman 002.cbz',
        results: [finished('Comics/Batman/Batman 001.cbz', true)]
    }));

    assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: 'Resuming active job...' }]);
    const lookups = h.requests(page, 'GET', JOB_PATH);
    assert.equal(lookups.length, 2, 'the job is checked, then polled to fill in the modal');
    assert.match(lookups[0].headers.authorization, /^Bearer /);
    h.assertProgress(page, {
        open: true,
        title: 'Renaming All Files...',
        text: '1 / 4 files',
        percent: '25%',
        succeeded: '1',
        failed: '0',
        currentFile: 'Completed: Batman 001.cbz',
        results: [{ success: true, file: 'Batman 001.cbz', detail: 'Completed successfully' }],
        cancelVisible: true,
        closeVisible: false
    });

    h.jobUpdate(page, 'running', h.progress(3, 4, 2, 1));
    h.assertProgress(page, { title: 'Renaming All Files... - 2 succeeded, 1 failed', text: '3 / 4 files' });
    assert.deepEqual(page.errors, []);
});

test('a queued job without a title resumes under a generic one', async (t) => {
    let polls = 0;
    let releasePoll;
    const page = await bootResuming(t, () => (++polls === 1
        ? h.jobStatus({ status: 'queued' })
        : new Promise(resolve => { releasePoll = resolve; })), { job_id: JOB_ID });

    assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: 'Resuming active job...' }]);
    h.assertProgress(page, h.startingProgress('Resuming Job...'));

    releasePoll(h.jobStatus({ status: 'queued' }));
    await page.settle();
    h.assertProgress(page, { open: true, title: 'Processing...', text: '0 / 4 files', currentFile: 'Waiting for the next completed file...' });
    assert.deepEqual(page.errors, []);
});

const FINISHED_WHILE_AWAY = [
    {
        name: 'completed with some failures',
        status: h.jobStatus({
            status: 'completed',
            processed_items: 3,
            failed_items: 1,
            results: [
                finished('Comics/A.cbz', true),
                finished('Comics/B.cbz', false, 'Corrupt archive'),
                finished('Comics/C.cbz', true),
                finished('Comics/D.cbz', true)
            ]
        }),
        message: { type: 'success', text: 'Batch processing completed: 3 of 4 files processed successfully, 1 failed' },
        reloads: 1
    },
    {
        name: 'completed without failures',
        status: h.jobStatus({ status: 'completed', total_items: 1, processed_items: 1, results: [finished('Comics/A.cbz', true)] }),
        message: { type: 'success', text: 'Batch processing completed: 1 of 1 files processed successfully' },
        reloads: 1
    },
    {
        name: 'completed with every file failed',
        status: h.jobStatus({
            status: 'completed',
            total_items: 2,
            failed_items: 2,
            results: [finished('Comics/A.cbz', false, 'Corrupt archive'), finished('Comics/B.cbz', false, 'Corrupt archive')]
        }),
        message: { type: 'warning', text: 'Batch processing completed: 0 of 2 files processed successfully, 2 failed' },
        reloads: 1
    },
    {
        name: 'completed with no counts',
        status: { job_id: JOB_ID, status: 'completed' },
        message: { type: 'warning', text: 'Batch processing completed: 0 of 0 files processed successfully' },
        reloads: 1
    },
    {
        // JobsController.GetJob has no error field (see the final report).
        name: 'failed',
        status: h.jobStatus({ status: 'failed' }),
        message: { type: 'error', text: 'Batch processing failed: Unknown error' },
        reloads: 0
    },
    {
        name: 'failed with a reason',
        status: h.jobStatus({ status: 'failed', error: 'Disk full' }),
        message: { type: 'error', text: 'Batch processing failed: Disk full' },
        reloads: 0
    },
    {
        name: 'cancelled',
        status: h.jobStatus({ status: 'cancelled', processed_items: 1 }),
        message: { type: 'warning', text: 'Batch processing was cancelled' },
        reloads: 0
    },
    {
        name: 'interrupted',
        status: h.jobStatus({ status: 'interrupted', processed_items: 2, failed_items: 1 }),
        message: {
            type: 'warning',
            text: 'Batch processing was interrupted by a server restart after 3 of 4 files. Re-run it to process the rest.'
        },
        reloads: 1
    },
    {
        name: 'interrupted with no counts',
        status: { job_id: JOB_ID, status: 'interrupted' },
        message: {
            type: 'warning',
            text: 'Batch processing was interrupted by a server restart after 0 of 0 files. Re-run it to process the rest.'
        },
        reloads: 1
    }
];

for (const scenario of FINISHED_WHILE_AWAY) {
    test(`a job ${scenario.name} while the page was closed is reported at boot`, async (t) => {
        const page = await bootResuming(t, scenario.status);

        assert.deepEqual(h.takeMessages(page), [scenario.message]);
        assert.equal(h.requests(page, 'GET', JOB_PATH).length, 1, 'a finished job is not tracked');
        assert.equal(h.requests(page, 'GET', LIBRARY_RELOAD).length, scenario.reloads);
        h.assertProgress(page, { open: false, indicatorVisible: false });
        assert.deepEqual(page.errors, []);
    });
}

test('a job in a state the page does not know is left alone at boot', async (t) => {
    const page = await bootResuming(t, h.jobStatus({ status: 'paused' }));

    assert.deepEqual(h.messages(page), []);
    assert.equal(h.requests(page, 'GET', JOB_PATH).length, 1);
    assert.equal(h.requests(page, 'GET', LIBRARY_RELOAD).length, 0);
    assert.equal(h.progressView(page).open, false);
    assert.deepEqual(page.errors, []);
});

test('an active job id that is not a job id is ignored at boot', async (t) => {
    const page = await bootResuming(t, h.jobStatus(), { job_id: 'job-1', job_title: 'Processing Files...' });

    assert.deepEqual(h.messages(page), []);
    assert.equal(page.fetchCalls.filter(call => call.path.startsWith('/api/jobs/')).length, 0);
    assert.ok(page.consoleMessages.some(message => message.type === 'warn'
        && message.args[0] === '[JOB RESUME] Invalid job_id format: job-1 (expected UUID) - ignoring'));
    assert.equal(h.progressView(page).open, false);
    assert.deepEqual(page.errors, []);
});

const LOOKUP_FAILURES = [
    { name: 'no longer exists (404)', answer: () => respond(404, { error: 'Job not found' }), message: { type: 'warning', text: 'Previous batch processing job is no longer available' } },
    { name: 'is refused (403)', answer: () => 403, message: { type: 'warning', text: 'Previous batch processing job is no longer available' } },
    { name: 'fails on the server (500)', answer: () => respond(500, 'Error'), message: { type: 'error', text: 'Server error checking job status. Please refresh to try again.' } },
    { name: 'cannot be reached', answer: () => { throw new TypeError('Failed to fetch'); }, message: { type: 'warning', text: 'Could not check job status. Please refresh to try again.' } }
];

for (const failure of LOOKUP_FAILURES) {
    test(`an active job whose status ${failure.name} is not resumed`, async (t) => {
        const page = await bootResuming(t, failure.answer);

        assert.deepEqual(h.takeMessages(page), [failure.message]);
        assert.equal(h.requests(page, 'GET', JOB_PATH).length, 1);
        assert.equal(h.progressView(page).open, false);
        assert.equal(h.loginRedirects(page), 0);
        assert.deepEqual(page.errors, []);
    });
}

test('an active job whose status needs a fresh sign-in sends the user to the login page', async (t) => {
    const page = await bootResuming(t, 401);

    assert.equal(h.loginRedirects(page), 1);
    assert.deepEqual(h.messages(page), []);
    assert.equal(h.progressView(page).open, false);
    assert.deepEqual(page.errors, []);
});

test('Cancel asks first, then asks the server to cancel the tracked job', async (t) => {
    const CONFIRM = 'Are you sure you want to cancel the current batch processing job?';
    let answer = () => ({ success: true });
    const job = h.trackedJob();
    const page = await h.bootJobsApp(t, { routes: { ...job.routes, [`POST ${JOB_PATH}/cancel`]: call => answer(call) } });
    const cancels = () => h.requests(page, 'POST', `${JOB_PATH}/cancel`);

    // Before any job has been tracked there is nothing to cancel.
    page.run('cancelCurrentJob()');
    await page.settle();
    assert.deepEqual(h.takeConfirms(page), []);
    assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: 'No active job to cancel' }]);

    await h.startTrackedJob(page);
    h.answerConfirms(page, false);
    page.click('#progressCancelBtn');
    await page.settle();
    assert.deepEqual(h.takeConfirms(page), [CONFIRM]);
    assert.equal(cancels().length, 0);
    h.assertProgress(page, { open: true, cancelVisible: true, closeVisible: false });

    // A refused or failed cancel leaves the job running in the modal.
    for (const [response, reason] of [
        [() => respond(404, { success: false, error: 'Job not found or already completed' }), 'Job not found or already completed'],
        [() => respond(500, 'Internal Server Error'), 'Failed to cancel job (HTTP 500)'],
        [() => ({ success: false, error: 'Job is finishing' }), 'Job is finishing'],
        [() => ({ success: false }), 'Failed to cancel job'],
        [() => { throw new TypeError('Failed to fetch'); }, 'Failed to fetch']
    ]) {
        answer = response;
        page.click('#progressCancelBtn');
        await page.settle();
        assert.deepEqual(h.takeConfirms(page), [CONFIRM]);
        assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: `Failed to cancel job: ${reason}` }]);
        h.assertProgress(page, { open: true, title: h.TRACKED_TITLE, cancelVisible: true, closeVisible: false });
    }

    answer = () => ({ success: true });
    page.click('#progressCancelBtn');
    await page.settle();
    assert.deepEqual(h.takeConfirms(page), [CONFIRM]);
    assert.equal(cancels().length, 6);
    assert.deepEqual(h.takeMessages(page), [{ type: 'warning', text: 'Batch processing cancelled' }]);
    h.assertProgress(page, { open: true, cancelVisible: true });

    // The server then reports the job cancelled.
    h.jobUpdate(page, 'cancelled', h.progress(1, 4));
    await h.tick(t, page, 500);
    h.assertProgress(page, { title: 'Cancelled - Job was cancelled', cancelVisible: false, closeVisible: true });
    await h.tick(t, page, 2000);
    assert.equal(h.progressView(page).open, false);
    assert.deepEqual(page.errors, []);
});

test('Cancel sends the session token, which the server requires to cancel the job', async (t) => {
    const token = validToken();
    const job = h.trackedJob();
    // JobsController is [Authorize] and reads a local login's JWT only from
    // the Authorization header: without it the cancel is answered 401.
    const page = await h.bootJobsApp(t, {
        localStorage: { jwt_token: token },
        routes: {
            ...job.routes,
            [`POST ${JOB_PATH}/cancel`]: call => (call.headers.authorization === `Bearer ${token}` ? { success: true } : respond(401))
        }
    });
    await h.startTrackedJob(page);

    page.click('#progressCancelBtn');
    await page.settle();
    const cancels = h.requests(page, 'POST', `${JOB_PATH}/cancel`);
    assert.equal(cancels.length, 1);
    assert.equal(cancels[0].headers.authorization, `Bearer ${token}`);
    assert.equal(h.loginRedirects(page), 0);
    assert.deepEqual(h.takeMessages(page), [{ type: 'warning', text: 'Batch processing cancelled' }]);
    h.assertProgress(page, { open: true, title: h.TRACKED_TITLE, cancelVisible: true, closeVisible: false });

    h.jobUpdate(page, 'cancelled', h.progress(1, 4));
    await h.tick(t, page, 500);
    h.assertProgress(page, { title: 'Cancelled - Job was cancelled', cancelVisible: false, closeVisible: true });
    await h.tick(t, page, 2000);
    assert.equal(h.progressView(page).open, false);
    assert.deepEqual(page.errors, []);
});

test('a cancel the server answers 401 sends the user to the login page', async (t) => {
    const job = h.trackedJob();
    const page = await h.bootJobsApp(t, { routes: { ...job.routes, [`POST ${JOB_PATH}/cancel`]: respond(401) } });
    await h.startTrackedJob(page);

    page.click('#progressCancelBtn');
    await page.settle();
    assert.equal(h.requests(page, 'POST', `${JOB_PATH}/cancel`).length, 1);
    assert.equal(h.loginRedirects(page), 1);
    assert.deepEqual(h.takeMessages(page), []);
    assert.deepEqual(page.errors, []);
});
