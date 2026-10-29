'use strict';

// Series detail page (js/app/series.js renderSeriesDetail and friends): the
// header, synopsis, issue cards, loading/failed/empty states, selection and
// navigation in and out of the page.

const test = require('node:test');
const assert = require('node:assert/strict');
const { bootApp, deferred, addReadOnlyGate, isDisplayed } = require('./support/app');
const { respond } = require('./support/dom');
const {
    seriesCard, issue, issuesBody, libraryBody, issuesPath, bootSeries, callsTo, text, issueCards, readerUrl
} = require('./support/series-helpers');

const ISSUES = issuesPath('saga');

test('renders the series header, synopsis and one card per issue', async () => {
    const series = seriesCard({
        aliases: ['Saga Comic', 'Saga (2012)'],
        metadata_source: 'ComicVine',
        last_lookup_utc: '2024-01-02T03:04:05Z',
        synopsis: '  A space opera.  '
    });
    const page = await bootSeries({
        series,
        issues: [issue(1, { read: true }), issue(2), issue(3, { duplicate: true, volume: '1' })]
    });

    const panel = page.$('#seriesDetailPanel');
    assert.ok(panel, 'the series detail panel is rendered');
    assert.match(text(panel.querySelector('h2')), /^Saga\b/);
    const meta = panel.querySelectorAll('.series-detail-summary .series-detail-meta');
    const lookedUp = page.run("new Date('2024-01-02T03:04:05Z').toLocaleString()");
    assert.deepEqual([...meta].map(text), [
        '3 issues · 3.0 MB',
        'Also known as: Saga Comic, Saga (2012)',
        `Source: ComicVine · ${lookedUp}`
    ]);
    const synopsis = panel.querySelector('[data-synopsis-block]');
    assert.equal(text(synopsis), 'A space opera.');
    assert.equal(synopsis.classList.contains('series-detail-synopsis--clamped'), false);
    assert.equal(synopsis.querySelector('button'), null, 'a short synopsis has no toggle');

    const cover = panel.querySelector('.series-detail-cover');
    assert.equal(cover.dataset.protectedImage, '/comics/Saga/Saga 001.cbz');
    assert.equal(cover.dataset.protectedImageFallback, '');

    const cards = issueCards(page);
    assert.deepEqual(cards.map(card => card.dataset.filePath), [
        '/comics/Saga/Saga 001.cbz', '/comics/Saga/Saga 002.cbz', '/comics/Saga/Saga 003.cbz'
    ]);
    const [first, second, third] = cards;
    assert.ok(first.classList.contains('series-issue-card--read'));
    assert.equal(first.querySelector('.series-issue-read-corner').title, 'Read');
    assert.match(text(first.querySelector('.dropdown-menu')), /Mark Unread/);
    assert.doesNotMatch(text(first.querySelector('.dropdown-menu')), /Mark Read|Review Duplicate/);
    assert.ok(second.classList.contains('series-issue-card--unread'));
    assert.equal(second.querySelector('.series-issue-read-corner').title, 'Unread');
    assert.match(text(second.querySelector('.dropdown-menu')), /Mark Read/);
    assert.equal(text(second.querySelector('.series-issue-badge')), '#2');
    assert.equal(text(second.querySelector('.series-issue-title')), 'Saga #2');
    assert.equal(text(second.querySelector('.series-issue-subtitle')), '2012');
    assert.equal(text(second.querySelector('.series-issue-body .series-detail-meta')), '1.0 MB');
    assert.ok(third.classList.contains('series-issue-card--duplicate'));
    assert.equal(text(third.querySelector('.series-issue-duplicate-badge')), '🔁 Duplicate');
    assert.match(text(third.querySelector('.dropdown-menu')), /Review Duplicate/);
    assert.equal(text(third.querySelector('.series-issue-subtitle')), '2012 · Vol. 1');

    assert.equal(text(panel.querySelector('.series-detail-selection-meta')), '3 issues in this series');
    assert.equal(panel.querySelector('.series-detail-missing-banner'), null, 'nothing is missing');
    assert.equal(panel.querySelector('#seriesIssuesSentinel'), null, 'everything is loaded');
    assert.equal(text(panel.querySelector('.series-detail-actions .btn-primary')), 'Read First Issue');
    assert.deepEqual(page.errors, []);
});

test('falls back to file names and omits the badge and subtitle parts an issue lacks', async () => {
    const page = await bootSeries({
        series: seriesCard({ issue_count: 1, last_lookup_utc: '2024-01-02T03:04:05Z' }),
        issues: [
            issue(1, { title: null, year: null, volume: '2' }),
            issue(2, { title: '', year: null }),
            issue('', { file_path: '/comics/Saga/Saga Special.cbz', file_name: 'Saga Special.cbz', title: null, year: 2015 })
        ]
    });

    const [first, second, special] = issueCards(page);
    assert.equal(text(first.querySelector('.series-issue-title')), 'Saga 001.cbz');
    assert.equal(first.querySelector('.series-issue-cover-button').getAttribute('aria-label'), 'Read Saga 001.cbz');
    assert.equal(first.querySelector('.series-issue-select').getAttribute('aria-label'), 'Select Saga 001.cbz');
    assert.equal(text(first.querySelector('.series-issue-subtitle')), 'Vol. 2');
    assert.equal(text(second.querySelector('.series-issue-title')), 'Saga 002.cbz');
    assert.equal(text(second.querySelector('.series-issue-subtitle')), '');
    assert.equal(special.dataset.filePath, '/comics/Saga/Saga Special.cbz');
    assert.equal(special.querySelector('.series-issue-badge'), null, 'no issue number, no badge');
    assert.equal(text(special.querySelector('.series-issue-subtitle')), '2015');

    // The backend count wins over the library card's stale issue_count.
    const meta = page.$$('#seriesDetailPanel .series-detail-summary .series-detail-meta').map(text);
    assert.deepEqual(meta, ['3 issues · 3.0 MB'], 'no aliases and no metadata source lines');
});

test('shows the external series image with the first issue as fallback', async () => {
    const page = await bootSeries({
        series: seriesCard({
            has_external_image: true,
            external_image_url: '/api/series-images/saga',
            metadata_source: 'AniList'
        })
    });

    const cover = page.$('#seriesDetailPanel .series-detail-cover');
    assert.equal(cover.dataset.protectedImage, '/api/series-images/saga');
    assert.equal(cover.dataset.protectedImageFallback, '/comics/Saga/Saga 001.cbz');
    assert.equal(cover.alt, 'Saga cover');
    const meta = page.$$('#seriesDetailPanel .series-detail-summary .series-detail-meta').map(text);
    assert.deepEqual(meta, ['3 issues · 3.0 MB', 'Source: AniList'], 'no lookup time without last_lookup_utc');
});

test('a single issue is counted in the singular', async () => {
    const page = await bootSeries({ series: seriesCard({ issue_count: 1 }), issues: [issue(1)] });

    assert.equal(text(page.$('#seriesDetailPanel .series-detail-summary .series-detail-meta')), '1 issue · 3.0 MB');
    assert.equal(text(page.$('#seriesDetailPanel .series-detail-selection-meta')), '1 issue in this series');
});

test('a long synopsis is clamped behind a Show more / Show less toggle', async () => {
    const synopsis = 'Star-crossed lovers flee a galactic war. '.repeat(10).trim();
    assert.ok(synopsis.length > 320);
    const page = await bootSeries({ series: seriesCard({ synopsis }) });

    const block = page.$('#seriesDetailPanel [data-synopsis-block]');
    const toggle = block.querySelector('.series-detail-synopsis-toggle');
    assert.equal(text(block.querySelector('.series-detail-synopsis-text')), synopsis);
    assert.ok(block.classList.contains('series-detail-synopsis--clamped'));
    assert.equal(toggle.textContent, 'Show more');

    page.click(toggle);
    assert.ok(block.classList.contains('series-detail-synopsis--expanded'));
    assert.equal(block.classList.contains('series-detail-synopsis--clamped'), false);
    assert.equal(toggle.textContent, 'Show less');

    page.click(toggle);
    assert.equal(block.classList.contains('series-detail-synopsis--expanded'), false);
    assert.ok(block.classList.contains('series-detail-synopsis--clamped'));
    assert.equal(toggle.textContent, 'Show more');

    // A toggle outside any synopsis block is ignored.
    const stray = page.document.createElement('button');
    stray.textContent = 'untouched';
    page.run('toggleSeriesSynopsis')(stray);
    assert.equal(stray.textContent, 'untouched');
});

test('Read First Issue and an issue cover open the reader on that issue', async () => {
    const page = await bootSeries({ issues: [issue(2), issue(1), issue(3)] });

    page.click('#seriesDetailPanel .series-detail-actions .btn-primary');
    assert.equal(page.navigations.at(-1).url, readerUrl('/comics/Saga/Saga 002.cbz'), 'the first issue the API listed');
    const snapshot = JSON.parse(page.window.sessionStorage.getItem('comicReaderReturnState'));
    assert.equal(snapshot.currentSeriesDetailId, 'saga', 'the reader returns to this series');

    page.click(issueCards(page)[2].querySelector('.series-issue-cover-button'));
    assert.equal(page.navigations.at(-1).url, readerUrl('/comics/Saga/Saga 003.cbz'));
});

test('shows a loading state until the issues arrive', async () => {
    const gate = deferred();
    const series = seriesCard();
    const page = await bootSeries({ series, routes: { [`GET ${ISSUES}`]: () => gate.promise } });

    const panel = page.$('#seriesDetailPanel');
    assert.equal(text(panel.querySelector('.loading')), 'Loading issues...');
    assert.equal(panel.querySelector('.series-detail-selection-bar'), null);
    assert.equal(panel.querySelector('.series-detail-actions .btn-primary'), null, 'nothing to read yet');
    assert.equal(text(panel.querySelector('.series-detail-summary .series-detail-meta')), '3 issues · 3.0 MB',
        'the library card count is shown meanwhile');

    gate.resolve(issuesBody(series, [issue(1), issue(2)]));
    await page.settle();
    assert.equal(page.$('#seriesDetailPanel .loading'), null);
    assert.equal(issueCards(page).length, 2);
    assert.equal(text(page.$('#seriesDetailPanel .series-detail-summary .series-detail-meta')), '2 issues · 3.0 MB');
});

test('a failed issue load offers a Retry that refetches the issues', async () => {
    const series = seriesCard();
    const page = await bootSeries({ series, routes: { [`GET ${ISSUES}`]: respond(500, { error: 'boom' }) } });

    const empty = page.$('#seriesDetailPanel .empty-state');
    assert.equal(text(empty), 'Failed to load issues. Retry');
    assert.equal(issueCards(page).length, 0);
    assert.equal(page.$('#seriesDetailPanel .series-detail-selection-bar'), null);

    page.route(`GET ${ISSUES}`, issuesBody(series, [issue(1), issue(2), issue(3)]));
    page.click(empty.querySelector('button'));
    await page.settle();
    const calls = callsTo(page, ISSUES, 'GET');
    assert.equal(calls.length, 2);
    assert.deepEqual(calls[1].query, { per_page: '-1', page: '1' });
    assert.equal(issueCards(page).length, 3);
    assert.equal(page.$('#seriesDetailPanel .empty-state'), null);
});

test('a network error while loading issues is shown as a failed load', async () => {
    const page = await bootSeries({
        routes: { [`GET ${ISSUES}`]: () => { throw new TypeError('Failed to fetch'); } }
    });

    assert.equal(text(page.$('#seriesDetailPanel .empty-state')), 'Failed to load issues. Retry');
    assert.ok(page.consoleMessages.some(entry => entry.type === 'error' && entry.args[0] === 'loadSeriesIssues failed'));
});

test('an empty series says so and offers nothing to read', async () => {
    const page = await bootSeries({ series: seriesCard({ issue_count: 0, total_size: 0 }), issues: [] });

    const panel = page.$('#seriesDetailPanel');
    assert.equal(text(panel.querySelector('.empty-state')), 'No issues in this series.');
    assert.equal(panel.querySelector('.empty-state button'), null);
    assert.equal(panel.querySelector('.series-detail-selection-bar'), null);
    assert.equal(panel.querySelector('.series-detail-actions .btn-primary'), null);
    assert.equal(text(panel.querySelector('.series-detail-summary .series-detail-meta')), '0 issues · 0 B');
});

test('a header filter that hides every issue offers to clear the filter', async () => {
    const series = seriesCard();
    const all = [issue(1), issue(2), issue(3)];
    const page = await bootSeries({
        series,
        routes: {
            [`GET ${ISSUES}`]: call => issuesBody(series, call.query.filter === 'unmarked' ? [] : all),
            'PUT /api/preferences': {},
            'POST /api/preferences': {}
        }
    });
    assert.equal(issueCards(page).length, 3);

    page.click('#headerFilterMenu [data-filter="unmarked"]');
    await page.settle();
    const filtered = callsTo(page, ISSUES).at(-1);
    assert.deepEqual(filtered.query, { per_page: '-1', page: '1', filter: 'unmarked' });
    const empty = page.$('#seriesDetailPanel .empty-state');
    assert.equal(text(empty.querySelector('p')), 'No issues in this series match the current filter.');
    assert.equal(page.window.location.hash, '#/series/saga', 'the filter keeps the series open');

    page.click(empty.querySelector('button'));
    await page.settle();
    assert.deepEqual(callsTo(page, ISSUES).at(-1).query, { per_page: '-1', page: '1' });
    assert.equal(issueCards(page).length, 3);
});

test('a search that hides every issue says so without a Clear filter button', async () => {
    const series = seriesCard();
    const page = await bootSeries({
        series,
        routes: { [`GET ${ISSUES}`]: issuesBody(series, []) }
    });
    assert.equal(text(page.$('#seriesDetailPanel .empty-state')), 'No issues in this series.');

    page.$('#headerSearchInput').value = 'zzz';
    page.run('filterFiles()');
    await page.settle();
    const empty = page.$('#seriesDetailPanel .empty-state');
    assert.equal(text(empty), 'No issues in this series match the current filter.');
    assert.equal(empty.querySelector('button'), null, 'only a header filter can be cleared from here');
});

test('the Select all checkbox follows the issue checkboxes', async () => {
    const page = await bootSeries();
    const selectAll = () => page.$('#selectAll');
    const checkbox = index => issueCards(page)[index].querySelector('.series-issue-select input');

    assert.equal(selectAll().checked, false);
    assert.equal(selectAll().indeterminate, false);

    page.input(checkbox(0), true);
    assert.equal(selectAll().checked, false);
    assert.equal(selectAll().indeterminate, true, 'some issues selected');
    assert.ok(issueCards(page)[0].classList.contains('series-issue-card--selected'));

    page.input(selectAll(), true);
    assert.equal(selectAll().checked, true);
    assert.equal(selectAll().indeterminate, false);
    assert.deepEqual(issueCards(page).map(card => card.classList.contains('series-issue-card--selected')), [true, true, true]);
    assert.deepEqual(issueCards(page).map(card => card.querySelector('.series-issue-select input').checked), [true, true, true]);
    assert.deepEqual(page.evaluate('[...selectedFiles].sort()'), [
        '/comics/Saga/Saga 001.cbz', '/comics/Saga/Saga 002.cbz', '/comics/Saga/Saga 003.cbz'
    ]);

    page.input(checkbox(1), false);
    assert.equal(selectAll().indeterminate, true);

    page.input(selectAll(), false);
    assert.equal(selectAll().checked, false);
    assert.equal(selectAll().indeterminate, false);
    assert.deepEqual(page.evaluate('[...selectedFiles]'), []);
});

test('a re-render keeps the Select all checkbox partially checked', async () => {
    const page = await bootSeries();
    page.input(issueCards(page)[1].querySelector('.series-issue-select input'), true);

    page.run("renderSeriesDetail('saga')");
    const selectAll = page.$('#selectAll');
    assert.equal(selectAll.checked, false);
    assert.equal(selectAll.indeterminate, true);
    assert.deepEqual(issueCards(page).map(card => card.classList.contains('series-issue-card--selected')), [false, true, false]);
});

test('Back returns to the library and re-opening the series reuses the loaded issues', async () => {
    const page = await bootSeries();
    assert.equal(callsTo(page, ISSUES).length, 1);

    page.click('#seriesDetailPanel .series-detail-back');
    await page.settle();
    assert.equal(page.window.location.hash, '#/library');
    assert.equal(page.$('#seriesDetailPanel'), null);
    assert.equal(page.evaluate('currentSeriesDetailId'), null);

    const card = page.$('#fileList .series-card[aria-label="Open series Saga"]');
    assert.ok(card, 'the library grid lists the series');
    page.click(card);
    await page.settle();
    assert.equal(page.window.location.hash, '#/series/saga');
    assert.equal(issueCards(page).length, 3);
    assert.equal(callsTo(page, ISSUES).length, 1, 'the cached issues are reused');
});

test('a series opened from the overview returns to the overview', async () => {
    const series = seriesCard();
    const page = await bootApp({
        hash: '#/home',
        routes: {
            'GET /api/overview': { continue_reading: [], series_updates: [series], newly_added_series: [] },
            'GET /api/files/series': libraryBody([series]),
            [`GET ${ISSUES}`]: issuesBody(series, [issue(1)]),
            'GET /api/suwayomi/status': { enabled: false }
        }
    });

    page.click('#overviewRows .overview-series-card');
    await page.settle();
    assert.equal(page.window.location.hash, '#/series/saga');
    assert.equal(issueCards(page).length, 1);

    page.click('#seriesDetailPanel .series-detail-back');
    await page.settle();
    assert.equal(page.window.location.hash, '#/home');
    assert.equal(page.evaluate('currentSeriesDetailId'), null);
});

test('leaving the series before its issues arrive does not render it again', async () => {
    const gate = deferred();
    const series = seriesCard();
    const page = await bootSeries({ series, routes: { [`GET ${ISSUES}`]: () => gate.promise } });

    page.click('#seriesDetailPanel .series-detail-back');
    await page.settle();
    assert.equal(page.window.location.hash, '#/library');

    gate.resolve(issuesBody(series, [issue(1), issue(2), issue(3)]));
    await page.settle();
    assert.equal(page.$('#seriesDetailPanel'), null, 'the library stays on screen');
    assert.ok(page.$('#fileList .series-card'));

    page.run("openSeriesDetail('saga')");
    await page.settle();
    assert.equal(issueCards(page).length, 3);
    assert.equal(callsTo(page, ISSUES).length, 1, 'the late response was still cached');
});

test('read-only users get the page without its write controls', async () => {
    const page = await bootSeries({
        user: { canModifyLibrary: false, canAdminister: false, roles: ['ReadOnly'] },
        issues: [issue(1), issue(3)],
        suwayomi: { enabled: true, connected: true }
    });

    assert.ok(page.document.body.classList.contains('user-read-only'));
    const writeOnly = page.$$('#seriesDetailPanel [data-requires-write]').map(text);
    assert.ok(writeOnly.includes('Email Selected'));
    assert.ok(writeOnly.includes('Email All Issues'));
    assert.ok(writeOnly.includes('Auto-send New Issues'));
    assert.ok(writeOnly.includes('Download from Suwayomi'));
    assert.ok(writeOnly.includes('Email to Ereader…'));
    // Reading stays available.
    page.click('#seriesDetailPanel .series-detail-actions .btn-primary');
    assert.equal(page.navigations.at(-1).url, readerUrl('/comics/Saga/Saga 001.cbz'));
});

test('read-only users are offered only the read actions of an issue and no Series Actions', async () => {
    const boot = user => bootSeries({
        user,
        issues: [issue(1), issue(2, { read: true }), issue(3, { duplicate: true })],
        beforeScripts: addReadOnlyGate
    });
    // What each issue's menu shows once opened (menus.js then moves it to
    // <body>, out of its card); a divider reads as '—'.
    const issueMenus = async page => {
        const menus = [];
        for (const card of issueCards(page)) {
            page.click(card.querySelector('.series-issue-actions-toggle'));
            await page.waitFor(() => page.$('body > .dropdown-menu.show'));
            menus.push([...page.$('body > .dropdown-menu.show').children]
                .filter(isDisplayed)
                .map(item => (item.classList.contains('dropdown-divider') ? '—' : text(item))));
            page.run('closeAllDropdowns()');
            await page.settle();
        }
        return menus;
    };
    const seriesActions = page => page.$('#seriesDetailPanel .series-actions-dropdown');
    const seriesActionItems = page => [...seriesActions(page).querySelectorAll('.dropdown-item')];

    const readOnly = await boot({ canModifyLibrary: false, canAdminister: false, roles: ['ReadOnly'] });
    assert.ok(readOnly.document.body.classList.contains('user-read-only'));
    assert.deepEqual(await issueMenus(readOnly), [
        ['Info', 'View/Edit', 'Read Comic', '—', 'Mark Read'],
        ['Info', 'View/Edit', 'Read Comic', '—', 'Mark Unread'],
        ['Info', 'View/Edit', 'Read Comic', 'Review Duplicate', '—', 'Mark Read']
    ]);
    // Every series action changes the library or sends mail, so the menu is
    // hidden, and each item is marked too for when it is open in <body>.
    assert.equal(isDisplayed(seriesActions(readOnly)), false);
    assert.deepEqual(seriesActionItems(readOnly).filter(item => !item.hasAttribute('data-requires-write')).map(text), []);

    const admin = await boot({});
    assert.deepEqual((await issueMenus(admin))[0], [
        'Info', 'View/Edit', 'Read Comic', 'Email to Ereader…', '—', 'Mark Read',
        '—', 'Process', 'Rename', 'Normalize', 'Remove Metadata', '—', 'Delete'
    ]);
    assert.ok(isDisplayed(seriesActions(admin)));
    assert.deepEqual(seriesActionItems(admin).filter(isDisplayed).map(text), [
        'Manage Names', 'Manage Folders', 'Refresh Metadata', 'Refresh Folder', 'Update Covers',
        'Email All Issues', 'Auto-send New Issues', 'Reset Processed Status'
    ]);
});
