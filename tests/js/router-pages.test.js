'use strict';

// js/app/router.js — moving between the top-level pages: what each page
// shows, titles and active navigation, scroll/focus on page changes, the
// Settings return route, series pages (deep links, placeholders, leaving
// them) and browser history.

const test = require('node:test');
const assert = require('node:assert/strict');
const { deferred } = require('./support/app');
const { boot, press, requests, seriesCard, seriesIssue, seriesIssues, seriesPage } = require('./support/core-helpers');

const SAGA = seriesCard();
const ISSUES = `/api/files/series/${SAGA.id}/issues`;
const NAV_IDS = ['navOverviewBtn', 'navLibraryBtn', 'sidebarOverviewBtn', 'sidebarLibraryBtn', 'sidebarSettingsBtn', 'sidebarJobsBtn'];

/** Routes for a library holding one series, Saga, with one issue. */
const libraryRoutes = (overrides = {}) => ({
    'GET /api/files/series': seriesPage([SAGA]),
    [`GET ${ISSUES}`]: seriesIssues(SAGA, [seriesIssue()]),
    ...overrides
});

async function go(page, hash) {
    page.run(`navigate(${JSON.stringify(hash)})`);
    await page.settle();
}

function pageState(page) {
    const shown = selector => page.$(selector).style.display !== 'none';
    return {
        view: page.evaluate('currentView'),
        title: page.document.title,
        topbar: page.$('#topbarTitle').textContent,
        sections: {
            overview: !page.$('#overviewView').hidden,
            health: shown('#libraryHealthDashboard'),
            files: shown('#libraryFilesView'),
            settings: !page.$('#settingsView').hidden,
            jobs: !page.$('#jobsView').hidden
        },
        headerControls: ['.header-search', '.header-filter-dropdown', '.header-sort-dropdown'].map(shown),
        layoutToggles: [!page.$('#seriesLayoutToggle').hidden, !page.$('#seriesLayoutToolbarMobile').hidden],
        settingsBodyClass: page.document.body.classList.contains('view-settings'),
        activeNav: NAV_IDS.filter(id => page.$(`#${id}`).classList.contains('active')),
        currentNav: NAV_IDS.filter(id => page.$(`#${id}`).getAttribute('aria-current') === 'page')
    };
}

const NONE = { overview: false, health: false, files: false, settings: false, jobs: false };
const HOME = {
    view: 'home', title: 'Overview · Comic Maintainer', topbar: 'Overview',
    sections: { ...NONE, overview: true }, headerControls: [false, false, false], layoutToggles: [false, false],
    settingsBodyClass: false, activeNav: ['navOverviewBtn', 'sidebarOverviewBtn'], currentNav: ['navOverviewBtn', 'sidebarOverviewBtn']
};
const LIBRARY = {
    view: 'library', title: 'Library · Comic Maintainer', topbar: 'Library',
    sections: { ...NONE, health: true, files: true }, headerControls: [true, true, true], layoutToggles: [true, true],
    settingsBodyClass: false, activeNav: ['navLibraryBtn', 'sidebarLibraryBtn'], currentNav: ['navLibraryBtn', 'sidebarLibraryBtn']
};
const SERIES = { ...LIBRARY, view: 'series', layoutToggles: [false, false] };
const SETTINGS = {
    view: 'settings', title: 'Settings · Comic Maintainer', topbar: 'Settings',
    sections: { ...NONE, settings: true }, headerControls: [false, false, false], layoutToggles: [false, false],
    settingsBodyClass: true, activeNav: ['sidebarSettingsBtn'], currentNav: ['sidebarSettingsBtn']
};
const JOBS = {
    view: 'jobs', title: 'Scheduled Jobs · Comic Maintainer', topbar: 'Scheduled Jobs',
    sections: { ...NONE, jobs: true }, headerControls: [false, false, false], layoutToggles: [false, false],
    settingsBodyClass: false, activeNav: ['sidebarJobsBtn'], currentNav: ['sidebarJobsBtn']
};

test('each page shows its own sections, header controls, title and active navigation', async () => {
    const page = await boot({ hash: '#/home', routes: libraryRoutes() });
    assert.deepEqual(pageState(page), HOME);

    const steps = [
        ['#navLibraryBtn', '#/library', LIBRARY],
        ['#sidebarSettingsBtn', '#/settings', SETTINGS],
        ['#sidebarJobsBtn', '#/jobs', JOBS],
        ['#sidebarOverviewBtn', '#/home', HOME],
        ['#sidebarLibraryBtn', '#/library', LIBRARY]
    ];
    for (const [button, hash, expected] of steps) {
        page.click(button);
        await page.settle();
        assert.equal(page.window.location.hash, hash);
        assert.deepEqual(pageState(page), expected, hash);
    }

    await go(page, `#/series/${SAGA.id}`);
    assert.deepEqual(pageState(page), SERIES);
});

test('unknown routes show the overview', async () => {
    const page = await boot({ hash: '#/nope' });

    assert.equal(page.window.location.hash, '#/nope');
    assert.deepEqual(pageState(page), HOME);
    assert.equal(requests(page, 'GET', '/api/overview').length, 1);
});

test('a route change with no hash at all is the overview, and Settings returns there', async () => {
    const page = await boot({ hash: '#/library', routes: libraryRoutes() });
    assert.equal(page.evaluate('settingsReturnHash'), '#/library');

    page.window.history.replaceState(null, '', '/index.html');
    page.run('handleRouteChange()');
    await page.settle();

    assert.equal(page.window.location.hash, '');
    assert.equal(page.evaluate('currentView'), 'home');
    assert.equal(page.evaluate('settingsReturnHash'), '#/home');
});

test('moving to another page scrolls to the top and focuses the content; moving within the library does not', async () => {
    const page = await boot({ hash: '#/home', routes: libraryRoutes() });
    const main = page.$('#mainContent');
    const mainFocusCalls = [];
    const focus = main.focus;
    main.focus = function (options) {
        mainFocusCalls.push(options);
        return focus.call(this, options);
    };
    const settingsScrollTops = [];
    Object.defineProperty(page.$('#settingsView'), 'scrollTop', { configurable: true, get: () => 0, set: value => settingsScrollTops.push(value) });
    const scrollsBefore = page.scrolls.length;
    assert.equal(page.scrolls.filter(scroll => scroll.y === 0).length, 0, 'the first page shown keeps its scroll position');

    page.$('#sidebarLibraryBtn').focus();
    page.click('#sidebarLibraryBtn');
    await page.settle();
    assert.deepEqual(page.scrolls.slice(scrollsBefore), [{ x: 0, y: 0 }]);
    assert.deepEqual(mainFocusCalls.map(options => ({ ...options })), [{ preventScroll: true }]);
    assert.equal(page.document.activeElement, main);

    await go(page, `#/series/${SAGA.id}`);
    await go(page, '#/library');
    assert.equal(page.scrolls.length, scrollsBefore + 1, 'library and its series pages are one page');
    assert.equal(mainFocusCalls.length, 1);

    // Focus already inside the content area is left where it is.
    page.$('#libraryHealthToggle').focus();
    await go(page, '#/settings');
    assert.deepEqual(page.scrolls.slice(scrollsBefore), [{ x: 0, y: 0 }, { x: 0, y: 0 }]);
    assert.deepEqual(settingsScrollTops, [0], 'Settings scrolls inside its own section');
    assert.equal(mainFocusCalls.length, 1);
    assert.equal(page.document.activeElement, page.$('#libraryHealthToggle'));
});

test('navigating to the route already shown renders it again', async () => {
    const page = await boot({ hash: '#/home' });
    const overviewLoads = requests(page, 'GET', '/api/overview').length;

    page.click('#navOverviewBtn');
    await page.settle();

    assert.equal(page.window.location.hash, '#/home');
    assert.equal(requests(page, 'GET', '/api/overview').length, overviewLoads + 1);
});

test('closing Settings returns to the page it was opened from', async () => {
    const page = await boot({ hash: '#/library', routes: libraryRoutes() });

    await go(page, '#/settings');
    assert.equal(page.evaluate('settingsReturnHash'), '#/library');
    page.click('button[onclick="closeSettings()"]');
    await page.settle();
    assert.equal(page.window.location.hash, '#/library');

    await go(page, `#/series/${SAGA.id}`);
    await go(page, '#/settings');
    page.click('button[onclick="closeSettings()"]');
    await page.settle();
    assert.equal(page.window.location.hash, `#/series/${SAGA.id}`);
    assert.equal(page.evaluate('currentView'), 'series');

    const direct = await boot({ hash: '#/settings' });
    assert.equal(direct.evaluate('settingsReturnHash'), '#/home', 'opened directly, Settings returns to the overview');
});

test('a series deep link opens that series, and every other page closes it', async () => {
    for (const [hash, view] of [['#/library', 'library'], ['#/home', 'home'], ['#/settings', 'settings'], ['#/jobs', 'jobs']]) {
        const page = await boot({ hash: `#/series/${SAGA.id}`, routes: libraryRoutes() });
        assert.equal(page.evaluate('currentSeriesDetailId'), SAGA.id);
        assert.equal(page.evaluate('currentSeriesDetailSeries.title'), 'Saga');
        assert.match(page.$('#seriesDetailPanel h2').textContent, /Saga/);

        await go(page, hash);
        assert.equal(page.evaluate('currentView'), view);
        assert.equal(page.evaluate('currentSeriesDetailId'), null, hash);
        assert.equal(page.evaluate('currentSeriesDetailSeries'), null, hash);
        assert.equal(page.evaluate('currentSeriesDetailTitleKeys'), null, hash);
        if (view === 'library') {
            assert.equal(page.$('#seriesDetailPanel'), null);
            assert.ok(page.$('#fileList .series-card'), 'the series list is shown again');
        }
    }
});

test('a series that is not in the loaded library opens with a placeholder until its issues arrive', async () => {
    const other = seriesCard({ id: 'series-9', title: 'Paper Girls', canonical_title: 'Paper Girls', cover_file_path: 'Paper Girls/Paper Girls 001.cbz' });
    const issues = deferred();
    const page = await boot({
        hash: '#/library',
        routes: libraryRoutes({ 'GET /api/files/series/series-9/issues': () => issues.promise })
    });

    await go(page, '#/series/series-9');
    assert.deepEqual(page.evaluate('currentSeriesDetailSeries'), { id: 'series-9', title: '', total_size: 0, issue_count: 0 });
    assert.ok(page.$('#seriesDetailPanel'), 'the detail stays open while loading');

    issues.resolve(seriesIssues(other, [seriesIssue({ file_path: 'Paper Girls/Paper Girls 001.cbz', file_name: 'Paper Girls 001.cbz' })]));
    await page.settle();
    assert.equal(page.evaluate('currentSeriesDetailSeries.title'), 'Paper Girls');

    await go(page, '#/series/series-9');
    assert.equal(page.evaluate('currentSeriesDetailSeries.title'), 'Paper Girls', 'a known series is not reset to the placeholder');
});

test('a series link without an id goes to the library', async () => {
    const page = await boot({ hash: '#/series/', routes: libraryRoutes() });

    assert.equal(page.window.location.hash, '#/library');
    assert.equal(page.evaluate('currentView'), 'library');
    assert.equal(page.evaluate('currentSeriesDetailId'), null);
});

test('opening a series before the library has loaded fetches the library first', async () => {
    const page = await boot({ hash: '#/home', routes: { 'GET /api/files/series': seriesPage([]) } });
    assert.equal(page.evaluate('seriesLibrary.length'), 0);
    page.route('GET /api/files/series', seriesPage([SAGA]));
    page.route(`GET ${ISSUES}`, seriesIssues(SAGA));
    const listLoads = requests(page, 'GET', '/api/files/series').length;

    await go(page, `#/series/${SAGA.id}`);

    assert.equal(requests(page, 'GET', '/api/files/series').length, listLoads + 1);
    assert.equal(page.evaluate('currentSeriesDetailSeries.title'), 'Saga', 'the loaded entry is used, not a placeholder');
});

test('the library list is fetched on arrival only when it is neither loaded nor loading', async () => {
    const page = await boot({ hash: '#/home', routes: { 'GET /api/files/series': seriesPage([]) } });
    const listLoads = () => requests(page, 'GET', '/api/files/series').length;
    const before = listLoads();

    const pending = deferred();
    page.route('GET /api/files/series', () => pending.promise);
    await go(page, '#/library');
    assert.equal(listLoads(), before + 1, 'nothing loaded yet');

    await go(page, '#/home');
    await go(page, '#/library');
    assert.equal(listLoads(), before + 1, 'already loading');

    pending.resolve(seriesPage([SAGA]));
    await page.settle();
    await go(page, '#/home');
    await go(page, '#/library');
    assert.equal(listLoads(), before + 1, 'already loaded');
    assert.ok(page.$('#fileList .series-card'));
});

test('browser back and forward move between pages', async () => {
    const page = await boot({ hash: '#/home', routes: libraryRoutes() });
    await go(page, '#/library');
    await go(page, '#/settings');

    page.window.history.back();
    await page.waitFor(() => page.evaluate('currentView') === 'library');
    assert.equal(page.window.location.hash, '#/library');

    page.window.history.forward();
    await page.waitFor(() => page.evaluate('currentView') === 'settings');
    assert.deepEqual(pageState(page), SETTINGS);
});

test('an Actions menu left open on a series page closes when the page changes without a click', async () => {
    // An open menu is moved to <body> (js/app/menus.js), so it stayed on
    // screen over the next page, still acting on the previous page's issue.
    // A click anywhere else closes it, but Back/Forward (also the mouse back
    // button and swipe-back) and the "g" shortcuts change the page without one.
    const page = await boot({ hash: '#/library', routes: libraryRoutes() });
    const keys = (...sequence) => sequence.forEach(key => press(page, key, {}, page.document.body));
    const leftOpen = () => page.$$('body > .dropdown-menu, .dropdown-menu.show');

    async function leaveWithMenuOpen(name, leave, view) {
        const toggle = page.$('#seriesDetailPanel .series-issue-actions-toggle');
        const menu = toggle.nextElementSibling;
        page.click(toggle);
        assert.deepEqual(leftOpen(), [menu], `${name}: the menu is open, in <body>`);

        leave();
        await page.waitFor(() => page.evaluate('currentView') === view);
        assert.deepEqual(leftOpen(), [], name);
    }

    for (const [name, leave, view] of [
        ['Back', () => page.window.history.back(), 'library'],
        ['g l', () => keys('g', 'l'), 'library'],
        ['g h', () => keys('g', 'h'), 'home'],
        ['g s', () => keys('g', 's'), 'settings'],
        ['g j', () => keys('g', 'j'), 'jobs']
    ]) {
        await go(page, `#/series/${SAGA.id}`);
        await leaveWithMenuOpen(name, leave, view);
    }

    await go(page, `#/series/${SAGA.id}`);
    await go(page, '#/settings');
    page.window.history.back();
    await page.waitFor(() => page.evaluate('currentView') === 'series');
    await page.settle();
    await leaveWithMenuOpen('Forward', () => page.window.history.forward(), 'settings');
});

test('the issue range selection is reset only when leaving the open series', async () => {
    const page = await boot({ hash: `#/series/${SAGA.id}`, routes: libraryRoutes() });
    page.run("lastSelectedIssuePath = 'Saga/Saga 001.cbz'; seriesIssueRangeSelectArmed = true;");

    await go(page, `#/series/${SAGA.id}`);
    assert.equal(page.evaluate('lastSelectedIssuePath'), 'Saga/Saga 001.cbz');
    assert.equal(page.evaluate('seriesIssueRangeSelectArmed'), true);

    await go(page, '#/library');
    assert.equal(page.evaluate('lastSelectedIssuePath'), null);
    assert.equal(page.evaluate('seriesIssueRangeSelectArmed'), false);
});

test('routing tolerates missing page sections and navigation items', async () => {
    const page = await boot({ hash: '#/home' });
    const optional = [
        '#overviewView', '#settingsView', '#jobsView', '#libraryHealthDashboard', '#libraryFilesView',
        '.header-search', '.header-filter-dropdown', '.header-sort-dropdown', '#seriesLayoutToggle',
        '#seriesLayoutToolbarMobile', ...NAV_IDS.map(id => `#${id}`)
    ];

    page.run("currentView = 'bogus'; updateNavActiveState();");
    assert.equal(page.$('#topbarTitle').textContent, '', 'no title for an unknown view');

    // (#seriesLayoutToolbarMobile sits inside #libraryFilesView.)
    [...optional, '#topbarTitle'].forEach(selector => page.$(selector)?.remove());
    assert.deepEqual(optional.filter(selector => page.$(selector)), []);
    page.run("applyViewVisibility('library'); updateNavActiveState(); enterPage('bogus');");
    assert.equal(page.document.title, 'Overview · Comic Maintainer');
    assert.deepEqual(page.errors, []);
});
