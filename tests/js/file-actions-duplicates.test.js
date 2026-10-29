'use strict';

// js/app/file-actions.js: the Duplicate Review modal, opened from the sidebar,
// the library toolbar and a duplicate issue's menu. It steps through
// GET /api/files?filter=duplicates and hands the current file to the info,
// tag editor, reader, read-toggle and delete actions, or deletes them all in
// one background job (POST /api/jobs/delete-selected).

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const {
    bootPage,
    bootSeriesPage,
    seriesIssue,
    messages,
    messageTexts,
    isModalOpen,
    pressEscape,
    buttonByText,
    clickIssueAction,
    answerConfirms,
    encodedPath,
    callsTo
} = require('./support/email-helpers');

const A = 'Saga/Saga 001.cbz';
const B = 'Saga (2012)/Saga 001.cbz';
const C = 'Saga (2012)/Saga 002.cbz';
// Noon UTC, so the local date is 2020-01-15 in any test time zone.
const OLD = Date.UTC(2020, 0, 15, 12) / 1000;

// A FileDto of GET /api/files.
function duplicateFile(overrides = {}) {
    return {
        relative_path: A,
        name: 'Saga 001.cbz',
        size: 1536,
        modified: OLD,
        processed: true,
        renamed: true,
        normalized: true,
        duplicate: true,
        read: false,
        ...overrides
    };
}

const FILE_A = duplicateFile();
const FILE_B = duplicateFile({ relative_path: B, size: 3 * 1024 * 1024, read: true });
const FILE_C = duplicateFile({ relative_path: C, name: 'Saga 002.cbz', size: 900 });

// GET /api/files as FilesController.GetFiles returns it.
function filesPage(files) {
    return { files, page: 1, total_pages: 1, total_files: files.length, unmarked_count: 0 };
}

function boot({ files = [FILE_A, FILE_B, FILE_C], routes = {}, ...options } = {}) {
    return bootPage({
        hash: '#/library',
        ...options,
        routes: { 'GET /api/files': filesPage(files), ...routes }
    });
}

function openFromSidebar(page) {
    return page.click(buttonByText(page, '.sidebar-nav', 'Review Duplicates'));
}

function reviewButton(page, text) {
    return buttonByText(page, '#duplicateReviewContent', text);
}

function shown(page) {
    return {
        counter: page.$('#duplicateReviewCounter').textContent,
        name: page.$('#duplicateReviewName').textContent,
        path: page.$('#duplicateReviewPath').textContent
    };
}

function duplicateListCalls(page) {
    return callsTo(page, 'GET', '/api/files').filter(call => call.query.filter === 'duplicates');
}

test('Review Duplicates shows a loading state, then the first duplicate', async t => {
    const answer = deferred();
    const page = await boot({ routes: { 'GET /api/files': () => answer.promise } });
    t.after(() => page.close());

    openFromSidebar(page);
    await page.settle();

    assert.ok(isModalOpen(page, 'duplicateReviewModal'));
    assert.deepEqual(shown(page), { counter: '0 of 0', name: 'Loading duplicates...', path: '' });
    assert.equal(page.$('#duplicateReviewEmptyState').style.display, 'none');
    assert.equal(page.$('#duplicateReviewContent').style.display, 'block');
    assert.ok(page.$('#duplicateReviewPrevBtn').disabled);
    assert.ok(page.$('#duplicateReviewNextBtn').disabled);
    assert.ok(page.$('#duplicateReviewDeleteAllBtn').disabled);
    assert.equal(page.$('#duplicateReviewDeleteAllBtn').textContent, '🗑️ Delete All Duplicates');
    const [call] = duplicateListCalls(page);
    assert.deepEqual(call.query, { filter: 'duplicates', per_page: '-1', sort: 'name' });
    assert.match(call.headers.authorization, /^Bearer /);

    answer.resolve(filesPage([FILE_A, FILE_B, FILE_C]));
    await page.settle();

    assert.deepEqual(shown(page), { counter: '1 of 3', name: 'Saga 001.cbz', path: A });
    assert.equal(page.$('#duplicateReviewSize').textContent, '1.5 KB');
    assert.equal(page.$('#duplicateReviewModified').textContent, '2020-01-15');
    assert.equal(page.$('#duplicateReviewReadStatus').textContent, '📚 Unread');
    assert.equal(page.$('#duplicateReviewReadToggleBtn').textContent, '✅ Mark Read');
    assert.ok(page.$('#duplicateReviewPrevBtn').disabled);
    assert.ok(!page.$('#duplicateReviewNextBtn').disabled);
    assert.ok(!page.$('#duplicateReviewDeleteAllBtn').disabled);
    assert.equal(page.$('#duplicateReviewDeleteAllBtn').textContent, '🗑️ Delete All 3 Duplicates');
    assert.deepEqual(messages(page), []);
});

test('Previous and Next step through the duplicates and stop at either end', async t => {
    const page = await boot();
    t.after(() => page.close());
    openFromSidebar(page);
    await page.settle();

    page.click('#duplicateReviewNextBtn');
    assert.deepEqual(shown(page), { counter: '2 of 3', name: 'Saga 001.cbz', path: B });
    assert.equal(page.$('#duplicateReviewSize').textContent, '3.0 MB');
    assert.equal(page.$('#duplicateReviewReadStatus').textContent, '👁️ Read');
    assert.equal(page.$('#duplicateReviewReadToggleBtn').textContent, '📚 Mark Unread');
    assert.ok(!page.$('#duplicateReviewPrevBtn').disabled);
    assert.ok(!page.$('#duplicateReviewNextBtn').disabled);

    page.click('#duplicateReviewNextBtn');
    assert.deepEqual(shown(page), { counter: '3 of 3', name: 'Saga 002.cbz', path: C });
    assert.equal(page.$('#duplicateReviewSize').textContent, '900 B');
    assert.ok(page.$('#duplicateReviewNextBtn').disabled);
    // A step past either end (e.g. a key repeat on a just-disabled button) is ignored.
    page.run('changeDuplicateReviewItem(1)');
    assert.equal(shown(page).counter, '3 of 3');

    page.click('#duplicateReviewPrevBtn');
    page.click('#duplicateReviewPrevBtn');
    assert.deepEqual(shown(page), { counter: '1 of 3', name: 'Saga 001.cbz', path: A });
    assert.ok(page.$('#duplicateReviewPrevBtn').disabled);
    page.run('changeDuplicateReviewItem(-1)');
    assert.equal(shown(page).counter, '1 of 3');
    assert.equal(duplicateListCalls(page).length, 1);
});

test('the library toolbar offers Review Duplicates when the library has some', async t => {
    const page = await boot({
        routes: {
            'GET /api/files/counts': { total: 6, processed: 4, unprocessed: 2, duplicates: 3, combinableFolders: 0 }
        }
    });
    t.after(() => page.close());

    const button = page.$('#reviewDuplicatesBtn');
    assert.equal(button.hidden, false);
    page.click(button);
    await page.settle();

    assert.ok(isModalOpen(page, 'duplicateReviewModal'));
    assert.equal(shown(page).counter, '1 of 3');
});

test('Review Duplicate on a duplicate issue opens the review on that file', async t => {
    const page = await bootSeriesPage({
        issues: [
            seriesIssue({ file_path: A }),
            seriesIssue({ file_path: C, file_name: 'Saga 002.cbz', title: 'Saga 002', issue: '2', duplicate: true })
        ],
        routes: { 'GET /api/files': filesPage([FILE_A, FILE_B, FILE_C]) }
    });
    t.after(() => page.close());

    assert.throws(() => clickIssueAction(page, A, 'Review Duplicate'), /No "Review Duplicate" action/);
    clickIssueAction(page, C, 'Review Duplicate');
    await page.settle();

    assert.ok(isModalOpen(page, 'duplicateReviewModal'));
    assert.deepEqual(shown(page), { counter: '3 of 3', name: 'Saga 002.cbz', path: C });

    // A file that is no longer a duplicate falls back to the first one.
    await page.run('openDuplicateReviewModal("Saga/Saga 999.cbz")');
    assert.deepEqual(shown(page), { counter: '1 of 3', name: 'Saga 001.cbz', path: A });
});

test('no duplicates shows the empty state and the per-file actions do nothing', async t => {
    const page = await boot({ files: [] });
    t.after(() => page.close());

    openFromSidebar(page);
    await page.settle();

    const emptyState = page.$('#duplicateReviewEmptyState');
    assert.equal(emptyState.style.display, 'block');
    assert.equal(emptyState.textContent, 'No duplicate files are waiting for review.');
    assert.equal(page.$('#duplicateReviewContent').style.display, 'none');
    assert.equal(shown(page).counter, '0 of 0');
    assert.ok(page.$('#duplicateReviewDeleteAllBtn').disabled);
    assert.equal(page.$('#duplicateReviewDeleteAllBtn').textContent, '🗑️ Delete All Duplicates');

    const callCount = page.fetchCalls.length;
    const navigationCount = page.navigations.length;
    for (const action of ['openDuplicateInfo', 'openDuplicateTags', 'openDuplicateReader',
        'toggleDuplicateReadStatus', 'deleteCurrentDuplicate']) {
        await page.run(`${action}()`);
    }
    assert.equal(page.fetchCalls.length, callCount);
    assert.ok(isModalOpen(page, 'duplicateReviewModal'));
    assert.ok(!isModalOpen(page, 'fileInfoModal'));
    assert.ok(!isModalOpen(page, 'tagModal'));
    assert.deepEqual(page.dialogs, []);
    assert.equal(page.navigations.length, navigationCount);

    // A response without a file list is treated as empty too.
    page.route('GET /api/files', { page: 1 });
    openFromSidebar(page);
    await page.settle();
    assert.equal(emptyState.style.display, 'block');
    assert.equal(shown(page).counter, '0 of 0');
});

test('a failed load is shown in the empty state and the next open starts afresh', async t => {
    const page = await boot({ routes: { 'GET /api/files': 500 } });
    t.after(() => page.close());
    const emptyState = page.$('#duplicateReviewEmptyState');

    openFromSidebar(page);
    await page.settle();
    assert.equal(emptyState.style.display, 'block');
    assert.equal(emptyState.textContent, 'Failed to load duplicates: HTTP error! status: 500');
    assert.equal(page.$('#duplicateReviewContent').style.display, 'none');
    assert.equal(shown(page).counter, '0 of 0');
    assert.ok(page.consoleMessages.some(entry => entry.type === 'error'
        && entry.args[0] === 'Failed to load duplicates for review:'));

    page.route('GET /api/files', () => { throw new TypeError('Failed to fetch'); });
    openFromSidebar(page);
    await page.settle();
    assert.equal(emptyState.textContent, 'Failed to load duplicates: Failed to fetch');

    const answer = deferred();
    page.route('GET /api/files', () => answer.promise);
    openFromSidebar(page);
    await page.settle();
    assert.equal(emptyState.style.display, 'none');
    assert.equal(emptyState.textContent, 'No duplicate files are waiting for review.');
    assert.equal(shown(page).name, 'Loading duplicates...');

    answer.resolve(filesPage([FILE_C]));
    await page.settle();
    assert.deepEqual(shown(page), { counter: '1 of 1', name: 'Saga 002.cbz', path: C });
    assert.deepEqual(messages(page), []);
});

test('an expired session goes to the login page instead of showing an error', async t => {
    const page = await boot({ routes: { 'GET /api/files': 401 } });
    t.after(() => page.close());

    openFromSidebar(page);
    await page.settle();

    assert.ok(page.navigations.some(nav => nav.url.includes('/login.html')));
    assert.equal(page.$('#duplicateReviewEmptyState').style.display, 'none');
    assert.ok(!page.consoleMessages.some(entry => entry.args[0] === 'Failed to load duplicates for review:'));
});

test('answers for a closed or superseded review are ignored', async t => {
    const page = await boot();
    t.after(() => page.close());
    const answers = [];
    page.route('GET /api/files', () => {
        const answer = deferred();
        answers.push(answer);
        return answer.promise;
    });
    const closeButton = buttonByText(page, '#duplicateReviewModal .modal-footer', 'Close');

    // Closed before the list arrived.
    openFromSidebar(page);
    page.click(closeButton);
    answers[0].resolve(filesPage([FILE_A, FILE_B]));
    await page.settle();
    assert.deepEqual(shown(page), { counter: '0 of 0', name: 'Loading duplicates...', path: '' });

    // Reopened while the first answer was outstanding: the newest one wins.
    openFromSidebar(page);
    openFromSidebar(page);
    answers[2].resolve(filesPage([FILE_C]));
    await page.settle();
    answers[1].resolve(filesPage([FILE_A, FILE_B]));
    await page.settle();
    assert.deepEqual(shown(page), { counter: '1 of 1', name: 'Saga 002.cbz', path: C });

    // A failure after closing is not reported.
    page.click(closeButton);
    openFromSidebar(page);
    page.click(closeButton);
    answers[3].reject(new TypeError('Failed to fetch'));
    await page.settle();
    assert.equal(page.$('#duplicateReviewEmptyState').style.display, 'none');
    assert.ok(!page.consoleMessages.some(entry => entry.args[0] === 'Failed to load duplicates for review:'));

    // Closed after the response arrived but while its body was still streaming.
    let body;
    page.route('GET /api/files', () => new Response(new ReadableStream({ start(controller) { body = controller; } }),
        { headers: { 'Content-Type': 'application/json' } }));
    openFromSidebar(page);
    await page.settle();
    page.click(closeButton);
    body.enqueue(new TextEncoder().encode(JSON.stringify(filesPage([FILE_A]))));
    body.close();
    await page.settle();
    assert.deepEqual(shown(page), { counter: '0 of 0', name: 'Loading duplicates...', path: '' });
    assert.ok(!isModalOpen(page, 'duplicateReviewModal'));
});

test('Mark Read and Mark Unread update the file and keep it in view', async t => {
    const read = new Map([[A, false], [B, false]]);
    const page = await boot({
        routes: {
            'GET /api/files': () => filesPage([
                duplicateFile({ read: read.get(A) }),
                duplicateFile({ relative_path: B, read: read.get(B) })
            ]),
            [`POST /api/files/${encodedPath(B)}/read`]: call => { read.set(B, call.body.read); return respond(200); }
        }
    });
    t.after(() => page.close());
    openFromSidebar(page);
    await page.settle();
    page.click('#duplicateReviewNextBtn');

    page.click('#duplicateReviewReadToggleBtn');
    await page.settle();

    const [markRead] = callsTo(page, 'POST', `/api/files/${encodedPath(B)}/read`);
    assert.deepEqual(markRead.body, { read: true });
    assert.equal(markRead.headers['content-type'], 'application/json');
    assert.deepEqual(messageTexts(page, 'success'), ['File marked as read!']);
    assert.equal(duplicateListCalls(page).length, 2);
    assert.deepEqual(shown(page), { counter: '2 of 2', name: 'Saga 001.cbz', path: B });
    assert.equal(page.$('#duplicateReviewReadStatus').textContent, '👁️ Read');
    assert.equal(page.$('#duplicateReviewReadToggleBtn').textContent, '📚 Mark Unread');

    page.click('#duplicateReviewReadToggleBtn');
    await page.settle();

    assert.deepEqual(callsTo(page, 'POST', `/api/files/${encodedPath(B)}/read`).map(call => call.body),
        [{ read: true }, { read: false }]);
    assert.ok(messageTexts(page, 'success').includes('File marked as unread!'));
    assert.equal(shown(page).counter, '2 of 2');
    assert.equal(page.$('#duplicateReviewReadStatus').textContent, '📚 Unread');
    assert.equal(read.get(A), false);
});

test('View Info, View/Edit Tags and Read Comic hand the current duplicate over', async t => {
    const page = await boot({
        routes: {
            [`GET /api/files/${encodedPath(B)}/tags`]: { series: 'Saga', title: 'Chapter One', issue: '1', authors: [], tags: [] }
        }
    });
    t.after(() => page.close());

    openFromSidebar(page);
    await page.settle();
    page.click('#duplicateReviewNextBtn');
    page.click(reviewButton(page, 'View Info'));
    assert.ok(!isModalOpen(page, 'duplicateReviewModal'));
    assert.ok(isModalOpen(page, 'fileInfoModal'));
    assert.equal(page.$('#fileInfoName').textContent, 'Saga 001.cbz');
    assert.equal(page.$('#fileInfoPath').textContent, B);
    page.run('closeFileInfoModal()');

    openFromSidebar(page);
    await page.settle();
    page.click('#duplicateReviewNextBtn');
    page.click(reviewButton(page, 'View/Edit Tags'));
    await page.settle();
    assert.ok(!isModalOpen(page, 'duplicateReviewModal'));
    assert.ok(isModalOpen(page, 'tagModal'));
    assert.equal(callsTo(page, 'GET', `/api/files/${encodedPath(B)}/tags`).length, 1);
    assert.equal(page.$('#title').value, 'Chapter One');
    page.run('closeModal()');

    openFromSidebar(page);
    await page.settle();
    page.click(reviewButton(page, 'Read Comic'));
    assert.ok(!isModalOpen(page, 'duplicateReviewModal'));
    assert.ok(page.navigations.some(nav => nav.url === `http://localhost/reader.html?file=${encodeURIComponent(A)}`));
});

test('Delete Duplicate deletes the confirmed file and reloads the review', async t => {
    let files = [FILE_A, FILE_B];
    const page = await boot({
        routes: {
            'GET /api/files': () => filesPage(files),
            [`DELETE /api/files/${encodedPath(B)}`]: () => { files = files.filter(file => file !== FILE_B); return respond(200); },
            [`DELETE /api/files/${encodedPath(A)}`]: () => { files = files.filter(file => file !== FILE_A); return respond(200); }
        }
    });
    t.after(() => page.close());
    answerConfirms(page, true);
    openFromSidebar(page);
    await page.settle();
    page.click('#duplicateReviewNextBtn');

    page.click(reviewButton(page, 'Delete Duplicate'));
    await page.settle();

    assert.deepEqual(page.dialogs.map(dialog => dialog.message),
        [`Are you sure you want to delete ${B}?\n\nThis action cannot be undone!`]);
    assert.equal(callsTo(page, 'DELETE', `/api/files/${encodedPath(B)}`).length, 1);
    assert.deepEqual(messageTexts(page, 'success'), ['File deleted successfully!']);
    assert.ok(isModalOpen(page, 'duplicateReviewModal'));
    assert.deepEqual(shown(page), { counter: '1 of 1', name: 'Saga 001.cbz', path: A });

    // Deleting the last one leaves nothing to review.
    page.click(reviewButton(page, 'Delete Duplicate'));
    await page.settle();
    assert.equal(callsTo(page, 'DELETE', `/api/files/${encodedPath(A)}`).length, 1);
    assert.equal(page.$('#duplicateReviewEmptyState').style.display, 'block');
    assert.equal(page.$('#duplicateReviewEmptyState').textContent, 'No duplicate files are waiting for review.');
    assert.equal(shown(page).counter, '0 of 0');
});

test('Delete Duplicate keeps the file when the deletion is declined or fails', async t => {
    const page = await boot({ files: [FILE_A, FILE_B], routes: { [`DELETE /api/files/${encodedPath(A)}`]: 500 } });
    t.after(() => page.close());
    answerConfirms(page, false, true);
    openFromSidebar(page);
    await page.settle();

    page.click(reviewButton(page, 'Delete Duplicate'));
    await page.settle();
    assert.equal(page.dialogs.length, 1);
    assert.equal(callsTo(page, 'DELETE', `/api/files/${encodedPath(A)}`).length, 0);
    assert.ok(isModalOpen(page, 'duplicateReviewModal'));
    assert.deepEqual(shown(page), { counter: '1 of 2', name: 'Saga 001.cbz', path: A });

    page.click(reviewButton(page, 'Delete Duplicate'));
    await page.settle();
    assert.equal(callsTo(page, 'DELETE', `/api/files/${encodedPath(A)}`).length, 1);
    assert.deepEqual(messageTexts(page, 'error'), ['Failed to delete file: HTTP error! status: 500']);
    assert.match(shown(page).counter, / of 2$/);
});

test('Delete All asks first, then deletes every duplicate in one background job', async t => {
    const page = await boot({
        routes: {
            'POST /api/jobs/delete-selected': { job_id: 'job-9', jobId: 'job-9', total_items: 3, totalItems: 3 },
            'GET /api/jobs/job-9': { job_id: 'job-9', status: 'running', total_items: 3, processed_items: 0, results: [] }
        }
    });
    t.after(() => page.close());
    answerConfirms(page, false, true);
    openFromSidebar(page);
    await page.settle();
    const deleteAll = page.$('#duplicateReviewDeleteAllBtn');

    page.click(deleteAll);
    await page.settle();
    const prompt = 'Are you sure you want to delete all 3 file(s) marked as duplicates?\n\nThis action cannot be undone!';
    assert.deepEqual(page.dialogs.map(dialog => dialog.message), [prompt]);
    assert.equal(callsTo(page, 'POST', '/api/jobs/delete-selected').length, 0);
    assert.ok(isModalOpen(page, 'duplicateReviewModal'));

    page.click(deleteAll);
    await page.settle();

    assert.deepEqual(page.dialogs.map(dialog => dialog.message), [prompt, prompt]);
    assert.ok(!isModalOpen(page, 'duplicateReviewModal'));
    const [call] = callsTo(page, 'POST', '/api/jobs/delete-selected');
    assert.deepEqual(call.body, { Files: [A, B, C] });
    assert.equal(call.headers['content-type'], 'application/json');
    assert.match(call.headers.authorization, /^Bearer /);
    assert.deepEqual(messageTexts(page, 'info'), ['Deleting 3 duplicate(s) in background']);
    assert.ok(isModalOpen(page, 'progressModal'));
    assert.equal(page.$('#progressTitle').textContent, 'Deleting 3 duplicate(s)...');
    assert.equal(callsTo(page, 'GET', '/api/jobs/job-9').length, 1);
    // One job, not a DELETE per file.
    assert.deepEqual(page.fetchCalls.filter(entry => entry.method === 'DELETE'), []);
});

test('Delete All leaves out entries without a path', async t => {
    const ghost = duplicateFile({ relative_path: '', name: 'ghost.cbz' });
    const page = await boot({
        files: [ghost, FILE_C],
        routes: {
            'POST /api/jobs/delete-selected': { job_id: 'job-3' },
            'GET /api/jobs/job-3': { job_id: 'job-3', status: 'running', total_items: 1, processed_items: 0, results: [] }
        }
    });
    t.after(() => page.close());
    openFromSidebar(page);
    await page.settle();
    assert.equal(page.$('#duplicateReviewDeleteAllBtn').textContent, '🗑️ Delete All 2 Duplicates');

    page.click('#duplicateReviewDeleteAllBtn');
    await page.settle();

    assert.deepEqual(page.dialogs.map(dialog => dialog.message),
        ['Are you sure you want to delete all 1 file(s) marked as duplicates?\n\nThis action cannot be undone!']);
    assert.deepEqual(callsTo(page, 'POST', '/api/jobs/delete-selected')[0].body, { Files: [C] });
    // Without a count in the answer, the number of files sent is shown.
    assert.deepEqual(messageTexts(page, 'info'), ['Deleting 1 duplicate(s) in background']);
    assert.equal(callsTo(page, 'GET', '/api/jobs/job-3').length, 1);

    // With nothing deletable there is nothing to confirm.
    page.route('GET /api/files', filesPage([ghost]));
    openFromSidebar(page);
    await page.settle();
    page.click('#duplicateReviewDeleteAllBtn');
    await page.settle();
    assert.deepEqual(messageTexts(page, 'error'), ['No duplicates to delete']);
    assert.equal(page.dialogs.length, 1);
    assert.equal(callsTo(page, 'POST', '/api/jobs/delete-selected').length, 1);
    assert.ok(isModalOpen(page, 'duplicateReviewModal'));
});

test('Delete All failures are reported and close the progress modal', async t => {
    const page = await boot({ routes: { 'POST /api/jobs/delete-selected': respond(400, { error: 'No files specified' }) } });
    t.after(() => page.close());
    const deleteAll = async () => {
        openFromSidebar(page);
        await page.settle();
        page.click('#duplicateReviewDeleteAllBtn');
        await page.settle();
        assert.ok(!isModalOpen(page, 'progressModal'));
    };

    await deleteAll();
    // A proxy error page is not JSON.
    page.route('POST /api/jobs/delete-selected', respond(502, 'Bad Gateway'));
    await deleteAll();
    page.route('POST /api/jobs/delete-selected', () => { throw new TypeError('Failed to fetch'); });
    await deleteAll();

    assert.deepEqual(messageTexts(page, 'error'), [
        'Failed to start delete: No files specified',
        'Failed to start delete: Failed to start delete job (HTTP 502)',
        'Failed to start delete: Failed to fetch'
    ]);
    assert.equal(page.consoleMessages.filter(entry => entry.type === 'error'
        && entry.args[0] === '[DUPLICATES] Error starting delete-all job:').length, 3);
    assert.equal(callsTo(page, 'POST', '/api/jobs/delete-selected').length, 3);
});

test('Delete All with an expired session closes the progress modal and goes to login', async t => {
    const page = await boot({ routes: { 'POST /api/jobs/delete-selected': 401 } });
    t.after(() => page.close());
    openFromSidebar(page);
    await page.settle();

    page.click('#duplicateReviewDeleteAllBtn');
    await page.settle();

    assert.ok(!isModalOpen(page, 'progressModal'));
    assert.ok(page.navigations.some(nav => nav.url.includes('/login.html')));
    assert.deepEqual(messageTexts(page, 'error'), []);
});

test('Close, the close button and Escape close the review', async t => {
    const page = await boot();
    t.after(() => page.close());

    openFromSidebar(page);
    await page.settle();
    page.click(buttonByText(page, '#duplicateReviewModal .modal-footer', 'Close'));
    assert.ok(!isModalOpen(page, 'duplicateReviewModal'));

    openFromSidebar(page);
    await page.settle();
    page.click('#duplicateReviewModal .close-btn');
    assert.ok(!isModalOpen(page, 'duplicateReviewModal'));

    openFromSidebar(page);
    await page.settle();
    await pressEscape(page);
    assert.ok(!isModalOpen(page, 'duplicateReviewModal'));
});

test('read-only users can review duplicates but the delete actions are hidden from them', async t => {
    const page = await boot({
        user: { canModifyLibrary: false, canAdminister: false, roles: ['ReadOnly'] },
        routes: {
            'GET /api/files/counts': { total: 6, processed: 4, unprocessed: 2, duplicates: 3, combinableFolders: 0 }
        },
        beforeScripts(window) {
            // css/main.css: the read-only gate (the harness loads no stylesheets).
            const style = window.document.createElement('style');
            style.textContent = 'body.user-read-only [data-requires-write] { display: none !important; }';
            window.document.head.appendChild(style);
        }
    });
    t.after(() => page.close());
    const visible = element => page.window.getComputedStyle(element).display !== 'none';

    assert.ok(page.document.body.classList.contains('user-read-only'));
    assert.ok(!visible(page.$('#reviewDuplicatesBtn')));
    const sidebarButton = buttonByText(page, '.sidebar-nav', 'Review Duplicates');
    assert.ok(visible(sidebarButton));

    page.click(sidebarButton);
    await page.settle();
    assert.ok(isModalOpen(page, 'duplicateReviewModal'));
    assert.equal(shown(page).counter, '1 of 3');
    assert.ok(!visible(page.$('#duplicateReviewDeleteAllBtn')));
    assert.ok(!visible(reviewButton(page, 'Delete Duplicate')));
    assert.ok(visible(reviewButton(page, 'View Info')));
});
