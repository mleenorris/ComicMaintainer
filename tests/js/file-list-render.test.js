'use strict';

// js/app/file-list.js (with library.js's loadFolders): the legacy Files view —
// one collapsible section per folder, a row per file with its processing
// status, size and age, and the empty, loading and error states around them.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const {
    makeFolder,
    makeFile,
    encodeFolder,
    folderFilesMatcher,
    folderPages,
    bootLibrary,
    bootFilesView,
    folderCalls,
    folderFileCalls,
    callsTo,
    messages,
    scrollObserver
} = require('./support/library-helpers');

const MiB = 1024 * 1024;

/** The folder sections as rendered. */
function folderSections(page) {
    return page.$$('#fileList .folder-section').map(section => ({
        path: section.querySelector('.directory-path')?.textContent ?? null,
        count: section.querySelector('.directory-file-count')?.textContent ?? null,
        collapsed: section.querySelector('.directory-content').classList.contains('collapsed'),
        rows: section.querySelectorAll('.file-item').length
    }));
}

/** Expand the folder at `path` from its header, as a click on its arrow does. */
async function expandFolder(page, path) {
    const content = page.$$('#fileList .directory-content').find(element => element.dataset.dir === path);
    page.click(content.parentElement.querySelector('.directory-toggle-btn'));
    await page.settle();
}

const rowNames = page => page.$$('#fileList .file-item .file-name').map(element => element.title);

/** Counts renderFileList() calls from now on; returns a reader for the count. */
function countRenders(page) {
    page.run(`(() => {
        const render = renderFileList;
        window.fileListRenders = 0;
        renderFileList = function () { window.fileListRenders++; return render.apply(this, arguments); };
    })()`);
    return () => page.evaluate('window.fileListRenders');
}

/**
 * Records what is drawn into #fileList from now on, one entry per element
 * added: 'loading', 'header', 'folder' or an empty state's heading.
 */
function recordDrawn(page) {
    const drawn = [];
    const kind = node => node.matches('.loading') ? 'loading'
        : node.matches('.file-list-header') ? 'header'
            : node.matches('.folder-section') ? 'folder'
                : node.querySelector('h2')?.textContent ?? node.className;
    new page.window.MutationObserver(records => {
        for (const record of records) {
            for (const node of record.addedNodes) {
                if (node.nodeType === 1 && node.id !== 'libraryScrollSentinel') drawn.push(kind(node));
            }
        }
    }).observe(page.$('#fileList'), { childList: true });
    return drawn;
}

/**
 * Boot the library, then switch to the Files view with `folders` listed
 * (as bootFilesView does) while counting renders and recording the drawing.
 */
async function openFilesViewWatched(folders) {
    const page = await bootLibrary({ routes: { 'GET /api/files/folders': folderPages(folders) } });
    const renders = countRenders(page);
    const drawn = recordDrawn(page);
    page.run("libraryViewMode = 'files'");
    await page.run('loadActiveLibraryView()');
    await page.settle();
    return { page, renders, drawn };
}

test('the Files view lists each folder collapsed, under the list header', async () => {
    const page = await bootFilesView({ folders: [makeFolder('Batman'), makeFolder('Saga', { file_count: 1 })] });

    assert.deepEqual(folderCalls(page).at(-1).query, { offset: '0', limit: '100' });
    const list = page.$('#fileList');
    assert.ok(list.firstElementChild.matches('.file-list-header'));
    assert.equal(page.$('#toggleAllBtn').textContent.trim(), '▶');
    assert.equal(page.$('#selectAll').checked, false);
    assert.deepEqual(folderSections(page), [
        { path: 'Batman', count: '2 files', collapsed: true, rows: 0 },
        { path: 'Saga', count: '1 file', collapsed: true, rows: 0 }
    ]);
    assert.ok(page.$$('#fileList .directory-toggle').every(arrow => arrow.classList.contains('collapsed')));
    assert.equal(list.lastElementChild.id, 'libraryScrollSentinel');
    assert.equal(folderFileCalls(page).length, 0, 'folder contents load on demand');
    assert.equal(page.$('#selectInfo').textContent, 'No files selected');
});

test('expanding a folder shows a loading row until its files arrive', async () => {
    const gate = deferred();
    const page = await bootFilesView({ folders: [makeFolder('Batman')] });
    page.route(folderFilesMatcher, async call => {
        await gate.promise;
        return { path: call.folder, files: [makeFile('Batman/Batman #001.cbz'), makeFile('Batman/Batman #002.cbz')] };
    });

    await expandFolder(page, 'Batman');

    assert.equal(page.$('#fileList .folder-loading').textContent.trim(), 'Loading files...');
    assert.ok(!page.$('#fileList .directory-toggle').classList.contains('collapsed'));
    const [call] = folderFileCalls(page);
    assert.equal(call.path, `/api/files/folders/${encodeFolder('Batman')}/files`);
    assert.deepEqual(call.query, {});
    assert.match(call.headers.authorization, /^Bearer /);

    gate.resolve();
    await page.settle();
    assert.equal(page.$('#fileList .folder-loading'), null);
    assert.deepEqual(rowNames(page), ['Batman #001.cbz', 'Batman #002.cbz']);
    assert.deepEqual(folderSections(page), [{ path: 'Batman', count: '2 files', collapsed: false, rows: 2 }]);
});

test('a folder answer without a file list shows the folder open and empty', async () => {
    const page = await bootFilesView({ folders: [makeFolder('Batman')] });
    page.route(folderFilesMatcher, call => ({ path: call.folder }));

    await expandFolder(page, 'Batman');

    assert.deepEqual(folderSections(page), [{ path: 'Batman', count: '2 files', collapsed: false, rows: 0 }]);
    assert.equal(page.$('#fileList .folder-loading'), null);
    assert.equal(page.$('#fileList .folder-error'), null);
});

test('file rows show the processing status, read state, size and age', async () => {
    const files = [
        makeFile('Mixed/dupe.cbz', { duplicate: true, renamed: true, normalized: true, size: 500 }),
        makeFile('Mixed/done.cbz', { renamed: true, normalized: true, size: 1536 }),
        makeFile('Mixed/renamed.cbz', { renamed: true, size: 5 * MiB }),
        makeFile('Mixed/normalized.cbz', { normalized: true, size: 2.5 * 1024 * MiB, read: true }),
        makeFile('Mixed/new.cbz', { size: 3 * 1024 * 1024 * MiB })
    ];
    const page = await bootFilesView({ folders: [makeFolder('Mixed', { file_count: 5 })], files: { Mixed: files } });

    await expandFolder(page, 'Mixed');

    const rows = page.$$('#fileList .file-item').map(row => ({
        name: row.querySelector('.file-name').title,
        className: row.className,
        badge: row.querySelector('.status-badge').title,
        icon: row.querySelector('.status-badge [aria-hidden="true"]').textContent,
        read: row.querySelector('.read-indicator')?.title ?? null,
        size: row.children[3].textContent,
        age: row.children[4].textContent
    }));
    assert.deepEqual(rows, [
        { name: 'dupe.cbz', className: 'file-item status-duplicate', badge: 'Duplicate', icon: '🔁', read: null, size: '500 B', age: '3h ago' },
        { name: 'done.cbz', className: 'file-item status-marked', badge: 'Processed (Renamed & Normalized)', icon: '✅', read: null, size: '1.5 KB', age: '3h ago' },
        { name: 'renamed.cbz', className: 'file-item status-renamed', badge: 'Renamed', icon: '🔵', read: null, size: '5.0 MB', age: '3h ago' },
        { name: 'normalized.cbz', className: 'file-item status-normalized', badge: 'Normalized', icon: '🔴', read: 'Read', size: '2.5 GB', age: '3h ago' },
        { name: 'new.cbz', className: 'file-item status-unmarked', badge: 'Unmarked', icon: '⚠️', read: null, size: '3.0 TB', age: '3h ago' }
    ]);
    // Files inside a folder don't repeat their path.
    assert.equal(page.$('#fileList .file-item .file-path'), null);

    const [dupe, , , normalized, fresh] = page.$$('#fileList .file-item');
    const actions = row => [...row.querySelectorAll('.dropdown-item')].map(item => item.textContent.trim());
    assert.equal(dupe.querySelector('.dropdown-menu').id, 'dropdown-Mixed_dupe_cbz');
    assert.ok(actions(dupe).includes('Review Duplicate'));
    assert.ok(!actions(fresh).includes('Review Duplicate'));
    assert.ok(actions(normalized).includes('Mark Unread') && !actions(normalized).includes('Mark Read'));
    assert.ok(actions(fresh).includes('Mark Read') && !actions(fresh).includes('Mark Unread'));
});

test('a file\'s age reads as a relative time for the past week, then as a date', async () => {
    const now = new Date(2024, 5, 15, 12, 0, 0).getTime();
    const seconds = ms => Math.floor(ms / 1000);
    const ages = {
        'Ages/just-now.cbz': seconds(now) - 20,
        'Ages/minutes.cbz': seconds(now) - 5 * 60 - 10,
        'Ages/hours.cbz': seconds(now) - 3 * 3600 - 10,
        'Ages/yesterday.cbz': seconds(now) - 30 * 3600,
        'Ages/days.cbz': seconds(now) - 3 * 86400 - 10,
        'Ages/this-year.cbz': seconds(new Date(2024, 2, 1, 12).getTime()),
        'Ages/last-year.cbz': seconds(new Date(2023, 11, 25, 12).getTime())
    };
    const files = Object.entries(ages).map(([path, modified]) => makeFile(path, { modified }));
    const page = await bootFilesView({ folders: [makeFolder('Ages', { file_count: files.length })], files: { Ages: files } });
    // Pin the page's clock (only argument-less `new Date()` / Date.now()).
    page.run(`window.Date = class extends Date {
        constructor(...args) { if (args.length) super(...args); else super(${now}); }
        static now() { return ${now}; }
    }`);

    await expandFolder(page, 'Ages');

    const shown = page.$$('#fileList .file-item').map(row => row.children[4].textContent);
    assert.deepEqual(shown, ['Just now', '5 min ago', '3h ago', 'Yesterday', '3 days ago', '03-01', '2023-12-25']);
});

test('a file dated ahead of the browser\'s clock reads Just now for up to a minute, then as a date', async () => {
    const now = new Date(2024, 5, 15, 12, 0, 0).getTime();
    const seconds = ms => Math.floor(ms / 1000);
    const ages = {
        'Ahead/a-second.cbz': seconds(now) + 1,
        'Ahead/under-a-minute.cbz': seconds(now) + 59,
        'Ahead/a-minute.cbz': seconds(now) + 60,
        'Ahead/minutes.cbz': seconds(now) + 2 * 60,
        'Ahead/hours.cbz': seconds(now) + 2 * 3600,
        'Ahead/days.cbz': seconds(now) + 3 * 86400,
        'Ahead/next-year.cbz': seconds(new Date(2025, 0, 2, 12).getTime())
    };
    const files = Object.entries(ages).map(([path, modified]) => makeFile(path, { modified }));
    const page = await bootFilesView({ folders: [makeFolder('Ahead', { file_count: files.length })], files: { Ahead: files } });
    page.run(`window.Date = class extends Date {
        constructor(...args) { if (args.length) super(...args); else super(${now}); }
        static now() { return ${now}; }
    }`);

    await expandFolder(page, 'Ahead');

    const shown = page.$$('#fileList .file-item').map(row => row.children[4].textContent);
    assert.deepEqual(shown, ['Just now', 'Just now', 'Just now', '06-15', '06-15', '06-18', '2025-01-02']);
});

test('long file names are shortened in the middle, keeping the extension', async () => {
    const names = [
        'Saga #001.cbz',
        'The Amazing Spider-Man by Dan Slott Omnibus Volume 01 (2016).cbz',
        'A file name without any extension that goes on and on',
        'A long name whose last dot is too far.from the end of it all'
    ];
    const files = names.map(name => makeFile(`Long/${name}`));
    const page = await bootFilesView({ folders: [makeFolder('Long', { file_count: 4 })], files: { Long: files } });

    await expandFolder(page, 'Long');

    const parts = page.$$('#fileList .file-item .file-name').map(element => ({
        title: element.title,
        whole: element.querySelector('.file-name-content')?.textContent ?? null,
        start: element.querySelector('.file-name-start')?.textContent ?? null,
        end: element.querySelector('.file-name-end')?.textContent ?? null
    }));
    assert.deepEqual(parts, [
        { title: names[0], whole: 'Saga #001.cbz', start: null, end: null },
        { title: names[1], whole: null, start: 'The Amazing Spider-Man by Dan ...', end: 'e 01 (2016).cbz' },
        { title: names[2], whole: null, start: 'A file name without any extens...', end: ' goes on and on' },
        { title: names[3], whole: null, start: 'A long name whose last dot is ...', end: 'e end of it all' }
    ]);
});

test('files at the library root are listed at once, under no folder header, and names are escaped', async () => {
    const tricky = 'Bob\'s <b>Comics & "Co" #1.cbz';
    const page = await bootFilesView({
        folders: [makeFolder('', { file_count: 1 })],
        routes: { 'GET /api/files/folders/files': { path: '', files: [makeFile(tricky)] } }
    });

    // The root has no header to open it by, so it is never collapsed. Its path
    // is empty, and an empty segment (folders//files) routes nowhere, so its
    // files come from the root's own URL.
    assert.equal(page.$('#fileList .directory-header'), null);
    assert.equal(page.$('#fileList .directory-content').classList.contains('collapsed'), false);
    assert.equal(callsTo(page, '/api/files/folders/files').length, 1);
    assert.deepEqual(folderFileCalls(page), []);
    const row = page.$('#fileList .file-item');
    assert.equal(row.querySelector('.file-name').title, tricky);
    assert.equal(row.querySelector('.file-path').textContent, tricky);
    assert.equal(row.querySelector('b'), null, 'markup in names is not interpreted');
    assert.equal(row.querySelector('.dropdown-menu').id, 'dropdown-Bob_s__b_Comics____Co___1_cbz');
});

test('the library root\'s files follow the filter, even once loaded', async () => {
    const page = await bootFilesView({
        folders: [makeFolder('', { file_count: 2 })],
        routes: {
            'GET /api/files/folders/files': call => ({
                path: '',
                files: [makeFile('Done.cbz', { processed: true }), makeFile('Todo.cbz')]
                    .filter(file => call.query.filter !== 'unmarked' || !file.processed)
            })
        }
    });
    assert.deepEqual(rowNames(page), ['Done.cbz', 'Todo.cbz']);

    page.click('#headerFilterMenu [data-filter="unmarked"]');
    await page.settle();

    assert.deepEqual(callsTo(page, '/api/files/folders/files').at(-1).query, { filter: 'unmarked' });
    assert.deepEqual(rowNames(page), ['Todo.cbz']);
});

test('a filter change while the library root\'s files load supersedes that request', async () => {
    const gate = deferred();
    const files = [makeFile('Done.cbz', { processed: true }), makeFile('Todo.cbz')];
    const page = await bootFilesView({
        folders: [makeFolder('', { file_count: 2 })],
        routes: {
            'GET /api/files/folders/files': async call => {
                if (call.query.filter !== 'unmarked') await gate.promise;
                return { path: '', files: files.filter(file => call.query.filter !== 'unmarked' || !file.processed) };
            }
        }
    });
    assert.equal(page.$('#fileList .folder-loading').textContent.trim(), 'Loading files...');

    // Switch to Unmarked before the unfiltered answer arrives.
    page.click('#headerFilterMenu [data-filter="unmarked"]');
    await page.settle();

    assert.deepEqual(callsTo(page, '/api/files/folders/files').at(-1).query, { filter: 'unmarked' });
    assert.deepEqual(rowNames(page), ['Todo.cbz']);

    // The older, unfiltered answer arriving late must not replace it.
    gate.resolve();
    await page.settle();
    assert.deepEqual(rowNames(page), ['Todo.cbz']);
});

test('the library root\'s files that fail to load offer a retry', async () => {
    let failure = 500;
    const page = await bootFilesView({
        folders: [makeFolder('', { file_count: 1 })],
        routes: { 'GET /api/files/folders/files': () => failure || { path: '', files: [makeFile('Root Issue.cbz')] } }
    });
    assert.match(page.$('#fileList .folder-error').textContent, /Failed to load files in this folder\./);

    failure = null;
    page.click('#fileList .folder-error button');
    await page.settle();

    assert.equal(callsTo(page, '/api/files/folders/files').length, 2);
    assert.deepEqual(rowNames(page), ['Root Issue.cbz']);
});

test('folder names are escaped and URL-encoded for their files request', async () => {
    const folder = 'Ästhetik – 日本 <x> \'quoted\'';
    const page = await bootFilesView({
        folders: [makeFolder(folder)],
        files: { [folder]: [makeFile(`${folder}/Issue 1.cbz`)] }
    });

    assert.equal(page.$('#fileList .directory-path').textContent, folder);
    assert.equal(page.$('#fileList .directory-path x'), null);
    await expandFolder(page, folder);

    assert.equal(folderFileCalls(page).at(-1).folder, folder);
    assert.deepEqual(rowNames(page), ['Issue 1.cbz']);
});

test('folder paths and file paths are encoded and split like the server expects', async () => {
    const page = await bootFilesView();

    assert.equal(page.evaluate("encodeFolderPath('a/b?c')"), encodeFolder('a/b?c'));
    assert.equal(page.evaluate("encodeFolderPath('>>>?')"), encodeFolder('>>>?'), 'URL-safe alphabet, no padding');
    assert.equal(page.evaluate('encodeFolderPath(null)'), '');
    assert.equal(page.evaluate("encodeFolderPath('\\uD800')"), '', 'a lone surrogate cannot be encoded');

    assert.deepEqual(page.evaluate(`[
        extractDisplayName('Batman/Batman #001.cbz'),
        extractDisplayName('C:\\\\Comics\\\\Saga.cbz'),
        extractDisplayName('plain.cbz'),
        extractDisplayName('trailing/'),
        extractDisplayName('')
    ]`), ['Batman #001.cbz', 'Saga.cbz', 'plain.cbz', 'trailing/', '']);
});

// ── Empty, loading and error states ───────────────────────────────────────

test('an empty library says so once it has loaded, and so does a filter that nothing matches', async () => {
    const page = await bootFilesView();

    assert.equal(page.$('#fileList .loading'), null, 'the loading placeholder is gone');
    assert.equal(page.$('#fileList .empty-state h2').textContent, 'No comic files found');
    assert.equal(page.$('#fileList').lastElementChild.id, 'libraryScrollSentinel');
    assert.equal(page.$('#selectInfo').textContent, 'No files selected');

    page.click('#headerFilterMenu [data-filter="duplicates"]');
    await page.settle();
    assert.equal(folderCalls(page).at(-1).query.filter, 'duplicates');

    assert.equal(page.$('#fileList .loading'), null);
    assert.equal(page.$('#fileList .empty-state h2').textContent, 'No matching files found');
});

test('a folder list answer without folders counts as an empty library', async () => {
    const page = await bootFilesView({ routes: { 'GET /api/files/folders': {} } });

    assert.equal(page.$('#fileList .empty-state h2').textContent, 'No comic files found');
    assert.deepEqual(messages(page).filter(m => m.type === 'error'), []);
});

test('a filter or search that matches none of the listed folders replaces them with its empty state', async t => {
    const all = [makeFolder('Batman'), makeFolder('Saga')];
    const page = await bootFilesView({
        routes: { 'GET /api/files/folders': call => folderPages(call.query.filter || call.query.search ? [] : all)(call) }
    });
    assert.deepEqual(folderSections(page).map(section => section.path), ['Batman', 'Saga']);

    page.click('#headerFilterMenu [data-filter="unmarked"]');
    await page.settle();
    assert.equal(folderCalls(page).at(-1).query.filter, 'unmarked');
    assert.deepEqual(folderSections(page), []);
    assert.equal(page.$('#fileList .loading'), null);
    assert.equal(page.$('#fileList .empty-state h2').textContent, 'No matching files found');

    page.click('#headerFilterMenu [data-filter="all"]');
    await page.settle();
    assert.deepEqual(folderSections(page).map(section => section.path), ['Batman', 'Saga']);

    t.mock.timers.enable({ apis: ['setTimeout'] });
    page.input('#headerSearchInput', 'zzz');
    t.mock.timers.tick(300);
    await page.settle();
    assert.equal(folderCalls(page).at(-1).query.search, 'zzz');
    assert.deepEqual(folderSections(page), []);
    assert.equal(page.$('#fileList .empty-state h2').textContent, 'No matching files found');
});

test('a loaded folder list is drawn once, with no loading or empty state in between', async () => {
    const { page, renders, drawn } = await openFilesViewWatched([makeFolder('Batman'), makeFolder('Saga')]);

    assert.equal(renders(), 1);
    assert.deepEqual(drawn, ['header', 'folder', 'folder']);
    assert.deepEqual(folderSections(page).map(section => section.path), ['Batman', 'Saga']);
});

test('an empty folder list is drawn once, straight as its empty state', async () => {
    const { page, renders, drawn } = await openFilesViewWatched([]);

    assert.equal(renders(), 1);
    assert.deepEqual(drawn, ['No comic files found']);
    assert.equal(page.$('#fileList .loading'), null);
});

test('a list emptied by a reload in flight shows its loading state when redrawn', async () => {
    const gate = deferred();
    const page = await bootFilesView({ folders: [makeFolder('Batman')] });
    page.route('GET /api/files/folders', async () => {
        await gate.promise;
        return { folders: [makeFolder('Batman')], total_folders: 1 };
    });

    page.run('loadActiveLibraryView()');
    // Anything redrawing the list meanwhile (here: the select-all box).
    page.click('#selectAll');
    assert.equal(page.$('#fileList .loading p').textContent, 'Loading files...');

    gate.resolve();
    await page.settle();
    assert.deepEqual(folderSections(page).map(section => section.path), ['Batman']);
});

test('a folder list that fails to load is reported with the failed request\'s reference', async () => {
    const page = await bootFilesView({
        routes: { 'GET /api/files/folders': respond(500, 'boom', { 'X-Correlation-Id': 'corr-7' }) }
    });

    assert.deepEqual(messages(page).filter(m => m.type === 'error').map(m => m.text), [
        'Failed to load files: HTTP error! status: 500 (ref: corr-7)'
    ]);
});

test('a folder list that cannot be fetched is reported', async () => {
    const page = await bootFilesView({
        routes: { 'GET /api/files/folders': () => { throw new TypeError('Failed to fetch'); } }
    });

    assert.deepEqual(messages(page).filter(m => m.type === 'error').map(m => m.text), ['Failed to load files: Failed to fetch']);
});

test('a folder list failure without a reference is not reported with an earlier request\'s', async () => {
    // The library counts fail with a reference while the library opens, then
    // the folder list fails on a proxy's error page.
    const page = await bootFilesView({
        routes: {
            'GET /api/files/counts': respond(500, { error: 'boom' }, { 'X-Correlation-Id': 'corr-counts' }),
            'GET /api/files/folders': respond(502, 'Bad Gateway')
        }
    });

    assert.equal(callsTo(page, '/api/files/counts').length, 1);
    assert.deepEqual(messages(page).filter(m => m.type === 'error').map(m => m.text), ['Failed to load files: HTTP error! status: 502']);
});

test('an expired session while listing folders goes to the login page', async () => {
    const page = await bootFilesView({ routes: { 'GET /api/files/folders': 401 } });

    assert.ok(page.navigations.some(n => n.url === 'http://localhost/login.html'));
    assert.deepEqual(messages(page).filter(m => m.type === 'error'), []);
});

test('a folder whose files fail to load offers a retry', async () => {
    let failure = 500;
    const page = await bootFilesView({ folders: [makeFolder('Batman')] });
    page.route(folderFilesMatcher, call => {
        if (failure === 'network') throw new TypeError('Failed to fetch');
        if (failure) return failure;
        return { path: call.folder, files: [makeFile('Batman/Batman #001.cbz')] };
    });

    await expandFolder(page, 'Batman');
    const error = page.$('#fileList .folder-error');
    assert.match(error.textContent, /Failed to load files in this folder\./);

    failure = 'network';
    page.click(error.querySelector('button'));
    await page.settle();
    assert.ok(page.$('#fileList .folder-error'));

    failure = null;
    page.click('#fileList .folder-error button');
    await page.settle();
    assert.equal(folderFileCalls(page).length, 3);
    assert.deepEqual(rowNames(page), ['Batman #001.cbz']);
});

test('an expired session while opening a folder goes to the login page', async () => {
    const page = await bootFilesView({ folders: [makeFolder('Batman')] });
    page.route(folderFilesMatcher, 401);

    await expandFolder(page, 'Batman');

    assert.ok(page.navigations.some(n => n.url === 'http://localhost/login.html'));
    assert.ok(page.$('#fileList .folder-error'));
});

// ── Query, refresh and paging ─────────────────────────────────────────────

test('search, filter and sort apply to the folder list and to folder contents', async t => {
    const page = await bootFilesView({ folders: [makeFolder('Batman')] });

    page.click('#headerFilterMenu [data-filter="unmarked"]');
    page.click('#headerSortMenu [data-sort="date"]');
    page.click('#headerSortMenu [data-sort="date"]');
    t.mock.timers.enable({ apis: ['setTimeout'] });
    page.input('#headerSearchInput', 'bat man');
    t.mock.timers.tick(300);
    await page.settle();

    assert.deepEqual(folderCalls(page).at(-1).query, {
        offset: '0', limit: '100', search: 'bat man', filter: 'unmarked', sort: 'date', direction: 'desc'
    });
    await expandFolder(page, 'Batman');
    assert.deepEqual(folderFileCalls(page).at(-1).query, { search: 'bat man', filter: 'unmarked', sort: 'date', direction: 'desc' });
});

test('a plain reload keeps open folders; a refresh re-collapses them and reloads their files', async () => {
    const page = await bootFilesView({
        folders: [makeFolder('Batman'), makeFolder('Saga')],
        files: { Batman: [makeFile('Batman/Batman #001.cbz')] }
    });
    await expandFolder(page, 'Batman');
    const countsBefore = callsTo(page, '/api/files/counts').length;

    await page.run('loadActiveLibraryView()');
    await page.settle();
    assert.deepEqual(folderSections(page).map(section => section.collapsed), [false, true]);
    assert.equal(folderFileCalls(page).length, 1, 'cached folder contents are reused');
    assert.equal(callsTo(page, '/api/files/counts').length, countsBefore);

    await page.run('loadActiveLibraryView({ refresh: true })');
    await page.settle();
    assert.deepEqual(folderCalls(page).at(-1).query, { offset: '0', limit: '100', refresh: 'true' });
    assert.deepEqual(folderSections(page).map(section => section.collapsed), [true, true]);
    assert.equal(callsTo(page, '/api/files/counts').length, countsBefore + 1, 'the library health summary is refreshed');

    await expandFolder(page, 'Batman');
    assert.equal(folderFileCalls(page).length, 2);
});

test('a new filter or sort lists open folders\' files again, and closed ones once reopened', async () => {
    const page = await bootFilesView({ folders: [makeFolder('Batman'), makeFolder('Saga')] });
    // Each folder holds one file, named after the filter it was listed for.
    page.route(folderFilesMatcher, call => ({
        path: call.folder,
        files: [makeFile(`${call.folder}/${call.folder} ${call.query.filter || 'all'}.cbz`)]
    }));
    const requested = () => folderFileCalls(page).map(call => [call.folder, call.query]);
    await expandFolder(page, 'Batman');
    await expandFolder(page, 'Saga');
    await expandFolder(page, 'Saga');
    assert.deepEqual(requested(), [['Batman', {}], ['Saga', {}]]);

    page.click('#headerFilterMenu [data-filter="unmarked"]');
    await page.settle();
    assert.deepEqual(folderSections(page).map(section => section.collapsed), [false, true]);
    assert.deepEqual(rowNames(page), ['Batman unmarked.cbz']);
    assert.deepEqual(requested().slice(2), [['Batman', { filter: 'unmarked' }]], 'the closed folder is not fetched yet');

    await expandFolder(page, 'Saga');
    assert.deepEqual(rowNames(page), ['Batman unmarked.cbz', 'Saga unmarked.cbz']);
    assert.deepEqual(requested().slice(3), [['Saga', { filter: 'unmarked' }]]);

    page.click('#headerSortMenu [data-sort="date"]');
    await page.settle();
    assert.deepEqual(requested().slice(4), [
        ['Batman', { filter: 'unmarked', sort: 'date' }],
        ['Saga', { filter: 'unmarked', sort: 'date' }]
    ]);

    // A plain reload for the same query keeps the files listed for it.
    await page.run('loadActiveLibraryView()');
    await page.settle();
    assert.equal(folderFileCalls(page).length, 6);
});

test('a new filter lists many open folders\' files again a few at a time', async () => {
    const names = ['A', 'B', 'C', 'D', 'E', 'F'];
    const page = await bootFilesView({ folders: names.map(name => makeFolder(name)) });
    let gate = null;
    let inFlight = 0;
    let maxInFlight = 0;
    page.route(folderFilesMatcher, async call => {
        inFlight++;
        maxInFlight = Math.max(maxInFlight, inFlight);
        try {
            if (gate) await gate.promise;
            return { path: call.folder, files: [makeFile(`${call.folder}/${call.folder}.cbz`)] };
        } finally {
            inFlight--;
        }
    });
    for (const name of names) await expandFolder(page, name);
    assert.equal(folderFileCalls(page).length, names.length);

    gate = deferred();
    maxInFlight = 0;
    page.click('#headerFilterMenu [data-filter="unmarked"]');
    await page.settle();
    assert.equal(folderFileCalls(page).length, names.length + 4, 'no more than 4 folders are fetched at once');

    gate.resolve();
    await page.settle();
    assert.equal(maxInFlight, 4);
    assert.deepEqual(folderFileCalls(page).slice(names.length).map(call => [call.folder, call.query]),
        names.map(name => [name, { filter: 'unmarked' }]));
});

test('quick successive filter changes still list open folders\' files a few at a time', async () => {
    const names = ['A', 'B', 'C', 'D', 'E', 'F'];
    const page = await bootFilesView({ folders: names.map(name => makeFolder(name)) });
    let gate = null;
    let inFlight = 0;
    let maxInFlight = 0;
    page.route(folderFilesMatcher, async call => {
        inFlight++;
        maxInFlight = Math.max(maxInFlight, inFlight);
        try {
            if (gate) await gate.promise;
            return { path: call.folder, files: [makeFile(`${call.folder}/${call.folder}.cbz`)] };
        } finally {
            inFlight--;
        }
    });
    for (const name of names) await expandFolder(page, name);
    const before = folderFileCalls(page).length;

    gate = deferred();
    maxInFlight = 0;
    page.click('#headerFilterMenu [data-filter="unmarked"]');
    await page.settle();
    page.click('#headerFilterMenu [data-filter="marked"]');
    await page.settle();
    assert.equal(folderFileCalls(page).length, before + 4, 'the second reload waits for the first one\'s fetches');

    gate.resolve();
    await page.settle();
    assert.equal(maxInFlight, 4, 'no more than 4 folders are fetched at once across both reloads');
    const latest = new Map(folderFileCalls(page).map(call => [call.folder, call.query]));
    assert.deepEqual(names.map(name => latest.get(name)), names.map(() => ({ filter: 'marked' })));
    assert.deepEqual(page.errors, []);
});

test('a folder\'s checkbox and expand all fetch files listed for an earlier filter again', async () => {
    const page = await bootFilesView({ folders: [makeFolder('Batman'), makeFolder('Saga')] });
    page.route(folderFilesMatcher, call => ({
        path: call.folder,
        files: [makeFile(`${call.folder}/${call.folder} ${call.query.filter || 'all'}.cbz`)]
    }));
    const requested = () => folderFileCalls(page).map(call => [call.folder, call.query]);
    const toggleAll = async () => {
        page.click('#toggleAllBtn');
        await page.settle();
    };
    await toggleAll();
    await toggleAll();
    assert.deepEqual(folderSections(page).map(section => section.collapsed), [true, true]);

    page.click('#headerFilterMenu [data-filter="unmarked"]');
    await page.settle();
    assert.equal(folderFileCalls(page).length, 2, 'closed folders are not fetched for the new filter yet');

    const batman = page.$$('#fileList .folder-section')
        .find(section => section.querySelector('.directory-content').dataset.dir === 'Batman');
    page.input(batman.querySelector('.directory-checkbox'), true);
    await page.settle();
    assert.deepEqual(page.evaluate('[...selectedFiles]'), ['Batman/Batman unmarked.cbz']);

    await toggleAll();
    assert.deepEqual(rowNames(page), ['Batman unmarked.cbz', 'Saga unmarked.cbz']);
    assert.deepEqual(requested().slice(2), [
        ['Batman', { filter: 'unmarked' }],
        ['Saga', { filter: 'unmarked' }]
    ], 'each folder is fetched once for the new filter');
});

test('loadFolders() still accepts the legacy boolean refresh flag', async () => {
    const page = await bootFilesView({ folders: [makeFolder('Batman')] });

    await page.run('loadFolders(true)');
    assert.equal(folderCalls(page).at(-1).query.refresh, 'true');

    await page.run('loadFolders(false)');
    assert.equal(folderCalls(page).at(-1).query.refresh, undefined);
    assert.deepEqual(folderSections(page).map(section => section.path), ['Batman']);
});

test('scrolling to the end loads the next page of folders, keeping the rendered ones', async () => {
    const all = Array.from({ length: 150 }, (_, index) => makeFolder(`Folder ${String(index + 1).padStart(3, '0')}`));
    const page = await bootFilesView({ folders: all });
    assert.equal(folderSections(page).length, 100);
    const firstSection = page.$('#fileList .folder-section');

    scrollObserver(page).trigger(false);
    await page.settle();
    assert.equal(folderCalls(page).length, 1);

    scrollObserver(page).trigger();
    await page.settle();
    assert.deepEqual(folderCalls(page).at(-1).query, { offset: '100', limit: '100' });
    assert.equal(folderSections(page).length, 150);
    assert.equal(page.$('#fileList .folder-section'), firstSection, 'unchanged sections are not rebuilt');
    assert.equal(page.$('#fileList').lastElementChild.id, 'libraryScrollSentinel');

    scrollObserver(page).trigger();
    await page.settle();
    assert.equal(folderCalls(page).length, 2, 'nothing more to load');
});

test('a reload requested while folders load aborts the stale request and runs once it ends', async () => {
    const gate = deferred();
    const page = await bootFilesView({ folders: [makeFolder('Batman')] });
    const requests = folderCalls(page).length;
    let answered = 0;
    page.route('GET /api/files/folders', async () => {
        answered += 1;
        if (answered === 1) await gate.promise;
        return { folders: [makeFolder(`Reload ${answered}`)], total_folders: 1 };
    });

    page.run('loadActiveLibraryView()');
    page.run('loadFolders({ append: true })');
    assert.equal(folderCalls(page).length, requests + 1, 'an append during a load is dropped');

    page.run('loadActiveLibraryView()');
    await page.settle();

    assert.equal(folderCalls(page).length, requests + 2);
    assert.deepEqual(folderSections(page).map(section => section.path), ['Reload 2']);
    assert.deepEqual(messages(page).filter(m => m.type === 'error'), [], 'the aborted request is not an error');
});

test('without AbortController a folder reload waits for the load in flight', async () => {
    const gate = deferred();
    const page = await bootFilesView({ folders: [makeFolder('Batman')] });
    page.run('window.AbortController = undefined');
    const requests = folderCalls(page).length;
    let answered = 0;
    page.route('GET /api/files/folders', async () => {
        answered += 1;
        if (answered === 1) await gate.promise;
        return { folders: [makeFolder(`Reload ${answered}`)], total_folders: 1 };
    });

    page.run('loadActiveLibraryView()');
    page.run('loadActiveLibraryView()');
    await page.settle();
    assert.equal(folderCalls(page).length, requests + 1, 'the reload waits');
    assert.equal(folderCalls(page).at(-1).init.signal, undefined);

    gate.resolve();
    await page.settle();
    assert.equal(folderCalls(page).length, requests + 2);
    assert.deepEqual(folderSections(page).map(section => section.path), ['Reload 2']);
});
