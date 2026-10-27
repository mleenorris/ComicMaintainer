'use strict';

// Series issue loading (js/app/series.js loadSeriesIssues and the paging /
// remapping around it): multi-page responses fed through the issues-grid
// sentinel observer or the eager fallback, response fallbacks, deep links to
// series outside the loaded library, and keeping the page open when a library
// reload gives the series a new id.

const test = require('node:test');
const assert = require('node:assert/strict');
const { deferred } = require('./support/app');
const { respond } = require('./support/dom');
const {
    seriesCard, issue, issuesBody, issuesPath, bootSeries, callsTo, text, issueCards
} = require('./support/series-helpers');

const ISSUES = issuesPath('saga');
const paths = page => issueCards(page).map(card => card.dataset.filePath);
const selectionMeta = page => text(page.$('#seriesDetailPanel .series-detail-selection-meta'));
// Ticks the issue card of `filePath`, as a user selecting it would.
const tickIssue = (page, filePath) => page.click(issueCards(page)
    .find(card => card.dataset.filePath === filePath)
    .querySelector('.series-issue-select input[type="checkbox"]'));
// The observer watching the issues grid's sentinel (not the cover-image or
// library-scroll observers).
const sentinelObserver = page => page.intersectionObservers.find(observer =>
    [...observer.targets].some(target => target.id === 'seriesIssuesSentinel'));
const pageRequests = (page, id = 'saga') => callsTo(page, issuesPath(id)).map(call => call.query.page);

// A two-page issue list: #1-#2, then #2 again (a concurrent move) plus #3-#4.
function twoPages(series) {
    return {
        1: issuesBody(series, [issue(1), issue(2)], { issue_count: 4, total_pages: 2, per_page: 2 }),
        2: issuesBody(series, [issue(2), issue(3), issue(4)], { issue_count: 4, total_pages: 2, per_page: 2, page: 2 })
    };
}

test('the next page loads when the sentinel scrolls into view', async () => {
    const series = seriesCard({ issue_count: 4 });
    const pages = twoPages(series);
    const page = await bootSeries({ series, routes: { [`GET ${ISSUES}`]: call => pages[call.query.page] } });

    assert.equal(selectionMeta(page), '4 issues in this series · showing 2');
    assert.equal(text(page.$('#seriesDetailPanel .series-detail-summary .series-detail-meta')), '4 issues · 3.0 MB');
    assert.ok(page.$('#seriesIssuesSentinel'), 'a sentinel marks the end of the loaded issues');
    // Selecting everything re-renders the grid and re-observes its sentinel.
    page.click('#selectAll');
    assert.equal(page.$('#selectAll').checked, true);
    const sentinel = page.$('#seriesIssuesSentinel');
    const observer = sentinelObserver(page);
    assert.equal(observer.options.rootMargin, '400px 0px');
    assert.deepEqual([...observer.targets], [sentinel]);
    assert.equal(page.intersectionObservers.filter(o => [...o.targets].some(t => t.id === 'seriesIssuesSentinel')).length, 1,
        'the previous sentinel observer was disconnected');

    observer.trigger(false);
    await page.settle();
    assert.deepEqual(pageRequests(page), ['1'], 'not intersecting yet');

    observer.trigger(true);
    await page.settle();
    assert.deepEqual(pageRequests(page), ['1', '2']);
    assert.deepEqual(callsTo(page, ISSUES)[1].query, { per_page: '-1', page: '2' });
    assert.deepEqual(paths(page), [
        '/comics/Saga/Saga 001.cbz', '/comics/Saga/Saga 002.cbz', '/comics/Saga/Saga 003.cbz', '/comics/Saga/Saga 004.cbz'
    ], 'the repeated #2 is listed once');
    assert.equal(selectionMeta(page), '4 issues in this series');
    assert.equal(page.$('#seriesIssuesSentinel'), null);
    assert.equal(observer.targets.size, 0, 'the observer is disconnected once everything is loaded');
    assert.equal(page.$('#selectAll').checked, false, 'the new issues are not selected');
    assert.equal(page.$('#selectAll').indeterminate, true);

    // An intersection queued before the disconnect, and a repeated request for
    // the loaded page, load nothing more.
    observer.trigger(true, [sentinel]);
    page.run("loadSeriesIssues('saga', false, 2)");
    await page.settle();
    assert.deepEqual(pageRequests(page), ['1', '2']);
});

test('a page that is still loading is not requested twice', async () => {
    const series = seriesCard({ issue_count: 4 });
    const pages = twoPages(series);
    const gate = deferred();
    const page = await bootSeries({
        series,
        routes: { [`GET ${ISSUES}`]: call => (call.query.page === '2' ? gate.promise : pages[1]) }
    });

    const observer = sentinelObserver(page);
    observer.trigger(true);
    observer.trigger(true);
    page.run("loadSeriesIssues('saga', false, 2)");
    await page.settle();
    assert.deepEqual(pageRequests(page), ['1', '2']);

    gate.resolve(pages[2]);
    await page.settle();
    assert.equal(issueCards(page).length, 4);
});

test('a failed later page keeps the loaded issues and stops paging', async () => {
    const series = seriesCard({ issue_count: 4 });
    const pages = twoPages(series);
    const page = await bootSeries({
        series,
        routes: { [`GET ${ISSUES}`]: call => (call.query.page === '2' ? respond(500, { error: 'boom' }) : pages[1]) }
    });

    sentinelObserver(page).trigger(true);
    await page.settle();
    assert.deepEqual(pageRequests(page), ['1', '2']);
    assert.equal(issueCards(page).length, 2);
    assert.equal(page.$('#seriesDetailPanel .empty-state'), null, 'the loaded issues stay on screen');

    const retryObserver = sentinelObserver(page);
    assert.ok(retryObserver, 'the sentinel is observed again');
    retryObserver.trigger(true);
    await page.settle();
    assert.deepEqual(pageRequests(page), ['1', '2'], 'the failed page is not retried on scroll');
    assert.equal(retryObserver.targets.size, 0);
});

test('an intersection reported after leaving the series loads nothing', async () => {
    const series = seriesCard({ issue_count: 4 });
    const pages = twoPages(series);
    const page = await bootSeries({ series, routes: { [`GET ${ISSUES}`]: call => pages[call.query.page] } });
    const sentinel = page.$('#seriesIssuesSentinel');
    const observer = sentinelObserver(page);

    page.click('#seriesDetailPanel .series-detail-back');
    await page.settle();
    assert.equal(page.window.location.hash, '#/library');
    assert.equal(observer.targets.size, 0, 'leaving the series disconnects the observer');

    observer.trigger(true, [sentinel]);
    await page.settle();
    assert.deepEqual(pageRequests(page), ['1']);
});

test('without IntersectionObserver every remaining page is loaded eagerly', async () => {
    const series = seriesCard({ issue_count: 3 });
    const body = (n, issues) => issuesBody(series, issues, { issue_count: 3, total_pages: 3, per_page: 1, page: n });
    const pages = { 1: body(1, [issue(1)]), 2: body(2, [issue(2)]), 3: body(3, [issue(3)]) };
    const page = await bootSeries({
        series,
        globals: { IntersectionObserver: undefined },
        routes: { [`GET ${ISSUES}`]: call => pages[call.query.page] }
    });
    await page.waitFor(() => issueCards(page).length === 3);

    assert.deepEqual(pageRequests(page), ['1', '2', '3']);
    assert.equal(selectionMeta(page), '3 issues in this series');
    assert.equal(page.$('#seriesIssuesSentinel'), null);
    assert.deepEqual(page.errors, []);
});

test('the eager fallback stops at a failed page', async () => {
    const series = seriesCard({ issue_count: 3 });
    const body = (n, issues) => issuesBody(series, issues, { issue_count: 3, total_pages: 3, per_page: 1, page: n });
    const page = await bootSeries({
        series,
        globals: { IntersectionObserver: undefined },
        routes: { [`GET ${ISSUES}`]: call => (call.query.page === '1' ? body(1, [issue(1)]) : 503) }
    });
    await page.settle();

    assert.deepEqual(pageRequests(page), ['1', '2']);
    assert.deepEqual(paths(page), ['/comics/Saga/Saga 001.cbz']);
});

test('the eager fallback stops when the series is left', async () => {
    const series = seriesCard({ issue_count: 3 });
    const body = (n, issues) => issuesBody(series, issues, { issue_count: 3, total_pages: 3, per_page: 1, page: n });
    // Both pages are held back so the library has arrived before the eager
    // loop starts: a second render of the panel while a page is in flight
    // makes the loop's loadSeriesIssues call return at once, and the loop then
    // spins without yielding to the event loop.
    const first = deferred();
    const second = deferred();
    const page = await bootSeries({
        series,
        globals: { IntersectionObserver: undefined },
        routes: { [`GET ${ISSUES}`]: call => (call.query.page === '1' ? first.promise : second.promise) }
    });
    first.resolve(body(1, [issue(1)]));
    await page.settle();
    assert.deepEqual(pageRequests(page), ['1', '2']);

    page.click('#seriesDetailPanel .series-detail-back');
    await page.settle();
    second.resolve(body(2, [issue(2)]));
    await page.settle();
    assert.deepEqual(pageRequests(page), ['1', '2'], 'page 3 is never requested');
    assert.equal(page.$('#seriesDetailPanel'), null);
});

test('missing counts and paging fields in the response fall back to safe defaults', async () => {
    const page = await bootSeries({ routes: { [`GET ${ISSUES}`]: {} } });

    assert.equal(text(page.$('#seriesDetailPanel .empty-state')), 'No issues in this series.');
    assert.deepEqual(page.evaluate(`(() => {
        const entry = seriesIssuesCache.get('saga');
        return { total: entry.total, totalPages: entry.totalPages, perPage: entry.perPage, allLoaded: entry.allLoaded };
    })()`), { total: 0, totalPages: 1, perPage: 100, allLoaded: true });
    assert.equal(text(page.$('#seriesDetailPanel .series-detail-summary .series-detail-meta')), '3 issues · 3.0 MB',
        'the library card still provides the header');
});

test('a later page reporting no total falls back to the loaded issue count', async () => {
    const series = seriesCard({ issue_count: 2 });
    const pages = {
        1: issuesBody(series, [issue(1)], { issue_count: 2, total_pages: 2, per_page: 1 }),
        2: { issues: [issue(2)], issue_count: 0, total_pages: 0, per_page: 0 }
    };
    const page = await bootSeries({ series, routes: { [`GET ${ISSUES}`]: call => pages[call.query.page] } });

    sentinelObserver(page).trigger(true);
    await page.settle();
    assert.equal(issueCards(page).length, 2);
    assert.equal(selectionMeta(page), '2 issues in this series');
});

test('a file deleted while paging shrinks the issue count to what the server reports', async () => {
    const series = seriesCard({ issue_count: 2 });
    const pages = {
        1: issuesBody(series, [issue(1)], { issue_count: 2, total_pages: 2, per_page: 1 }),
        // By the time page 2 is requested the second file is gone.
        2: issuesBody(series, [], { issue_count: 1, total_pages: 1, per_page: 1, page: 2 })
    };
    const page = await bootSeries({ series, routes: { [`GET ${ISSUES}`]: call => pages[call.query.page] } });
    assert.equal(selectionMeta(page), '2 issues in this series · showing 1');

    sentinelObserver(page).trigger(true);
    await page.settle();

    assert.deepEqual(pageRequests(page), ['1', '2']);
    assert.deepEqual(paths(page), ['/comics/Saga/Saga 001.cbz']);
    assert.equal(selectionMeta(page), '1 issue in this series');
    assert.equal(page.$('#seriesIssuesSentinel'), null, 'nothing is left to load');
});

// Regression test for #849: an issue missing from a partly loaded list may
// still be on a later page.
test('a reload that comes in pages unselects a vanished issue only once its last page is in', async () => {
    const series = seriesCard({ issue_count: 4 });
    let pages = twoPages(series);
    const page = await bootSeries({ series, routes: { [`GET ${ISSUES}`]: call => pages[call.query.page] } });
    sentinelObserver(page).trigger(true);
    await page.settle();
    tickIssue(page, '/comics/Saga/Saga 002.cbz');
    tickIssue(page, '/comics/Saga/Saga 004.cbz');
    assert.equal(text(page.$('#selectInfo')), '2 files selected');

    // #4 is deleted; the processed-file event reloads the list from page 1.
    pages = {
        1: issuesBody(series, [issue(1), issue(2)], { issue_count: 3, total_pages: 2, per_page: 2 }),
        2: issuesBody(series, [issue(3)], { issue_count: 3, total_pages: 2, per_page: 2, page: 2 })
    };
    page.eventSources.at(-1).emit('message', { type: 'file_processed', data: { filename: '/comics/Saga/Saga 004.cbz', success: true } });
    await page.settle();
    assert.deepEqual(paths(page), ['/comics/Saga/Saga 001.cbz', '/comics/Saga/Saga 002.cbz']);
    assert.equal(text(page.$('#selectInfo')), '2 files selected', '#4 may still be on the next page');

    sentinelObserver(page).trigger(true);
    await page.settle();
    assert.deepEqual(pageRequests(page), ['1', '2', '1', '2']);
    assert.deepEqual(paths(page), ['/comics/Saga/Saga 001.cbz', '/comics/Saga/Saga 002.cbz', '/comics/Saga/Saga 003.cbz']);
    assert.deepEqual(page.evaluate('[...selectedFiles]'), ['/comics/Saga/Saga 002.cbz']);
    assert.equal(text(page.$('#selectInfo')), '1 file selected');
});

test('a deep link to a series outside the loaded library renders from its issues response', async () => {
    const gate = deferred();
    const lost = seriesCard({ id: 'lost', title: 'Lost Series', canonical_title: 'Lost Series', aliases: ['LS'] });
    const issues = [
        issue(1, { file_path: '/comics/Lost/Lost 001.cbz', file_name: 'Lost 001.cbz', title: 'Lost #1' }),
        issue(2, { file_path: '/comics/Lost/Lost 002.cbz', file_name: 'Lost 002.cbz', title: 'Lost #2' })
    ];
    const { id: _omitted, ...body } = issuesBody(lost, issues);
    const page = await bootSeries({
        series: lost,
        library: [seriesCard()],
        routes: { [`GET ${issuesPath('lost')}`]: () => gate.promise }
    });

    // Until the issues arrive there is only a placeholder to show.
    assert.equal(page.window.location.hash, '#/series/lost');
    assert.equal(text(page.$('#seriesDetailPanel .loading')), 'Loading issues...');
    assert.equal(text(page.$('#seriesDetailPanel h2')), '✕');

    gate.resolve(body);
    await page.settle();
    assert.equal(page.evaluate('currentSeriesDetailId'), 'lost');
    assert.match(text(page.$('#seriesDetailPanel h2')), /^Lost Series\b/);
    assert.deepEqual(page.$$('#seriesDetailPanel .series-detail-summary .series-detail-meta').map(text), [
        '2 issues · 2.0 MB',
        'Also known as: LS'
    ]);
    assert.deepEqual(paths(page), ['/comics/Lost/Lost 001.cbz', '/comics/Lost/Lost 002.cbz']);
    assert.equal(page.evaluate('currentSeriesDetailSeries.id'), 'lost', 'the requested id stands in for the missing one');
});

test('re-opening a series while its first load is in flight does not request it again', async () => {
    const gate = deferred();
    const series = seriesCard();
    const page = await bootSeries({ series, routes: { [`GET ${ISSUES}`]: () => gate.promise } });

    page.click('#seriesDetailPanel .series-detail-back');
    await page.settle();
    page.click('#fileList .series-card');
    await page.settle();
    assert.equal(page.window.location.hash, '#/series/saga');
    assert.equal(text(page.$('#seriesDetailPanel .loading')), 'Loading issues...');

    gate.resolve(issuesBody(series, [issue(1), issue(2), issue(3)]));
    await page.settle();
    assert.equal(issueCards(page).length, 3);
    assert.equal(callsTo(page, ISSUES).length, 1);
});

// A library reload (e.g. after a metadata refresh) can give the open series a
// new id: the backend's id is the union-find representative of its files.
async function reloadLibrary(page, series) {
    page.route('GET /api/files/series', { series, page: 1, total_pages: 1, total_series: series.length, offset: 0, limit: 60 });
    page.run('loadSeriesLibrary({ refresh: true })');
    await page.settle();
}

test('a library reload that renames the series id keeps it open under the new id', async () => {
    const page = await bootSeries({ series: seriesCard({ aliases: ['Saga Comic'] }) });
    assert.equal(issueCards(page).length, 3);

    await reloadLibrary(page, [
        seriesCard({ id: 'monstress', title: 'Monstress', canonical_title: 'Monstress', aliases: ['Monstress Vol'] }),
        seriesCard({ id: 'saga-2', title: 'Saga (2012)', canonical_title: 'Saga', aliases: ['Saga Comic', 'Saga Deluxe'] })
    ]);

    assert.equal(page.evaluate('currentSeriesDetailId'), 'saga-2');
    assert.match(text(page.$('#seriesDetailPanel h2')), /^Saga \(2012\)/);
    assert.equal(text(page.$$('#seriesDetailPanel .series-detail-summary .series-detail-meta')[1]),
        'Also known as: Saga Comic, Saga Deluxe');
    assert.equal(issueCards(page).length, 3, 'the loaded issues move to the new id');
    assert.deepEqual(callsTo(page, issuesPath('saga-2')), [], 'so they are not fetched again');
    assert.equal(page.evaluate("seriesIssuesCache.has('saga')"), false);
});

test('a renamed series is recognised by one of its aliases', async () => {
    const page = await bootSeries({ series: seriesCard({ aliases: ['Saga Comic'] }) });

    await reloadLibrary(page, [
        seriesCard({ id: 'saga-deluxe', title: 'Saga Deluxe', canonical_title: 'Saga Deluxe', aliases: [' SAGA COMIC '] })
    ]);

    assert.equal(page.evaluate('currentSeriesDetailId'), 'saga-deluxe');
    assert.match(text(page.$('#seriesDetailPanel h2')), /^Saga Deluxe\b/);
    assert.equal(issueCards(page).length, 3);
});

test('a renamed series is recognised by its title and refetched when its issues had failed', async () => {
    const page = await bootSeries({ routes: { [`GET ${ISSUES}`]: 500 } });
    assert.equal(text(page.$('#seriesDetailPanel .empty-state')), 'Failed to load issues. Retry');
    const renamed = seriesCard({ id: 'saga-3', canonical_title: 'Saga: The Series' });
    page.route(`GET ${issuesPath('saga-3')}`, issuesBody(renamed, [issue(1), issue(2)]));

    await reloadLibrary(page, [renamed]);

    assert.equal(page.evaluate('currentSeriesDetailId'), 'saga-3');
    assert.equal(callsTo(page, issuesPath('saga-3')).length, 1, 'the failed load is not carried over');
    assert.equal(issueCards(page).length, 2);
    assert.equal(page.evaluate("seriesIssuesCache.has('saga')"), false);
});

test('a series the reloaded library no longer lists stays open from its last snapshot', async () => {
    const page = await bootSeries({ series: seriesCard({ aliases: ['Saga Comic'] }) });

    await reloadLibrary(page, [seriesCard({ id: 'monstress', title: 'Monstress', canonical_title: 'Monstress', aliases: null })]);

    assert.equal(page.window.location.hash, '#/series/saga');
    assert.equal(page.evaluate('currentSeriesDetailId'), 'saga');
    assert.match(text(page.$('#seriesDetailPanel h2')), /^Saga\b/);
    assert.equal(issueCards(page).length, 3);
});

test('an intersection for a series id that was remapped loads nothing', async () => {
    const series = seriesCard({ issue_count: 4 });
    const pages = twoPages(series);
    const page = await bootSeries({ series, routes: { [`GET ${ISSUES}`]: call => pages[call.query.page] } });
    const sentinel = page.$('#seriesIssuesSentinel');
    const observer = sentinelObserver(page);

    await reloadLibrary(page, [seriesCard({ id: 'saga-2', issue_count: 4 })]);
    assert.equal(page.evaluate('currentSeriesDetailId'), 'saga-2');

    observer.trigger(true, [sentinel]);
    await page.settle();
    assert.deepEqual(pageRequests(page), ['1']);
    assert.deepEqual(pageRequests(page, 'saga-2'), []);
});

test('rendering a series that is neither listed nor open returns to the library', async () => {
    const page = await bootSeries();
    page.click('#seriesDetailPanel .series-detail-back');
    await page.settle();
    const navigations = page.navigations.length;

    page.run("renderSeriesDetail('gone')");
    await page.settle();
    assert.equal(page.navigations.length, navigations, 'already on the library: navigate() re-renders in place');
    assert.equal(page.window.location.hash, '#/library');
    assert.equal(page.$('#seriesDetailPanel'), null);
    assert.ok(page.$('#fileList .series-card'));
});

test('issue loading ignores a missing id and page appends for another or an unrendered series', async () => {
    const page = await bootSeries({ issues: [] });
    const requests = page.fetchCalls.length;
    const panel = page.$('#seriesDetailPanel');

    page.run("loadSeriesIssues('')");
    page.run("appendSeriesIssuePage('monstress', 2)");
    await page.settle();
    assert.equal(page.fetchCalls.length, requests);
    assert.equal(page.$('#seriesDetailPanel'), panel, 'nothing re-rendered');

    // The empty series has no grid to append to, so the panel is re-rendered.
    page.run("appendSeriesIssuePage('saga', 2)");
    assert.notEqual(page.$('#seriesDetailPanel'), panel);
    assert.equal(text(page.$('#seriesDetailPanel .empty-state')), 'No issues in this series.');
});

test('there is no series id to remap while the library grid is showing', async () => {
    const page = await bootSeries({ hash: '#/library' });

    assert.equal(page.$('#seriesDetailPanel'), null);
    assert.equal(page.evaluate('remapCurrentSeriesDetailId()'), null);
});
