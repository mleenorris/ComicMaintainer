'use strict';

// Read-status actions (js/app/jobs.js): Mark Read / Mark Unread on one issue,
// on the selection and on the whole library, and the Refresh Library button.
// Read status is per user (FilesController.MarkFilesReadBatch is a
// [PerUserWriteOperation]), so these use plain requests, not background jobs.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const h = require('./support/jobs-helpers');

const { ISSUE_A, ISSUE_B, b64 } = h;
const ISSUES_PATH = '/api/files/series/s1/issues';
const LIBRARY_REFRESH = /^\/api\/files\/series\?offset=0&limit=60&refresh=true$/;

for (const { label, read, filePath } of [
    { label: 'Mark Read', read: true, filePath: ISSUE_A },
    { label: 'Mark Unread', read: false, filePath: ISSUE_B }
]) {
    const word = read ? 'read' : 'unread';
    test(`the issue menu's ${label} updates that issue and reloads the series`, async (t) => {
        let answer = _call => ({ success: true });
        const readPath = `/api/files/${b64(filePath)}/read`;
        const page = await h.bootSeriesDetail(t, {
            issues: [h.seriesIssue(ISSUE_A), h.seriesIssue(ISSUE_B, { read: true })],
            routes: { [`POST ${readPath}`]: call => answer(call) }
        });
        const sent = () => h.requests(page, 'POST', readPath);
        const issueLoads = () => h.requests(page, 'GET', ISSUES_PATH).length;
        const loadsAtBoot = issueLoads();

        h.chooseIssueAction(page, filePath, label);
        await page.settle();
        assert.deepEqual(h.takeConfirms(page), [], 'no confirmation for a single issue');
        assert.deepEqual(sent()[0].body, { read });
        assert.equal(sent()[0].headers['content-type'], 'application/json');
        assert.match(sent()[0].headers.authorization, /^Bearer /);
        assert.deepEqual(h.takeMessages(page), [
            { type: 'info', text: `Marking file as ${word}...` },
            { type: 'success', text: `File marked as ${word}!` }
        ]);
        assert.equal(issueLoads(), loadsAtBoot + 1, 'the open series reloads its issues in place');
        assert.equal(h.requests(page, 'GET', LIBRARY_REFRESH).length, 0, 'the series library is not rebuilt');
        assert.ok(page.$('#seriesDetailPanel'));

        answer = () => respond(500, 'Error');
        h.chooseIssueAction(page, filePath, label);
        await page.settle();
        assert.deepEqual(h.takeMessages(page).at(-1), { type: 'error', text: `Failed to mark file as ${word}: HTTP error! status: 500` });

        answer = () => { throw new TypeError('Failed to fetch'); };
        h.chooseIssueAction(page, filePath, label);
        await page.settle();
        assert.deepEqual(h.takeMessages(page).at(-1), { type: 'error', text: `Failed to mark file as ${word}: Failed to fetch` });
        assert.equal(issueLoads(), loadsAtBoot + 1);

        answer = () => 401;
        h.chooseIssueAction(page, filePath, label);
        await page.settle();
        assert.equal(h.loginRedirects(page), 1);
        assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: `Marking file as ${word}...` }]);
        assert.deepEqual(page.errors, []);
    });
}

for (const { label, read, call, itemId, confirmTail } of [
    {
        label: 'Mark Selected Read',
        read: true,
        call: 'markSelectedFilesRead()',
        itemId: 'markSelectedReadItem',
        confirmTail: 'as read?\n\nThis overwrites the current read status and cannot be undone.'
    },
    {
        label: 'Mark Selected Unread',
        read: false,
        call: 'markSelectedFilesUnread()',
        itemId: 'markSelectedUnreadItem',
        confirmTail: 'as unread?\n\nThis clears the current read status and cannot be undone.'
    }
]) {
    const word = read ? 'read' : 'unread';
    test(`${label} updates every selected issue`, async (t) => {
        let answer = _call => ({ success: true });
        const page = await h.bootSeriesDetail(t, { routes: { 'POST /api/files/read-batch': c => answer(c) } });
        const sent = () => h.requests(page, 'POST', '/api/files/read-batch');
        const choose = () => h.chooseMenuItem(page, 'markDropdown', label);
        const issueLoads = () => h.requests(page, 'GET', ISSUES_PATH).length;

        assert.equal(page.$(`#${itemId}`).disabled, true);
        page.run(call);
        await page.settle();
        assert.deepEqual(h.takeMessages(page), [{ type: 'warning', text: 'No files selected' }]);
        assert.deepEqual(h.takeConfirms(page), []);

        h.selectIssue(page, ISSUE_A);
        h.answerConfirms(page, false);
        choose();
        await page.settle();
        assert.deepEqual(h.takeConfirms(page), [`Mark 1 selected file ${confirmTail}`]);
        assert.equal(sent().length, 0);

        h.selectIssue(page, ISSUE_B);
        const loadsBefore = issueLoads();
        choose();
        await page.settle();
        assert.deepEqual(h.takeConfirms(page), [`Mark 2 selected files ${confirmTail}`]);
        assert.deepEqual(sent()[0].body, { files: [ISSUE_A, ISSUE_B], read });
        assert.equal(sent()[0].headers['content-type'], 'application/json');
        assert.match(sent()[0].headers.authorization, /^Bearer /);
        assert.deepEqual(h.takeMessages(page), [
            { type: 'info', text: `Marking selected files as ${word}...` },
            { type: 'success', text: `2 file(s) marked as ${word}!` }
        ]);
        assert.equal(issueLoads(), loadsBefore + 1, 'the open series reloads its issues');

        answer = () => respond(500, 'Error');
        choose();
        await page.settle();
        assert.deepEqual(h.takeMessages(page).at(-1), { type: 'error', text: `Failed to mark files as ${word}: HTTP error! status: 500` });

        answer = () => { throw new TypeError('Failed to fetch'); };
        choose();
        await page.settle();
        assert.deepEqual(h.takeMessages(page).at(-1), { type: 'error', text: `Failed to mark files as ${word}: Failed to fetch` });

        answer = () => 401;
        choose();
        await page.settle();
        assert.equal(h.loginRedirects(page), 1);
        assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: `Marking selected files as ${word}...` }]);
        assert.deepEqual(page.errors, []);
    });
}

for (const { label, read, confirm } of [
    {
        label: 'Mark All Read',
        read: true,
        confirm: 'Mark EVERY file in the library as read?\n\nThis overwrites the read status of all files and cannot be undone.'
    },
    {
        label: 'Mark All Unread',
        read: false,
        confirm: 'Mark EVERY file in the library as unread?\n\nThis clears the read status of all files and cannot be undone.'
    }
]) {
    const word = read ? 'read' : 'unread';
    test(`${label} lists every library file and updates them all`, async (t) => {
        let listAnswer = _call => ({ files: [{ relative_path: ISSUE_A }, { relative_path: ISSUE_B }], total_files: 2 });
        let batchAnswer = _call => ({ success: true });
        const page = await h.bootJobsApp(t, {
            routes: {
                'GET /api/files': c => listAnswer(c),
                'POST /api/files/read-batch': c => batchAnswer(c)
            }
        });
        const lists = () => h.requests(page, 'GET', '/api/files');
        const batches = () => h.requests(page, 'POST', '/api/files/read-batch');
        const refreshes = () => h.requests(page, 'GET', LIBRARY_REFRESH).length;
        const choose = () => h.chooseMenuItem(page, 'markDropdown', label);

        h.answerConfirms(page, false);
        choose();
        await page.settle();
        assert.deepEqual(h.takeConfirms(page), [confirm]);
        assert.equal(lists().length, 0);
        assert.deepEqual(h.messages(page), []);

        choose();
        await page.settle();
        assert.deepEqual(h.takeConfirms(page), [confirm]);
        assert.equal(lists()[0].url, '/api/files?per_page=-1');
        assert.match(lists()[0].headers.authorization, /^Bearer /);
        assert.deepEqual(batches()[0].body, { files: [ISSUE_A, ISSUE_B], read });
        assert.deepEqual(h.takeMessages(page), [
            { type: 'info', text: `Marking all files as ${word}...` },
            { type: 'success', text: `All files marked as ${word}!` }
        ]);
        assert.equal(refreshes(), 1, 'the series library reloads');

        // A listing without files still sends an (empty) update.
        listAnswer = () => ({});
        choose();
        await page.settle();
        assert.deepEqual(batches().at(-1).body, { files: [], read });
        assert.deepEqual(h.takeMessages(page).at(-1), { type: 'success', text: `All files marked as ${word}!` });

        listAnswer = () => respond(500, 'Error');
        const batchesBefore = batches().length;
        choose();
        await page.settle();
        assert.deepEqual(h.takeMessages(page).at(-1), { type: 'error', text: `Failed to mark files as ${word}: HTTP error! status: 500` });
        assert.equal(batches().length, batchesBefore, 'nothing is updated without the file list');

        listAnswer = () => ({ files: [{ relative_path: ISSUE_A }] });
        batchAnswer = () => respond(503, 'Unavailable');
        choose();
        await page.settle();
        assert.deepEqual(h.takeMessages(page).at(-1), { type: 'error', text: `Failed to mark files as ${word}: HTTP error! status: 503` });

        batchAnswer = () => { throw new TypeError('Failed to fetch'); };
        choose();
        await page.settle();
        assert.deepEqual(h.takeMessages(page).at(-1), { type: 'error', text: `Failed to mark files as ${word}: Failed to fetch` });
        assert.equal(refreshes(), 2);

        batchAnswer = () => 401;
        choose();
        await page.settle();
        assert.equal(h.loginRedirects(page), 1);
        assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: `Marking all files as ${word}...` }]);
        assert.equal(refreshes(), 2);
        assert.deepEqual(page.errors, []);
    });
}

test('Mark All Read sends a signed-out user to the login page when the file list is refused', async (t) => {
    const page = await h.bootJobsApp(t, {
        routes: { 'GET /api/files': 401, 'POST /api/files/read-batch': 401 }
    });

    h.chooseMenuItem(page, 'markDropdown', 'Mark All Read');
    await page.settle();
    assert.ok(h.loginRedirects(page) >= 1);
    assert.ok(!h.messages(page).some(message => message.type === 'success'));
    assert.deepEqual(page.errors, []);
});

test('Refresh Library reloads the series library from the server', async (t) => {
    const page = await h.bootJobsApp(t);
    const button = page.$$('button').find(element => element.getAttribute('onclick') === 'refreshFiles()');
    assert.ok(button, 'the toolbar has a Refresh Library button');
    assert.equal(h.requests(page, 'GET', LIBRARY_REFRESH).length, 0);

    page.click(button);
    await page.settle();
    assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: 'Refreshing file list...' }]);
    assert.equal(h.requests(page, 'GET', LIBRARY_REFRESH).length, 1);
    assert.deepEqual(page.errors, []);
});
