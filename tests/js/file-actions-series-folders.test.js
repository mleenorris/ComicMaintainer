'use strict';

// js/app/file-actions.js: the Manage Folders modal of a series page. It lists
// the folders on disk that hold the series' files
// (GET /api/files/series/{id}/folders) and, when the server found a combine
// group for them, "Merge Folders" hands the series over to the Combine
// Folders modal on that group with the series' suggested destination.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred, addReadOnlyGate, isDisplayed } = require('./support/app');
const {
    bootSeriesPage,
    combinableFolder,
    combinableGroup,
    messages,
    isModalOpen,
    pressEscape,
    buttonByText,
    clickSeriesAction,
    callsTo
} = require('./support/email-helpers');

const FOLDERS = 'GET /api/files/series/saga/folders';
const COMBINABLE = 'GET /api/files/combinable-folders';

// GET /api/files/series/{id}/folders (FilesController.GetSeriesFolders):
// a SeriesFolderDto per folder plus the combine group they belong to.
function seriesFolders(overrides = {}) {
    return {
        id: 'saga',
        title: 'Saga',
        folders: [
            { directory: '/comics/Saga', file_count: 12, total_size: 1.5 * 1024 * 1024 },
            { directory: '/comics/Saga (2012)', file_count: 1, total_size: 900 }
        ],
        combine_group_key: 'saga',
        suggested_destination_directory: '/comics/Saga',
        ...overrides
    };
}

const NO_GROUP = { combine_group_key: null, suggested_destination_directory: null };

function openManageFolders(page) {
    return clickSeriesAction(page, 'Manage Folders');
}

function closeButton(page) {
    return buttonByText(page, '#seriesFoldersModal .modal-footer', 'Close');
}

function folderRows(page) {
    return page.$$('#seriesFoldersList .combine-folder-row')
        .map(row => [...row.children].map(cell => cell.textContent));
}

function mergeNote(page) {
    const note = page.$('#seriesFoldersMergeNote');
    return note.style.display === 'none' ? null : note.textContent;
}

function emptyState(page) {
    const element = page.$('#seriesFoldersEmptyState');
    return element.style.display === 'none' ? null : element.textContent.trim();
}

// The combine-folders card the modal has selected as the destination.
function selectedCombineFolder(page) {
    const selected = page.$$('#combineFoldersList .combine-folder-card.selected');
    assert.equal(selected.length, 1);
    assert.ok(selected[0].querySelector('input[type="radio"]').checked);
    return selected[0].querySelector('.combine-folder-path').textContent;
}

test('Manage Folders shows a loading state, then the folders that hold the series', async t => {
    const answer = deferred();
    const page = await bootSeriesPage({ routes: { [FOLDERS]: () => answer.promise } });
    t.after(() => page.close());

    openManageFolders(page);

    assert.ok(isModalOpen(page, 'seriesFoldersModal'));
    assert.equal(page.$('#seriesFoldersModalTitle').textContent, 'Folders for "Saga"');
    assert.equal(page.$('#seriesFoldersList .loading p').textContent, 'Loading folders...');
    assert.equal(emptyState(page), null);
    assert.equal(mergeNote(page), null);
    assert.ok(page.$('#seriesFoldersMergeBtn').disabled);
    const [call] = callsTo(page, 'GET', '/api/files/series/saga/folders');
    assert.match(call.headers.authorization, /^Bearer /);

    answer.resolve(seriesFolders());
    await page.settle();

    assert.deepEqual(folderRows(page), [
        ['/comics/Saga', '12 files · 1.5 MB'],
        ['/comics/Saga (2012)', '1 file · 900 B']
    ]);
    assert.equal(page.$('#seriesFoldersList .loading'), null);
    assert.equal(emptyState(page), null);
    assert.equal(mergeNote(page), 'Click "Merge Folders" to combine these folders into one on disk.');
    assert.ok(!page.$('#seriesFoldersMergeBtn').disabled);
    assert.deepEqual(messages(page), []);
});

test('a series that lives in one folder has nothing to merge', async t => {
    const page = await bootSeriesPage({
        routes: {
            [FOLDERS]: seriesFolders({
                folders: [{ directory: '/comics/Saga', file_count: 2, total_size: 2048 }],
                ...NO_GROUP
            })
        }
    });
    t.after(() => page.close());

    openManageFolders(page);
    await page.settle();

    assert.deepEqual(folderRows(page), [['/comics/Saga', '2 files · 2.0 KB']]);
    assert.equal(mergeNote(page), 'This series lives in a single folder — nothing to merge.');
    assert.ok(page.$('#seriesFoldersMergeBtn').disabled);
});

test('folders without a combine group point to the global Combine Folders tool', async t => {
    const page = await bootSeriesPage({ routes: { [FOLDERS]: seriesFolders(NO_GROUP) } });
    t.after(() => page.close());

    openManageFolders(page);
    await page.settle();

    assert.equal(folderRows(page).length, 2);
    assert.equal(mergeNote(page), 'Use the global "Combine Folders" tool from the toolbar to merge these folders.');
    assert.ok(page.$('#seriesFoldersMergeBtn').disabled);

    // The merge needs a group: run directly (the button is disabled), it does nothing.
    await page.run('openMergeForSeriesFolders()');
    await page.settle();
    assert.ok(isModalOpen(page, 'seriesFoldersModal'));
    assert.ok(!isModalOpen(page, 'combineFoldersModal'));
    assert.deepEqual(callsTo(page, 'GET', '/api/files/combinable-folders'), []);
});

test('a series without folders on disk shows the empty state', async t => {
    // An empty folder list, then a response without one.
    const answers = [seriesFolders({ folders: [], ...NO_GROUP }), { id: 'saga', title: 'Saga' }];
    const page = await bootSeriesPage({ routes: { [FOLDERS]: () => answers.shift() } });
    t.after(() => page.close());

    for (let open = 1; open <= 2; open++) {
        openManageFolders(page);
        await page.settle();

        assert.equal(page.$('#seriesFoldersList').children.length, 0, `open ${open}`);
        assert.equal(emptyState(page), 'No folders found for this series.', `open ${open}`);
        assert.equal(mergeNote(page), null);
        assert.ok(page.$('#seriesFoldersMergeBtn').disabled);
        page.click(closeButton(page));
    }
    assert.equal(callsTo(page, 'GET', '/api/files/series/saga/folders').length, 2);
});

test('a failed load is shown in the modal and opening it again retries', async t => {
    const page = await bootSeriesPage({ routes: { [FOLDERS]: respond(404, { error: 'Series not found' }) } });
    t.after(() => page.close());

    openManageFolders(page);
    await page.settle();
    assert.equal(emptyState(page), 'Failed to load folders: HTTP error! status: 404');
    assert.equal(page.$('#seriesFoldersList').children.length, 0);
    assert.equal(mergeNote(page), null);
    assert.ok(page.$('#seriesFoldersMergeBtn').disabled);
    assert.ok(page.consoleMessages.some(entry => entry.type === 'error'
        && entry.args[0] === 'Failed to load series folders:'));
    // Nothing to merge after a failure.
    await page.run('openMergeForSeriesFolders()');
    assert.deepEqual(callsTo(page, 'GET', '/api/files/combinable-folders'), []);

    page.click(closeButton(page));
    page.route(FOLDERS, respond(500, 'Error retrieving series folders'));
    openManageFolders(page);
    await page.settle();
    assert.equal(emptyState(page), 'Failed to load folders: HTTP error! status: 500');

    page.click(closeButton(page));
    page.route(FOLDERS, () => { throw new TypeError('Failed to fetch'); });
    openManageFolders(page);
    await page.settle();
    assert.equal(emptyState(page), 'Failed to load folders: Failed to fetch');

    page.click(closeButton(page));
    page.route(FOLDERS, seriesFolders());
    openManageFolders(page);
    await page.settle();
    assert.equal(emptyState(page), null);
    assert.equal(folderRows(page).length, 2);
    assert.ok(!page.$('#seriesFoldersMergeBtn').disabled);
    assert.deepEqual(messages(page), []);
});

test('an expired session goes to the login page', async t => {
    const page = await bootSeriesPage({ routes: { [FOLDERS]: 401 } });
    t.after(() => page.close());

    openManageFolders(page);
    await page.settle();

    assert.ok(page.navigations.some(nav => nav.url.includes('/login.html')));
    assert.equal(emptyState(page), null);
    assert.ok(page.$('#seriesFoldersMergeBtn').disabled);
    assert.ok(!page.consoleMessages.some(entry => entry.args[0] === 'Failed to load series folders:'));
});

test('Close, × and Escape close the modal and forget the folders', async t => {
    const page = await bootSeriesPage({ routes: { [FOLDERS]: seriesFolders() } });
    t.after(() => page.close());
    const closers = {
        Close: () => page.click(closeButton(page)),
        '×': () => page.click('#seriesFoldersModal .close-btn'),
        Escape: () => pressEscape(page)
    };

    for (const [name, close] of Object.entries(closers)) {
        openManageFolders(page);
        await page.settle();
        assert.ok(!page.$('#seriesFoldersMergeBtn').disabled, name);

        await close();
        assert.ok(!isModalOpen(page, 'seriesFoldersModal'), name);
        // The closed modal no longer offers its group to merge.
        await page.run('openMergeForSeriesFolders()');
        await page.settle();
        assert.ok(!isModalOpen(page, 'combineFoldersModal'), name);
    }
    assert.deepEqual(callsTo(page, 'GET', '/api/files/combinable-folders'), []);
});

test('opening the modal for a series id encodes it and titles it generically without a title', async t => {
    const page = await bootSeriesPage({
        routes: { 'GET /api/files/series/saga%20%26%20co/folders': seriesFolders({ id: 'saga & co', title: '' }) }
    });
    t.after(() => page.close());

    await page.run('openSeriesFoldersModal("saga & co")');
    await page.settle();
    assert.equal(page.$('#seriesFoldersModalTitle').textContent, 'Series Folders');
    assert.equal(folderRows(page).length, 2);

    page.click(closeButton(page));
    const requests = page.fetchCalls.length;
    // Without a series id there is nothing to show.
    await page.run('openSeriesFoldersModal("")');
    assert.equal(page.fetchCalls.length, requests);
    assert.ok(!isModalOpen(page, 'seriesFoldersModal'));
});

test('Merge Folders opens Combine Folders on the series group with the series suggestion', async t => {
    const answer = deferred();
    const otherGroup = combinableGroup({
        groupKey: 'paper-girls',
        seriesName: 'Paper Girls',
        suggestedDestinationDirectory: '/comics/Paper Girls',
        folders: [
            combinableFolder({ directory: '/comics/Paper Girls' }),
            combinableFolder({ directory: '/comics/Paper Girls (2015)', isSuggested: false })
        ]
    });
    const page = await bootSeriesPage({
        routes: {
            // The series suggests the other folder than the combine group does.
            [FOLDERS]: seriesFolders({ suggested_destination_directory: '/comics/Saga (2012)' }),
            [COMBINABLE]: () => answer.promise
        }
    });
    t.after(() => page.close());

    openManageFolders(page);
    await page.settle();
    page.click('#seriesFoldersMergeBtn');
    await page.settle();

    assert.ok(!isModalOpen(page, 'seriesFoldersModal'));
    assert.ok(isModalOpen(page, 'combineFoldersModal'));
    assert.equal(page.$('#combineFoldersGroupTitle').textContent, 'Loading combinable folders...');

    answer.resolve({ groups: [otherGroup, combinableGroup()] });
    await page.settle();

    assert.equal(page.$('#combineFoldersCounter').textContent, '2 of 2');
    assert.equal(page.$('#combineFoldersGroupTitle').textContent, 'Saga');
    assert.equal(selectedCombineFolder(page), '/comics/Saga (2012)');
    assert.equal(page.$('#combineFoldersList .suggested .combine-folder-path').textContent, '/comics/Saga');
    assert.ok(!page.$('#combineFoldersPrevBtn').disabled);
    assert.ok(page.$('#combineFoldersNextBtn').disabled);
    assert.ok(!page.$('#combineFoldersConfirmBtn').disabled);
    assert.equal(callsTo(page, 'GET', '/api/files/combinable-folders').length, 1);
});

test('Merge Folders falls back to the group suggestion and to the first group', async t => {
    const sagaGroup = combinableGroup({
        suggestedDestinationDirectory: '/comics/Saga (2012)',
        folders: [
            combinableFolder({ isSuggested: false }),
            combinableFolder({ directory: '/comics/Saga (2012)', isSuggested: true })
        ]
    });
    const firstGroup = combinableGroup({
        groupKey: 'paper-girls',
        seriesName: 'Paper Girls',
        suggestedDestinationDirectory: '/comics/Paper Girls',
        folders: [
            combinableFolder({ directory: '/comics/Paper Girls' }),
            combinableFolder({ directory: '/comics/Paper Girls (2015)', isSuggested: false })
        ]
    });
    const page = await bootSeriesPage({ routes: { [COMBINABLE]: { groups: [firstGroup, sagaGroup] } } });
    t.after(() => page.close());
    const mergeWith = async folders => {
        page.route(FOLDERS, seriesFolders(folders));
        openManageFolders(page);
        await page.settle();
        page.click('#seriesFoldersMergeBtn');
        await page.settle();
        assert.ok(isModalOpen(page, 'combineFoldersModal'));
        return {
            counter: page.$('#combineFoldersCounter').textContent,
            title: page.$('#combineFoldersGroupTitle').textContent,
            selected: selectedCombineFolder(page)
        };
    };

    // No series suggestion: the group's own.
    assert.deepEqual(await mergeWith({ suggested_destination_directory: null }), {
        counter: '2 of 2', title: 'Saga', selected: '/comics/Saga (2012)'
    });
    page.click('#combineFoldersModal .close-btn');

    // A suggestion that is not a folder of the group: the group's own.
    assert.deepEqual(await mergeWith({ suggested_destination_directory: '/comics/Elsewhere' }), {
        counter: '2 of 2', title: 'Saga', selected: '/comics/Saga (2012)'
    });
    page.click('#combineFoldersModal .close-btn');

    // A group the combine list no longer has: the list opens on its first group.
    assert.deepEqual(await mergeWith({ combine_group_key: 'series:saga' }), {
        counter: '1 of 2', title: 'Paper Girls', selected: '/comics/Paper Girls'
    });
});

test('read-only users are not offered the merge, even with the modal open', async t => {
    const page = await bootSeriesPage({
        user: { canModifyLibrary: false, canAdminister: false, roles: ['ReadOnly'] },
        beforeScripts: addReadOnlyGate,
        routes: { [FOLDERS]: seriesFolders() }
    });
    t.after(() => page.close());

    // Series Actions (and its Manage Folders) is hidden from them.
    assert.equal(isDisplayed(page.$('.series-actions-dropdown')), false);
    await page.run("openSeriesFoldersModal('saga', 'Saga')");
    await page.settle();

    assert.deepEqual(folderRows(page), [
        ['/comics/Saga', '12 files · 1.5 MB'],
        ['/comics/Saga (2012)', '1 file · 900 B']
    ]);
    assert.equal(isDisplayed(page.$('#seriesFoldersMergeNote')), false);
    assert.deepEqual(page.$$('#seriesFoldersModal .modal-footer .btn').filter(isDisplayed).map(button => button.textContent.trim()), ['Close']);
});
