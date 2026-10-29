'use strict';

// Series issue loading (js/app/series.js loadSeriesIssues and the remapping
// around it): the single request for every issue, response fallbacks, deep
// links to series outside the loaded library, keeping the page open when a
// library reload gives the series a new id, and pruning selected issues that
// a reload, 404 lookup or remap no longer lists.

const test = require('node:test');
const assert = require('node:assert/strict');
const { deferred } = require('./support/app');
const {
    seriesCard, issue, issuesBody, libraryBody, issuesPath, bootSeries, callsTo, text, issueCards
} = require('./support/series-helpers');

const ISSUES = issuesPath('saga');
const paths = page => issueCards(page).map(card => card.dataset.filePath);
const selectionMeta = page => text(page.$('#seriesDetailPanel .series-detail-selection-meta'));
// Ticks the issue card of `filePath`, as a user selecting it would.
const tickIssue = (page, filePath) => page.click(issueCards(page)
    .find(card => card.dataset.filePath === filePath)
    .querySelector('.series-issue-select input[type="checkbox"]'));
// Answers the lookup of a series whose id 404s (GET /api/files/series?search=…)
// with `handler`.
const routeSeriesLookup = (page, handler) =>
    page.route(call => call.path === '/api/files/series' && 'search' in call.query, handler);

test('every issue comes from one request, even when the response reports more pages', async () => {
    // The client asks for the whole list (per_page=-1) and never pages, so no
    // later page can be left spinning or be fetched in a loop, with or
    // without IntersectionObserver.
    const series = seriesCard({ issue_count: 4 });
    const body = issuesBody(series, [issue(1), issue(2)], { issue_count: 4, total_pages: 2, per_page: 2 });
    for (const globals of [{}, { IntersectionObserver: undefined }]) {
        const page = await bootSeries({ series, globals, routes: { [`GET ${ISSUES}`]: body } });
        await page.settle();

        assert.deepEqual(callsTo(page, ISSUES).map(call => call.query), [{ per_page: '-1' }]);
        assert.deepEqual(paths(page), ['/comics/Saga/Saga 001.cbz', '/comics/Saga/Saga 002.cbz']);
        assert.equal(page.$('#seriesDetailPanel .spinner'), null, 'nothing is shown as still loading');
        assert.deepEqual(page.errors, []);
    }
});

test('a response without counts falls back to its issue list, then to the library card', async () => {
    const listed = await bootSeries({ routes: { [`GET ${ISSUES}`]: { issues: [issue(1), issue(2)] } } });
    assert.equal(issueCards(listed).length, 2);
    assert.equal(selectionMeta(listed), '2 issues in this series');

    const empty = await bootSeries({ routes: { [`GET ${ISSUES}`]: {} } });
    assert.equal(text(empty.$('#seriesDetailPanel .empty-state')), 'No issues in this series.');
    assert.equal(text(empty.$('#seriesDetailPanel .series-detail-summary .series-detail-meta')), '3 issues · 3.0 MB',
        'the library card still provides the header');
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

test('a renamed series whose issues are still loading fetches them under the new id', async () => {
    const gate = deferred();
    const page = await bootSeries({ routes: { [`GET ${ISSUES}`]: () => gate.promise } });
    assert.equal(text(page.$('#seriesDetailPanel .loading')), 'Loading issues...');
    const renamed = seriesCard({ id: 'saga-2' });
    page.route(`GET ${issuesPath('saga-2')}`, issuesBody(renamed, [issue(1), issue(2)]));

    await reloadLibrary(page, [renamed]);

    assert.equal(page.evaluate('currentSeriesDetailId'), 'saga-2');
    assert.equal(callsTo(page, issuesPath('saga-2')).length, 1, 'the unfinished load is not carried over');
    assert.equal(issueCards(page).length, 2);

    // The old id's answer arriving afterwards changes nothing.
    gate.resolve(issuesBody(seriesCard(), [issue(1), issue(2), issue(3)]));
    await page.settle();
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

test('issue loading ignores a missing id and a series already loaded without issues', async () => {
    const page = await bootSeries({ issues: [] });
    const requests = page.fetchCalls.length;
    const panel = page.$('#seriesDetailPanel');

    page.run("loadSeriesIssues(''); loadSeriesIssues('saga')");
    await page.settle();
    assert.equal(page.fetchCalls.length, requests);
    assert.equal(page.$('#seriesDetailPanel'), panel, 'nothing re-rendered');
});

test('there is no series id to remap while the library grid is showing', async () => {
    const page = await bootSeries({ hash: '#/library' });

    assert.equal(page.$('#seriesDetailPanel'), null);
    assert.equal(page.evaluate('remapCurrentSeriesDetailId()'), null);
});

test('an unfiltered answer arriving after a filter change leaves the filtered series summary alone', async () => {
    const unfiltered = deferred();
    const filtered = deferred();
    const page = await bootSeries({
        library: [seriesCard({ id: 'other', title: 'Other', canonical_title: 'Other' })],
        routes: { [`GET ${ISSUES}`]: call => (call.query.filter ? filtered.promise : unfiltered.promise) }
    });
    page.run("filterMode = 'duplicates'; loadSeriesIssues('saga')");
    await page.settle();
    assert.equal(callsTo(page, ISSUES).length, 2);

    unfiltered.resolve(issuesBody(seriesCard(), [issue(1), issue(2), issue(3)]));
    await page.settle();
    assert.equal(page.evaluate('currentSeriesDetailSeries.issue_count'), 0, 'the superseded answer does not update the snapshot');
    assert.equal(page.evaluate('currentSeriesDetailSeries.total_size'), 0);
    assert.equal(text(page.$('#seriesDetailPanel .loading')), 'Loading issues...');

    filtered.resolve(issuesBody(seriesCard(), [issue(1, { duplicate: true })]));
    await page.settle();
    assert.deepEqual(paths(page), ['/comics/Saga/Saga 001.cbz']);
    assert.equal(text(page.$('#seriesDetailPanel .series-detail-summary .series-detail-meta')), '1 issue · 1.0 MB');
});

// The old cards stay clickable while a forced reload is pending, so an issue
// selected then is checked against the reloaded list too.
test('an issue selected while a reload is pending is unselected if the reload no longer lists it', async () => {
    const series = seriesCard({ issue_count: 2 });
    let answer = () => issuesBody(series, [issue(1), issue(2)], { issue_count: 2 });
    const page = await bootSeries({ series, routes: { [`GET ${ISSUES}`]: () => answer() } });
    assert.equal(text(page.$('#selectInfo')), 'No files selected');

    const reload = deferred();
    answer = () => reload.promise;
    page.run("loadSeriesIssues('saga', true)");
    await page.settle();
    tickIssue(page, '/comics/Saga/Saga 001.cbz');
    tickIssue(page, '/comics/Saga/Saga 002.cbz');
    assert.equal(text(page.$('#selectInfo')), '2 files selected');

    // #2 was deleted meanwhile.
    reload.resolve(issuesBody(series, [issue(1)], { issue_count: 1 }));
    await page.settle();
    assert.deepEqual(paths(page), ['/comics/Saga/Saga 001.cbz']);
    assert.deepEqual(page.evaluate('[...selectedFiles]'), ['/comics/Saga/Saga 001.cbz']);
    assert.equal(text(page.$('#selectInfo')), '1 file selected');
});

// A filter change drops the cached list, so a reload of it that lands after
// the newer list must not unselect an issue the old filter merely hides now.
test('a filtered reload landing after a filter switch unselects nothing', async () => {
    const series = seriesCard({ issue_count: 2 });
    const unmarkedReload = deferred();
    let unmarkedAnswer = () => issuesBody(series, [issue(1), issue(2)], { issue_count: 2 });
    const page = await bootSeries({
        series,
        routes: {
            [`GET ${ISSUES}`]: call => (call.query.filter === 'unmarked'
                ? unmarkedAnswer()
                : issuesBody(series, [issue(1), issue(2)], { issue_count: 2 }))
        }
    });
    page.run("setHeaderFilter('unmarked')");
    await page.settle();
    tickIssue(page, '/comics/Saga/Saga 002.cbz');
    assert.equal(text(page.$('#selectInfo')), '1 file selected');

    unmarkedAnswer = () => unmarkedReload.promise;
    page.run("loadSeriesIssues('saga', true)");
    await page.settle();
    // #2 is marked meanwhile; the user switches to All, which still lists it.
    page.run("setHeaderFilter('all')");
    await page.settle();
    assert.deepEqual(paths(page), ['/comics/Saga/Saga 001.cbz', '/comics/Saga/Saga 002.cbz']);

    unmarkedReload.resolve(issuesBody(series, [issue(1)], { issue_count: 1 }));
    await page.settle();
    assert.deepEqual(page.evaluate('[...selectedFiles]'), ['/comics/Saga/Saga 002.cbz']);
    assert.equal(text(page.$('#selectInfo')), '1 file selected');
});

// A filter switch takes effect at once but drops the cached list only once it
// has been saved, so a reload under the old filter that lands in between is
// still cached and must not unselect an issue that filter merely hides now.
test('a filtered reload landing while a filter switch is being saved unselects nothing', async () => {
    const series = seriesCard({ issue_count: 2 });
    const unmarkedReload = deferred();
    let unmarkedAnswer = () => issuesBody(series, [issue(1), issue(2)], { issue_count: 2 });
    const page = await bootSeries({
        series,
        routes: {
            [`GET ${ISSUES}`]: call => (call.query.filter === 'unmarked'
                ? unmarkedAnswer()
                : issuesBody(series, [issue(1), issue(2)], { issue_count: 2 }))
        }
    });
    page.run("setHeaderFilter('unmarked')");
    await page.settle();
    tickIssue(page, '/comics/Saga/Saga 002.cbz');
    assert.equal(text(page.$('#selectInfo')), '1 file selected');

    unmarkedAnswer = () => unmarkedReload.promise;
    page.run("loadSeriesIssues('saga', true)");
    await page.settle();
    // #2 is marked meanwhile; the user switches to All, which still lists it,
    // and the reload lands before that choice has been saved.
    const save = deferred();
    page.route('PUT /api/preferences', () => save.promise);
    page.run("setHeaderFilter('all')");
    unmarkedReload.resolve(issuesBody(series, [issue(1)], { issue_count: 1 }));
    await page.settle();
    assert.deepEqual(page.evaluate('[...selectedFiles]'), ['/comics/Saga/Saga 002.cbz']);
    assert.equal(text(page.$('#selectInfo')), '1 file selected');

    save.resolve({});
    await page.settle();
    assert.equal(callsTo(page, ISSUES).at(-1).query.filter, undefined);
    assert.deepEqual(paths(page), ['/comics/Saga/Saga 001.cbz', '/comics/Saga/Saga 002.cbz']);
    assert.deepEqual(page.$$('.series-issue-card--selected').map(card => card.dataset.filePath), ['/comics/Saga/Saga 002.cbz']);
    assert.deepEqual(page.evaluate('[...selectedFiles]'), ['/comics/Saga/Saga 002.cbz']);
    assert.equal(text(page.$('#selectInfo')), '1 file selected');
});

test('pending selection checks survive a filter change and series-id remap', async () => {
    const series = seriesCard({ issue_count: 2 });
    const oldReload = deferred();
    const page = await bootSeries({ series });
    tickIssue(page, '/comics/Saga/Saga 001.cbz');

    page.route(`GET ${ISSUES}`, call => (call.query.filter === 'unmarked'
        ? issuesBody(series, [issue(2)], { issue_count: 1 })
        : oldReload.promise));
    page.run("loadSeriesIssues('saga', true)");
    await page.settle();

    page.run("setHeaderFilter('unmarked')");
    await page.settle();
    assert.deepEqual(page.evaluate('[...selectedFiles]'), ['/comics/Saga/Saga 001.cbz']);

    // The old all-issues reload and its cache entry are now superseded.
    oldReload.resolve(issuesBody(series, [issue(2)], { issue_count: 1 }));
    await page.settle();
    assert.deepEqual(page.evaluate('[...selectedFiles]'), ['/comics/Saga/Saga 001.cbz']);

    await reloadLibrary(page, [seriesCard({ id: 'saga-2' })]);
    assert.equal(page.evaluate('currentSeriesDetailId'), 'saga-2');

    // The all-filter confirmation is retained through the remap and removes
    // the issue that is absent from the replacement series.
    page.route(`GET ${issuesPath('saga-2')}`, issuesBody(seriesCard({ id: 'saga-2' }), [issue(2)]));
    page.run("setHeaderFilter('all')");
    await page.settle();
    assert.deepEqual(page.evaluate('[...selectedFiles]'), []);
    assert.equal(text(page.$('#selectInfo')), 'No files selected');
});

// Regression test for #849: once its last issue is gone, a series' id no
// longer resolves and its reload 404s.
test('a reload that finds the series gone unselects its issues', async () => {
    const series = seriesCard({ issue_count: 1 });
    const page = await bootSeries({ series, issues: [issue(1)] });
    tickIssue(page, '/comics/Saga/Saga 001.cbz');
    assert.equal(text(page.$('#selectInfo')), '1 file selected');

    // The only issue is deleted in another tab, and the series lookup fails.
    page.route(`GET ${ISSUES}`, 404);
    let lookup = 500;
    routeSeriesLookup(page, () => lookup);
    page.eventSources.at(-1).emit('message', { type: 'file_processed', data: { filename: '/comics/Saga/Saga 001.cbz', success: true } });
    await page.settle();
    assert.equal(text(page.$('#seriesDetailPanel .empty-state')), 'Failed to load issues. Retry');
    assert.deepEqual(callsTo(page, '/api/files/series').at(-1).query, { per_page: '-1', search: 'Saga' },
        'the series is looked up by its title');
    assert.equal(text(page.$('#selectInfo')), '1 file selected', 'nothing is unselected without an answer');

    // On retry the lookup only finds a series whose title merely contains it.
    lookup = libraryBody([seriesCard({ id: 'saga-art', title: 'The Art of Saga', canonical_title: 'The Art of Saga' })]);
    page.click('#seriesDetailPanel .empty-state button');
    await page.settle();
    assert.equal(callsTo(page, issuesPath('saga-art')).length, 0);
    assert.deepEqual(page.evaluate('[...selectedFiles]'), []);
    assert.equal(text(page.$('#selectInfo')), 'No files selected');
    assert.equal(page.$('#batchUpdateBtn').disabled, true);
});

// A series can get a new id (e.g. a pinned name changed in another tab), so a
// 404 for its old one only unselects what the series no longer lists.
test('a reload that 404s keeps the issues the series still lists under a new id', async () => {
    const page = await bootSeries();
    page.run("setHeaderFilter('unmarked')");
    await page.settle();
    tickIssue(page, '/comics/Saga/Saga 001.cbz');
    tickIssue(page, '/comics/Saga/Saga 002.cbz');
    assert.equal(text(page.$('#selectInfo')), '2 files selected');

    // #1 is deleted, and the series is 'saga-2' now.
    const renamed = seriesCard({ id: 'saga-2', title: 'Saga (2012)' });
    page.route(`GET ${ISSUES}`, 404);
    routeSeriesLookup(page, libraryBody([
        seriesCard({ id: 'saga-art', title: 'The Art of Saga', canonical_title: 'The Art of Saga' }),
        renamed
    ]));
    page.route(`GET ${issuesPath('saga-2')}`, issuesBody(renamed, [issue(2), issue(3)]));
    page.eventSources.at(-1).emit('message', { type: 'file_processed', data: { filename: '/comics/Saga/Saga 001.cbz', success: true } });
    await page.settle();

    assert.deepEqual(callsTo(page, '/api/files/series').at(-1).query, { per_page: '-1', search: 'Saga' },
        'the filter may hide the series, so the lookup does without it');
    assert.deepEqual(callsTo(page, issuesPath('saga-2')).map(call => call.query),
        [{ per_page: '-1', filter: 'unmarked' }]);
    assert.deepEqual(page.evaluate('[...selectedFiles]'), ['/comics/Saga/Saga 002.cbz']);
    assert.equal(text(page.$('#selectInfo')), '1 file selected');
});

// The lookup takes a request of its own, so a reload that lists the issue again
// meanwhile has the last word.
test('a series lookup that answers after a newer reload unselects nothing', async () => {
    const series = seriesCard({ issue_count: 1 });
    let answer = () => issuesBody(series, [issue(1)]);
    const page = await bootSeries({ series, routes: { [`GET ${ISSUES}`]: () => answer() } });
    tickIssue(page, '/comics/Saga/Saga 001.cbz');
    const lookup = deferred();
    routeSeriesLookup(page, () => lookup.promise);

    answer = () => 404;
    page.run("loadSeriesIssues('saga', true)");
    await page.settle();
    assert.equal(callsTo(page, '/api/files/series').at(-1).query.search, 'Saga', 'the lookup is pending');
    // The issue is back by the time the user retries.
    answer = () => issuesBody(series, [issue(1)]);
    page.click('#seriesDetailPanel .empty-state button');
    await page.settle();
    assert.deepEqual(paths(page), ['/comics/Saga/Saga 001.cbz']);

    lookup.resolve(libraryBody([]));
    await page.settle();
    assert.deepEqual(page.evaluate('[...selectedFiles]'), ['/comics/Saga/Saga 001.cbz']);
    assert.equal(text(page.$('#selectInfo')), '1 file selected');
});

// A metadata change can replace a series' title but keep one of its aliases,
// so the lookup searches each name it is known by.
test('a reload that 404s finds the series by an alias it kept', async () => {
    const page = await bootSeries({ series: seriesCard({ aliases: ['Space Opera'] }) });
    tickIssue(page, '/comics/Saga/Saga 001.cbz');
    tickIssue(page, '/comics/Saga/Saga 002.cbz');
    assert.equal(text(page.$('#selectInfo')), '2 files selected');

    // #1 is deleted, and the series is now listed only by its old alias.
    const renamed = seriesCard({ id: 'space-opera', title: 'Space Opera', canonical_title: 'Space Opera' });
    page.route(`GET ${ISSUES}`, 404);
    routeSeriesLookup(page, call => libraryBody(call.query.search === 'Space Opera' ? [renamed] : []));
    page.route(`GET ${issuesPath('space-opera')}`, issuesBody(renamed, [issue(2), issue(3)]));
    page.eventSources.at(-1).emit('message', { type: 'file_processed', data: { filename: '/comics/Saga/Saga 001.cbz', success: true } });
    await page.settle();

    assert.deepEqual(callsTo(page, '/api/files/series').filter(call => 'search' in call.query).map(call => call.query.search),
        ['Saga', 'Space Opera']);
    assert.equal(callsTo(page, issuesPath('space-opera')).length, 1);
    assert.deepEqual(page.evaluate('[...selectedFiles]'), ['/comics/Saga/Saga 002.cbz']);
    assert.equal(text(page.$('#selectInfo')), '1 file selected');
});

// A library reload can give the series its new id while the lookup is still
// pending, so the list loaded under that id confirms the issues instead.
test('a series remapped while its 404 lookup is pending unselects what the new id no longer lists', async () => {
    const page = await bootSeries();
    tickIssue(page, '/comics/Saga/Saga 001.cbz');
    tickIssue(page, '/comics/Saga/Saga 002.cbz');
    const lookup = deferred();
    routeSeriesLookup(page, () => lookup.promise);

    // #1 is deleted, and the series is 'saga-2' now.
    page.route(`GET ${ISSUES}`, 404);
    page.route(`GET ${issuesPath('saga-2')}`, issuesBody(seriesCard({ id: 'saga-2' }), [issue(2), issue(3)]));
    page.run("loadSeriesIssues('saga', true)");
    await page.settle();
    assert.equal(callsTo(page, '/api/files/series').at(-1).query.search, 'Saga', 'the lookup is pending');

    await reloadLibrary(page, [seriesCard({ id: 'saga-2' })]);
    assert.equal(page.evaluate('currentSeriesDetailId'), 'saga-2');
    assert.deepEqual(paths(page).filter(Boolean), ['/comics/Saga/Saga 002.cbz', '/comics/Saga/Saga 003.cbz']);
    assert.deepEqual(page.evaluate('[...selectedFiles]'), ['/comics/Saga/Saga 002.cbz']);
    assert.equal(text(page.$('#selectInfo')), '1 file selected');

    // The stale lookup changes nothing.
    lookup.resolve(libraryBody([]));
    await page.settle();
    assert.deepEqual(page.evaluate('[...selectedFiles]'), ['/comics/Saga/Saga 002.cbz']);
});

// When the new id's list is cached already, a reload of it takes the pending
// issues over.
test('a series remapped to a cached list while its reload is pending is pruned by a reload of that list', async () => {
    const page = await bootSeries();
    page.route(`GET ${issuesPath('saga-2')}`, issuesBody(seriesCard({ id: 'saga-2' }), [issue(2), issue(3)]));
    page.run("loadSeriesIssues('saga-2')");
    await page.settle();
    tickIssue(page, '/comics/Saga/Saga 001.cbz');
    tickIssue(page, '/comics/Saga/Saga 002.cbz');

    const reload = deferred();
    page.route(`GET ${ISSUES}`, () => reload.promise);
    page.run("loadSeriesIssues('saga', true)");
    await page.settle();

    await reloadLibrary(page, [seriesCard({ id: 'saga-2' })]);
    assert.equal(page.evaluate('currentSeriesDetailId'), 'saga-2');
    assert.deepEqual(page.evaluate('[...selectedFiles]'), ['/comics/Saga/Saga 002.cbz']);
    assert.equal(text(page.$('#selectInfo')), '1 file selected');

    // The old id's answer arriving afterwards changes nothing.
    reload.resolve(issuesBody(seriesCard(), []));
    await page.settle();
    assert.deepEqual(page.evaluate('[...selectedFiles]'), ['/comics/Saga/Saga 002.cbz']);
});

// The cached list of the new id may predate the change that remapped the
// series, so it is reloaded rather than pruned against.
test('a series remapped to a stale cached list keeps the issues a fresh reload of it lists', async () => {
    const page = await bootSeries();
    let saga2 = [issue(2), issue(3)];
    page.route(`GET ${issuesPath('saga-2')}`, () => issuesBody(seriesCard({ id: 'saga-2' }), saga2));
    page.run("loadSeriesIssues('saga-2')");
    await page.settle();
    tickIssue(page, '/comics/Saga/Saga 001.cbz');
    tickIssue(page, '/comics/Saga/Saga 002.cbz');

    const reload = deferred();
    page.route(`GET ${ISSUES}`, () => reload.promise);
    page.run("loadSeriesIssues('saga', true)");
    await page.settle();

    // #1 moved to 'saga-2' after its list was cached.
    saga2 = [issue(1), issue(2), issue(3)];
    await reloadLibrary(page, [seriesCard({ id: 'saga-2' })]);
    assert.equal(page.evaluate('currentSeriesDetailId'), 'saga-2');
    assert.equal(callsTo(page, issuesPath('saga-2')).length, 2, 'the cached list is reloaded');
    assert.deepEqual(paths(page).filter(Boolean), [
        '/comics/Saga/Saga 001.cbz', '/comics/Saga/Saga 002.cbz', '/comics/Saga/Saga 003.cbz'
    ]);
    assert.deepEqual(page.evaluate('[...selectedFiles]'), ['/comics/Saga/Saga 001.cbz', '/comics/Saga/Saga 002.cbz']);
    assert.equal(text(page.$('#selectInfo')), '2 files selected');

    reload.resolve(issuesBody(seriesCard(), []));
    await page.settle();
    assert.deepEqual(page.evaluate('[...selectedFiles]'), ['/comics/Saga/Saga 001.cbz', '/comics/Saga/Saga 002.cbz']);
});

// A fully loaded list has nothing left to confirm, so the issues it lists are
// handed over instead.
test('a loaded series remapped to a cached list unselects what a fresh reload of that list no longer has', async () => {
    const page = await bootSeries();
    let saga2 = [issue(3)];
    page.route(`GET ${issuesPath('saga-2')}`, () => issuesBody(seriesCard({ id: 'saga-2' }), saga2));
    page.run("loadSeriesIssues('saga-2')");
    await page.settle();
    tickIssue(page, '/comics/Saga/Saga 001.cbz');
    tickIssue(page, '/comics/Saga/Saga 002.cbz');

    // #1 is deleted, and #2 moved to 'saga-2' after its list was cached.
    saga2 = [issue(2), issue(3)];
    await reloadLibrary(page, [seriesCard({ id: 'saga-2' })]);
    assert.equal(page.evaluate('currentSeriesDetailId'), 'saga-2');
    assert.equal(callsTo(page, issuesPath('saga-2')).length, 2, 'the cached list is reloaded');
    assert.deepEqual(paths(page).filter(Boolean), ['/comics/Saga/Saga 002.cbz', '/comics/Saga/Saga 003.cbz']);
    assert.deepEqual(page.evaluate('[...selectedFiles]'), ['/comics/Saga/Saga 002.cbz']);
    assert.equal(text(page.$('#selectInfo')), '1 file selected');
});
