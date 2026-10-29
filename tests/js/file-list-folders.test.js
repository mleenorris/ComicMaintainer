'use strict';

// js/app/file-list.js: working with folders in the legacy Files view —
// opening and closing them (one at a time or all at once), selecting files,
// whole folders or everything loaded, and keeping open folders fresh as the
// server reports processed files.

const test = require('node:test');
const assert = require('node:assert/strict');
const { deferred } = require('./support/app');
const {
    makeFolder,
    makeFile,
    encodeFolder,
    folderFilesMatcher,
    folderPages,
    bootFilesView,
    callsTo,
    folderFileCalls
} = require('./support/library-helpers');

const section = (page, path) => page.$$('#fileList .folder-section')
    .find(element => element.querySelector('.directory-content').dataset.dir === path);
const isCollapsed = (page, path) => section(page, path).querySelector('.directory-content').classList.contains('collapsed');
const rowsIn = (page, path) => [...section(page, path).querySelectorAll('.file-item .file-name')].map(element => element.title);
const toggleAllLabel = page => page.$('#toggleAllBtn').textContent.trim();
const selected = page => page.evaluate('[...selectedFiles].sort()');
const fileCheckbox = (page, path) => page.$$('#fileList .file-item')
    .find(row => row.querySelector('.file-name').title === path.split('/').pop())
    .querySelector('input[type="checkbox"]');

async function clickFolderToggle(page, path, part = '.directory-toggle-btn') {
    page.click(section(page, path).querySelector(part));
    await page.settle();
}

/** Two-file folders named after `names`, each served its files. */
function library(...names) {
    const files = {};
    for (const name of names) files[name] = [makeFile(`${name}/${name} #001.cbz`), makeFile(`${name}/${name} #002.cbz`)];
    return { folders: names.map(name => makeFolder(name)), files };
}

// The library root's files: its path is empty, so it has a URL of its own.
const ROOT_FILES = '/api/files/folders/files';

/** library(...names) listed after the library root, which holds "Root Issue.cbz". */
function libraryWithRoot(...names) {
    const { folders, files } = library(...names);
    return {
        folders: [makeFolder('', { file_count: 1 }), ...folders],
        files,
        routes: { [`GET ${ROOT_FILES}`]: { path: '', files: [makeFile('Root Issue.cbz')] } }
    };
}

test('a folder opens and closes from its arrow or its name, loading its files once', async () => {
    const page = await bootFilesView(library('Batman', 'Saga'));

    await clickFolderToggle(page, 'Batman');
    assert.equal(isCollapsed(page, 'Batman'), false);
    assert.deepEqual(rowsIn(page, 'Batman'), ['Batman #001.cbz', 'Batman #002.cbz']);

    await clickFolderToggle(page, 'Batman', '.directory-name-section');
    assert.equal(isCollapsed(page, 'Batman'), true);
    assert.ok(section(page, 'Batman').querySelector('.directory-toggle').classList.contains('collapsed'));
    assert.deepEqual(rowsIn(page, 'Batman'), [], 'a closed folder renders no rows');

    await clickFolderToggle(page, 'Batman', '.directory-name-section');
    assert.deepEqual(rowsIn(page, 'Batman'), ['Batman #001.cbz', 'Batman #002.cbz']);
    assert.deepEqual(folderFileCalls(page).map(call => call.folder), ['Batman']);
});

test('the expand-all control follows the folders opened one by one', async () => {
    const page = await bootFilesView(library('Batman', 'Saga'));
    assert.equal(toggleAllLabel(page), '▶');

    // Any open folder turns it into "collapse all"...
    await clickFolderToggle(page, 'Batman');
    assert.equal(toggleAllLabel(page), '▼');

    page.click('#toggleAllBtn');
    await page.settle();
    assert.equal(isCollapsed(page, 'Batman'), true);
    assert.equal(isCollapsed(page, 'Saga'), true);
    assert.equal(toggleAllLabel(page), '▶');

    // ...and once the last open folder is closed it expands everything again.
    await clickFolderToggle(page, 'Saga');
    await clickFolderToggle(page, 'Saga');
    assert.equal(isCollapsed(page, 'Saga'), true);
    page.click('#toggleAllBtn');
    await page.settle();
    assert.equal(isCollapsed(page, 'Batman'), false);
    assert.equal(isCollapsed(page, 'Saga'), false);
    assert.equal(toggleAllLabel(page), '▼');
});

test('the expand-all icon shows the state left by the folder last closed or reopened', async () => {
    const page = await bootFilesView(library('Batman', 'Saga'));
    await clickFolderToggle(page, 'Batman');
    assert.equal(toggleAllLabel(page), '▼');

    // Closing the only open folder leaves every folder closed...
    await clickFolderToggle(page, 'Batman');
    assert.equal(toggleAllLabel(page), '▶');

    // ...and reopening it, its files already loaded, leaves one open.
    await clickFolderToggle(page, 'Batman');
    assert.equal(folderFileCalls(page).length, 1);
    assert.equal(toggleAllLabel(page), '▼');

    // A click does what the icon shows.
    page.click('#toggleAllBtn');
    await page.settle();
    assert.equal(isCollapsed(page, 'Batman'), true);
    assert.equal(isCollapsed(page, 'Saga'), true);
    assert.equal(toggleAllLabel(page), '▶');
});

test('the expand-all control counts only the folders still listed', async () => {
    const listed = { all: ['Batman', 'Saga'], unmarked: ['Batman', 'Other'] };
    const page = await bootFilesView({
        routes: {
            'GET /api/files/folders': call => folderPages(listed[call.query.filter || 'all'].map(name => makeFolder(name)))(call)
        }
    });
    await clickFolderToggle(page, 'Batman');

    // Saga, which is closed, drops out of the list; Batman stays open.
    page.click('#headerFilterMenu [data-filter="unmarked"]');
    await page.settle();
    assert.deepEqual(page.$$('#fileList .directory-path').map(element => element.textContent), ['Batman', 'Other']);
    assert.equal(isCollapsed(page, 'Batman'), false);
    assert.equal(toggleAllLabel(page), '▼');

    page.click('#toggleAllBtn');
    await page.settle();
    assert.equal(isCollapsed(page, 'Batman'), true, 'collapse all closes the open folder');
    assert.equal(isCollapsed(page, 'Other'), true);
    assert.equal(toggleAllLabel(page), '▶');
});

test('expand all opens every folder, loading at most four at a time', async () => {
    const names = ['A', 'B', 'C', 'D', 'E', 'F'];
    const page = await bootFilesView({ folders: names.map(name => makeFolder(name, { file_count: 1 })) });
    const gates = new Map();
    page.route(folderFilesMatcher, async call => {
        const gate = deferred();
        gates.set(call.folder, gate);
        await gate.promise;
        return { path: call.folder, files: [makeFile(`${call.folder}/${call.folder} #001.cbz`)] };
    });
    const requested = () => folderFileCalls(page).map(call => call.folder);

    page.click('#toggleAllBtn');
    await page.settle();
    assert.deepEqual(requested(), ['A', 'B', 'C', 'D']);
    assert.ok(names.every(name => !isCollapsed(page, name)));
    assert.equal(page.$$('#fileList .folder-loading').length, 4);
    assert.equal(toggleAllLabel(page), '▼');

    gates.get('B').resolve();
    await page.settle();
    assert.deepEqual(requested(), ['A', 'B', 'C', 'D', 'E']);
    assert.deepEqual(rowsIn(page, 'B'), ['B #001.cbz']);

    for (const name of ['A', 'C', 'D', 'E']) gates.get(name).resolve();
    await page.settle();
    assert.deepEqual(requested(), names);
    gates.get('F').resolve();
    await page.settle();
    assert.deepEqual(names.map(name => rowsIn(page, name).length), [1, 1, 1, 1, 1, 1]);

    // Collapse all, then expand all again from the cache.
    page.click('#toggleAllBtn');
    await page.settle();
    assert.ok(names.every(name => isCollapsed(page, name)));
    assert.equal(toggleAllLabel(page), '▶');

    page.click('#toggleAllBtn');
    await page.settle();
    assert.ok(names.every(name => !isCollapsed(page, name) && rowsIn(page, name).length === 1));
    assert.equal(folderFileCalls(page).length, 6, 'loaded folders are not fetched again');
});

test('expand all retries folders that failed to load', async () => {
    const { folders, files } = library('Batman', 'Saga');
    const page = await bootFilesView({ folders });
    let failSaga = true;
    page.route(folderFilesMatcher, call => (call.folder === 'Saga' && failSaga ? 500 : { path: call.folder, files: files[call.folder] }));

    page.click('#toggleAllBtn');
    await page.settle();
    assert.ok(section(page, 'Saga').querySelector('.folder-error'));
    assert.deepEqual(rowsIn(page, 'Batman'), ['Batman #001.cbz', 'Batman #002.cbz']);

    failSaga = false;
    page.click('#toggleAllBtn');
    await page.settle();
    page.click('#toggleAllBtn');
    await page.settle();

    assert.deepEqual(folderFileCalls(page).map(call => call.folder), ['Batman', 'Saga', 'Saga']);
    assert.deepEqual(rowsIn(page, 'Saga'), ['Saga #001.cbz', 'Saga #002.cbz']);
});

test('the library root has no header to close it by, so it is open from the start and stays open', async () => {
    const page = await bootFilesView(libraryWithRoot('Batman'));

    assert.equal(section(page, '').querySelector('.directory-header'), null);
    assert.deepEqual(rowsIn(page, ''), ['Root Issue.cbz']);
    assert.equal(isCollapsed(page, 'Batman'), true);
    assert.equal(callsTo(page, ROOT_FILES).length, 1);
    assert.equal(toggleAllLabel(page), '▶', 'the open root does not count as an open folder');

    page.click('#toggleAllBtn');
    await page.settle();
    assert.deepEqual(rowsIn(page, 'Batman'), ['Batman #001.cbz', 'Batman #002.cbz']);
    assert.deepEqual(rowsIn(page, ''), ['Root Issue.cbz']);
    assert.deepEqual(folderFileCalls(page).map(call => call.path), [`/api/files/folders/${encodeFolder('Batman')}/files`]);
    assert.equal(callsTo(page, ROOT_FILES).length, 1, 'expand all does not fetch the loaded root again');
    assert.equal(toggleAllLabel(page), '▼');

    page.click('#toggleAllBtn');
    await page.settle();
    assert.equal(isCollapsed(page, 'Batman'), true);
    assert.equal(isCollapsed(page, ''), false, 'collapse all leaves the root open');
    assert.deepEqual(rowsIn(page, ''), ['Root Issue.cbz']);
    assert.equal(toggleAllLabel(page), '▶');
});

// ── Selection ─────────────────────────────────────────────────────────────

test('a folder\'s checkbox selects all of its files, loading them first', async () => {
    const page = await bootFilesView(library('Batman', 'Saga'));

    page.input(section(page, 'Batman').querySelector('.directory-checkbox'), true);
    await page.settle();

    assert.deepEqual(selected(page), ['Batman/Batman #001.cbz', 'Batman/Batman #002.cbz']);
    assert.equal(isCollapsed(page, 'Batman'), true, 'selecting does not open the folder');
    assert.equal(section(page, 'Batman').querySelector('.directory-checkbox').checked, true);
    assert.equal(page.$('#selectInfo').textContent, '2 files selected');
    assert.equal(page.$('#batchUpdateBtn').disabled, false);
    assert.equal(page.$('#deleteSelectedBtn').disabled, false);
    assert.equal(page.$('#renameSelectedItem').disabled, false);

    page.input(section(page, 'Batman').querySelector('.directory-checkbox'), false);
    await page.settle();

    assert.deepEqual(selected(page), []);
    assert.equal(page.$('#selectInfo').textContent, 'No files selected');
    assert.equal(page.$('#batchUpdateBtn').disabled, true);
    assert.equal(page.$('#deleteSelectedBtn').disabled, true);
    assert.deepEqual(folderFileCalls(page).map(call => call.folder), ['Batman'], 'a loaded folder is not fetched again');
});

test('a folder whose files cannot be loaded selects nothing', async () => {
    const page = await bootFilesView({ folders: [makeFolder('Broken')] });
    page.route(folderFilesMatcher, 500);

    page.input(section(page, 'Broken').querySelector('.directory-checkbox'), true);
    await page.settle();

    assert.deepEqual(selected(page), []);
    assert.equal(page.$('#selectInfo').textContent, 'No files selected');
    assert.equal(page.$('#batchUpdateBtn').disabled, true);
});

test('ticking a folder while its files are loading waits for that request', async () => {
    const gate = deferred();
    const { folders, files } = library('Batman');
    const page = await bootFilesView({ folders });
    page.route(folderFilesMatcher, async call => {
        await gate.promise;
        return { path: call.folder, files: files[call.folder] };
    });

    await clickFolderToggle(page, 'Batman');
    assert.ok(section(page, 'Batman').querySelector('.folder-loading'));
    page.input(section(page, 'Batman').querySelector('.directory-checkbox'), true);
    await page.settle();
    assert.deepEqual(selected(page), []);

    gate.resolve();
    await page.settle();
    assert.deepEqual(selected(page), ['Batman/Batman #001.cbz', 'Batman/Batman #002.cbz']);
    assert.equal(section(page, 'Batman').querySelector('.directory-checkbox').checked, true);
    assert.deepEqual(rowsIn(page, 'Batman'), ['Batman #001.cbz', 'Batman #002.cbz']);
    assert.equal(folderFileCalls(page).length, 1, 'the folder is fetched once');
});

test('file checkboxes select single files, and the folder shows a partial selection', async () => {
    const page = await bootFilesView(library('Batman'));
    await clickFolderToggle(page, 'Batman');

    page.input(fileCheckbox(page, 'Batman/Batman #001.cbz'), true);
    assert.deepEqual(selected(page), ['Batman/Batman #001.cbz']);
    assert.equal(page.$('#selectInfo').textContent, '1 file selected');
    assert.equal(page.$('#selectAll').indeterminate, true);
    assert.equal(page.$('#selectAll').checked, false);

    // The folder's box shows the partial selection once the list redraws.
    await clickFolderToggle(page, 'Batman');
    let folderBox = section(page, 'Batman').querySelector('.directory-checkbox');
    assert.equal(folderBox.checked, false);
    assert.equal(folderBox.style.opacity, '0.5');

    await clickFolderToggle(page, 'Batman');
    page.input(fileCheckbox(page, 'Batman/Batman #002.cbz'), true);
    assert.equal(page.$('#selectAll').checked, true);
    assert.equal(page.$('#selectAll').indeterminate, false);
    await clickFolderToggle(page, 'Batman');
    folderBox = section(page, 'Batman').querySelector('.directory-checkbox');
    assert.equal(folderBox.checked, true);
    assert.equal(folderBox.style.opacity, '');
    // The rows keep their ticks when the folder is reopened.
    await clickFolderToggle(page, 'Batman');
    assert.ok(page.$$('#fileList .file-item input[type="checkbox"]').every(box => box.checked));
});

test('select all selects every loaded file but not files in unopened folders', async () => {
    const page = await bootFilesView(library('Batman', 'Saga'));

    page.click('#selectAll');
    await page.settle();
    assert.deepEqual(selected(page), [], 'nothing is loaded yet');
    assert.equal(page.$('#selectAll').checked, false);

    await clickFolderToggle(page, 'Batman');
    page.click('#selectAll');
    await page.settle();
    assert.deepEqual(selected(page), ['Batman/Batman #001.cbz', 'Batman/Batman #002.cbz']);
    assert.equal(page.$('#selectAll').checked, true);
    assert.equal(page.$('#selectInfo').textContent, '2 files selected');
    assert.ok(page.$$('#fileList .file-item input[type="checkbox"]').every(box => box.checked));

    page.click('#selectAll');
    await page.settle();
    assert.deepEqual(selected(page), []);
    assert.equal(page.$('#selectAll').checked, false);
});

// ── Live updates ──────────────────────────────────────────────────────────

const fileProcessed = (page, filename) => page.eventSources.at(-1)
    .emit('message', { type: 'file_processed', data: { filename, success: true } });

test('a processed file reloads its folder if open, or drops its cached files if closed', async () => {
    const page = await bootFilesView(library('Batman', 'Saga', 'Other'));
    await clickFolderToggle(page, 'Batman');
    await clickFolderToggle(page, 'Saga');
    await clickFolderToggle(page, 'Saga');
    const requested = () => folderFileCalls(page).map(call => call.folder);
    assert.deepEqual(requested(), ['Batman', 'Saga']);

    fileProcessed(page, 'Batman\\Batman #001.cbz');
    await page.settle();
    assert.deepEqual(requested(), ['Batman', 'Saga', 'Batman'], 'the open folder is reloaded');
    assert.deepEqual(rowsIn(page, 'Batman'), ['Batman #001.cbz', 'Batman #002.cbz']);

    fileProcessed(page, 'Saga/Saga #001.cbz');
    fileProcessed(page, 'Other/Other #001.cbz');
    fileProcessed(page, 'loose.cbz');
    page.eventSources.at(-1).emit('message', { type: 'file_processed', data: { success: true } });
    await page.settle();
    assert.equal(folderFileCalls(page).length, 3, 'closed and never-opened folders are not fetched');

    await clickFolderToggle(page, 'Saga');
    assert.deepEqual(requested(), ['Batman', 'Saga', 'Batman', 'Saga'], 'the closed folder reloads when reopened');
});

test('a processed file at the library root reloads the root\'s files', async () => {
    const page = await bootFilesView(libraryWithRoot('Batman'));
    assert.equal(callsTo(page, ROOT_FILES).length, 1);

    fileProcessed(page, 'Root Issue.cbz');
    await page.settle();

    assert.equal(callsTo(page, ROOT_FILES).length, 2);
    assert.deepEqual(folderFileCalls(page), []);
    assert.deepEqual(rowsIn(page, ''), ['Root Issue.cbz']);
});

test('selections of files gone from a reloaded folder are dropped; others are kept', async () => {
    const { folders, files } = library('Batman', 'Saga');
    const page = await bootFilesView({ folders, files });
    await clickFolderToggle(page, 'Batman');
    page.input(fileCheckbox(page, 'Batman/Batman #002.cbz'), true);
    // A selection in a folder that isn't loaded here (e.g. made in the series view).
    page.run("selectedFiles.add('Saga/Saga #001.cbz')");

    files.Batman = files.Batman.slice(0, 1);
    fileProcessed(page, 'Batman/Batman #002.cbz');
    await page.settle();

    assert.deepEqual(rowsIn(page, 'Batman'), ['Batman #001.cbz']);
    assert.deepEqual(selected(page), ['Saga/Saga #001.cbz']);
    assert.equal(page.$('#selectInfo').textContent, '1 file selected');
});
