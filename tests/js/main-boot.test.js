'use strict';

// js/main.js — the bootstrap: saved preferences applied once they arrive,
// background boot tasks that fail, main.js running after the document has
// loaded, viewport resizes and phone rotation.

const test = require('node:test');
const assert = require('node:assert/strict');
const { runWwwrootScript } = require('./support/sandbox');
const { deferred } = require('./support/app');
const { boot, loadApp, requests, consoleLines, preferences, press, seriesCard, seriesIssue, seriesIssues, seriesPage } = require('./support/core-helpers');

const SAGA = seriesCard();
const PORTRAIT = '(max-width: 600px) and (orientation: portrait)';
const MOBILE = '(max-width: 768px)';

const libraryRoutes = (overrides = {}) => ({
    'GET /api/files/series': seriesPage([SAGA]),
    [`GET /api/files/series/${SAGA.id}/issues`]: seriesIssues(SAGA),
    ...overrides
});
const activeItems = (page, menu) => page.$$(`#${menu} .header-dropdown-item.active`).map(item => item.dataset.filter || item.dataset.sort);
const seriesListLoads = page => requests(page, 'GET', '/api/files/series');

test('a visit without a route lands on the overview', async () => {
    for (const hash of ['', '#']) {
        const page = await boot({ hash });
        assert.equal(page.window.location.hash, '#/home', JSON.stringify(hash));
        assert.equal(page.evaluate('currentView'), 'home');
        assert.equal(requests(page, 'GET', '/api/overview').length, 1);
    }
});

test('saved page size, filter and sort are applied when the preferences arrive, and the library reloads', async () => {
    const page = await boot({
        hash: '#/library',
        routes: libraryRoutes({ 'GET /api/preferences': preferences({ perPage: 50, filterMode: 'unmarked', sortMode: 'date' }) }),
        beforeScripts(window) {
            // index.html no longer has a page-size selector; a page that does is kept in sync.
            const select = window.document.createElement('select');
            select.id = 'perPageSelect';
            for (const size of [50, 100, 200]) select.add(new window.Option(String(size), String(size), size === 100, size === 100));
            window.document.body.appendChild(select);
        }
    });

    assert.equal(page.evaluate('perPage'), 50);
    assert.equal(page.$('#perPageSelect').value, '50');
    assert.equal(page.$('#headerFilterLabel').textContent, 'Unmarked');
    assert.deepEqual(activeItems(page, 'headerFilterMenu'), ['unmarked']);
    assert.equal(page.$('#headerSortLabel').textContent, 'Date ↑');
    assert.deepEqual(activeItems(page, 'headerSortMenu'), ['date']);
    assert.equal(page.evaluate('libraryViewMode'), 'series');

    const last = seriesListLoads(page).at(-1);
    assert.equal(last.query.filter, 'unmarked');
    assert.equal(last.query.sort, 'date');
    assert.ok(page.$('#fileList .series-card'), 'the reloaded library is shown');
});

test('the library reloads for a saved page size alone, but not for the defaults', async () => {
    const custom = await boot({ hash: '#/library', routes: libraryRoutes({ 'GET /api/preferences': preferences({ perPage: 25 }) }) });
    assert.equal(custom.evaluate('perPage'), 25);
    assert.equal(seriesListLoads(custom).length, 2);

    const defaults = await boot({ hash: '#/library', routes: libraryRoutes({ 'GET /api/preferences': preferences({ perPage: 0, filterMode: '', sortMode: '', sortDirection: '' }) }) });
    assert.equal(defaults.evaluate('perPage'), 100, 'no saved size means the default');
    assert.equal(defaults.evaluate('filterMode'), 'all');
    assert.equal(defaults.evaluate('sortMode'), 'name');
    assert.equal(defaults.evaluate('sortDirection'), 'asc');
    assert.equal(seriesListLoads(defaults).length, 1);
});

test('a saved filter alone is applied to the library, not just shown in the header', async () => {
    const page = await boot({ hash: '#/library', routes: libraryRoutes({ 'GET /api/preferences': preferences({ filterMode: 'unread' }) }) });

    assert.equal(page.$('#headerFilterLabel').textContent, 'Unread');
    assert.deepEqual(activeItems(page, 'headerFilterMenu'), ['unread']);
    const loads = seriesListLoads(page);
    assert.equal(loads.length, 2, 'the library started with the defaults, then reloads');
    assert.deepEqual(loads.at(-1).query, { offset: '0', limit: '60', filter: 'unread' });
    assert.ok(page.$('#fileList .series-card'), 'the filtered library is shown');
});

test('a saved descending sort is restored, and the library reloads in that order', async () => {
    const page = await boot({ hash: '#/library', routes: libraryRoutes({ 'GET /api/preferences': preferences({ sortMode: 'date', sortDirection: 'desc' }) }) });

    assert.equal(page.$('#headerSortLabel').textContent, 'Date ↓');
    assert.deepEqual(activeItems(page, 'headerSortMenu'), ['date']);
    assert.deepEqual(seriesListLoads(page).at(-1).query, { offset: '0', limit: '60', sort: 'date', direction: 'desc' });
});

test('a saved direction alone reverses the default sort; one that the app does not know is ignored', async () => {
    const reversed = await boot({ hash: '#/library', routes: libraryRoutes({ 'GET /api/preferences': preferences({ sortDirection: 'desc' }) }) });
    assert.equal(reversed.$('#headerSortLabel').textContent, 'Name ↓');
    assert.equal(seriesListLoads(reversed).length, 2);
    assert.deepEqual(seriesListLoads(reversed).at(-1).query, { offset: '0', limit: '60', direction: 'desc' });

    const unknown = await boot({ hash: '#/library', routes: libraryRoutes({ 'GET /api/preferences': preferences({ sortMode: 'date', sortDirection: 'sideways' }) }) });
    assert.equal(unknown.evaluate('sortDirection'), 'asc');
    assert.equal(unknown.$('#headerSortLabel').textContent, 'Date ↑');
    assert.equal(seriesListLoads(unknown).at(-1).query.direction, undefined);
});

test('a series page opened before the preferences arrive lists its issues with the saved filter', async () => {
    const read = seriesIssue({ read: true });
    const unread = seriesIssue({ file_path: 'Saga/Saga 002.cbz', file_name: 'Saga 002.cbz', title: 'Chapter Two', issue: '2' });
    const issuesPath = `/api/files/series/${SAGA.id}/issues`;
    const page = await boot({
        hash: `#/series/${SAGA.id}`,
        routes: libraryRoutes({
            'GET /api/preferences': preferences({ filterMode: 'unread' }),
            [`GET ${issuesPath}`]: call => seriesIssues(SAGA, call.query.filter === 'unread' ? [unread] : [read, unread])
        })
    });

    const loads = requests(page, 'GET', issuesPath);
    assert.equal(loads[0].query.filter, undefined, 'the page started loading before the preferences arrived');
    assert.equal(loads.at(-1).query.filter, 'unread');
    assert.equal(page.window.location.hash, `#/series/${SAGA.id}`, 'the series page stays open');
    assert.deepEqual(page.$$('#seriesDetailPanel .series-issue-card[data-file-path]').map(card => card.dataset.filePath), ['Saga/Saga 002.cbz']);
});

test('a series page whose id changes under the saved filter still lists its issues', async () => {
    // The filter groups the series under another id, and the filtered library
    // answers before the filtered issues requested under the old id.
    const filtered = seriesCard({ id: 'series-2' });
    const read = seriesIssue({ read: true });
    const unread = seriesIssue({ file_path: 'Saga/Saga 002.cbz', file_name: 'Saga 002.cbz', title: 'Chapter Two', issue: '2' });
    const issuesFor = series => call => seriesIssues(series, call.query.filter === 'unread' ? [unread] : [read, unread]);
    const gate = deferred();
    const page = await boot({
        hash: `#/series/${SAGA.id}`,
        routes: libraryRoutes({
            'GET /api/preferences': preferences({ filterMode: 'unread' }),
            'GET /api/files/series': call => seriesPage(call.query.filter === 'unread' ? [filtered] : [SAGA]),
            [`GET /api/files/series/${SAGA.id}/issues`]: call => (call.query.filter === 'unread' ? gate.promise : issuesFor(SAGA)(call)),
            [`GET /api/files/series/${filtered.id}/issues`]: issuesFor(filtered)
        })
    });
    gate.resolve(seriesIssues(filtered, [unread]));
    await page.settle();

    assert.equal(page.evaluate('currentSeriesDetailId'), 'series-2');
    assert.equal(page.$('#seriesDetailPanel .loading'), null, 'the page is not left loading');
    assert.deepEqual(page.$$('#seriesDetailPanel .series-issue-card[data-file-path]').map(card => card.dataset.filePath), ['Saga/Saga 002.cbz']);
});

test('a saved filter the app no longer knows is labelled "All"', async () => {
    const page = await boot({ hash: '#/library', routes: libraryRoutes({ 'GET /api/preferences': preferences({ filterMode: 'retired' }) }) });

    assert.equal(page.evaluate('filterMode'), 'retired');
    assert.equal(page.$('#headerFilterLabel').textContent, 'All');
    assert.deepEqual(activeItems(page, 'headerFilterMenu'), []);
});

test('a failing job check or health load is logged instead of surfacing as an unexpected error', async () => {
    const page = loadApp({ hash: '#/home' });
    // Both normally handle their own errors; main.js still guards the promises it starts.
    page.run(`
        checkAndResumeActiveJob = async () => { throw new Error('job service down'); };
        loadLibraryHealth = async () => { throw new Error('counts unavailable'); };
    `);
    await page.settle();

    const errors = consoleLines(page, 'error');
    assert.ok(errors.includes('Failed to check active job: job service down'), errors.join('\n'));
    assert.ok(errors.includes('Failed to load library health: counts unavailable'), errors.join('\n'));
    assert.deepEqual(requests(page, 'POST', '/api/client-errors'), []);
    assert.ok(page.$('#overviewRows .overview-row'), 'the rest of the app still started');
});

test('main.js run after the document has loaded starts the app straight away', async () => {
    const page = loadApp({ hash: '#/home', withoutMain: true });
    await page.settle();
    assert.equal(page.document.readyState, 'complete');
    assert.deepEqual(page.fetchCalls, [], 'nothing starts without main.js');

    runWwwrootScript('js/main.js', page.context);
    await page.settle();

    // initializeApp
    assert.equal(page.$('#usernameDisplay').textContent, 'alice');
    assert.equal(requests(page, 'GET', '/api/overview').length, 1);
    assert.equal(page.eventSources.length, 1);
    // initModalAccessibility
    const shortcuts = page.$('#keyboardShortcutsModal');
    assert.equal(shortcuts.getAttribute('role'), 'dialog');
    assert.equal(shortcuts.getAttribute('aria-hidden'), 'true');
    // initDropdownAriaExpandedSync
    page.$('#headerSortMenu').classList.add('show');
    await page.settle(1);
    assert.equal(page.$('#headerSortToggle').getAttribute('aria-expanded'), 'true');
    page.$('#headerSortMenu').classList.remove('show');
    // initLibraryKeyboardShortcuts
    press(page, '?', {}, page.document.body);
    assert.ok(shortcuts.classList.contains('active'));
    // initConnectivityAwareness
    Object.defineProperty(page.window.navigator, 'onLine', { value: false, configurable: true });
    page.window.dispatchEvent(new page.window.Event('offline'));
    assert.equal(page.$$('.app-offline-banner').length, 1);
});

test('the job progress button follows the viewport between phone and desktop sizes', async t => {
    t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
    const media = { [MOBILE]: false };
    const page = await boot({ hash: '#/library', media, routes: libraryRoutes() });
    const indicator = page.$('#progressIndicator');
    const resize = () => {
        page.window.dispatchEvent(new page.window.Event('resize'));
        t.mock.timers.tick(150);
    };

    page.run('setProgressIndicatorVisible(true)');
    assert.equal(indicator.style.display, 'flex');
    assert.equal(indicator.classList.contains('progress-indicator-btn--mobile-sticky'), false);

    media[MOBILE] = true;
    resize();
    assert.equal(indicator.classList.contains('progress-indicator-btn--mobile-sticky'), true);

    media[MOBILE] = false;
    resize();
    assert.equal(indicator.classList.contains('progress-indicator-btn--mobile-sticky'), false);

    page.run('setProgressIndicatorVisible(false)');
    media[MOBILE] = true;
    resize();
    assert.equal(indicator.style.display, 'none', 'a hidden button stays hidden');
    assert.equal(indicator.classList.contains('progress-indicator-btn--mobile-sticky'), false);
});

/** Rotate the (fake) phone: later matchMedia calls see it, and every listener is told. */
function rotate(page, media, portrait) {
    media[PORTRAIT] = portrait;
    page.mediaQueries.filter(list => list.media === PORTRAIT).forEach(list => list.change(portrait));
}

test('rotating a phone switches the automatic series layout between list and compact grid', async () => {
    const media = { [PORTRAIT]: false };
    const page = await boot({ hash: '#/library', media, routes: libraryRoutes() });
    assert.ok(page.$('#fileList .series-grid--compact'));
    assert.ok(page.$('#seriesLayoutCompactBtn').classList.contains('active'));

    rotate(page, media, true);
    assert.ok(page.$('#fileList .series-list'));
    assert.equal(page.$('#fileList .series-grid--compact'), null);
    assert.ok(page.$('#seriesLayoutListBtn').classList.contains('active'));
    assert.ok(page.$('#seriesLayoutListBtnMobile').classList.contains('active'));

    rotate(page, media, false);
    assert.ok(page.$('#fileList .series-grid--compact'));
});

test('a layout the user picked is kept when the phone rotates', async () => {
    const media = { [PORTRAIT]: false };
    const page = await boot({ hash: '#/library', media, routes: libraryRoutes(), localStorage: { seriesLayout: 'grid' } });
    const grid = page.$('#fileList .series-grid--titled');
    assert.ok(grid, 'the saved layout is used');
    assert.ok(page.$('#seriesLayoutGridBtn').classList.contains('active'));

    rotate(page, media, true);
    assert.equal(page.$('#fileList .series-grid--titled'), grid, 'not even re-rendered');
});

test('rotating while a series page is open leaves that page in place', async () => {
    const media = { [PORTRAIT]: false };
    const page = await boot({ hash: `#/series/${SAGA.id}`, media, routes: libraryRoutes() });
    const panel = page.$('#seriesDetailPanel');
    assert.ok(panel);

    rotate(page, media, true);
    assert.equal(page.$('#seriesDetailPanel'), panel);
    assert.equal(page.$('#fileList .series-list'), null);
});

test('browsers with only the legacy MediaQueryList.addListener still follow rotation', async () => {
    const media = { [PORTRAIT]: false };
    const page = await boot({
        hash: '#/library',
        media,
        routes: libraryRoutes(),
        beforeScripts(window) {
            const matchMedia = window.matchMedia;
            window.matchMedia = query => {
                const list = matchMedia(query);
                if (query === PORTRAIT) delete list.addEventListener; // Safari < 14
                return list;
            };
        }
    });

    rotate(page, media, true);
    assert.ok(page.$('#fileList .series-list'));
});

test('a browser that cannot evaluate the rotation query still starts normally', async () => {
    const page = await boot({
        hash: '#/library',
        routes: libraryRoutes(),
        beforeScripts(window) {
            const matchMedia = window.matchMedia;
            window.matchMedia = query => {
                if (query === PORTRAIT) throw new window.DOMException('Unsupported media query', 'SyntaxError');
                return matchMedia(query);
            };
        }
    });

    assert.ok(page.$('#fileList .series-grid--compact'));
    assert.deepEqual(page.errors, []);
    assert.equal(page.mediaQueries.some(list => list.media === PORTRAIT), false);
});
