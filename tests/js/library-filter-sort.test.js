'use strict';

// The library header controls (js/app/library.js): filter and sort menus,
// the search box, the settings menu and their aria-expanded sync, including
// the paths taken while a series page is open.

const test = require('node:test');
const assert = require('node:assert/strict');
const { plain } = require('./support/dom');
const {
    MOBILE_QUERY,
    makeSeries,
    makeSeriesList,
    bootLibrary,
    callsTo,
    seriesCalls,
    savedPreferences,
    renderedTitles
} = require('./support/library-helpers');

const activeItems = (page, menu) => page.$$(`${menu} .header-dropdown-item.active`).map(item => item.dataset.filter || item.dataset.sort);

test('choosing a filter saves it, relabels the menu and reloads the series with it', async () => {
    const page = await bootLibrary({ series: makeSeriesList(2) });
    const toggle = page.$('#headerFilterToggle');

    page.click(toggle);
    await page.settle(1);
    assert.ok(page.$('#headerFilterMenu').classList.contains('show'));
    assert.equal(toggle.getAttribute('aria-expanded'), 'true');

    page.click('#headerFilterMenu [data-filter="unread"]');
    await page.settle();

    assert.deepEqual(savedPreferences(page), [{ filterMode: 'unread' }]);
    assert.equal(page.$('#headerFilterLabel').textContent, 'Unread');
    assert.deepEqual(activeItems(page, '#headerFilterMenu'), ['unread']);
    assert.ok(!page.$('#headerFilterMenu').classList.contains('show'));
    assert.equal(toggle.getAttribute('aria-expanded'), 'false');
    assert.deepEqual(seriesCalls(page).at(-1).query, { offset: '0', limit: '60', filter: 'unread' });

    // Back to "All": the filter parameter is dropped again.
    page.click('#headerFilterMenu [data-filter="all"]');
    await page.settle();
    assert.equal(page.$('#headerFilterLabel').textContent, 'All');
    assert.deepEqual(seriesCalls(page).at(-1).query, { offset: '0', limit: '60' });
    assert.deepEqual(renderedTitles(page), ['Series 001', 'Series 002']);
});

test('an unknown filter mode is labelled "All" and highlights no item', async () => {
    const page = await bootLibrary({ series: makeSeriesList(1) });

    // setFilter() is the legacy entry point; it forwards to setHeaderFilter().
    page.run("setFilter('marked')");
    await page.settle();
    assert.equal(page.$('#headerFilterLabel').textContent, 'Marked');

    page.run("setHeaderFilter('bogus')");
    await page.settle();
    assert.equal(page.$('#headerFilterLabel').textContent, 'All');
    assert.deepEqual(activeItems(page, '#headerFilterMenu'), []);
    assert.equal(seriesCalls(page).at(-1).query.filter, 'bogus');
});

test('sorting by the same field flips the direction; a new field starts ascending', async () => {
    const page = await bootLibrary({ series: makeSeriesList(2) });
    const toggle = page.$('#headerSortToggle');

    page.click(toggle);
    await page.settle(1);
    assert.ok(page.$('#headerSortMenu').classList.contains('show'));
    assert.equal(toggle.getAttribute('aria-expanded'), 'true');

    page.click('#headerSortMenu [data-sort="date"]');
    await page.settle();
    assert.equal(page.$('#headerSortLabel').textContent, 'Date ↑');
    assert.deepEqual(activeItems(page, '#headerSortMenu'), ['date']);
    assert.ok(!page.$('#headerSortMenu').classList.contains('show'));
    assert.equal(toggle.getAttribute('aria-expanded'), 'false');
    assert.deepEqual(seriesCalls(page).at(-1).query, { offset: '0', limit: '60', sort: 'date' });

    page.click('#headerSortMenu [data-sort="date"]');
    await page.settle();
    assert.equal(page.$('#headerSortLabel').textContent, 'Date ↓');
    assert.deepEqual(seriesCalls(page).at(-1).query, { offset: '0', limit: '60', sort: 'date', direction: 'desc' });

    page.click('#headerSortMenu [data-sort="date"]');
    await page.settle();
    assert.equal(page.$('#headerSortLabel').textContent, 'Date ↑', 'a third click flips it back');
    assert.deepEqual(seriesCalls(page).at(-1).query, { offset: '0', limit: '60', sort: 'date' });

    page.click('#headerSortMenu [data-sort="size"]');
    await page.settle();
    assert.equal(page.$('#headerSortLabel').textContent, 'Size ↑');
    assert.deepEqual(seriesCalls(page).at(-1).query, { offset: '0', limit: '60', sort: 'size' });

    page.click('#headerSortMenu [data-sort="name"]');
    await page.settle();
    assert.equal(page.$('#headerSortLabel').textContent, 'Name ↑');
    assert.deepEqual(seriesCalls(page).at(-1).query, { offset: '0', limit: '60' });

    assert.deepEqual(savedPreferences(page), [
        { sortMode: 'date', sortDirection: 'asc' },
        { sortMode: 'date', sortDirection: 'desc' },
        { sortMode: 'date', sortDirection: 'asc' },
        { sortMode: 'size', sortDirection: 'asc' },
        { sortMode: 'name', sortDirection: 'asc' }
    ], 'the direction is saved with the field');
});

test('an unknown sort mode falls back to the "Name" label', async () => {
    const page = await bootLibrary({ series: makeSeriesList(1) });

    page.run("setSort('rating')");
    await page.settle();

    assert.equal(page.$('#headerSortLabel').textContent, 'Name ↑');
    assert.deepEqual(activeItems(page, '#headerSortMenu'), []);
    assert.equal(seriesCalls(page).at(-1).query.sort, 'rating');
});

test('opening one header menu closes the other', async () => {
    const page = await bootLibrary({ series: makeSeriesList(1) });
    const filterMenu = page.$('#headerFilterMenu');
    const sortMenu = page.$('#headerSortMenu');

    page.click('#headerFilterToggle');
    page.click('#headerSortToggle');
    assert.ok(sortMenu.classList.contains('show'));
    assert.ok(!filterMenu.classList.contains('show'));

    // toggleFilterDropdown() is the legacy name of the filter toggle.
    let stopped = 0;
    page.window.toggleFilterDropdown({ stopPropagation: () => { stopped++; } });
    assert.equal(stopped, 1);
    assert.ok(filterMenu.classList.contains('show'));
    assert.ok(!sortMenu.classList.contains('show'));

    // Toggling again closes it.
    page.click('#headerFilterToggle');
    assert.ok(!filterMenu.classList.contains('show'));
});

test('the settings menu opens from its toggle and closes on request', async () => {
    const page = await bootLibrary({ series: makeSeriesList(1) });
    const toggle = page.$('#settingsMenuToggle');
    const menu = page.$('#settingsDropdownMenu');

    page.click(toggle);
    await page.settle(1);
    assert.ok(menu.classList.contains('show'));
    assert.equal(toggle.getAttribute('aria-expanded'), 'true');

    page.run('closeSettingsMenu()');
    await page.settle(1);
    assert.ok(!menu.classList.contains('show'));
    assert.equal(toggle.getAttribute('aria-expanded'), 'false');
});

test('aria-expanded sync skips missing menus and works without MutationObserver', async () => {
    const page = await bootLibrary({ series: makeSeriesList(1) });
    page.$('#settingsMenuToggle').remove();
    page.$('#headerSortMenu').classList.add('show');
    page.run('window.MutationObserver = undefined');

    page.run('initDropdownAriaExpandedSync()');

    // Synced once, immediately.
    assert.equal(page.$('#headerSortToggle').getAttribute('aria-expanded'), 'true');
    assert.equal(page.$('#headerFilterToggle').getAttribute('aria-expanded'), 'false');
});

test('typing in the search box reloads once the typing pauses', async t => {
    const page = await bootLibrary({ series: [makeSeries(1, { title: 'Batman' }), makeSeries(2, { title: 'Saga' })] });
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const before = seriesCalls(page).length;

    page.input('#headerSearchInput', 'bat');
    assert.equal(page.$('#headerSearchClear').hidden, false);
    t.mock.timers.tick(299);
    page.input('#headerSearchInput', 'batm');
    t.mock.timers.tick(299);
    await page.settle();
    assert.equal(seriesCalls(page).length, before);

    t.mock.timers.tick(1);
    await page.settle();
    assert.equal(seriesCalls(page).length, before + 1);
    assert.deepEqual(seriesCalls(page).at(-1).query, { offset: '0', limit: '60', search: 'batm' });
});

test('clearing the search reloads the full list immediately', async t => {
    const page = await bootLibrary({ series: makeSeriesList(2) });
    t.mock.timers.enable({ apis: ['setTimeout'] });

    page.input('#headerSearchInput', 'saga');
    t.mock.timers.tick(300);
    await page.settle();
    assert.equal(seriesCalls(page).at(-1).query.search, 'saga');

    page.click('#headerSearchClear');
    await page.settle();

    assert.deepEqual(seriesCalls(page).at(-1).query, { offset: '0', limit: '60' });
    assert.equal(page.$('#headerSearchInput').value, '');
    assert.equal(page.$('#headerSearchClear').hidden, true);
    assert.equal(page.document.activeElement, page.$('#headerSearchInput'));
});

test('on a phone the filter applies to the series list, which stays in series mode', async () => {
    const page = await bootLibrary({ series: makeSeriesList(2), media: { [MOBILE_QUERY]: true } });

    page.click('#headerFilterMenu [data-filter="missing"]');
    await page.settle();

    assert.equal(page.evaluate('libraryViewMode'), 'series');
    assert.equal(seriesCalls(page).at(-1).query.filter, 'missing');
    // Only the filter is saved, and no view change is announced.
    assert.deepEqual(savedPreferences(page), [{ filterMode: 'missing' }]);
    assert.equal(page.$('#mobileLibraryViewStatus').textContent, '');
    assert.deepEqual(renderedTitles(page), ['Series 001', 'Series 002']);
});

// ── With a series page open ───────────────────────────────────────────────

const ISSUES = [
    { file_path: 'Series 001/Series 001 #001.cbz', file_name: 'Series 001 #001.cbz', issue: '1' },
    { file_path: 'Series 001/Series 001 #002.cbz', file_name: 'Series 001 #002.cbz', issue: '2' }
];

/** Boot onto Series 001's page; `issues` answers its issue requests, other options go to bootLibrary(). */
async function bootSeriesPage({ issues = { issues: ISSUES, issue_count: 2 }, ...options } = {}) {
    const page = await bootLibrary({
        hash: '#/series/series-1',
        series: makeSeriesList(2),
        routes: { 'GET /api/files/series/series-1/issues': issues },
        ...options
    });
    assert.ok(page.$('#seriesDetailPanel .series-issues-grid'), 'the series page is open');
    return page;
}

const issueCalls = page => callsTo(page, '/api/files/series/series-1/issues');

test('a filter change on a series page re-fetches its issues and refreshes the library behind it', async () => {
    const page = await bootSeriesPage();
    const issuesBefore = issueCalls(page).length;

    page.click('#headerFilterMenu [data-filter="unread"]');
    await page.settle();

    assert.equal(issueCalls(page).length, issuesBefore + 1);
    assert.equal(issueCalls(page).at(-1).query.filter, 'unread');
    assert.deepEqual(seriesCalls(page).at(-1).query, { offset: '0', limit: '60', refresh: 'true', filter: 'unread' });
    // The user stays on the series page.
    assert.equal(page.window.location.hash, '#/series/series-1');
    assert.ok(page.$('#seriesDetailPanel .series-issues-grid'));
    assert.equal(page.$('#fileList .series-grid--compact'), null);
});

test('on a phone a filter change, or "Clear filter", on a series page also keeps the user on it', async () => {
    // Phones used to also switch to the retired Files view here, which closed
    // the series before its issues were re-fetched and announced the switch.
    const page = await bootSeriesPage({
        media: { [MOBILE_QUERY]: true },
        // Every filter hides both issues, so the page offers "Clear filter".
        issues: call => (call.query.filter ? { issues: [], issue_count: 0 } : { issues: ISSUES, issue_count: 2 })
    });
    const issuesBefore = issueCalls(page).length;

    page.click('#headerFilterMenu [data-filter="unread"]');
    await page.settle();

    assert.equal(issueCalls(page).length, issuesBefore + 1);
    assert.equal(issueCalls(page).at(-1).query.filter, 'unread');
    assert.deepEqual(seriesCalls(page).at(-1).query, { offset: '0', limit: '60', refresh: 'true', filter: 'unread' });
    assert.equal(page.window.location.hash, '#/series/series-1');
    assert.equal(page.evaluate('currentSeriesDetailId'), 'series-1');
    assert.deepEqual(renderedTitles(page), [], 'the library list does not replace the series page');
    const clearFilter = page.$('#seriesDetailPanel .empty-state button');
    assert.equal(clearFilter.textContent, 'Clear filter');

    page.click(clearFilter);
    await page.settle();

    assert.equal(issueCalls(page).length, issuesBefore + 2);
    assert.equal(issueCalls(page).at(-1).query.filter, undefined);
    assert.equal(page.$$('#seriesDetailPanel .series-issues-grid > .series-issue-card').length, 2);
    assert.equal(page.evaluate('currentSeriesDetailId'), 'series-1');
    // Only the filter is saved, and no view change is announced.
    assert.deepEqual(savedPreferences(page), [{ filterMode: 'unread' }, { filterMode: 'all' }]);
    assert.equal(page.$('#mobileLibraryViewStatus').textContent, '');
});

test('a search on a series page re-fetches its issues and refreshes the library behind it', async () => {
    const page = await bootSeriesPage();
    const issuesBefore = issueCalls(page).length;

    page.run("document.getElementById('headerSearchInput').value = 'two'; filterFiles()");
    // The issues are shown as loading until the new list arrives.
    assert.equal(page.$('#seriesDetailPanel .loading p').textContent, 'Loading issues...');
    await page.settle();

    assert.equal(issueCalls(page).length, issuesBefore + 1);
    assert.deepEqual(seriesCalls(page).at(-1).query, { offset: '0', limit: '60', refresh: 'true', search: 'two' });
    assert.ok(page.$('#seriesDetailPanel .series-issues-grid'));
});

test('a sort change on a series page only refreshes the library behind it', async () => {
    const page = await bootSeriesPage();
    const issuesBefore = issueCalls(page).length;

    page.click('#headerSortMenu [data-sort="date"]');
    await page.settle();

    assert.equal(issueCalls(page).length, issuesBefore);
    assert.deepEqual(seriesCalls(page).at(-1).query, { offset: '0', limit: '60', refresh: 'true', sort: 'date' });
    assert.ok(page.$('#seriesDetailPanel .series-issues-grid'));
    assert.deepEqual(plain(page.evaluate('currentSeriesDetailId')), 'series-1');
});
