'use strict';

// Batch jobs over the current selection (js/app/jobs.js): the Process /
// Rename / Normalize / Remove Metadata "Selected" menu items, Delete and
// Clear Processed Status, started from a series detail page whose issues are
// ticked like a user would.

const test = require('node:test');
const assert = require('node:assert/strict');
const { deferred } = require('./support/app');
const { respond } = require('./support/dom');
const h = require('./support/jobs-helpers');

const { ISSUE_A, ISSUE_B } = h;
const plural = n => (n > 1 ? 's' : '');
const forcePrompt = (description, status) =>
    `${description}\n\nClick OK to include files already marked as ${status}, or Cancel to skip them.`;

const SELECTED_ACTIONS = [
    {
        name: 'Process Selected',
        menu: ['processDropdown', 'Process Selected'],
        itemId: 'processSelectedItem',
        call: 'processSelectedFilesAsync()',
        empty: 'Please select at least one file to process',
        confirm: n => `This will process ${n} selected file${plural(n)} asynchronously. Continue?`,
        force: n => forcePrompt(`This will process ${n} selected file${plural(n)}.`, 'processed'),
        path: '/api/jobs/process-selected',
        starting: 'Starting async processing...',
        started: n => `Started processing ${n} files in background`,
        tracked: 'Processing Selected Files...',
        failure: 'Failed to start processing: ',
        serverFailure: 'Failed to start processing job'
    },
    {
        // The non-async variant is not wired to the toolbar; the page exposes it as a global.
        name: 'processSelectedFiles()',
        call: 'processSelectedFiles()',
        empty: 'No files selected',
        confirm: n => `This will process ${n} selected file${plural(n)}. Continue?`,
        force: n => forcePrompt(`This will process ${n} selected file${plural(n)}.`, 'processed'),
        path: '/api/jobs/process-selected',
        starting: 'Starting processing...',
        started: n => `Started processing ${n} selected files in background`,
        tracked: 'Processing Selected Files...',
        failure: 'Failed to start processing: ',
        serverFailure: 'Failed to start processing job'
    },
    {
        name: 'Rename Selected',
        menu: ['renameDropdown', 'Rename Selected'],
        itemId: 'renameSelectedItem',
        call: 'renameSelectedFiles()',
        empty: 'No files selected',
        confirm: n => `This will rename ${n} selected file${plural(n)} based on metadata. Continue?`,
        force: n => forcePrompt(`This will rename ${n} selected file${plural(n)} based on metadata.`, 'renamed'),
        path: '/api/jobs/rename-selected',
        starting: 'Starting rename...',
        started: n => `Started renaming ${n} selected files in background`,
        tracked: 'Renaming Selected Files...',
        failure: 'Failed to start renaming: ',
        serverFailure: 'Failed to start renaming job'
    },
    {
        name: 'Normalize Selected',
        menu: ['normalizeDropdown', 'Normalize Selected'],
        itemId: 'normalizeSelectedItem',
        call: 'normalizeSelectedFiles()',
        empty: 'No files selected',
        confirm: n => `This will normalize metadata for ${n} selected file${plural(n)}. Continue?`,
        force: n => forcePrompt(`This will normalize metadata for ${n} selected file${plural(n)}.`, 'normalized'),
        path: '/api/jobs/normalize-selected',
        starting: 'Starting normalize...',
        started: n => `Started normalizing ${n} selected files in background`,
        tracked: 'Normalizing Selected Files...',
        failure: 'Failed to start normalizing: ',
        serverFailure: 'Failed to start normalizing job'
    },
    {
        name: 'Remove Metadata Selected',
        menu: ['metadataDropdown', 'Remove Metadata Selected'],
        itemId: 'removeMetadataSelectedItem',
        call: 'removeMetadataSelected()',
        empty: 'Please select at least one file',
        confirm: n => `Remove embedded metadata (ComicInfo.xml) from ${n} selected file${plural(n)}?\n\n`
            + 'Each archive will be rewritten without its ComicInfo.xml and the files will be marked unprocessed so they can be re-processed.',
        path: '/api/jobs/remove-metadata-selected',
        credentials: 'same-origin',
        starting: 'Starting metadata removal...',
        started: n => `Started removing metadata from ${n} selected file${n === 1 ? '' : 's'} in background`,
        tracked: 'Removing Metadata from Selected Files...',
        failure: 'Failed to remove metadata: ',
        serverFailure: 'Failed to start remove metadata job'
    }
];

for (const action of SELECTED_ACTIONS) {
    test(`${action.name} starts a job for the selected issues`, async (t) => {
        let answer = _call => ({ job_id: h.JOB_ID, total_items: 2 });
        const page = await h.bootSeriesDetail(t, {
            routes: {
                [`POST ${action.path}`]: call => answer(call),
                [`GET /api/jobs/${h.JOB_ID}`]: h.jobStatus({ total_items: 2 })
            }
        });
        const start = () => (action.menu ? h.chooseMenuItem(page, ...action.menu) : page.run(action.call));
        const starts = () => h.requests(page, 'POST', action.path);
        const confirmsFor = (n, force) => (action.force ? [action.confirm(n), action.force(n)] : [action.confirm(n)]);
        const forceAnswer = answer => (action.force ? [answer] : []);
        const expectedBody = (files, force) => (action.force ? { Files: files, ForceReprocess: force } : { Files: files });

        // Nothing selected yet: the menu item is disabled, and the action
        // itself refuses to run without a selection.
        if (action.itemId) assert.equal(page.$(`#${action.itemId}`).disabled, true);
        page.run(action.call);
        await page.settle();
        assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: action.empty }]);
        assert.deepEqual(h.takeConfirms(page), []);
        assert.equal(h.progressView(page).open, false);

        h.selectIssue(page, ISSUE_A);
        h.selectIssue(page, ISSUE_B);
        if (action.itemId) assert.equal(page.$(`#${action.itemId}`).disabled, false);

        h.answerConfirms(page, false);
        start();
        await page.settle();
        assert.deepEqual(h.takeConfirms(page), [action.confirm(2)]);
        assert.equal(starts().length, 0);
        assert.equal(h.progressView(page).open, false);

        const pending = deferred();
        answer = () => pending.promise;
        h.answerConfirms(page, true, ...forceAnswer(true));
        start();
        await page.settle();
        assert.deepEqual(h.takeConfirms(page), confirmsFor(2));
        assert.equal(starts().length, 1);
        const [post] = starts();
        assert.deepEqual(post.body, expectedBody([ISSUE_A, ISSUE_B], true));
        assert.equal(post.headers['content-type'], 'application/json');
        assert.match(post.headers.authorization, /^Bearer /);
        if (action.credentials) assert.equal(post.init.credentials, action.credentials);
        h.assertProgress(page, h.startingProgress(action.starting));

        pending.resolve({ job_id: h.JOB_ID, total_items: 2 });
        await page.settle();
        assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: action.started(2) }]);
        assert.equal(h.requests(page, 'GET', `/api/jobs/${h.JOB_ID}`).length, 1);
        h.assertProgress(page, { open: true, title: action.tracked, text: '0 / 2 files', cancelVisible: true });
        assert.equal(page.run('selectedFiles.size'), 2, 'the selection is kept while the job runs');

        // A single selected issue is worded in the singular; Cancel on the
        // force prompt skips files that were already handled.
        h.selectIssue(page, ISSUE_B, false);
        answer = () => ({ job_id: h.JOB_ID, total_items: 1 });
        h.answerConfirms(page, true, ...forceAnswer(false));
        start();
        await page.settle();
        assert.deepEqual(h.takeConfirms(page), confirmsFor(1));
        assert.deepEqual(starts().at(-1).body, expectedBody([ISSUE_A], false));
        assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: action.started(1) }]);

        answer = () => respond(500, { error: 'Error starting job' });
        start();
        await page.settle();
        h.takeConfirms(page);
        assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: action.failure + action.serverFailure }]);
        assert.equal(h.progressView(page).open, false);

        answer = () => { throw new TypeError('Failed to fetch'); };
        start();
        await page.settle();
        h.takeConfirms(page);
        assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: `${action.failure}Failed to fetch` }]);
        assert.equal(h.progressView(page).open, false);

        answer = () => 401;
        start();
        await page.settle();
        h.takeConfirms(page);
        assert.equal(h.loginRedirects(page), 1);
        assert.deepEqual(h.messages(page), []);
        assert.equal(h.progressView(page).open, false);
        assert.deepEqual(page.errors, []);
    });
}

test('Delete deletes the selected issues in a background job', async (t) => {
    let answer = _call => ({ job_id: h.JOB_ID, jobId: h.JOB_ID, total_items: 2, totalItems: 2 });
    const page = await h.bootSeriesDetail(t, {
        routes: {
            'POST /api/jobs/delete-selected': call => answer(call),
            [`GET /api/jobs/${h.JOB_ID}`]: h.jobStatus({ total_items: 2 }),
            [`GET /api/jobs/${h.OTHER_JOB_ID}`]: h.jobStatus({ job_id: h.OTHER_JOB_ID, total_items: 1 })
        }
    });
    const deletes = () => h.requests(page, 'POST', '/api/jobs/delete-selected');
    const selectBoth = () => {
        h.selectIssue(page, ISSUE_A);
        h.selectIssue(page, ISSUE_B);
    };

    assert.equal(page.$('#deleteSelectedBtn').disabled, true);
    page.run('deleteSelectedFiles()');
    await page.settle();
    assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: 'No files selected' }]);
    assert.deepEqual(h.takeConfirms(page), []);

    selectBoth();
    assert.equal(page.$('#deleteSelectedBtn').disabled, false);
    h.answerConfirms(page, false);
    page.click('#deleteSelectedBtn');
    await page.settle();
    assert.deepEqual(h.takeConfirms(page), ['Are you sure you want to delete 2 file(s)? This action cannot be undone.']);
    assert.equal(deletes().length, 0);

    const pending = deferred();
    answer = () => pending.promise;
    page.click('#deleteSelectedBtn');
    await page.settle();
    assert.deepEqual(h.takeConfirms(page), ['Are you sure you want to delete 2 file(s)? This action cannot be undone.']);
    assert.deepEqual(deletes()[0].body, { Files: [ISSUE_A, ISSUE_B] });
    assert.equal(deletes()[0].headers['content-type'], 'application/json');
    assert.match(deletes()[0].headers.authorization, /^Bearer /);
    h.assertProgress(page, h.startingProgress('Deleting 2 file(s)...'));

    pending.resolve({ job_id: h.JOB_ID, jobId: h.JOB_ID, total_items: 2, totalItems: 2 });
    await page.settle();
    assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: 'Deleting 2 file(s) in background' }]);
    assert.equal(page.run('selectedFiles.size'), 0, 'the deleted files are no longer selected');
    assert.equal(h.requests(page, 'GET', `/api/jobs/${h.JOB_ID}`).length, 1);
    h.assertProgress(page, { open: true, title: 'Deleting 2 file(s)...', text: '0 / 2 files', cancelVisible: true });

    // The snake_case-only shape is understood too, and without a count the
    // selection size is reported.
    selectBoth();
    answer = () => ({ job_id: h.OTHER_JOB_ID });
    page.click('#deleteSelectedBtn');
    await page.settle();
    h.takeConfirms(page);
    assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: 'Deleting 2 file(s) in background' }]);
    assert.equal(h.requests(page, 'GET', `/api/jobs/${h.OTHER_JOB_ID}`).length, 1);

    // The server's error message is shown when there is one...
    selectBoth();
    answer = () => respond(400, { error: 'No files specified' });
    page.click('#deleteSelectedBtn');
    await page.settle();
    h.takeConfirms(page);
    assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: 'Failed to start delete: No files specified' }]);
    assert.equal(h.progressView(page).open, false);

    // ...otherwise the HTTP status.
    answer = () => respond(502, 'Bad Gateway');
    page.click('#deleteSelectedBtn');
    await page.settle();
    h.takeConfirms(page);
    assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: 'Failed to start delete: Failed to start delete job (HTTP 502)' }]);
    assert.equal(h.progressView(page).open, false);

    answer = () => { throw new TypeError('Failed to fetch'); };
    page.click('#deleteSelectedBtn');
    await page.settle();
    h.takeConfirms(page);
    assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: 'Failed to start delete: Failed to fetch' }]);

    answer = () => 401;
    page.click('#deleteSelectedBtn');
    await page.settle();
    h.takeConfirms(page);
    assert.equal(h.loginRedirects(page), 1);
    assert.deepEqual(h.messages(page), []);
    assert.equal(h.progressView(page).open, false);
    assert.equal(page.run('selectedFiles.size'), 2, 'nothing was deleted, so the selection stays');
    assert.deepEqual(page.errors, []);
});

test('Clear Processed Status clears the flags of the selected issues', async (t) => {
    let answer = _call => ({ cleared: 1, requested: 2 });
    const page = await h.bootSeriesDetail(t, {
        routes: { 'POST /api/status/clear-selected': call => answer(call) }
    });
    const clears = () => h.requests(page, 'POST', '/api/status/clear-selected');
    const clearSelected = () => h.chooseMenuItem(page, 'processDropdown', 'Clear Processed Status');
    const confirmText = n => `Clear the renamed/normalized flags on ${n} selected file${plural(n)}?\n\n`
        + 'The files will then be re-processed on the next Process / Rename / Normalize run.';

    assert.equal(page.$('#clearSelectedStatusItem').disabled, true);
    page.run('clearSelectedProcessedStatus()');
    await page.settle();
    assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: 'Please select at least one file' }]);

    h.selectIssue(page, ISSUE_A);
    h.answerConfirms(page, false);
    clearSelected();
    await page.settle();
    assert.deepEqual(h.takeConfirms(page), [confirmText(1)]);
    assert.equal(clears().length, 0);

    h.selectIssue(page, ISSUE_B);
    answer = () => respond(500, { error: 'Error clearing status' });
    clearSelected();
    await page.settle();
    assert.deepEqual(h.takeConfirms(page), [confirmText(2)]);
    assert.deepEqual(clears()[0].body, { Files: [ISSUE_A, ISSUE_B] });
    assert.equal(clears()[0].init.credentials, 'same-origin');
    assert.match(clears()[0].headers.authorization, /^Bearer /);
    assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: 'Failed to clear processed status' }]);

    answer = () => { throw new TypeError('Failed to fetch'); };
    clearSelected();
    await page.settle();
    assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: 'Failed to clear processed status: Failed to fetch' }]);

    // The server reports how many of the requested files had flags to clear.
    // The list refresh that follows is not asserted: it loads the folder view
    // (loadFiles() → GET /api/files/folders) even on a series page, which
    // looks like a bug.
    answer = () => ({ cleared: 1, requested: 2 });
    clearSelected();
    await page.settle();
    assert.deepEqual(h.takeMessages(page)[0], {
        type: 'success',
        text: 'Cleared processed status on 1 of 2 file(s). Run Process / Rename / Normalize to reapply.'
    });

    answer = () => 401;
    page.run('clearSelectedProcessedStatus()');
    await page.settle();
    assert.equal(h.loginRedirects(page), 1);
    assert.deepEqual(h.messages(page), []);
    assert.deepEqual(page.errors, []);
});
