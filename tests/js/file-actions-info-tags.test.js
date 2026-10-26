'use strict';

// js/app/file-actions.js: the File Information modal, the single-file tag
// editor (View/Edit) and the batch tag editor, opened from a series page's
// issue menus and selection toolbar.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond, plain } = require('./support/dom');
const {
    bootSeriesPage,
    seriesIssue,
    messages,
    messageTexts,
    isModalOpen,
    pressEscape,
    inputUi,
    clickIssueAction,
    encodedPath,
    callsTo
} = require('./support/email-helpers');

const FIRST = 'Saga/Saga 001.cbz';
const SECOND = 'Saga/Saga 002.cbz';
const ISSUES = [
    seriesIssue(),
    seriesIssue({ file_path: SECOND, file_name: 'Saga 002.cbz', title: 'Saga 002', issue: '2', duplicate: true })
];

// GET /api/files/{encoded}/tags returns ComicMetadata (camelCase).
function comicMetadata(overrides = {}) {
    return {
        series: 'Saga',
        title: 'Chapter One',
        issue: '1',
        volume: '2012',
        publisher: 'Image',
        year: 2012,
        summary: 'Alana and Marko flee.',
        authors: ['Brian K. Vaughan'],
        tags: [],
        isUserEdited: false,
        userLockedFieldsMask: 0,
        ...overrides
    };
}

function boot(options = {}) {
    return bootSeriesPage({ issues: ISSUES, ...options });
}

test('Info on an issue shows its name and path and no duplicate call-out', async t => {
    const page = await boot();
    t.after(() => page.close());

    clickIssueAction(page, FIRST, 'Info');

    assert.ok(isModalOpen(page, 'fileInfoModal'));
    assert.equal(page.$('#fileInfoName').textContent, 'Saga 001.cbz');
    assert.equal(page.$('#fileInfoPath').textContent, FIRST);
    assert.equal(page.$('#fileInfoDuplicateReview').style.display, 'none');
    assert.equal(page.$('#fileInfoDuplicateReviewBtn').dataset.filepath, undefined);
    // The series page never fills the folder file cache the details come from.
    assert.equal(page.$('#fileInfoSize').textContent, '');
    assert.equal(page.$('#fileInfoProcessed').textContent, '');

    page.click('#fileInfoModal .modal-footer .btn-primary');
    assert.ok(!isModalOpen(page, 'fileInfoModal'));
});

test('Info for a loaded file shows its size and processing status', async t => {
    const page = await boot();
    t.after(() => page.close());
    page.run(`folderFiles.set('Saga', { status: 'loaded', files: [
        { relative_path: ${JSON.stringify(FIRST)}, name: 'Saga 001.cbz', size: 1536, processed: true, renamed: true, normalized: true, duplicate: false },
        { relative_path: ${JSON.stringify(SECOND)}, name: 'Saga 002.cbz', size: 3 * 1024 * 1024, processed: false, renamed: false, normalized: false, duplicate: true }
    ] }); folderFiles.set('Other', { status: 'loading', files: [{ relative_path: 'Other/x.cbz' }] });`);

    page.run(`showFileInfo(${JSON.stringify(FIRST)}, 'Saga 001.cbz')`);
    assert.equal(page.$('#fileInfoSize').textContent, '1.5 KB');
    assert.equal(page.$('#fileInfoProcessed').textContent, '✅ Yes (Renamed & Normalized)');
    assert.equal(page.$('#fileInfoRenamed').textContent, '🔵 Yes');
    assert.equal(page.$('#fileInfoNormalized').textContent, '🔴 Yes');
    assert.equal(page.$('#fileInfoDuplicate').textContent, 'No');
    assert.equal(page.$('#fileInfoDuplicateReview').style.display, 'none');

    page.run(`showFileInfo(${JSON.stringify(SECOND)}, 'Saga 002.cbz')`);
    assert.equal(page.$('#fileInfoSize').textContent, '3.0 MB');
    assert.equal(page.$('#fileInfoProcessed').textContent, '⚠️ No');
    assert.equal(page.$('#fileInfoRenamed').textContent, 'No');
    assert.equal(page.$('#fileInfoNormalized').textContent, 'No');
    assert.equal(page.$('#fileInfoDuplicate').textContent, '🔁 Yes');
    assert.equal(page.$('#fileInfoDuplicateReview').style.display, 'block');
    assert.equal(page.$('#fileInfoDuplicateReviewBtn').dataset.filepath, SECOND);

    // Back to a non-duplicate: the call-out hides and forgets the path.
    page.run(`showFileInfo(${JSON.stringify(FIRST)}, 'Saga 001.cbz')`);
    assert.equal(page.$('#fileInfoDuplicateReview').style.display, 'none');
    assert.equal(page.$('#fileInfoDuplicateReviewBtn').dataset.filepath, undefined);

    // A file outside the cache hides the call-out too.
    page.run(`showFileInfo(${JSON.stringify(SECOND)}, 'Saga 002.cbz'); showFileInfo('Elsewhere/x.cbz', 'x.cbz')`);
    assert.equal(page.$('#fileInfoDuplicateReview').style.display, 'none');
    assert.equal(page.$('#fileInfoDuplicateReviewBtn').dataset.filepath, undefined);
});

test('Review Duplicate in the info modal opens the duplicate review on that file', async t => {
    const page = await boot({
        routes: {
            'GET /api/files': {
                files: [
                    { name: 'Other.cbz', relative_path: 'Other/Other.cbz', size: 10, modified: 0, read: false },
                    { name: 'Saga 002.cbz', relative_path: SECOND, size: 2048, modified: 0, read: true }
                ]
            }
        }
    });
    t.after(() => page.close());
    page.run(`folderFiles.set('Saga', { status: 'loaded', files: [
        { relative_path: ${JSON.stringify(SECOND)}, name: 'Saga 002.cbz', size: 2048, duplicate: true }
    ] })`);
    page.run(`showFileInfo(${JSON.stringify(SECOND)}, 'Saga 002.cbz')`);

    page.click('#fileInfoDuplicateReviewBtn');
    await page.settle();

    assert.ok(!isModalOpen(page, 'fileInfoModal'));
    assert.ok(isModalOpen(page, 'duplicateReviewModal'));
    assert.deepEqual(callsTo(page, 'GET', '/api/files').map(call => call.query),
        [{ filter: 'duplicates', per_page: '-1', sort: 'name' }]);
    assert.equal(page.$('#duplicateReviewName').textContent, 'Saga 002.cbz');
    assert.equal(page.$('#duplicateReviewCounter').textContent, '2 of 2');
});

test('the duplicate call-out does nothing without a file and Escape closes the info modal', async t => {
    const page = await boot();
    t.after(() => page.close());

    clickIssueAction(page, FIRST, 'Info');
    page.run('openDuplicateReviewFromFileInfo()');
    assert.ok(isModalOpen(page, 'fileInfoModal'));
    assert.ok(!isModalOpen(page, 'duplicateReviewModal'));

    await pressEscape(page);
    assert.ok(!isModalOpen(page, 'fileInfoModal'));
});

test('View/Edit loads the issue tags into the editor', async t => {
    const page = await boot({ routes: { [`GET /api/files/${encodedPath(FIRST)}/tags`]: comicMetadata() } });
    t.after(() => page.close());

    clickIssueAction(page, FIRST, 'View/Edit');
    await page.waitFor(() => isModalOpen(page, 'tagModal'));

    assert.equal(page.$('#modalTitle').textContent, `Edit Tags - ${FIRST}`);
    assert.deepEqual(
        ['title', 'series', 'issue', 'volume', 'publisher', 'year', 'summary'].map(id => page.$(`#${id}`).value),
        ['Chapter One', 'Saga', '1', '2012', 'Image', '2012', 'Alana and Marko flee.']
    );
    const [call] = callsTo(page, 'GET', `/api/files/${encodedPath(FIRST)}/tags`);
    assert.match(call.headers.authorization, /^Bearer /);
});

test('View/Edit clears fields the file has no value for', async t => {
    const page = await boot({
        routes: { [`GET /api/files/${encodedPath(FIRST)}/tags`]: comicMetadata({ title: null, year: null, summary: '' }) }
    });
    t.after(() => page.close());
    page.$('#title').value = 'stale';
    page.$('#year').value = '1999';

    page.run(`viewTags(${JSON.stringify(FIRST)})`);
    await page.waitFor(() => isModalOpen(page, 'tagModal'));

    assert.equal(page.$('#title').value, '');
    assert.equal(page.$('#year').value, '');
    assert.equal(page.$('#series').value, 'Saga');
});

test('View/Edit reports a failed tag load and leaves the editor closed', async t => {
    const page = await boot();
    t.after(() => page.close());
    const tagsPath = `GET /api/files/${encodedPath(FIRST)}/tags`;

    page.route(tagsPath, respond(500, 'Error getting tags'));
    await page.run(`viewTags(${JSON.stringify(FIRST)})`);
    assert.deepEqual(messageTexts(page, 'error'), ['Failed to load tags: HTTP error! status: 500']);

    page.route(tagsPath, { error: 'ComicInfo.xml is malformed' });
    await page.run(`viewTags(${JSON.stringify(FIRST)})`);
    assert.equal(messageTexts(page, 'error')[1], 'ComicInfo.xml is malformed');

    page.route(tagsPath, () => { throw new TypeError('Failed to fetch'); });
    await page.run(`viewTags(${JSON.stringify(FIRST)})`);
    assert.equal(messageTexts(page, 'error')[2], 'Failed to load tags: Failed to fetch');

    assert.ok(!isModalOpen(page, 'tagModal'));
    assert.equal(page.run('currentEditFile'), null);
});

test('View/Edit sends an expired session to the login page', async t => {
    const page = await boot({ routes: { [`GET /api/files/${encodedPath(FIRST)}/tags`]: 401 } });
    t.after(() => page.close());

    await page.run(`viewTags(${JSON.stringify(FIRST)})`);

    assert.ok(!isModalOpen(page, 'tagModal'));
    assert.ok(page.navigations.some(nav => nav.url.includes('/login.html')));
    assert.deepEqual(messages(page), []);
});

test('Save sends the edited tags as an update-metadata job', async t => {
    const page = await boot({
        routes: {
            [`GET /api/files/${encodedPath(FIRST)}/tags`]: comicMetadata({ summary: null }),
            'POST /api/jobs/update-metadata-selected': { job_id: 'job-7', total_items: 1 },
            'GET /api/jobs/job-7': { job_id: 'job-7', status: 'running', total_items: 1, processed_items: 0, results: [] }
        }
    });
    t.after(() => page.close());

    clickIssueAction(page, FIRST, 'View/Edit');
    await page.waitFor(() => isModalOpen(page, 'tagModal'));
    page.input('#title', 'The Will');
    page.input('#year', '2013 (reprint)');
    page.input('#publisher', '   ');
    page.click('#tagModal .modal-footer .btn-primary');
    await page.settle();

    assert.ok(!isModalOpen(page, 'tagModal'));
    const [call] = callsTo(page, 'POST', '/api/jobs/update-metadata-selected');
    assert.equal(call.headers['content-type'], 'application/json');
    // Blank fields are left out and Year is sent as a number.
    assert.deepEqual(call.body, {
        Files: [FIRST],
        Metadata: { Title: 'The Will', Series: 'Saga', Issue: '1', Volume: '2012', Year: 2013 }
    });
    assert.deepEqual(messageTexts(page, 'info'), ['Started updating metadata for file in background']);
    assert.equal(page.$('#progressTitle').textContent, 'Updating Metadata...');
    assert.ok(isModalOpen(page, 'progressModal'));
    assert.equal(callsTo(page, 'GET', '/api/jobs/job-7').length, 1);
    assert.equal(page.run('currentEditFile'), null);
});

test('Save drops a Year that is not a number', async t => {
    const page = await boot({
        routes: {
            [`GET /api/files/${encodedPath(FIRST)}/tags`]: comicMetadata(),
            'POST /api/jobs/update-metadata-selected': { job_id: 'job-1', total_items: 1 }
        }
    });
    t.after(() => page.close());

    await page.run(`viewTags(${JSON.stringify(FIRST)})`);
    page.input('#year', 'unknown');
    await page.run('saveTags()');

    const [call] = callsTo(page, 'POST', '/api/jobs/update-metadata-selected');
    assert.equal('Year' in call.body.Metadata, false);
    assert.equal(call.body.Metadata.Series, 'Saga');
});

test('Save reports a job the server did not start', async t => {
    const page = await boot({ routes: { [`GET /api/files/${encodedPath(FIRST)}/tags`]: comicMetadata() } });
    t.after(() => page.close());
    const save = async () => {
        await page.run(`viewTags(${JSON.stringify(FIRST)})`);
        await page.run('saveTags()');
    };

    page.route('POST /api/jobs/update-metadata-selected', { total_items: 1 });
    await save();
    page.route('POST /api/jobs/update-metadata-selected', respond(500, { error: 'boom' }));
    await save();
    page.route('POST /api/jobs/update-metadata-selected', () => { throw new TypeError('Failed to fetch'); });
    await save();

    assert.deepEqual(messageTexts(page, 'error'), [
        'Failed to update metadata: No job ID returned',
        'Failed to update metadata: Failed to start update metadata job',
        'Failed to update metadata: Failed to fetch'
    ]);
    assert.ok(!isModalOpen(page, 'progressModal'));
    assert.equal(page.run('currentEditFile'), null);
});

test('Save with an expired session closes the progress modal and goes to login', async t => {
    const page = await boot({
        routes: {
            [`GET /api/files/${encodedPath(FIRST)}/tags`]: comicMetadata(),
            'POST /api/jobs/update-metadata-selected': 401
        }
    });
    t.after(() => page.close());

    await page.run(`viewTags(${JSON.stringify(FIRST)})`);
    await page.run('saveTags()');

    assert.ok(!isModalOpen(page, 'progressModal'));
    assert.ok(page.navigations.some(nav => nav.url.includes('/login.html')));
    assert.deepEqual(messageTexts(page, 'error'), []);
});

test('Cancel and Escape close the tag editor without saving', async t => {
    const page = await boot({ routes: { [`GET /api/files/${encodedPath(FIRST)}/tags`]: comicMetadata() } });
    t.after(() => page.close());

    await page.run(`viewTags(${JSON.stringify(FIRST)})`);
    page.click('#tagModal .modal-footer .btn:not(.btn-primary)');
    assert.ok(!isModalOpen(page, 'tagModal'));
    assert.equal(page.run('currentEditFile'), null);
    await page.run('saveTags()');

    await page.run(`viewTags(${JSON.stringify(FIRST)})`);
    await pressEscape(page);
    assert.ok(!isModalOpen(page, 'tagModal'));

    assert.equal(callsTo(page, 'POST', '/api/jobs/update-metadata-selected').length, 0);
});

test('Update on a selection opens the batch editor and sends the filled tags', async t => {
    const page = await boot({
        routes: {
            'POST /api/jobs/update-metadata-selected': { job_id: 'job-9', total_items: 2 },
            'GET /api/jobs/job-9': { job_id: 'job-9', status: 'running', total_items: 2, processed_items: 0, results: [] }
        }
    });
    t.after(() => page.close());

    assert.ok(page.$('#batchUpdateBtn').disabled);
    page.run('batchUpdateTags()');
    assert.ok(!isModalOpen(page, 'batchModal'), 'nothing selected');

    for (const box of page.$$('.series-issue-card input[type="checkbox"]')) inputUi(page, box, true);
    assert.ok(!page.$('#batchUpdateBtn').disabled);
    page.click('#batchUpdateBtn');
    assert.ok(isModalOpen(page, 'batchModal'));

    page.input('#batch_series', 'Saga Deluxe');
    page.input('#batch_year', '2014');
    page.input('#batch_publisher', ' ');
    page.click('#batchModal .modal-footer .btn-primary');
    await page.settle();

    assert.ok(!isModalOpen(page, 'batchModal'));
    assert.equal(page.$('#batch_series').value, '', 'the form is reset');
    const [call] = callsTo(page, 'POST', '/api/jobs/update-metadata-selected');
    assert.deepEqual(call.body, { Files: [FIRST, SECOND], Metadata: { Series: 'Saga Deluxe', Year: 2014 } });
    assert.deepEqual(messageTexts(page, 'info'), ['Started updating metadata for 2 selected files in background']);
    assert.equal(page.$('#progressTitle').textContent, 'Updating Metadata for Selected Files...');
    assert.equal(callsTo(page, 'GET', '/api/jobs/job-9').length, 1);
});

test('the batch editor needs at least one tag and ignores a non-numeric year', async t => {
    const page = await boot({ routes: { 'POST /api/jobs/update-metadata-selected': { job_id: 'job-2', total_items: 1 } } });
    t.after(() => page.close());
    page.run(`selectedFiles.add(${JSON.stringify(FIRST)})`);
    page.run('batchUpdateTags()');

    page.click('#batchModal .modal-footer .btn-primary');
    assert.deepEqual(messageTexts(page, 'error'), ['Please enter at least one tag to update']);
    assert.ok(isModalOpen(page, 'batchModal'));

    page.input('#batch_year', 'soon');
    await page.run('saveBatchTags()');
    assert.equal(messageTexts(page, 'error').length, 2, 'a non-numeric year alone is no tag');
    assert.equal(callsTo(page, 'POST', '/api/jobs/update-metadata-selected').length, 0);

    page.input('#batch_writer', 'Brian K. Vaughan');
    await page.run('saveBatchTags()');
    const [call] = callsTo(page, 'POST', '/api/jobs/update-metadata-selected');
    assert.deepEqual(plain(call.body), { Files: [FIRST], Metadata: { Writer: 'Brian K. Vaughan' } });
});

test('batch update failures are reported and close the progress modal', async t => {
    const page = await boot();
    t.after(() => page.close());
    page.run(`selectedFiles.add(${JSON.stringify(FIRST)})`);
    const save = async () => {
        page.run('batchUpdateTags()');
        page.input('#batch_series', 'Saga');
        await page.run('saveBatchTags()');
    };

    page.route('POST /api/jobs/update-metadata-selected', respond(400, { error: 'No files' }));
    await save();
    page.route('POST /api/jobs/update-metadata-selected', () => { throw new TypeError('Failed to fetch'); });
    await save();

    assert.deepEqual(messageTexts(page, 'error'), [
        'Failed to start updating metadata: Failed to start update metadata job',
        'Failed to start updating metadata: Failed to fetch'
    ]);
    assert.ok(!isModalOpen(page, 'progressModal'));

    page.route('POST /api/jobs/update-metadata-selected', 401);
    await save();
    assert.ok(!isModalOpen(page, 'progressModal'));
    assert.ok(page.navigations.some(nav => nav.url.includes('/login.html')));
});

test('Cancel and Escape close the batch editor and clear it', async t => {
    const page = await boot();
    t.after(() => page.close());
    page.run(`selectedFiles.add(${JSON.stringify(FIRST)})`);

    page.run('batchUpdateTags()');
    page.input('#batch_series', 'Draft');
    page.click('#batchModal .modal-footer .btn:not(.btn-primary)');
    assert.ok(!isModalOpen(page, 'batchModal'));
    assert.equal(page.$('#batch_series').value, '');

    page.run('batchUpdateTags()');
    page.input('#batch_series', 'Draft');
    await pressEscape(page);
    assert.ok(!isModalOpen(page, 'batchModal'));
    assert.equal(page.$('#batch_series').value, '');
});
