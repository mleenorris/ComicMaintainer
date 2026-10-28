'use strict';

// Loading the series library (js/app/library.js loadSeriesLibrary and the
// infinite-scroll observer from js/app/file-list.js): requests, rendering,
// paging, reload/abort handling and error states.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const {
    makeSeries,
    makeSeriesList,
    seriesPages,
    bootLibrary,
    callsTo,
    seriesCalls,
    messages,
    imageObserver,
    scrollObserver,
    renderedTitles
} = require('./support/library-helpers');

test('the library opens on the first page of series, rendered as the compact grid', async () => {
    const page = await bootLibrary({ series: makeSeriesList(3) });

    const [first] = seriesCalls(page);
    assert.deepEqual(first.query, { offset: '0', limit: '60' });
    assert.match(first.headers.authorization, /^Bearer /);

    const cards = page.$$('#fileList .series-grid--compact > .series-card');
    assert.equal(cards.length, 3);
    assert.deepEqual(renderedTitles(page), ['Series 001', 'Series 002', 'Series 003']);
    assert.equal(cards[0].getAttribute('aria-label'), 'Open series Series 001');
    assert.equal(cards[0].querySelector('.series-count-badge').textContent, '3');
    assert.equal(cards[0].querySelector('.series-meta').textContent, '3.0 MB');

    const cover = cards[0].querySelector('img.series-cover');
    assert.equal(cover.dataset.protectedImage, 'Series 001/Series 001 #001.cbz');
    assert.equal(cover.dataset.protectedImageFallback, '');
    // Covers load lazily: they are only observed until they near the viewport.
    assert.ok(imageObserver(page).targets.has(cover));
    assert.equal(cover.getAttribute('src'), null);

    assert.equal(page.$('#seriesSelectionCount').textContent.trim(), 'No series selected');
    assert.equal(page.$('#seriesSelectAll').checked, false);
    assert.equal(page.$('#seriesSelectionClearBtn').hidden, true);
    assert.equal(page.$('#seriesSelectionEmailBtn').hidden, true);

    // The infinite-scroll sentinel sits at the end of the list, observed
    // against the list itself.
    const sentinel = page.$('#libraryScrollSentinel');
    assert.equal(page.$('#fileList').lastElementChild, sentinel);
    const observer = scrollObserver(page);
    assert.equal(observer.options.rootMargin, '400px');
    assert.ok(observer.targets.has(sentinel));

    assert.equal(page.$('#seriesLayoutToggle').hidden, false);
    assert.ok(page.$('#seriesLayoutCompactBtn').classList.contains('active'));
    assert.deepEqual(page.errors, []);
});

test('a loading state is shown until the first page arrives', async () => {
    const pending = deferred();
    const page = await bootLibrary({ routes: { 'GET /api/files/series': () => pending.promise } });

    assert.equal(page.$('#fileList .loading p').textContent, 'Loading series...');

    pending.resolve(seriesPages(makeSeriesList(2))({ query: {} }));
    await page.settle();
    assert.deepEqual(renderedTitles(page), ['Series 001', 'Series 002']);
});

test('an empty library explains how to build it', async () => {
    const page = await bootLibrary({ series: [] });

    assert.equal(page.$('#fileList .empty-state h2').textContent, 'No series found');
    assert.equal(page.$('#fileList .empty-state p').textContent, 'Process and normalize comics to build your series library view.');
    assert.equal(page.$('#seriesSelectAll'), null);
});

test('an answer without a series list counts as an empty library', async () => {
    const page = await bootLibrary({ routes: { 'GET /api/files/series': {} } });

    assert.equal(page.$('#fileList .empty-state h2').textContent, 'No series found');
    assert.deepEqual(messages(page).filter(m => m.type === 'error'), []);
});

test('an empty result for a search suggests a different search or filter', async () => {
    const page = await bootLibrary({ series: [] });

    page.run("document.getElementById('headerSearchInput').value = 'zzz'; filterFiles()");
    await page.settle();

    assert.equal(seriesCalls(page).at(-1).query.search, 'zzz');
    assert.equal(page.$('#fileList .empty-state p').textContent, 'Try a different search term or filter');
});

test('a server error is reported with the failed request\'s reference', async () => {
    const page = await bootLibrary({
        routes: { 'GET /api/files/series': respond(500, { error: 'boom' }, { 'X-Correlation-Id': 'corr-42' }) }
    });

    assert.deepEqual(messages(page).filter(m => m.type === 'error'), [
        { type: 'error', text: 'Failed to load series: HTTP error! status: 500 (ref: corr-42)' }
    ]);
    assert.equal(page.$('#fileList .series-card'), null);
});

test('a network failure is reported', async () => {
    const page = await bootLibrary({
        routes: { 'GET /api/files/series': () => { throw new TypeError('Failed to fetch'); } }
    });

    assert.deepEqual(messages(page).filter(m => m.type === 'error').map(m => m.text), ['Failed to load series: Failed to fetch']);
});

test('a failure without a reference is not reported with an earlier request\'s', async () => {
    // The library counts fail with a reference while the library opens...
    const page = await bootLibrary({
        series: makeSeriesList(2),
        routes: { 'GET /api/files/counts': respond(500, { error: 'boom' }, { 'X-Correlation-Id': 'corr-counts' }) }
    });
    assert.equal(callsTo(page, '/api/files/counts').length, 1);

    // ...then changing the filter fails offline, or on a proxy's error page.
    for (const [filter, failure, text] of [
        ['unread', () => { throw new TypeError('Failed to fetch'); }, 'Failed to load series: Failed to fetch'],
        ['duplicates', respond(502, 'Bad Gateway'), 'Failed to load series: HTTP error! status: 502']
    ]) {
        page.$('#messageContainer').replaceChildren();
        page.route('GET /api/files/series', failure);
        page.click(`#headerFilterMenu [data-filter="${filter}"]`);
        await page.settle();

        assert.equal(seriesCalls(page).at(-1).query.filter, filter);
        assert.deepEqual(messages(page).filter(m => m.type === 'error').map(m => m.text), [text]);
    }
});

test('an expired session sends the user to the login page', async () => {
    const page = await bootLibrary({ routes: { 'GET /api/files/series': 401 } });

    assert.ok(page.navigations.some(n => n.url === 'http://localhost/login.html'));
    assert.equal(page.window.localStorage.getItem('jwt_token'), null);
    assert.deepEqual(messages(page).filter(m => m.type === 'error'), []);
});

test('scrolling to the end appends the next page at the current offset until every series is loaded', async () => {
    const page = await bootLibrary({ series: makeSeriesList(130) });
    const firstCard = page.$('#fileList .series-card');
    assert.equal(page.$$('#fileList .series-card').length, 60);

    // Not intersecting: nothing to load.
    scrollObserver(page).trigger(false);
    await page.settle();
    assert.equal(seriesCalls(page).length, 1);

    scrollObserver(page).trigger(true);
    await page.settle();
    assert.deepEqual(seriesCalls(page).at(-1).query, { offset: '60', limit: '60' });
    const cards = page.$$('#fileList .series-card');
    assert.equal(cards.length, 120);
    // Appended, not re-rendered: the cards already shown are kept.
    assert.equal(cards[0], firstCard);
    assert.equal(renderedTitles(page)[119], 'Series 120');
    assert.ok(imageObserver(page).targets.has(cards[119].querySelector('img')));
    assert.equal(page.$('#fileList').lastElementChild.id, 'libraryScrollSentinel');

    scrollObserver(page).trigger(true);
    await page.settle();
    assert.deepEqual(seriesCalls(page).at(-1).query, { offset: '120', limit: '60' });
    assert.equal(page.$$('#fileList .series-card').length, 130);

    // Everything is loaded: reaching the end again requests nothing.
    scrollObserver(page).trigger(true);
    await page.settle();
    assert.equal(seriesCalls(page).length, 3);
    assert.deepEqual(page.errors, []);
});

test('an appended page renders in the list layout too', async () => {
    const page = await bootLibrary({ series: makeSeriesList(70), localStorage: { seriesLayout: 'list' } });
    assert.equal(page.$$('#fileList .series-list > .series-list-row').length, 60);

    scrollObserver(page).trigger(true);
    await page.settle();

    assert.equal(page.$$('#fileList .series-list > .series-list-row').length, 70);
    assert.equal(page.$$('#fileList .series-list').length, 1);
});

test('an appended page renders in the grid layout too', async () => {
    const page = await bootLibrary({ series: makeSeriesList(70), localStorage: { seriesLayout: 'grid' } });

    scrollObserver(page).trigger(true);
    await page.settle();

    assert.equal(page.$$('#fileList .series-grid--titled > .series-card--titled').length, 70);
});

test('an empty page while scrolling leaves the list as it is', async () => {
    // The server's total can run ahead of what it returns (e.g. series removed
    // between requests): the append adds nothing and keeps the rendered list.
    const all = makeSeriesList(60);
    const page = await bootLibrary({ routes: { 'GET /api/files/series': seriesPages(all, { total_series: 61 }) } });
    const before = page.$$('#fileList .series-card');

    scrollObserver(page).trigger(true);
    await page.settle();

    assert.deepEqual(seriesCalls(page).at(-1).query, { offset: '60', limit: '60' });
    assert.deepEqual(page.$$('#fileList .series-card'), before);
});

test('a page appended after an empty first page renders the list from scratch', async () => {
    let calls = 0;
    const all = makeSeriesList(2);
    const page = await bootLibrary({
        routes: {
            // The first answer is empty although the total says there is more.
            'GET /api/files/series': call => (calls++ === 0
                ? { series: [], total_series: 2, unmarked_count: 0 }
                : seriesPages(all)(call))
        }
    });
    assert.equal(page.$('#fileList .empty-state h2').textContent, 'No series found');

    scrollObserver(page).trigger(true);
    await page.settle();

    assert.deepEqual(seriesCalls(page).at(-1).query, { offset: '0', limit: '60' });
    assert.deepEqual(renderedTitles(page), ['Series 001', 'Series 002']);
    assert.equal(page.$('#seriesSelectionCount').textContent.trim(), 'No series selected');
});

test('a refresh reloads from the start with refresh=true and refreshes the library counts', async () => {
    const page = await bootLibrary({ series: makeSeriesList(70) });
    scrollObserver(page).trigger(true);
    await page.settle();
    const countsBefore = callsTo(page, '/api/files/counts').length;

    await page.run('loadActiveLibraryView(1, true)');
    await page.settle();

    assert.deepEqual(seriesCalls(page).at(-1).query, { offset: '0', limit: '60', refresh: 'true' });
    assert.equal(page.$$('#fileList .series-card').length, 60);
    assert.equal(callsTo(page, '/api/files/counts').length, countsBefore + 1);
});

test('legacy (page, refresh) and boolean arguments still control the refresh flag', async () => {
    const page = await bootLibrary({ series: makeSeriesList(2) });

    await page.run('loadSeriesLibrary(3, true)');
    assert.equal(seriesCalls(page).at(-1).query.refresh, 'true');
    assert.equal(seriesCalls(page).at(-1).query.offset, '0');

    await page.run('loadSeriesLibrary(2)');
    assert.equal(seriesCalls(page).at(-1).query.refresh, undefined);

    await page.run('loadSeriesLibrary(true)');
    assert.equal(seriesCalls(page).at(-1).query.refresh, 'true');

    await page.run('loadSeriesLibrary()');
    assert.equal(seriesCalls(page).at(-1).query.refresh, undefined);
    assert.deepEqual(renderedTitles(page), ['Series 001', 'Series 002']);
});

test('a reload requested while a page is loading aborts it and fetches the latest query', async () => {
    const stale = deferred();
    const all = makeSeriesList(3);
    const page = await bootLibrary({ series: all });

    page.route('GET /api/files/series', call => (call.query.filter === 'unread'
        ? seriesPages([all[1]])(call)
        : stale.promise));
    page.run('loadSeriesLibrary()');
    await page.settle();
    const inFlight = seriesCalls(page).at(-1);
    assert.equal(page.$('#fileList .loading p').textContent, 'Loading series...');

    page.click('#headerFilterMenu [data-filter="unread"]');
    await page.settle();

    assert.equal(inFlight.init.signal.aborted, true);
    assert.equal(seriesCalls(page).at(-1).query.filter, 'unread');
    assert.deepEqual(renderedTitles(page), ['Series 002']);

    // The superseded response is discarded even if it arrives later.
    stale.resolve(seriesPages(all)(inFlight));
    await page.settle();
    assert.deepEqual(renderedTitles(page), ['Series 002']);
    // An aborted request is not an error worth telling the user about.
    assert.deepEqual(messages(page).filter(m => m.type === 'error'), []);
});

test('without AbortController a reload waits for the load in flight, then fetches the latest query', async () => {
    const inFlightAnswer = deferred();
    const all = makeSeriesList(3);
    const page = await bootLibrary({ series: all });
    page.run('window.AbortController = undefined');

    page.route('GET /api/files/series', call => (call.query.filter === 'unread'
        ? seriesPages([all[1]])(call)
        : inFlightAnswer.promise));
    page.run('loadSeriesLibrary()');
    await page.settle();
    const inFlight = seriesCalls(page).at(-1);
    assert.equal(inFlight.init.signal, undefined);

    page.click('#headerFilterMenu [data-filter="unread"]');
    await page.settle();
    assert.equal(seriesCalls(page).at(-1), inFlight, 'the reload is not requested while the load runs');

    inFlightAnswer.resolve(seriesPages(all)(inFlight));
    await page.settle();
    assert.equal(seriesCalls(page).at(-1).query.filter, 'unread');
    assert.deepEqual(renderedTitles(page), ['Series 002']);
    assert.deepEqual(messages(page).filter(m => m.type === 'error'), []);
});

test('a scroll-triggered append is dropped while another load is in flight', async () => {
    const pending = deferred();
    const page = await bootLibrary({ series: makeSeriesList(2) });
    page.route('GET /api/files/series', () => pending.promise);

    page.run('loadSeriesLibrary()');
    await page.settle();
    const callsDuringLoad = seriesCalls(page).length;
    page.run('loadSeriesLibrary({ append: true })');
    await page.settle();
    assert.equal(seriesCalls(page).length, callsDuringLoad);

    pending.resolve({ series: [makeSeries(9)], total_series: 1, unmarked_count: 0 });
    await page.settle();
    // No reload was queued behind the dropped append.
    assert.equal(seriesCalls(page).length, callsDuringLoad);
    assert.deepEqual(renderedTitles(page), ['Series 009']);
});

test('saved filter and sort preferences are restored and the list reloaded with them', async () => {
    const page = await bootLibrary({
        series: makeSeriesList(2),
        prefs: { perPage: 100, filterMode: 'duplicates', sortMode: 'date' }
    });

    assert.equal(page.$('#headerFilterLabel').textContent, 'Duplicates');
    assert.ok(page.$('#headerFilterMenu [data-filter="duplicates"]').classList.contains('active'));
    assert.equal(page.$('#headerSortLabel').textContent, 'Date ↑');
    assert.ok(page.$('#headerSortMenu [data-sort="date"]').classList.contains('active'));
    assert.ok(!page.$('#headerSortMenu [data-sort="name"]').classList.contains('active'));
    const last = seriesCalls(page).at(-1).query;
    assert.equal(last.filter, 'duplicates');
    assert.equal(last.sort, 'date');
    assert.equal(last.direction, undefined);
    assert.deepEqual(renderedTitles(page), ['Series 001', 'Series 002']);
});

test('the "… Unmarked" actions show only while the library has unmarked files', async () => {
    let unmarked = 0;
    const all = makeSeriesList(1);
    const page = await bootLibrary({
        routes: { 'GET /api/files/series': call => seriesPages(all, { unmarked_count: unmarked })(call) }
    });
    const items = ['#processUnmarkedItem', '#renameUnmarkedItem', '#normalizeUnmarkedItem'];
    const displays = () => items.map(selector => page.$(selector).style.display);
    assert.deepEqual(items.map(selector => page.$(selector).textContent.trim()),
        ['Process Unmarked', 'Rename Unmarked', 'Normalize Unmarked']);

    await page.run('loadSeriesLibrary()');
    assert.deepEqual(displays(), ['none', 'none', 'none']);
    assert.equal(page.$('#processDropdown .action-dropdown-item').style.display, '', '"Process All" stays');

    unmarked = 4;
    await page.run('loadSeriesLibrary()');
    assert.deepEqual(displays(), ['', '', '']);
    assert.equal(page.evaluate('unmarkedCount'), 4);
    assert.equal(page.$('.controls-wrapper').style.display, '');

    // Without one of them, the others still follow the count.
    page.$('#renameUnmarkedItem').remove();
    unmarked = 0;
    await page.run('loadSeriesLibrary()');
    assert.deepEqual(['#processUnmarkedItem', '#normalizeUnmarkedItem'].map(selector => page.$(selector).style.display),
        ['none', 'none']);
});
