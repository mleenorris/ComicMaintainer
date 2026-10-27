'use strict';

// js/app/shell.js — the Library Health dashboard (counts, empty and error
// states, refresh coalescing and debouncing), its collapse toggle and the
// shared debounced viewport-resize listener.

const test = require('node:test');
const assert = require('node:assert/strict');
const { deferred } = require('./support/app');
const { boot, consoleLines, fileCounts, requests, wentToLogin } = require('./support/core-helpers');

const COUNTS = '/api/files/counts';
const MOBILE = '(max-width: 768px)';
const COLLAPSED_KEY = 'cm.libraryHealth.collapsed';
const useMockTimers = t => t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });

function dashboard(page) {
    const summary = page.$('#libraryHealthSummary');
    const button = id => ({ hidden: page.$(id).hidden, disabled: page.$(id).disabled });
    return {
        processed: page.$('#libraryHealthProcessed').textContent,
        unprocessed: page.$('#libraryHealthUnprocessed').textContent,
        duplicates: page.$('#libraryHealthDuplicates').textContent,
        combinableFolders: page.$('#libraryHealthCombinableFolders').textContent,
        summary: summary.textContent,
        summaryIsStatus: summary.classList.contains('library-health-summary--status'),
        emptyStateHidden: page.$('#libraryHealthEmpty').hidden,
        gridDisplay: page.$('#libraryHealthDashboard .library-health-grid').style.display,
        reviewDuplicates: button('#reviewDuplicatesBtn'),
        combineFolders: button('#combineFoldersBtn')
    };
}

function collapseState(page) {
    return {
        collapsed: page.$('#libraryHealthDashboard').classList.contains('library-health-dashboard--collapsed'),
        expanded: page.$('#libraryHealthToggle').getAttribute('aria-expanded'),
        chevron: page.$('#libraryHealthToggle .library-health-toggle-chevron').textContent,
        stored: (() => {
            try {
                return page.window.localStorage.getItem(COLLAPSED_KEY);
            } catch {
                return 'unavailable';
            }
        })()
    };
}

const EXPANDED = { collapsed: false, expanded: 'true', chevron: '▾', stored: '0' };
const COLLAPSED = { collapsed: true, expanded: 'false', chevron: '▸', stored: '1' };

const localized = (page, value) => page.evaluate(`(${value}).toLocaleString()`);

test('the dashboard shows the library counts and only the follow-up actions that have work', async () => {
    const counts = { total: 2500, processed: 1999, unprocessed: 501, duplicates: 0, combinableFolders: 4 };
    const page = await boot({ routes: { [`GET ${COUNTS}`]: fileCounts(counts) } });

    assert.deepEqual(dashboard(page), {
        processed: localized(page, 1999),
        unprocessed: '501',
        duplicates: '0',
        combinableFolders: '4',
        summary: '',
        summaryIsStatus: false,
        emptyStateHidden: true,
        gridDisplay: '',
        reviewDuplicates: { hidden: true, disabled: true },
        combineFolders: { hidden: false, disabled: false }
    });
    assert.equal(requests(page, 'GET', COUNTS)[0].headers.authorization, `Bearer ${page.window.localStorage.getItem('jwt_token')}`);
});

test('an empty library swaps the stat grid for getting-started guidance until files arrive', async () => {
    // An empty object exercises the zero defaults for every missing count.
    const page = await boot({ routes: { [`GET ${COUNTS}`]: {} } });

    assert.deepEqual(dashboard(page), {
        processed: '0',
        unprocessed: '0',
        duplicates: '0',
        combinableFolders: '0',
        summary: '',
        summaryIsStatus: false,
        emptyStateHidden: false,
        gridDisplay: 'none',
        reviewDuplicates: { hidden: true, disabled: true },
        combineFolders: { hidden: true, disabled: true }
    });

    page.route(`GET ${COUNTS}`, fileCounts({ duplicates: 5, combinableFolders: 0 }));
    page.click('button[onclick="refreshLibraryHealth()"]');
    await page.settle();

    const after = dashboard(page);
    assert.equal(after.emptyStateHidden, true);
    assert.equal(after.gridDisplay, '');
    assert.equal(after.duplicates, '5');
    assert.deepEqual(after.reviewDuplicates, { hidden: false, disabled: false });
    assert.deepEqual(after.combineFolders, { hidden: true, disabled: true });
});

test('a failed load is reported in the summary until the next successful refresh', async () => {
    const page = await boot({ routes: { [`GET ${COUNTS}`]: 500 } });

    let state = dashboard(page);
    assert.equal(state.summary, 'Unable to load library health right now.');
    assert.equal(state.summaryIsStatus, true);
    assert.equal(state.processed, '—', 'the placeholders stay until real counts arrive');
    assert.ok(consoleLines(page, 'error').includes('Failed to load library health: HTTP error! status: 500'));

    page.route(`GET ${COUNTS}`, () => { throw new TypeError('Failed to fetch'); });
    await page.run('loadLibraryHealth()');
    assert.ok(consoleLines(page, 'error').includes('Failed to load library health: Failed to fetch'));

    page.route(`GET ${COUNTS}`, fileCounts({ processed: 42 }));
    await page.run('loadLibraryHealth()');
    state = dashboard(page);
    assert.equal(state.summary, '');
    assert.equal(state.summaryIsStatus, false);
    assert.equal(state.processed, '42');
});

test('a 401 while loading the counts ends the session without showing an error', async () => {
    const page = await boot();
    page.route(`GET ${COUNTS}`, 401);

    await page.run('loadLibraryHealth()');

    assert.ok(wentToLogin(page));
    assert.equal(dashboard(page).summary, '');
    assert.ok(!consoleLines(page, 'error').some(line => line.startsWith('Failed to load library health')));
});

test('refreshes requested while a load is in flight collapse into a single follow-up load', async () => {
    const page = await boot();
    const before = requests(page, 'GET', COUNTS).length;
    const gate = deferred();
    page.route(`GET ${COUNTS}`, () => gate.promise);

    page.run('loadLibraryHealth(); loadLibraryHealth(); refreshLibraryHealth();');
    await page.settle();
    assert.equal(requests(page, 'GET', COUNTS).length, before + 1, 'only the first call fetches');

    page.route(`GET ${COUNTS}`, fileCounts({ processed: 8 }));
    gate.resolve(fileCounts({ processed: 7 }));
    await page.settle();

    assert.equal(requests(page, 'GET', COUNTS).length, before + 2, 'the queued refreshes run once, after the first');
    assert.equal(dashboard(page).processed, '8');

    await page.run('loadLibraryHealth()');
    assert.equal(requests(page, 'GET', COUNTS).length, before + 3, 'nothing is left in flight or pending');
});

test('scheduled refreshes are debounced, and the latest delay wins', async t => {
    useMockTimers(t);
    const page = await boot();
    const count = () => requests(page, 'GET', COUNTS).length;
    const before = count();

    page.run('scheduleLibraryHealthRefresh(); scheduleLibraryHealthRefresh();');
    t.mock.timers.tick(749);
    await page.settle();
    assert.equal(count(), before);
    t.mock.timers.tick(1);
    await page.settle();
    assert.equal(count(), before + 1, 'a burst of requests triggers one refresh after 750 ms');

    page.run('scheduleLibraryHealthRefresh(); scheduleLibraryHealthRefresh(250);');
    t.mock.timers.tick(250);
    await page.settle();
    assert.equal(count(), before + 2);
    t.mock.timers.tick(1000);
    await page.settle();
    assert.equal(count(), before + 2, 'the replaced 750 ms refresh never fires');
    assert.equal(page.evaluate('libraryHealthRefreshTimer'), null);
});

test('the dashboard still loads when its optional parts are missing', async () => {
    const page = await boot();
    for (const selector of ['#libraryHealthSummary', '#libraryHealthEmpty', '#reviewDuplicatesBtn', '#combineFoldersBtn']) {
        page.$(selector).remove();
    }

    page.route(`GET ${COUNTS}`, fileCounts({ processed: 64 }));
    await page.run('loadLibraryHealth()');
    assert.equal(page.$('#libraryHealthProcessed').textContent, '64');

    page.route(`GET ${COUNTS}`, 503);
    await page.run('loadLibraryHealth()');
    assert.ok(consoleLines(page, 'error').includes('Failed to load library health: HTTP error! status: 503'));
    assert.deepEqual(page.errors, []);
});

test('the dashboard starts expanded on desktop and remembers when it is collapsed', async () => {
    const page = await boot();
    assert.deepEqual(collapseState(page), EXPANDED);

    page.click('#libraryHealthToggle');
    assert.deepEqual(collapseState(page), COLLAPSED);

    page.click('#libraryHealthToggle');
    assert.deepEqual(collapseState(page), EXPANDED);

    const remembered = await boot({ localStorage: { [COLLAPSED_KEY]: '1' } });
    assert.deepEqual(collapseState(remembered), COLLAPSED);
});

test('the dashboard starts collapsed on phones unless it was expanded before', async () => {
    const phone = await boot({ media: { [MOBILE]: true } });
    assert.deepEqual(collapseState(phone), COLLAPSED);

    const expandedBefore = await boot({ media: { [MOBILE]: true }, localStorage: { [COLLAPSED_KEY]: '0' } });
    assert.deepEqual(collapseState(expandedBefore), EXPANDED);
});

test('the collapse toggle still works when storage is unavailable', async () => {
    const page = await boot({
        media: { [MOBILE]: true },
        beforeScripts(window) {
            const { getItem, setItem } = window.Storage.prototype;
            const deny = () => { throw new window.DOMException('Storage is disabled', 'SecurityError'); };
            window.Storage.prototype.getItem = function (key) { return key === COLLAPSED_KEY ? deny() : getItem.call(this, key); };
            window.Storage.prototype.setItem = function (key, value) { return key === COLLAPSED_KEY ? deny() : setItem.call(this, key, value); };
        }
    });

    assert.deepEqual(collapseState(page), { ...COLLAPSED, stored: 'unavailable' }, 'falls back to the viewport default');
    page.click('#libraryHealthToggle');
    assert.deepEqual(collapseState(page), { ...EXPANDED, stored: 'unavailable' });
    assert.deepEqual(page.errors, []);
});

test('the collapse toggle tolerates missing markup', async () => {
    const page = await boot();

    page.$('#libraryHealthToggle').remove();
    page.run('setLibraryHealthCollapsed(true)');
    assert.equal(page.$('#libraryHealthDashboard').classList.contains('library-health-dashboard--collapsed'), true);
    assert.equal(page.window.localStorage.getItem(COLLAPSED_KEY), '1');

    page.$('#libraryHealthDashboard').remove();
    page.run('toggleLibraryHealthDashboard(); setLibraryHealthCollapsed(false);');
    assert.equal(page.window.localStorage.getItem(COLLAPSED_KEY), '1', 'nothing to toggle, nothing stored');
});

test('viewport resize handlers run once per burst, and one failing handler does not stop the others', async t => {
    useMockTimers(t);
    const page = await boot();
    page.run(`
        window.resizeCalls = [];
        onViewportResize(() => resizeCalls.push('first'));
        onViewportResize(() => { throw new Error('layout exploded'); });
        onViewportResize(() => resizeCalls.push('last'));
    `);
    const resize = () => page.window.dispatchEvent(new page.window.Event('resize'));

    resize();
    t.mock.timers.tick(100);
    resize();
    t.mock.timers.tick(100);
    resize();
    t.mock.timers.tick(149);
    assert.deepEqual(page.evaluate('resizeCalls'), []);

    t.mock.timers.tick(1);
    assert.deepEqual(page.evaluate('resizeCalls'), ['first', 'last']);
    assert.deepEqual(consoleLines(page, 'error').filter(line => line.startsWith('Resize handler failed:')), ['Resize handler failed: layout exploded']);
    assert.equal(page.evaluate('viewportResizeTimer'), null);

    resize();
    t.mock.timers.tick(150);
    assert.deepEqual(page.evaluate('resizeCalls'), ['first', 'last', 'first', 'last']);
});
