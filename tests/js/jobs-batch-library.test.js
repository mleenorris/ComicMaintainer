'use strict';

// Library-wide batch jobs started from the library toolbar's action menus
// (js/app/jobs.js): confirm() → optional force-reprocess prompt → POST
// /api/jobs/... → the progress modal tracks the job; plus the declined,
// nothing-to-do, failed, network-error and signed-out paths.

const test = require('node:test');
const assert = require('node:assert/strict');
const { deferred } = require('./support/app');
const { respond } = require('./support/dom');
const h = require('./support/jobs-helpers');

// promptForForceReprocess() (js/app/library.js) asks this after the action's own confirm().
const forcePrompt = (description, status) =>
    `${description}\n\nClick OK to include files already marked as ${status}, or Cancel to skip them.`;

const fromMenu = (menuId, label) => page => h.chooseMenuItem(page, menuId, label);

const LIBRARY_ACTIONS = [
    {
        name: 'Process All',
        start: fromMenu('processDropdown', 'Process All'),
        confirm: 'This will process all files in the watched directory asynchronously. Continue?',
        force: forcePrompt('This will process all files in the watched directory asynchronously.', 'processed'),
        path: '/api/jobs/process-all',
        starting: 'Starting async processing...',
        started: n => `Started processing ${n} files in background`,
        nothingToDo: 'No files found to process',
        tracked: 'Processing Files...',
        failure: 'Failed to start processing: ',
        serverFailure: 'Failed to start processing job'
    },
    {
        // Not wired to the toolbar (it has the async variant above); the page exposes it as a global.
        name: 'processAllFiles()',
        start: page => { page.run('processAllFiles()'); },
        confirm: 'This will process all files in the watched directory. Continue?',
        force: forcePrompt('This will process all files in the watched directory.', 'processed'),
        path: '/api/jobs/process-all',
        starting: 'Starting processing...',
        started: n => `Started processing ${n} files in background`,
        nothingToDo: 'No files found to process',
        tracked: 'Processing All Files...',
        failure: 'Failed to start processing: ',
        serverFailure: 'Failed to start processing job'
    },
    {
        name: 'Process Unmarked',
        start: fromMenu('processDropdown', 'Process Unmarked'),
        confirm: 'This will process all unmarked files in the watched directory. Continue?',
        path: '/api/jobs/process-unmarked',
        starting: 'Starting async processing...',
        started: n => `Started processing ${n} unmarked files in background`,
        nothingToDo: 'No unmarked files found to process',
        tracked: 'Processing Unmarked Files...',
        failure: 'Failed to start processing: ',
        serverFailure: 'Failed to start processing job'
    },
    {
        name: 'Rename All',
        start: fromMenu('renameDropdown', 'Rename All'),
        confirm: 'This will rename all files in the watched directory based on metadata. Continue?',
        force: forcePrompt('This will rename all files in the watched directory based on metadata.', 'renamed'),
        path: '/api/jobs/rename-all',
        starting: 'Starting rename...',
        started: n => `Started renaming ${n} files in background`,
        nothingToDo: 'No files found to rename',
        tracked: 'Renaming All Files...',
        failure: 'Failed to start renaming: ',
        serverFailure: 'Failed to start renaming job'
    },
    {
        name: 'Rename Unmarked',
        start: fromMenu('renameDropdown', 'Rename Unmarked'),
        confirm: 'This will rename all unmarked files in the watched directory based on metadata. Continue?',
        path: '/api/jobs/rename-unmarked',
        starting: 'Starting async renaming...',
        started: n => `Started renaming ${n} unmarked files in background`,
        nothingToDo: 'No unmarked files found to rename',
        tracked: 'Renaming Unmarked Files...',
        failure: 'Failed to start renaming: ',
        serverFailure: 'Failed to start renaming job'
    },
    {
        name: 'Normalize All',
        start: fromMenu('normalizeDropdown', 'Normalize All'),
        confirm: 'This will normalize metadata for all files in the watched directory. Continue?',
        force: forcePrompt('This will normalize metadata for all files in the watched directory.', 'normalized'),
        path: '/api/jobs/normalize-all',
        starting: 'Starting normalize...',
        started: n => `Started normalizing ${n} files in background`,
        nothingToDo: 'No files found to normalize',
        tracked: 'Normalizing All Files...',
        failure: 'Failed to start normalizing: ',
        serverFailure: 'Failed to start normalizing job'
    },
    {
        name: 'Normalize Unmarked',
        start: fromMenu('normalizeDropdown', 'Normalize Unmarked'),
        confirm: 'This will normalize metadata for all unmarked files in the watched directory. Continue?',
        force: forcePrompt('This will normalize metadata for all unmarked files in the watched directory.', 'normalized'),
        path: '/api/jobs/normalize-unmarked',
        starting: 'Starting async normalizing...',
        started: n => `Started normalizing ${n} unmarked files in background`,
        nothingToDo: 'No unmarked files found to normalize',
        tracked: 'Normalizing Unmarked Files...',
        failure: 'Failed to start normalizing: ',
        serverFailure: 'Failed to start normalizing job'
    },
    {
        name: 'Remove Metadata All',
        start: fromMenu('metadataDropdown', 'Remove Metadata All'),
        confirm: 'Remove embedded metadata (ComicInfo.xml) from ALL files in the library?\n\n'
            + 'Every archive will be rewritten without its ComicInfo.xml and all files will be marked unprocessed so they can be re-processed. This may take a while.',
        path: '/api/jobs/remove-metadata-all',
        starting: 'Starting metadata removal...',
        started: n => `Started removing metadata from ${n} files in background`,
        nothingToDo: 'No files found in library',
        tracked: 'Removing Metadata from All Files...',
        failure: 'Failed to remove metadata: ',
        serverFailure: 'Failed to start remove metadata job'
    }
];

// GET /api/jobs/{id} right after the start: one file done, the next one in progress.
const JOB_AFTER_START = h.jobStatus({
    total_items: 4,
    processed_items: 1,
    current_file: 'Comics/Batman/Batman 002.cbz',
    results: [{ file: 'Comics/Batman/Batman 001.cbz', success: true, error: null }]
});

for (const action of LIBRARY_ACTIONS) {
    test(`${action.name} confirms, starts the job and tracks it in the progress modal`, async (t) => {
        let answer = _call => ({ job_id: h.JOB_ID, total_items: 4 });
        const page = await h.bootJobsApp(t, {
            routes: {
                [`POST ${action.path}`]: call => answer(call),
                [`GET /api/jobs/${h.JOB_ID}`]: JOB_AFTER_START
            }
        });
        const starts = () => h.requests(page, 'POST', action.path);
        const polls = () => h.requests(page, 'GET', `/api/jobs/${h.JOB_ID}`);

        // Declining the confirmation sends nothing.
        h.answerConfirms(page, false);
        action.start(page);
        await page.settle();
        assert.deepEqual(h.takeConfirms(page), [action.confirm]);
        assert.equal(starts().length, 0);
        assert.equal(h.progressView(page).open, false);

        // Accepted (and, where asked, including already-handled files): the
        // modal opens straight away, before the server answers.
        const pending = deferred();
        answer = () => pending.promise;
        h.answerConfirms(page, true, ...(action.force ? [true] : []));
        action.start(page);
        await page.settle();
        assert.deepEqual(h.takeConfirms(page), action.force ? [action.confirm, action.force] : [action.confirm]);
        assert.equal(starts().length, 1);
        assert.equal(starts()[0].url, action.force ? `${action.path}?forceReprocess=true` : action.path);
        assert.match(starts()[0].headers.authorization, /^Bearer /);
        h.assertProgress(page, h.startingProgress(action.starting));
        assert.equal(polls().length, 0);

        pending.resolve({ job_id: h.JOB_ID, total_items: 4 });
        await page.settle();
        assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: action.started(4) }]);
        assert.equal(polls().length, 1, 'the job is polled once for its current state');
        assert.match(polls()[0].headers.authorization, /^Bearer /);
        h.assertProgress(page, {
            open: true,
            title: action.tracked,
            text: '1 / 4 files',
            percent: '25%',
            bar: '25%',
            processed: '1',
            succeeded: '1',
            failed: '0',
            currentFile: 'Completed: Batman 001.cbz',
            resultsCount: '1 result',
            results: [{ success: true, file: 'Batman 001.cbz', detail: 'Completed successfully' }],
            cancelVisible: true,
            closeVisible: false
        });

        // Server events now drive the modal.
        h.jobUpdate(page, 'running', h.progress(2, 4));
        h.assertProgress(page, { title: action.tracked, text: '2 / 4 files', percent: '50%', succeeded: '2', failed: '0' });

        // The action can start another job once this one has ended.
        await h.endTrackedJob(t, page);

        if (action.force) {
            answer = () => ({ job_id: h.JOB_ID, total_items: 2 });
            h.answerConfirms(page, true, false);
            action.start(page);
            await page.settle();
            assert.deepEqual(h.takeConfirms(page), [action.confirm, action.force]);
            assert.equal(starts().at(-1).url, `${action.path}?forceReprocess=false`, 'Cancel on the force prompt skips handled files');
            assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: action.started(2) }]);
            await h.endTrackedJob(t, page);
        }

        // A server error closes the modal and says the job did not start.
        answer = () => respond(500, { error: 'Database is locked' });
        action.start(page);
        await page.settle();
        h.takeConfirms(page);
        assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: action.failure + action.serverFailure }]);
        assert.equal(h.progressView(page).open, false);

        // So does a network failure, with its own message.
        answer = () => { throw new TypeError('Failed to fetch'); };
        action.start(page);
        await page.settle();
        h.takeConfirms(page);
        assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: `${action.failure}Failed to fetch` }]);
        assert.equal(h.progressView(page).open, false);

        // An expired session goes back to the login page without an error toast.
        answer = () => 401;
        action.start(page);
        await page.settle();
        h.takeConfirms(page);
        assert.equal(h.loginRedirects(page), 1);
        assert.deepEqual(h.messages(page), []);
        assert.equal(h.progressView(page).open, false);
        assert.equal(h.progressView(page).cancelVisible, false);
        assert.deepEqual(page.errors, []);
    });
}

// JobsController's answer when the library (or its unmarked part) has no file
// to work on: no job is created, so there is nothing to track.
const NOTHING_TO_DO = { job_id: '00000000-0000-0000-0000-000000000000', total_items: 0 };

for (const action of LIBRARY_ACTIONS) {
    test(`${action.name} with nothing to do says so instead of tracking an empty job`, async (t) => {
        const page = await h.bootJobsApp(t, { routes: { [`POST ${action.path}`]: NOTHING_TO_DO } });

        h.answerConfirms(page, true, ...(action.force ? [true] : []));
        action.start(page);
        await page.settle();
        assert.deepEqual(h.takeConfirms(page), action.force ? [action.confirm, action.force] : [action.confirm]);
        assert.equal(h.requests(page, 'POST', action.path).length, 1);
        assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: action.nothingToDo }]);
        assert.equal(h.requests(page, 'GET', `/api/jobs/${NOTHING_TO_DO.job_id}`).length, 0, 'there is no job to poll');
        h.assertProgress(page, { open: false, cancelVisible: false, indicatorVisible: false });

        // Nothing runs, so Cancel has nothing to cancel and leaving the page is not questioned.
        page.run('cancelCurrentJob()');
        await page.settle();
        assert.deepEqual(h.takeConfirms(page), []);
        assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: 'No active job to cancel' }]);
        const leave = new page.window.Event('beforeunload', { cancelable: true });
        page.window.dispatchEvent(leave);
        assert.equal(leave.defaultPrevented, false);
        assert.deepEqual(page.errors, []);
    });
}

test('a start answered without a real job id is not tracked either', async (t) => {
    let answer;
    const page = await h.bootJobsApp(t, { routes: { 'POST /api/jobs/process-unmarked': () => answer } });

    for (const data of [{ job_id: NOTHING_TO_DO.job_id, total_items: 4 }, { job_id: '', total_items: 4 }, { total_items: 4 }]) {
        answer = data;
        h.chooseMenuItem(page, 'processDropdown', 'Process Unmarked');
        await page.settle();
        h.takeConfirms(page);
        assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: 'No unmarked files found to process' }], JSON.stringify(data));
        assert.equal(h.progressView(page).open, false);
    }
    assert.equal(h.requests(page, 'POST', '/api/jobs/process-unmarked').length, 3);
    assert.deepEqual(h.requests(page, 'GET', /^\/api\/jobs\//), [], 'no job is polled');
    assert.deepEqual(page.errors, []);
});

test('Remove Metadata All sends JSON with the session cookie and handles empty and single-file libraries', async (t) => {
    let answer = _call => ({ job_id: h.JOB_ID, total_items: 0 });
    const page = await h.bootJobsApp(t, {
        routes: {
            'POST /api/jobs/remove-metadata-all': call => answer(call),
            [`GET /api/jobs/${h.JOB_ID}`]: h.jobStatus({ total_items: 1 })
        }
    });

    h.chooseMenuItem(page, 'metadataDropdown', 'Remove Metadata All');
    await page.settle();
    const [start] = h.requests(page, 'POST', '/api/jobs/remove-metadata-all');
    assert.equal(start.headers['content-type'], 'application/json');
    assert.equal(start.init.credentials, 'same-origin');
    assert.equal(start.body, undefined);
    assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: 'No files found in library' }]);
    assert.equal(h.progressView(page).open, false);
    assert.equal(h.requests(page, 'GET', `/api/jobs/${h.JOB_ID}`).length, 0, 'there is no job to track');

    answer = () => ({ job_id: h.JOB_ID, total_items: 1 });
    h.chooseMenuItem(page, 'metadataDropdown', 'Remove Metadata All');
    await page.settle();
    assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: 'Started removing metadata from 1 file in background' }]);
    h.assertProgress(page, { open: true, title: 'Removing Metadata from All Files...', text: '0 / 1 files' });
    assert.deepEqual(page.errors, []);
});

test('read-only users get no batch menus, and a refused start reports the failure', async (t) => {
    const page = await h.bootJobsApp(t, {
        user: { canModifyLibrary: false, canAdminister: false },
        routes: { 'POST /api/jobs/process-unmarked': respond(403, { error: 'Forbidden' }) }
    });

    assert.ok(page.document.body.classList.contains('user-read-only'));
    for (const menuId of ['processDropdown', 'renameDropdown', 'normalizeDropdown', 'markDropdown', 'metadataDropdown']) {
        assert.ok(page.$(`#${menuId}`).closest('[data-requires-write]'), `#${menuId} is hidden from read-only users`);
    }
    assert.ok(page.$('#deleteSelectedBtn').hasAttribute('data-requires-write'));

    // The server enforces it too (CanModifyLibrary): a start that still gets
    // through is refused and reported like any other failed start.
    page.run('processUnmarkedFiles()');
    await page.settle();
    assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: 'Failed to start processing: Failed to start processing job' }]);
    assert.equal(h.progressView(page).open, false);
    assert.equal(h.loginRedirects(page), 0);
    assert.deepEqual(page.errors, []);
});
