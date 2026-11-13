'use strict';

// Refreshing the series library in the background (js/app/library.js
// refreshActiveLibraryView and loadSeriesLibrary's quiet mode): a file or
// job change refreshes the series already loaded in place, with no spinner
// and the scroll position kept, and waits while the library is off screen.
// Also: Back from a series returns to the same place in the library.

const test = require('node:test');
const assert = require('node:assert/strict');
const { deferred } = require('./support/app');
const { serverEvent } = require('./support/core-helpers');
const {
    makeSeries,
    makeSeriesList,
    seriesPages,
    bootLibrary,
    seriesCalls,
    messages,
    scrollObserver,
    renderedTitles
} = require('./support/library-helpers');

// handleFileListUpdatedEvent's debounce (events.js FILE_LIST_REFRESH_DEBOUNCE_DELAY).
const FILE_LIST_DEBOUNCE = 500;

async function bootWith120Loaded(t, options = {}) {
    const all = makeSeriesList(130);
    const page = await bootLibrary({ series: all, ...options });
    scrollObserver(page).trigger(true);
    await page.settle();
    assert.equal(page.$$('#fileList .series-card').length, 120);
    t.mock.timers.enable({ apis: ['setTimeout'] });
    return { page, all };
}

async function fileListUpdated(t, page) {
    serverEvent(page, 'file_list_updated', {});
    t.mock.timers.tick(FILE_LIST_DEBOUNCE);
    await page.settle(3);
}

test('a file change refreshes every loaded series in place, leaving unchanged cards alone', async t => {
    const { page } = await bootWith120Loaded(t);
    const fileList = page.$('#fileList');
    const firstCard = page.$('#fileList .series-card');
    fileList.scrollTop = 2400;
    const before = seriesCalls(page).length;

    await fileListUpdated(t, page);

    const calls = seriesCalls(page).slice(before);
    assert.equal(calls.length, 1);
    // The whole loaded list, not the first page, so nothing below is lost.
    assert.deepEqual(calls[0].query, { offset: '0', limit: '120' });
    // Nothing changed: the cards on screen are the same elements.
    assert.equal(page.$('#fileList .series-card'), firstCard);
    assert.equal(page.$$('#fileList .series-card').length, 120);
    assert.equal(fileList.scrollTop, 2400);
    assert.deepEqual(page.errors, []);
});

test('while a background refresh is in flight the series stay on screen, with no spinner', async t => {
    const { page, all } = await bootWith120Loaded(t);
    const pending = deferred();
    page.route('GET /api/files/series', () => pending.promise);

    serverEvent(page, 'file_list_updated', {});
    t.mock.timers.tick(FILE_LIST_DEBOUNCE);
    await page.settle(2);

    assert.equal(page.$('#fileList .loading'), null);
    assert.equal(page.$$('#fileList .series-card').length, 120);

    pending.resolve(seriesPages(all)({ query: { offset: '0', limit: '120' } }));
    await page.settle(3);
    assert.equal(page.$$('#fileList .series-card').length, 120);
});

test('a changed list is redrawn where the user was', async t => {
    const { page, all } = await bootWith120Loaded(t);
    const fileList = page.$('#fileList');
    fileList.scrollTop = 1800;
    page.window.scrollY = 300;
    const renamed = all.map(series => (series.id === 'series-2' ? makeSeries(2, { title: 'Series 002 (renamed)' }) : series));
    page.route('GET /api/files/series', seriesPages(renamed));

    await fileListUpdated(t, page);

    assert.equal(renderedTitles(page)[1], 'Series 002 (renamed)');
    assert.equal(page.$$('#fileList .series-card').length, 120);
    assert.equal(fileList.scrollTop, 1800);
    assert.deepEqual(page.scrolls.at(-1), { x: 0, y: 300 });
});

test('a failed background refresh keeps the series on screen', async t => {
    const { page } = await bootWith120Loaded(t);
    const firstCard = page.$('#fileList .series-card');
    page.route('GET /api/files/series', 500);

    await fileListUpdated(t, page);

    assert.equal(page.$('#fileList .series-card'), firstCard);
    assert.equal(page.$$('#fileList .series-card').length, 120);
    assert.match(messages(page).at(-1).text, /^Failed to load series/);
});

test('a finished job refreshes the library health and filters too', async t => {
    const { page } = await bootWith120Loaded(t);
    const counts = () => page.fetchCalls.filter(call => call.path === '/api/files/counts').length;
    const facets = () => page.fetchCalls.filter(call => call.path === '/api/files/series/facets').length;
    const countsBefore = counts();
    const facetsBefore = facets();
    const before = seriesCalls(page).length;

    await page.run('refreshActiveLibraryView()');
    await page.settle(3);

    const [call] = seriesCalls(page).slice(before);
    assert.deepEqual(call.query, { offset: '0', limit: '120', refresh: 'true' });
    assert.equal(page.$('#fileList .loading'), null);
    assert.ok(counts() > countsBefore);
    assert.ok(facets() > facetsBefore);
});

test('while the library is off screen a change waits until the library is shown again', async t => {
    const page = await bootLibrary({ series: makeSeriesList(3), hash: '#/home' });
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const before = seriesCalls(page).length;

    await fileListUpdated(t, page);
    await page.run('refreshActiveLibraryView()');
    await page.settle(2);
    assert.equal(seriesCalls(page).length, before, 'nothing is fetched for the overview');

    page.run("navigate('#/library')");
    await page.settle(3);
    const calls = seriesCalls(page).slice(before);
    assert.equal(calls.length, 1, 'one refresh for everything that happened meanwhile');
    assert.equal(calls[0].query.limit, '60');
    assert.equal(page.$$('#fileList .series-card').length, 3);

    // Shown again with nothing new: no refresh.
    page.run("navigate('#/home')");
    await page.settle(2);
    page.run("navigate('#/library')");
    await page.settle(2);
    assert.equal(seriesCalls(page).length, before + 1);
});

test('a library that was never loaded loads normally when shown, once', async t => {
    const page = await bootLibrary({ series: makeSeriesList(3), hash: '#/home', routes: { 'GET /api/files/series': () => ({ series: [], total_series: 0 }) } });
    t.mock.timers.enable({ apis: ['setTimeout'] });
    page.route('GET /api/files/series', seriesPages(makeSeriesList(3)));
    await fileListUpdated(t, page);
    const before = seriesCalls(page).length;

    page.run("navigate('#/library')");
    await page.settle(3);

    assert.equal(seriesCalls(page).length, before + 1);
    assert.equal(page.$$('#fileList .series-card').length, 3);
    assert.equal(page.evaluate('libraryRefreshDeferred'), false);
});

test('a background refresh asked for while the list is loading follows it, still quietly', async t => {
    const { page, all } = await bootWith120Loaded(t);
    const pending = deferred();
    page.route('GET /api/files/series', () => pending.promise);
    page.run('loadSeriesLibrary({ keepLoaded: true, quiet: true })');
    await page.settle(1);

    // Queued behind the one in flight (which it aborts).
    page.route('GET /api/files/series', seriesPages(all));
    page.run('refreshActiveLibraryView({ refresh: false })');
    pending.resolve(seriesPages(all)({ query: { offset: '0', limit: '120' } }));
    await page.settle(4);

    assert.equal(page.$('#fileList .loading'), null);
    assert.equal(page.$$('#fileList .series-card').length, 120);
    assert.equal(page.evaluate('seriesReloadQuiet'), true);
});

test('a reload asked for while a quiet one is loading shows the spinner, as it did', async t => {
    const { page, all } = await bootWith120Loaded(t);
    const pending = deferred();
    page.route('GET /api/files/series', () => pending.promise);
    page.run('refreshActiveLibraryView({ refresh: false })');
    await page.settle(1);

    const reload = deferred();
    page.route('GET /api/files/series', () => reload.promise);
    page.run('loadActiveLibraryView()');
    pending.resolve(seriesPages(all)({ query: { offset: '0', limit: '120' } }));
    await page.settle(3);

    assert.ok(page.$('#fileList .loading'), 'a filter change, say, starts the list over');
    reload.resolve(seriesPages(all)({ query: { offset: '0', limit: '60' } }));
    await page.settle(3);
    assert.equal(page.$$('#fileList .series-card').length, 60);
});

test('Back from a series returns to the same place in the library', async () => {
    const page = await bootLibrary({ series: makeSeriesList(70), routes: { 'GET /api/files/series/series-5/issues': { issues: [], total: 0 } } });
    const fileList = page.$('#fileList');
    fileList.scrollTop = 960;
    page.window.scrollY = 120;

    page.run("openSeriesDetail('series-5')");
    await page.settle(3);
    fileList.scrollTop = 0;
    page.run('closeSeriesDetail()');
    await page.settle(3);

    assert.equal(page.$$('#fileList .series-card').length, 60);
    assert.equal(fileList.scrollTop, 960);
    assert.deepEqual(page.scrolls.at(-1), { x: 0, y: 120 });
    assert.equal(page.evaluate('libraryScrollBeforeSeries'), null);
});

test('only a direct return from the series goes back to the old place', async () => {
    const page = await bootLibrary({ series: makeSeriesList(5), routes: { 'GET /api/files/series/series-2/issues': { issues: [], total: 0 } } });
    page.$('#fileList').scrollTop = 500;

    page.run("openSeriesDetail('series-2')");
    await page.settle(3);
    assert.deepEqual(page.evaluate('libraryScrollBeforeSeries'), { listTop: 500, pageTop: 0 });
    page.run("navigate('#/settings')");
    await page.settle(2);

    assert.equal(page.evaluate('libraryScrollBeforeSeries'), null);
});

test('a series opened from the overview leaves the library position alone', async () => {
    const page = await bootLibrary({ series: makeSeriesList(5), hash: '#/home', routes: { 'GET /api/files/series/series-2/issues': { issues: [], total: 0 } } });

    page.run("openSeriesDetail('series-2')");
    await page.settle(3);

    assert.equal(page.evaluate('libraryScrollBeforeSeries'), null);
});
