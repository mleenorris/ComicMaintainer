'use strict';

// js/app/file-actions.js: Preview Merge and Combine in the Combine Folders
// modal. Both post the chosen destination and the group's other folders.
// Preview (POST /api/files/combine-folders/preview) lists the moves the
// server plans; Combine (POST /api/files/combine-folders) asks first, then
// the server moves the files in a background job whose progress the page
// tracks.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const {
    bootPage,
    combinableFolder,
    combinableGroup,
    recordMessages,
    isModalOpen,
    buttonByText,
    answerConfirms,
    callsTo
} = require('./support/email-helpers');

const COMBINABLE = 'GET /api/files/combinable-folders';
const PREVIEW = 'POST /api/files/combine-folders/preview';
const COMBINE = 'POST /api/files/combine-folders';
const JOB_ID = '0b6e2b0e-6c1f-4f0e-9a51-3c1d2f7a9e01';

// Three folders of one series; /comics/Saga is the suggested destination.
const SAGA = combinableGroup({
    totalFileCount: 15,
    folders: [
        combinableFolder(),
        combinableFolder({ directory: '/comics/Saga (2012)', fileCount: 1, isSuggested: false }),
        combinableFolder({ directory: '/comics/Saga (Digital)', fileCount: 2, isSuggested: false })
    ]
});

// The body both requests carry when /comics/Saga (2012) is the destination.
const INTO_2012 = {
    groupKey: 'saga',
    destinationDirectory: '/comics/Saga (2012)',
    sourceDirectories: ['/comics/Saga', '/comics/Saga (Digital)']
};

// A move of the preview response (FilesController.PreviewCombineFolders).
function move(sourcePath, destinationPath, { conflict = false, skipped = false } = {}) {
    return {
        sourcePath,
        destinationPath,
        conflict,
        skipped,
        reason: skipped ? 'Source and destination are the same file.' : null
    };
}

const PROBLEM_DETAILS = {
    type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
    title: 'One or more validation errors occurred.',
    status: 400
};

function boot({ groups = [SAGA], routes = {}, ...options } = {}) {
    return bootPage({ hash: '#/library', ...options, routes: { [COMBINABLE]: { groups }, ...routes } });
}

async function openCombineFolders(page) {
    page.click(buttonByText(page, '.sidebar-nav', 'Combine Folders'));
    await page.settle();
}

function chooseDestination(page, directory) {
    const card = page.$$('#combineFoldersList .combine-folder-card')
        .find(element => element.querySelector('.combine-folder-path').textContent === directory);
    if (!card) throw new Error(`No folder card for ${directory}`);
    page.click(card);
}

function actions(page) {
    return {
        preview: !page.$('#combineFoldersPreviewBtn').disabled,
        combine: !page.$('#combineFoldersConfirmBtn').disabled
    };
}

/**
 * Records what Preview Merge renders into the preview panel, as one entry per
 * element: { em | h4 | 'span.conflict': text } or { list: [[class, text]] }.
 *
 * The panel cannot be read once the request settles: previewCombineFolders()
 * then re-renders the group, which hides and empties the panel
 * (file-actions.js:603-606 → 461-462, reported as a suspected bug), so the
 * test observes the rendering as it happens.
 */
function recordPreviewPanel(page) {
    const rendered = [];
    const observer = new page.window.MutationObserver(records => {
        for (const record of records) {
            for (const node of record.addedNodes) {
                if (node.nodeType !== 1) continue;
                rendered.push(node.tagName === 'UL'
                    ? { list: [...node.children].map(item => [item.className, item.textContent]) }
                    : { [node.tagName.toLowerCase() + (node.className ? `.${node.className}` : '')]: node.textContent });
            }
        }
    });
    observer.observe(page.$('#combineFoldersPreview'), { childList: true });
    return rendered;
}

const BUILDING = { em: 'Building preview...' };

function loggedErrors(page, prefix) {
    return page.consoleMessages.filter(entry => entry.type === 'error' && entry.args[0] === prefix);
}

test('Preview Merge posts the chosen destination and the other folders, then lists the planned moves', async t => {
    const answer = deferred();
    const page = await boot({ routes: { [PREVIEW]: () => answer.promise } });
    t.after(() => page.close());
    await openCombineFolders(page);
    const rendered = recordPreviewPanel(page);

    chooseDestination(page, '/comics/Saga (2012)');
    page.click('#combineFoldersPreviewBtn');

    const panel = page.$('#combineFoldersPreview');
    assert.equal(panel.style.display, 'block');
    assert.equal(panel.textContent, 'Building preview...');
    assert.deepEqual(actions(page), { preview: false, combine: false });
    const [call] = callsTo(page, 'POST', '/api/files/combine-folders/preview');
    assert.deepEqual(call.body, INTO_2012);
    assert.equal(call.headers['content-type'], 'application/json');
    assert.match(call.headers.authorization, /^Bearer /);

    answer.resolve({
        destination: '/comics/Saga (2012)',
        sources: INTO_2012.sourceDirectories,
        moves: [
            move('/comics/Saga/Saga 001.cbz', '/comics/Saga (2012)/Saga 001.cbz'),
            move('/comics/Saga (Digital)/Saga 001.cbz', '/comics/Saga (2012)/Saga 001 (1).cbz', { conflict: true }),
            move('/comics/Saga (2012)/Saga 003.cbz', '/comics/Saga (2012)/Saga 003.cbz', { skipped: true })
        ]
    });
    await page.settle();

    assert.deepEqual(rendered, [
        BUILDING,
        { h4: 'Move plan (3 files, 1 renamed, 1 skipped)' },
        {
            list: [
                ['', '/comics/Saga/Saga 001.cbz → /comics/Saga (2012)/Saga 001.cbz'],
                ['conflict', '/comics/Saga (Digital)/Saga 001.cbz → /comics/Saga (2012)/Saga 001 (1).cbz (renamed to avoid conflict)'],
                ['skipped', '/comics/Saga (2012)/Saga 003.cbz → /comics/Saga (2012)/Saga 003.cbz (skipped)']
            ]
        }
    ]);
    assert.deepEqual(actions(page), { preview: true, combine: true });
    assert.equal(callsTo(page, 'POST', '/api/files/combine-folders').length, 0);
});

test('the actions stay disabled while a preview is pending, even if another folder is picked', async t => {
    const answer = deferred();
    const page = await boot({ routes: { [PREVIEW]: () => answer.promise } });
    t.after(() => page.close());
    await openCombineFolders(page);

    page.click('#combineFoldersPreviewBtn');
    chooseDestination(page, '/comics/Saga (Digital)');
    assert.deepEqual(actions(page), { preview: false, combine: false });
    // A disabled button cannot start a second request.
    page.click('#combineFoldersPreviewBtn');
    assert.equal(callsTo(page, 'POST', '/api/files/combine-folders/preview').length, 1);

    answer.resolve({ moves: [] });
    await page.settle();
    assert.deepEqual(actions(page), { preview: true, combine: true });
    page.click('#combineFoldersPreviewBtn');
    const calls = callsTo(page, 'POST', '/api/files/combine-folders/preview');
    assert.equal(calls.length, 2);
    assert.equal(calls[0].body.destinationDirectory, '/comics/Saga');
    assert.equal(calls[1].body.destinationDirectory, '/comics/Saga (Digital)');
});

test('a preview without moves says so, and a single move is counted in the singular', async t => {
    const answers = [
        { destination: '/comics/Saga', sources: ['/comics/Saga (2012)', '/comics/Saga (Digital)'], moves: [] },
        { destination: '/comics/Saga', sources: ['/comics/Saga (2012)', '/comics/Saga (Digital)'] },
        { moves: [move('/comics/Saga (2012)/Saga 003.cbz', '/comics/Saga/Saga 003.cbz')] }
    ];
    const page = await boot({ routes: { [PREVIEW]: () => answers.shift() } });
    t.after(() => page.close());
    await openCombineFolders(page);
    const rendered = recordPreviewPanel(page);

    for (let preview = 0; preview < 3; preview++) {
        page.click('#combineFoldersPreviewBtn');
        await page.settle();
    }

    assert.deepEqual(rendered, [
        BUILDING,
        { em: 'No files will be moved.' },
        BUILDING,
        { em: 'No files will be moved.' },
        BUILDING,
        { h4: 'Move plan (1 file)' },
        { list: [['', '/comics/Saga (2012)/Saga 003.cbz → /comics/Saga/Saga 003.cbz']] }
    ]);
    const bodies = callsTo(page, 'POST', '/api/files/combine-folders/preview').map(call => call.body);
    assert.deepEqual(bodies, Array(3).fill({
        groupKey: 'saga',
        destinationDirectory: '/comics/Saga',
        sourceDirectories: ['/comics/Saga (2012)', '/comics/Saga (Digital)']
    }));
});

test('a failed preview is reported in the panel and the actions come back', async t => {
    const page = await boot({
        routes: { [PREVIEW]: respond(400, { error: 'Destination directory is not part of this group.' }) }
    });
    t.after(() => page.close());
    await openCombineFolders(page);
    const rendered = recordPreviewPanel(page);
    const preview = async () => {
        page.click('#combineFoldersPreviewBtn');
        await page.settle();
        assert.deepEqual(actions(page), { preview: true, combine: true });
        return rendered.splice(0);
    };

    assert.deepEqual(await preview(), [
        BUILDING,
        { 'span.conflict': 'Failed to build preview: Destination directory is not part of this group.' }
    ]);
    assert.equal(loggedErrors(page, 'Failed to build combine preview:').length, 1);

    // An error without a message (a validation problem).
    page.route(PREVIEW, respond(400, PROBLEM_DETAILS));
    assert.deepEqual(await preview(), [BUILDING, { 'span.conflict': 'Failed to build preview: HTTP error! status: 400' }]);

    page.route(PREVIEW, () => { throw new TypeError('Failed to fetch'); });
    assert.deepEqual(await preview(), [BUILDING, { 'span.conflict': 'Failed to build preview: Failed to fetch' }]);

    // The server's plain-text 500. (The page parses the body as JSON before
    // checking the status, so the detail is a JSON parse error; reported.)
    page.route(PREVIEW, respond(500, 'Error building combine preview'));
    const [building, failure] = await preview();
    assert.deepEqual(building, BUILDING);
    assert.match(failure['span.conflict'], /^Failed to build preview: /);

    assert.equal(loggedErrors(page, 'Failed to build combine preview:').length, 4);
    assert.equal(callsTo(page, 'POST', '/api/files/combine-folders/preview').length, 4);
});

test('an expired session during a preview goes to the login page', async t => {
    const page = await boot({ routes: { [PREVIEW]: 401 } });
    t.after(() => page.close());
    await openCombineFolders(page);
    const rendered = recordPreviewPanel(page);

    page.click('#combineFoldersPreviewBtn');
    await page.settle();

    assert.ok(page.navigations.some(nav => nav.url.includes('/login.html')));
    assert.deepEqual(rendered, [BUILDING]);
    assert.deepEqual(loggedErrors(page, 'Failed to build combine preview:'), []);
});

test('Combine asks before moving files and does nothing when declined', async t => {
    const page = await boot();
    t.after(() => page.close());
    answerConfirms(page, false);
    await openCombineFolders(page);

    page.click('#combineFoldersConfirmBtn');
    await page.settle();

    assert.deepEqual(page.dialogs.map(dialog => dialog.message), [
        'Combine 2 folders into:\n/comics/Saga\n\nFiles will be moved on disk. Continue?'
    ]);
    assert.deepEqual(callsTo(page, 'POST', '/api/files/combine-folders'), []);
    assert.deepEqual(actions(page), { preview: true, combine: true });
    assert.ok(isModalOpen(page, 'combineFoldersModal'));
});

test('a confirmed Combine moves the files in a background job and shows its progress', async t => {
    const answer = deferred();
    const page = await boot({
        routes: {
            [COMBINE]: () => answer.promise,
            [`GET /api/jobs/${JOB_ID}`]: { job_id: JOB_ID, status: 'running', total_items: 3, processed_items: 0, results: [] }
        }
    });
    t.after(() => page.close());
    const shown = recordMessages(page);
    await openCombineFolders(page);

    chooseDestination(page, '/comics/Saga (2012)');
    page.click('#combineFoldersConfirmBtn');

    assert.deepEqual(page.dialogs.map(dialog => dialog.message), [
        'Combine 2 folders into:\n/comics/Saga (2012)\n\nFiles will be moved on disk. Continue?'
    ]);
    assert.deepEqual(actions(page), { preview: false, combine: false });
    const [call] = callsTo(page, 'POST', '/api/files/combine-folders');
    assert.deepEqual(call.body, INTO_2012);
    assert.equal(call.headers['content-type'], 'application/json');
    assert.match(call.headers.authorization, /^Bearer /);

    answer.resolve(respond(202, {
        jobId: JOB_ID,
        job_id: JOB_ID,
        totalItems: 3,
        total_items: 3,
        destination: '/comics/Saga (2012)',
        sources: INTO_2012.sourceDirectories,
        skipped: 1,
        skippedResults: [{
            sourcePath: '/comics/Saga (2012)/Saga 003.cbz',
            destinationPath: '/comics/Saga (2012)/Saga 003.cbz',
            status: 'skipped',
            reason: 'Source and destination are the same file.'
        }]
    }));
    await page.settle();

    assert.deepEqual(shown[0], { text: 'Combining 3 file(s) in background (1 skipped)', type: 'info' });
    assert.ok(isModalOpen(page, 'progressModal'));
    assert.equal(page.$('#progressTitle').textContent, 'Combining 3 file(s)...');
    assert.equal(callsTo(page, 'GET', `/api/jobs/${JOB_ID}`).length, 1);
    assert.ok(!shown.some(message => message.type === 'error'));
    assert.equal(callsTo(page, 'POST', '/api/files/combine-folders').length, 1);
});

test('Combine names a single source folder and reads the snake_case job fields', async t => {
    const page = await boot({
        groups: [combinableGroup()],
        routes: {
            [COMBINE]: respond(202, { job_id: 'job-2', total_items: 4, skipped: 0 }),
            'GET /api/jobs/job-2': { job_id: 'job-2', status: 'running', total_items: 4, processed_items: 0, results: [] }
        }
    });
    t.after(() => page.close());
    const shown = recordMessages(page);
    await openCombineFolders(page);

    page.click('#combineFoldersConfirmBtn');
    await page.settle();

    assert.deepEqual(page.dialogs.map(dialog => dialog.message), [
        'Combine 1 folder into:\n/comics/Saga\n\nFiles will be moved on disk. Continue?'
    ]);
    assert.deepEqual(callsTo(page, 'POST', '/api/files/combine-folders')[0].body, {
        groupKey: 'saga',
        destinationDirectory: '/comics/Saga',
        sourceDirectories: ['/comics/Saga (2012)']
    });
    assert.deepEqual(shown[0], { text: 'Combining 4 file(s) in background', type: 'info' });
    assert.equal(page.$('#progressTitle').textContent, 'Combining 4 file(s)...');
    assert.equal(callsTo(page, 'GET', '/api/jobs/job-2').length, 1);
});

test('when nothing needs moving Combine says so and reloads the library and the folder list', async t => {
    const answers = [{ groups: [SAGA] }, { groups: [] }];
    const page = await boot({
        routes: {
            [COMBINABLE]: () => answers.shift(),
            'GET /api/files/counts': { total: 15, processed: 15, unprocessed: 0, duplicates: 0, combinableFolders: 1 },
            [COMBINE]: respond(202, {
                destination: '/comics/Saga',
                sources: ['/comics/Saga (2012)', '/comics/Saga (Digital)'],
                moved: 0,
                skipped: 2,
                failed: 0,
                totalItems: 0,
                results: []
            })
        }
    });
    t.after(() => page.close());
    const shown = recordMessages(page);
    await openCombineFolders(page);
    const countRequests = callsTo(page, 'GET', '/api/files/counts').length;

    page.click('#combineFoldersConfirmBtn');
    await page.settle();

    assert.deepEqual(shown, [
        { text: 'Combine complete: 0 moved, 2 skipped, 0 failed.', type: 'info' },
        { text: 'Refreshing file list...', type: 'info' }
    ]);
    // Library health is reloaded (refreshing the file list reloads it too).
    assert.ok(callsTo(page, 'GET', '/api/files/counts').length > countRequests);
    assert.equal(callsTo(page, 'GET', '/api/files/combinable-folders').length, 2);
    assert.ok(!isModalOpen(page, 'progressModal'));
    assert.ok(!page.fetchCalls.some(call => call.path.startsWith('/api/jobs/')));
    // The reloaded list has nothing left to combine.
    assert.ok(isModalOpen(page, 'combineFoldersModal'));
    const emptyState = page.$('#combineFoldersEmptyState');
    assert.equal(emptyState.style.display, 'block');
    assert.equal(emptyState.textContent, 'No folders look combinable right now.');
    assert.equal(page.$('#combineFoldersContent').style.display, 'none');
    assert.equal(page.$('#combineFoldersCounter').textContent, '0 of 0');
    assert.deepEqual(actions(page), { preview: false, combine: false });
});

test('a failed Combine is reported and the actions come back', async t => {
    const page = await boot({
        routes: {
            [COMBINE]: respond(400, {
                error: 'No combinable folder group matches the request. The library may have changed; please reload.'
            })
        }
    });
    t.after(() => page.close());
    const shown = recordMessages(page);
    await openCombineFolders(page);
    const combine = async () => {
        page.click('#combineFoldersConfirmBtn');
        await page.settle();
        assert.deepEqual(actions(page), { preview: true, combine: true });
        assert.ok(!isModalOpen(page, 'progressModal'));
        return shown.splice(0);
    };

    assert.deepEqual(await combine(), [{
        text: 'Failed to combine folders: No combinable folder group matches the request. The library may have changed; please reload.',
        type: 'error'
    }]);

    page.route(COMBINE, respond(400, PROBLEM_DETAILS));
    assert.deepEqual(await combine(), [{ text: 'Failed to combine folders: HTTP error! status: 400', type: 'error' }]);

    page.route(COMBINE, () => { throw new TypeError('Failed to fetch'); });
    assert.deepEqual(await combine(), [{ text: 'Failed to combine folders: Failed to fetch', type: 'error' }]);

    // The server's plain-text 500 (parsed as JSON first, as with the preview).
    page.route(COMBINE, respond(500, 'Error combining folders'));
    const [failure, ...rest] = await combine();
    assert.deepEqual(rest, []);
    assert.equal(failure.type, 'error');
    assert.match(failure.text, /^Failed to combine folders: /);

    assert.equal(loggedErrors(page, 'Failed to combine folders:').length, 4);
    assert.equal(page.dialogs.length, 4);
    assert.equal(callsTo(page, 'GET', '/api/files/combinable-folders').length, 1);
});

test('an expired session during Combine goes to the login page', async t => {
    const page = await boot({ routes: { [COMBINE]: 401 } });
    t.after(() => page.close());
    const shown = recordMessages(page);
    await openCombineFolders(page);

    page.click('#combineFoldersConfirmBtn');
    await page.settle();

    assert.ok(page.navigations.some(nav => nav.url.includes('/login.html')));
    assert.deepEqual(shown, []);
    assert.ok(!isModalOpen(page, 'progressModal'));
    assert.deepEqual(loggedErrors(page, 'Failed to combine folders:'), []);
});
