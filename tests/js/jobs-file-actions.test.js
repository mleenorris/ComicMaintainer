'use strict';

// Actions on one file from a series issue card's menu (js/app/jobs.js):
// Process / Rename / Normalize start a background job tracked in the progress
// modal; Delete and Remove Metadata act directly and refresh the library.

const test = require('node:test');
const assert = require('node:assert/strict');
const { deferred } = require('./support/app');
const { respond } = require('./support/dom');
const h = require('./support/jobs-helpers');

const { ISSUE_A, b64 } = h;
const forcePrompt = (description, status) =>
    `${description}\n\nClick OK to include files already marked as ${status}, or Cancel to skip them.`;

const SINGLE_FILE_JOBS = [
    {
        label: 'Process',
        confirm: `Process ${ISSUE_A}?`,
        force: forcePrompt(`This will process ${ISSUE_A}.`, 'processed'),
        route: 'POST /api/jobs/process-selected',
        assertRequest(call, force) {
            assert.equal(call.headers['content-type'], 'application/json');
            assert.deepEqual(call.body, { Files: [ISSUE_A], ForceReprocess: force });
        },
        accepted: { job_id: h.JOB_ID, total_items: 1 },
        starting: 'Starting processing...',
        started: 'Started processing file in background',
        tracked: 'Processing File...',
        failure: 'Failed to start processing: ',
        serverFailure: 'Failed to start processing job'
    },
    {
        label: 'Rename',
        confirm: `Rename ${ISSUE_A} based on metadata?`,
        force: forcePrompt(`This will rename ${ISSUE_A} based on metadata.`, 'renamed'),
        route: `POST /api/files/${b64(ISSUE_A)}/rename`,
        assertRequest(call, force) {
            assert.equal(call.url, `/api/files/${b64(ISSUE_A)}/rename?forceReprocess=${force}`);
        },
        // FilesController.RenameFileByEncodedPath answers { message, jobId }.
        accepted: { message: 'Rename job started', jobId: h.JOB_ID },
        withoutJob: { message: 'Rename job started' },
        starting: 'Starting rename...',
        started: 'Started renaming file in background',
        tracked: 'Renaming File...',
        failure: 'Failed to start renaming: ',
        serverFailure: 'Failed to start renaming job'
    },
    {
        label: 'Normalize',
        confirm: `Normalize metadata for ${ISSUE_A}?`,
        force: forcePrompt(`This will normalize metadata for ${ISSUE_A}.`, 'normalized'),
        route: 'POST /api/jobs/normalize-selected',
        assertRequest(call, force) {
            assert.equal(call.headers['content-type'], 'application/json');
            assert.deepEqual(call.body, { Files: [ISSUE_A], ForceReprocess: force });
        },
        accepted: { job_id: h.JOB_ID, total_items: 1 },
        withoutJob: { total_items: 0 },
        starting: 'Starting normalize...',
        started: 'Started normalizing file in background',
        tracked: 'Normalizing File...',
        failure: 'Failed to start normalizing: ',
        serverFailure: 'Failed to start normalizing job'
    }
];

for (const action of SINGLE_FILE_JOBS) {
    test(`the issue menu's ${action.label} starts a job for that file`, async (t) => {
        let answer = _call => action.accepted;
        const page = await h.bootSeriesDetail(t, {
            routes: {
                [action.route]: call => answer(call),
                [`GET /api/jobs/${h.JOB_ID}`]: h.jobStatus({ total_items: 1 })
            }
        });
        const [method, path] = action.route.split(' ');
        const sent = () => h.requests(page, method, path);
        const start = () => h.chooseIssueAction(page, ISSUE_A, action.label);

        h.answerConfirms(page, false);
        start();
        await page.settle();
        assert.deepEqual(h.takeConfirms(page), [action.confirm]);
        assert.equal(sent().length, 0);
        assert.equal(page.$$('.dropdown-menu.show').length, 0, 'choosing an action closes the menu');

        const pending = deferred();
        answer = () => pending.promise;
        h.answerConfirms(page, true, true);
        start();
        await page.settle();
        assert.deepEqual(h.takeConfirms(page), [action.confirm, action.force]);
        assert.equal(sent().length, 1);
        action.assertRequest(sent()[0], true);
        assert.match(sent()[0].headers.authorization, /^Bearer /);
        h.assertProgress(page, h.startingProgress(action.starting));

        pending.resolve(action.accepted);
        await page.settle();
        assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: action.started }]);
        assert.equal(h.requests(page, 'GET', `/api/jobs/${h.JOB_ID}`).length, 1);
        h.assertProgress(page, { open: true, title: action.tracked, text: '0 / 1 files', cancelVisible: true });

        answer = () => action.accepted;
        h.answerConfirms(page, true, false);
        start();
        await page.settle();
        assert.deepEqual(h.takeConfirms(page), [action.confirm, action.force]);
        action.assertRequest(sent().at(-1), false);
        assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: action.started }]);

        if (action.withoutJob) {
            answer = () => action.withoutJob;
            start();
            await page.settle();
            h.takeConfirms(page);
            assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: `${action.failure}No job ID returned` }]);
            assert.equal(h.progressView(page).open, false);
        }

        answer = () => respond(500, 'Error renaming file');
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

const DIRECT_FILE_ACTIONS = [
    {
        label: 'Delete',
        confirm: `Are you sure you want to delete ${ISSUE_A}?\n\nThis action cannot be undone!`,
        path: `/api/files/${b64(ISSUE_A)}`,
        working: 'Deleting file...',
        done: 'File deleted successfully!',
        failure: 'Failed to delete file: '
    },
    {
        label: 'Remove Metadata',
        confirm: `Remove embedded metadata (ComicInfo.xml) from:\n${ISSUE_A}\n\n`
            + 'The archive will be rewritten without its ComicInfo.xml and the file will be marked as unprocessed. Continue?',
        path: `/api/files/${b64(ISSUE_A)}/metadata`,
        working: 'Removing metadata...',
        done: 'Metadata removed and file marked unprocessed.',
        failure: 'Failed to remove metadata: '
    }
];

for (const action of DIRECT_FILE_ACTIONS) {
    test(`the issue menu's ${action.label} acts on that file and refreshes the library`, async (t) => {
        let answer = _call => undefined;
        const page = await h.bootSeriesDetail(t, { routes: { [`DELETE ${action.path}`]: call => answer(call) } });
        const sent = () => h.requests(page, 'DELETE', action.path);
        const refreshes = () => h.requests(page, 'GET', /^\/api\/files\/series\?offset=0&limit=60&refresh=true$/);
        const choose = () => h.chooseIssueAction(page, ISSUE_A, action.label);

        h.answerConfirms(page, false);
        choose();
        await page.settle();
        assert.deepEqual(h.takeConfirms(page), [action.confirm]);
        assert.equal(sent().length, 0);
        assert.deepEqual(h.messages(page), []);

        choose();
        await page.settle();
        assert.deepEqual(h.takeConfirms(page), [action.confirm]);
        assert.equal(sent().length, 1);
        assert.match(sent()[0].headers.authorization, /^Bearer /);
        assert.deepEqual(h.takeMessages(page), [
            { type: 'info', text: action.working },
            { type: 'success', text: action.done }
        ]);
        assert.equal(refreshes().length, 1, 'the library is reloaded');
        assert.equal(h.progressView(page).open, false, 'no background job is involved');

        answer = () => respond(500, 'Error');
        choose();
        await page.settle();
        assert.deepEqual(h.takeMessages(page), [
            { type: 'info', text: action.working },
            { type: 'error', text: `${action.failure}HTTP error! status: 500` }
        ]);

        answer = () => { throw new TypeError('Failed to fetch'); };
        choose();
        await page.settle();
        assert.deepEqual(h.takeMessages(page).at(-1), { type: 'error', text: `${action.failure}Failed to fetch` });
        assert.equal(refreshes().length, 1, 'failures do not reload the library');

        answer = () => 401;
        choose();
        await page.settle();
        assert.equal(h.loginRedirects(page), 1);
        assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: action.working }]);
        assert.equal(refreshes().length, 1);
        assert.deepEqual(page.errors, []);
    });
}
