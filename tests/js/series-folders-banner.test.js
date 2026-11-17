'use strict';

// js/app/series.js: the banner a series page shows when the series is split
// over folders that the folder-combine service lists as one group
// (GET /api/files/series/{id}/folders answers combinable: true). Its Merge
// button opens the Manage Folders modal, where the folders can be merged.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const { bootSeriesPage, messages, isModalOpen, buttonByText, callsTo } = require('./support/email-helpers');

const FOLDERS = 'GET /api/files/series/saga/folders';

// GET /api/files/series/{id}/folders (FilesController.GetSeriesFolders).
function seriesFolders(overrides = {}) {
    return {
        id: 'saga',
        title: 'Saga',
        folders: [
            { directory: '/comics/Saga', file_count: 12, total_size: 2048 },
            // A Windows library names its folders with backslashes.
            { directory: 'C:\\comics\\Saga (2012-)\\', file_count: 1, total_size: 900 }
        ],
        combine_group_key: 'saga',
        suggested_destination_directory: '/comics/Saga',
        combinable: true,
        ...overrides
    };
}

function banner(page) {
    return page.$('#seriesDetailPanel .series-detail-folders-banner');
}

test('a series split over combinable folders names them, and Merge opens Manage Folders', async t => {
    const page = await bootSeriesPage({ routes: { [FOLDERS]: seriesFolders() } });
    t.after(() => page.close());
    await page.settle();

    const shown = banner(page);
    assert.ok(shown);
    assert.equal(shown.getAttribute('role'), 'status');
    assert.ok(shown.hasAttribute('data-requires-write'), 'read-only users cannot merge, so they do not see it');
    assert.equal(shown.querySelector('.series-detail-folders-banner-text').textContent,
        'This series is in 2 folders: Saga, Saga (2012-).');
    assert.equal(callsTo(page, 'GET', '/api/files/series/saga/folders').length, 1);

    page.click(buttonByText(page, '#seriesDetailPanel .series-detail-folders-banner', 'Merge'));
    await page.settle();

    assert.ok(isModalOpen(page, 'seriesFoldersModal'));
    assert.equal(page.$('#seriesFoldersModalTitle').textContent, 'Folders for "Saga"');
    assert.ok(!page.$('#seriesFoldersMergeBtn').disabled, 'the folders can be merged from there');
    assert.deepEqual(messages(page), []);
});

test('no banner when the folders are not one combine group or the series has one folder', async t => {
    const answers = [
        seriesFolders({ combinable: false }),
        seriesFolders({ folders: [{ directory: '/comics/Saga', file_count: 2, total_size: 10 }] }),
        seriesFolders({ folders: null })
    ];
    const page = await bootSeriesPage({ routes: { [FOLDERS]: () => answers.shift() } });
    t.after(() => page.close());
    await page.settle();
    assert.equal(banner(page), null, 'not combinable');

    for (const description of ['one folder', 'no folder list']) {
        await page.run("loadSeriesIssues('saga', true)");
        await page.settle();
        assert.equal(banner(page), null, description);
    }
    assert.equal(callsTo(page, 'GET', '/api/files/series/saga/folders').length, 3);
});

test('the banner follows the folders each time the issues load again', async t => {
    const page = await bootSeriesPage({ routes: { [FOLDERS]: seriesFolders() } });
    t.after(() => page.close());
    await page.settle();
    assert.ok(banner(page));

    // The folders were merged: one is left.
    page.route(FOLDERS, seriesFolders({ folders: [{ directory: '/comics/Saga', file_count: 13, total_size: 2948 }] }));
    await page.run("loadSeriesIssues('saga', true)");
    await page.settle();

    assert.equal(banner(page), null);
});

test('a failed folder request shows no banner and no error', async t => {
    const page = await bootSeriesPage({ routes: { [FOLDERS]: respond(500, { error: 'boom' }) } });
    t.after(() => page.close());
    await page.settle();

    assert.equal(banner(page), null);
    assert.deepEqual(messages(page), []);
    assert.ok(page.consoleMessages.some(entry => entry.type === 'debug'
        && entry.args[0] === 'Could not load the folders of the series'));
});

test('an answer that arrives after the series was left is kept without showing the series again', async t => {
    const answer = deferred();
    const page = await bootSeriesPage({ routes: { [FOLDERS]: () => answer.promise } });
    t.after(() => page.close());
    await page.settle();

    await page.run('closeSeriesDetail()');
    await page.settle();
    answer.resolve(seriesFolders());
    await page.settle();

    assert.equal(page.$('#seriesDetailPanel'), null);
    assert.equal(page.evaluate("seriesFolderHints.get('saga').length"), 2);
});
