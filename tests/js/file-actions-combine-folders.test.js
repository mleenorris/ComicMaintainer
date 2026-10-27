'use strict';

// js/app/file-actions.js: the Combine Folders modal. It steps through the
// groups of GET /api/files/combinable-folders (folders that hold the same
// series and volume), shows each folder as a card and lets the user pick the
// folder to keep as the destination. Preview Merge and Combine are covered by
// file-actions-combine-merge.test.js.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const {
    bootPage,
    combinableFolder,
    combinableGroup,
    messages,
    isModalOpen,
    pressEscape,
    pressKey,
    buttonByText,
    callsTo
} = require('./support/email-helpers');

const COMBINABLE = 'GET /api/files/combinable-folders';

// Two folders; /comics/Saga is the suggested destination.
const SAGA = combinableGroup({ groupKey: 'saga|2012', volume: '2012' });
const PAPER_GIRLS = combinableGroup({
    groupKey: 'paper-girls',
    seriesName: 'Paper Girls',
    totalFileCount: 30,
    suggestedDestinationDirectory: '/comics/Paper Girls',
    suggestionReason: 'Folder name differs only by a non-distinguishing suffix: (Digital).',
    folders: [
        combinableFolder({ directory: '/comics/Paper Girls (Digital)', fileCount: 10, isSuggested: false }),
        combinableFolder({ directory: '/comics/Paper Girls', fileCount: 20 })
    ]
});

function boot({ groups = [SAGA, PAPER_GIRLS], routes = {}, ...options } = {}) {
    return bootPage({ hash: '#/library', ...options, routes: { [COMBINABLE]: { groups }, ...routes } });
}

function openFromSidebar(page) {
    return page.click(buttonByText(page, '.sidebar-nav', 'Combine Folders'));
}

function closeButton(page) {
    return buttonByText(page, '#combineFoldersModal .modal-footer', 'Close');
}

function directoryOf(card) {
    return card.querySelector('.combine-folder-path').textContent;
}

function cards(page) {
    return page.$$('#combineFoldersList .combine-folder-card');
}

function card(page, directory) {
    const found = cards(page).find(element => directoryOf(element) === directory);
    if (!found) throw new Error(`No folder card for ${directory}`);
    return found;
}

function shownGroup(page) {
    return {
        counter: page.$('#combineFoldersCounter').textContent,
        title: page.$('#combineFoldersGroupTitle').textContent,
        meta: page.$('#combineFoldersGroupMeta').textContent,
        suggestion: page.$('#combineFoldersSuggestion').textContent,
        selected: page.$$('#combineFoldersList .combine-folder-card.selected').map(directoryOf)
    };
}

function actions(page) {
    return {
        previous: !page.$('#combineFoldersPrevBtn').disabled,
        next: !page.$('#combineFoldersNextBtn').disabled,
        preview: !page.$('#combineFoldersPreviewBtn').disabled,
        combine: !page.$('#combineFoldersConfirmBtn').disabled
    };
}

const NO_ACTIONS = { previous: false, next: false, preview: false, combine: false };

function emptyStateText(page) {
    const element = page.$('#combineFoldersEmptyState');
    return element.style.display === 'none' ? null : element.textContent;
}

function contentShown(page) {
    return page.$('#combineFoldersContent').style.display !== 'none';
}

function cardDetails(element) {
    return {
        stats: [...element.querySelectorAll('.combine-folder-stats strong')].map(stat => stat.textContent),
        samples: [...element.querySelectorAll('.combine-folder-files li')].map(item => item.textContent)
    };
}

test('Combine Folders shows a loading state, then the first group of folders', async t => {
    const answer = deferred();
    const page = await boot({ routes: { [COMBINABLE]: () => answer.promise } });
    t.after(() => page.close());

    openFromSidebar(page);

    assert.ok(isModalOpen(page, 'combineFoldersModal'));
    assert.equal(page.$('#combineFoldersGroupTitle').textContent, 'Loading combinable folders...');
    assert.equal(page.$('#combineFoldersGroupMeta').textContent, '');
    assert.equal(page.$('#combineFoldersSuggestion').textContent, '');
    assert.deepEqual(cards(page), []);
    assert.equal(page.$('#combineFoldersPreview').style.display, 'none');
    assert.equal(emptyStateText(page), null);
    assert.ok(contentShown(page));
    assert.ok(page.$('#combineFoldersPreviewBtn').disabled);
    assert.ok(page.$('#combineFoldersConfirmBtn').disabled);
    const [call] = callsTo(page, 'GET', '/api/files/combinable-folders');
    assert.match(call.headers.authorization, /^Bearer /);

    answer.resolve({ groups: [SAGA, PAPER_GIRLS] });
    await page.settle();

    assert.deepEqual(shownGroup(page), {
        counter: '1 of 2',
        title: 'Saga · Vol. 2012',
        meta: '2 folders · 13 files',
        suggestion: 'Contains the most recently added file (2024-03-01).',
        selected: ['/comics/Saga']
    });
    assert.deepEqual(actions(page), { previous: false, next: true, preview: true, combine: true });
    assert.equal(emptyStateText(page), null);
    assert.ok(contentShown(page));

    const [first, second] = cards(page);
    assert.deepEqual([directoryOf(first), directoryOf(second)], ['/comics/Saga', '/comics/Saga (2012)']);
    assert.equal(first.className, 'combine-folder-card selected suggested');
    assert.equal(second.className, 'combine-folder-card');
    for (const [index, element] of [first, second].entries()) {
        assert.equal(element.getAttribute('role'), 'button');
        assert.equal(element.tabIndex, 0);
        const radio = element.querySelector('input[type="radio"]');
        assert.equal(radio.id, `combineFolderRadio_0_${index}`);
        assert.equal(radio.name, 'combineFolderDestination');
        assert.equal(radio.checked, index === 0);
        assert.equal(element.querySelector('label').htmlFor, radio.id);
    }
    assert.deepEqual(messages(page), []);
});

test('a folder card shows its file count, size, newest and oldest file and up to five sample names', async t => {
    const names = ['Saga 001.cbz', 'Saga 002.cbz', 'Saga 003.cbz', 'Saga 004.cbz', 'Saga 005.cbz', 'Saga 006.cbz'];
    const group = combinableGroup({
        totalFileCount: 1242,
        folders: [
            combinableFolder({ fileCount: 1234, totalSize: 3 * 1024 ** 3, sampleFileNames: names }),
            combinableFolder({
                directory: '/comics/Saga (2012)',
                fileCount: 6,
                totalSize: 900,
                newestFileAddedAt: null,
                oldestFileAddedAt: 'not a date',
                sampleFileNames: [],
                isSuggested: false
            }),
            combinableFolder({ directory: '/comics/Saga (old)', fileCount: 2, totalSize: 2048, sampleFileNames: undefined, isSuggested: false })
        ]
    });
    const page = await boot({ groups: [group] });
    t.after(() => page.close());

    openFromSidebar(page);
    await page.settle();

    assert.equal(shownGroup(page).meta, '3 folders · 1242 files');
    assert.deepEqual(cardDetails(card(page, '/comics/Saga')), {
        stats: [
            (1234).toLocaleString(),
            '3.0 GB',
            new Date('2024-03-01T12:00:00Z').toLocaleString(),
            new Date('2023-01-15T12:00:00Z').toLocaleString()
        ],
        samples: names.slice(0, 5)
    });
    assert.ok(card(page, '/comics/Saga').querySelector('.combine-folder-files').textContent.startsWith('Sample files:'));
    // Missing or unreadable dates show a dash; no names, no sample list.
    assert.deepEqual(cardDetails(card(page, '/comics/Saga (2012)')), { stats: ['6', '900 B', '—', '—'], samples: [] });
    assert.equal(card(page, '/comics/Saga (2012)').querySelector('.combine-folder-files'), null);
    assert.equal(card(page, '/comics/Saga (old)').querySelector('.combine-folder-files'), null);
    assert.equal(cardDetails(card(page, '/comics/Saga (old)')).stats[1], '2.0 KB');
});

test('Previous and Next step through the groups and reset the chosen destination', async t => {
    // A lone folder without a series name (defensive: the server only lists
    // groups of two or more folders).
    const loose = combinableGroup({
        groupKey: 'loose',
        seriesName: '',
        totalFileCount: 1,
        suggestedDestinationDirectory: '/comics/Loose',
        suggestionReason: '',
        folders: [combinableFolder({ directory: '/comics/Loose', fileCount: 1 })]
    });
    const page = await boot({ groups: [SAGA, PAPER_GIRLS, loose] });
    t.after(() => page.close());
    openFromSidebar(page);
    await page.settle();

    page.click(card(page, '/comics/Saga (2012)'));
    assert.deepEqual(shownGroup(page).selected, ['/comics/Saga (2012)']);

    page.click('#combineFoldersNextBtn');
    assert.deepEqual(shownGroup(page), {
        counter: '2 of 3',
        title: 'Paper Girls',
        meta: '2 folders · 30 files',
        suggestion: 'Folder name differs only by a non-distinguishing suffix: (Digital).',
        selected: ['/comics/Paper Girls']
    });
    assert.deepEqual(actions(page), { previous: true, next: true, preview: true, combine: true });
    assert.equal(card(page, '/comics/Paper Girls').className, 'combine-folder-card selected suggested');
    assert.equal(card(page, '/comics/Paper Girls').querySelector('input').id, 'combineFolderRadio_1_1');

    // Back on the first group its suggestion is selected again.
    page.click('#combineFoldersPrevBtn');
    assert.deepEqual(shownGroup(page).selected, ['/comics/Saga']);
    assert.equal(shownGroup(page).counter, '1 of 3');
    assert.deepEqual(actions(page), { previous: false, next: true, preview: true, combine: true });

    page.click('#combineFoldersNextBtn');
    page.click('#combineFoldersNextBtn');
    assert.deepEqual(shownGroup(page), {
        counter: '3 of 3',
        title: 'Combinable folders',
        meta: '1 folder · 1 file',
        suggestion: '',
        selected: ['/comics/Loose']
    });
    // A single folder has nothing to combine.
    assert.deepEqual(actions(page), { previous: true, next: false, preview: false, combine: false });

    // Steps past either end (the buttons are disabled there) are ignored.
    page.run('changeCombineFolderGroup(1)');
    assert.equal(shownGroup(page).counter, '3 of 3');
    page.run('changeCombineFolderGroup(-3)');
    assert.equal(shownGroup(page).counter, '3 of 3');
    assert.equal(callsTo(page, 'GET', '/api/files/combinable-folders').length, 1);
});

test('clicking a folder card or pressing Enter or Space on it makes it the destination', async t => {
    const page = await boot({ groups: [SAGA] });
    t.after(() => page.close());
    openFromSidebar(page);
    await page.settle();
    assert.deepEqual(actions(page), { previous: false, next: false, preview: true, combine: true });

    page.click(card(page, '/comics/Saga (2012)'));
    assert.deepEqual(shownGroup(page).selected, ['/comics/Saga (2012)']);
    assert.equal(card(page, '/comics/Saga (2012)').className, 'combine-folder-card selected');
    assert.equal(card(page, '/comics/Saga').className, 'combine-folder-card suggested');
    assert.ok(card(page, '/comics/Saga (2012)').querySelector('input').checked);
    assert.ok(!card(page, '/comics/Saga').querySelector('input').checked);

    page.click(card(page, '/comics/Saga').querySelector('input[type="radio"]'));
    assert.deepEqual(shownGroup(page).selected, ['/comics/Saga']);

    const enter = pressKey(page, card(page, '/comics/Saga (2012)'), 'Enter');
    assert.ok(enter.defaultPrevented);
    assert.deepEqual(shownGroup(page).selected, ['/comics/Saga (2012)']);

    const space = pressKey(page, card(page, '/comics/Saga'), ' ');
    assert.ok(space.defaultPrevented);
    assert.deepEqual(shownGroup(page).selected, ['/comics/Saga']);

    // Other keys are left alone.
    const arrow = pressKey(page, card(page, '/comics/Saga (2012)'), 'ArrowDown');
    assert.ok(!arrow.defaultPrevented);
    assert.deepEqual(shownGroup(page).selected, ['/comics/Saga']);
    assert.deepEqual(actions(page), { previous: false, next: false, preview: true, combine: true });
    assert.equal(callsTo(page, 'GET', '/api/files/combinable-folders').length, 1);
});

test('without a suggested folder nothing is selected until the user picks one', async t => {
    const group = combinableGroup({
        suggestedDestinationDirectory: '',
        suggestionReason: '',
        folders: [
            combinableFolder({ isSuggested: false }),
            combinableFolder({ directory: '/comics/Saga (2012)', isSuggested: false })
        ]
    });
    const page = await boot({ groups: [group] });
    t.after(() => page.close());
    openFromSidebar(page);
    await page.settle();

    assert.deepEqual(shownGroup(page).selected, []);
    assert.equal(page.$$('#combineFoldersList .suggested').length, 0);
    assert.ok(cards(page).every(element => !element.querySelector('input').checked));
    assert.deepEqual(actions(page), NO_ACTIONS);
    // Run directly (the buttons are disabled): without a destination neither asks nor posts.
    await page.run('previewCombineFolders()');
    await page.run('confirmCombineFolders()');
    assert.deepEqual(page.dialogs, []);
    assert.deepEqual(callsTo(page, 'POST', '/api/files/combine-folders/preview'), []);
    assert.deepEqual(callsTo(page, 'POST', '/api/files/combine-folders'), []);

    page.click(card(page, '/comics/Saga (2012)'));
    assert.deepEqual(shownGroup(page).selected, ['/comics/Saga (2012)']);
    assert.deepEqual(actions(page), { previous: false, next: false, preview: true, combine: true });
});

test('an empty list says no folders look combinable and offers no actions', async t => {
    // No groups, then a response without a group list.
    const answers = [{ groups: [] }, {}];
    const page = await boot({ routes: { [COMBINABLE]: () => answers.shift() } });
    t.after(() => page.close());

    for (let open = 1; open <= 2; open++) {
        openFromSidebar(page);
        await page.settle();

        assert.equal(emptyStateText(page), 'No folders look combinable right now.', `open ${open}`);
        assert.ok(!contentShown(page));
        assert.equal(page.$('#combineFoldersCounter').textContent, '0 of 0');
        assert.deepEqual(actions(page), NO_ACTIONS);
        page.click(closeButton(page));
    }

    // Run directly (the buttons are disabled): without a group nothing happens.
    await page.run('previewCombineFolders()');
    await page.run('confirmCombineFolders()');
    page.run('changeCombineFolderGroup(1)');
    assert.deepEqual(page.dialogs, []);
    assert.equal(page.$('#combineFoldersCounter').textContent, '0 of 0');
    assert.deepEqual(callsTo(page, 'POST', '/api/files/combine-folders/preview'), []);
    assert.deepEqual(callsTo(page, 'POST', '/api/files/combine-folders'), []);
    assert.equal(callsTo(page, 'GET', '/api/files/combinable-folders').length, 2);
});

test('a failed load is shown in the modal and opening it again retries', async t => {
    const page = await boot({ routes: { [COMBINABLE]: respond(500, 'Error retrieving combinable folders') } });
    t.after(() => page.close());

    openFromSidebar(page);
    await page.settle();
    assert.equal(emptyStateText(page), 'Failed to load combinable folders: HTTP error! status: 500');
    assert.ok(!contentShown(page));
    assert.equal(page.$('#combineFoldersCounter').textContent, '0 of 0');
    assert.deepEqual(actions(page), NO_ACTIONS);
    assert.ok(page.consoleMessages.some(entry => entry.type === 'error'
        && entry.args[0] === 'Failed to load combinable folders:'));

    page.click(closeButton(page));
    page.route(COMBINABLE, () => { throw new TypeError('Failed to fetch'); });
    openFromSidebar(page);
    await page.settle();
    assert.equal(emptyStateText(page), 'Failed to load combinable folders: Failed to fetch');

    // The next open shows the usual empty message again.
    page.click(closeButton(page));
    page.route(COMBINABLE, { groups: [] });
    openFromSidebar(page);
    await page.settle();
    assert.equal(emptyStateText(page), 'No folders look combinable right now.');

    page.click(closeButton(page));
    page.route(COMBINABLE, { groups: [SAGA] });
    openFromSidebar(page);
    await page.settle();
    assert.equal(emptyStateText(page), null);
    assert.ok(contentShown(page));
    assert.equal(shownGroup(page).counter, '1 of 1');
    assert.deepEqual(messages(page), []);
});

test('an expired session goes to the login page', async t => {
    const page = await boot({ routes: { [COMBINABLE]: 401 } });
    t.after(() => page.close());

    openFromSidebar(page);
    await page.settle();

    assert.ok(page.navigations.some(nav => nav.url.includes('/login.html')));
    assert.equal(emptyStateText(page), null);
    assert.ok(!page.consoleMessages.some(entry => entry.args[0] === 'Failed to load combinable folders:'));
});

test('the library toolbar and health card open Combine Folders when some folders are combinable', async t => {
    const page = await boot({
        routes: {
            'GET /api/files/counts': { total: 43, processed: 43, unprocessed: 0, duplicates: 0, combinableFolders: 2 }
        }
    });
    t.after(() => page.close());

    const toolbarButton = page.$('#combineFoldersBtn');
    assert.equal(toolbarButton.hidden, false);
    page.click(toolbarButton);
    await page.settle();
    assert.ok(isModalOpen(page, 'combineFoldersModal'));
    assert.equal(shownGroup(page).counter, '1 of 2');

    page.click(closeButton(page));
    page.click(page.$('#libraryHealthCombinableFolders').closest('button'));
    await page.settle();
    assert.ok(isModalOpen(page, 'combineFoldersModal'));
    assert.equal(callsTo(page, 'GET', '/api/files/combinable-folders').length, 2);
});

test('Close, × and Escape close the modal', async t => {
    const page = await boot();
    t.after(() => page.close());
    const closers = {
        Close: () => page.click(closeButton(page)),
        '×': () => page.click('#combineFoldersModal .close-btn'),
        Escape: () => pressEscape(page)
    };

    for (const [name, close] of Object.entries(closers)) {
        openFromSidebar(page);
        await page.settle();
        assert.ok(isModalOpen(page, 'combineFoldersModal'), name);

        await close();
        assert.ok(!isModalOpen(page, 'combineFoldersModal'), name);
    }
});

test('read-only users are not offered Combine Folders in the sidebar, toolbar or health cards', async t => {
    const page = await boot({
        user: { canModifyLibrary: false, canAdminister: false },
        routes: {
            'GET /api/files/counts': { total: 43, processed: 43, unprocessed: 0, duplicates: 0, combinableFolders: 2 }
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
    assert.ok(!visible(buttonByText(page, '.sidebar-nav', 'Combine Folders')));
    assert.ok(!visible(page.$('#combineFoldersBtn')));
    assert.ok(!visible(page.$('#libraryHealthCombinableFolders').closest('button')));
    // The read-only user still sees the other library tools.
    assert.ok(visible(buttonByText(page, '.sidebar-nav', 'Review Duplicates')));
});
