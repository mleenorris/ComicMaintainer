'use strict';

// Series issue loading (js/app/series.js loadSeriesIssues and the remapping
// around it): the single request for every issue, response fallbacks, deep
// links to series outside the loaded library, and keeping the page open when
// a library reload gives the series a new id.

const test = require('node:test');
const assert = require('node:assert/strict');
const { deferred } = require('./support/app');
const {
    seriesCard, issue, issuesBody, issuesPath, bootSeries, callsTo, text, issueCards
} = require('./support/series-helpers');

const ISSUES = issuesPath('saga');
const paths = page => issueCards(page).map(card => card.dataset.filePath);
const selectionMeta = page => text(page.$('#seriesDetailPanel .series-detail-selection-meta'));

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
