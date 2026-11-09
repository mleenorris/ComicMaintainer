'use strict';

// The genre and status filters of the library (js/app/library.js) and the
// provider details on a series page (js/app/series.js).

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const {
    makeSeries,
    makeSeriesList,
    bootLibrary,
    callsTo,
    seriesCalls,
    renderedTitles
} = require('./support/library-helpers');

const FACETS_PATH = '/api/files/series/facets';
const FACETS = {
    genres: [{ value: 'Action', count: 2 }, { value: 'Slice & <Life>', count: 1 }],
    statuses: [{ value: 'ongoing', count: 1 }, { value: 'completed', count: 2 }],
    series_without_genres: 1,
    series_without_status: 3
};

const bootWithFacets = (facets = FACETS, options = {}) =>
    bootLibrary({ series: makeSeriesList(2), ...options, routes: { [`GET ${FACETS_PATH}`]: facets, ...options.routes } });

const optionTexts = (page, id) => page.$$(`#${id} option`).map(option => option.textContent);
const optionValues = (page, id) => page.$$(`#${id} option`).map(option => option.value);

async function choose(page, id, value) {
    page.$(`#${id}`).value = value;
    page.$(`#${id}`).dispatchEvent(new page.window.Event('change', { bubbles: true }));
    await page.settle();
}

test('the filter menus list what the library holds and how many series have it', async () => {
    const page = await bootWithFacets();

    assert.equal(page.$('#libraryFacets').hidden, false);
    assert.deepEqual(optionTexts(page, 'libraryGenreFilter'), ['All genres', 'Action (2)', 'Slice & <Life> (1)', 'No data yet (1)']);
    assert.deepEqual(optionValues(page, 'libraryGenreFilter'), ['', 'Action', 'Slice & <Life>', 'none']);
    assert.deepEqual(optionTexts(page, 'libraryStatusFilter'), ['Any status', 'Ongoing (1)', 'Completed (2)', 'No data yet (3)']);
    assert.equal(page.$('#libraryFacetsHint').hidden, true);
    assert.equal(page.$('#libraryGenreFilter').disabled, false);
});

test('provider text in a filter menu is not parsed as markup', async () => {
    const page = await bootWithFacets({
        ...FACETS,
        genres: [{ value: '<img src=x onerror=alert(1)>', count: 1 }]
    });

    const option = page.$$('#libraryGenreFilter option')[1];
    assert.equal(option.querySelector('img'), null);
    assert.equal(option.textContent, '<img src=x onerror=alert(1)> (1)');
});

test('choosing a genre reloads the first page with it, encoded', async () => {
    const page = await bootWithFacets();

    await choose(page, 'libraryGenreFilter', 'Slice & <Life>');

    assert.deepEqual({ ...seriesCalls(page).at(-1).query }, { offset: '0', limit: '60', genre: 'Slice & <Life>' });
});

test('genre and status combine, and "All" drops the parameter again', async () => {
    const page = await bootWithFacets();

    await choose(page, 'libraryGenreFilter', 'Action');
    await choose(page, 'libraryStatusFilter', 'completed');
    assert.deepEqual({ ...seriesCalls(page).at(-1).query }, { offset: '0', limit: '60', genre: 'Action', status: 'completed' });

    await choose(page, 'libraryGenreFilter', '');
    assert.deepEqual({ ...seriesCalls(page).at(-1).query }, { offset: '0', limit: '60', status: 'completed' });
});

test('the series without data can be selected', async () => {
    const page = await bootWithFacets();

    await choose(page, 'libraryStatusFilter', 'none');

    assert.deepEqual({ ...seriesCalls(page).at(-1).query }, { offset: '0', limit: '60', status: 'none' });
});

test('with no genre or status data yet the menus are off and the bar says why', async () => {
    const page = await bootWithFacets({ genres: [], statuses: [], series_without_genres: 4, series_without_status: 4 });

    assert.equal(page.$('#libraryFacets').hidden, false);
    assert.equal(page.$('#libraryGenreFilter').disabled, true);
    assert.equal(page.$('#libraryStatusFilter').disabled, true);
    assert.equal(page.$('#libraryFacetsHint').hidden, false);
    assert.deepEqual(optionTexts(page, 'libraryGenreFilter'), ['All genres', 'No data yet (4)']);
});

test('a library where every series has data offers no "no data" choice', async () => {
    const page = await bootWithFacets({ ...FACETS, series_without_genres: 0, series_without_status: 0 });

    assert.deepEqual(optionValues(page, 'libraryGenreFilter'), ['', 'Action', 'Slice & <Life>']);
    assert.deepEqual(optionValues(page, 'libraryStatusFilter'), ['', 'ongoing', 'completed']);
});

test('a selection the library no longer holds stays listed and selected', async () => {
    const page = await bootWithFacets();
    await choose(page, 'libraryGenreFilter', 'Action');
    await choose(page, 'libraryStatusFilter', 'completed');

    page.route(`GET ${FACETS_PATH}`, { genres: [], statuses: [], series_without_genres: 0, series_without_status: 0 });
    await page.run('loadLibraryFacets()');
    await page.settle();

    assert.deepEqual(optionValues(page, 'libraryGenreFilter'), ['', 'Action']);
    assert.equal(page.$('#libraryGenreFilter').value, 'Action');
    assert.deepEqual(optionTexts(page, 'libraryStatusFilter'), ['Any status', 'Completed']);
    assert.equal(page.$('#libraryStatusFilter').value, 'completed');
});

test('a selected "no data" value that no longer applies keeps its label', async () => {
    const page = await bootWithFacets();
    await choose(page, 'libraryGenreFilter', 'none');
    await choose(page, 'libraryStatusFilter', 'none');

    page.route(`GET ${FACETS_PATH}`, { ...FACETS, series_without_genres: 0, series_without_status: 0 });
    await page.run('loadLibraryFacets()');
    await page.settle();

    assert.equal(page.$('#libraryGenreFilter').value, 'none');
    assert.equal(optionTexts(page, 'libraryGenreFilter').at(-1), 'No data yet');
    assert.equal(optionTexts(page, 'libraryStatusFilter').at(-1), 'No data yet');
});

test('a status the page has no label for is shown as it is', async () => {
    const page = await bootWithFacets({ ...FACETS, statuses: [{ value: 'rebooted', count: 1 }] });

    assert.equal(optionTexts(page, 'libraryStatusFilter')[1], 'rebooted (1)');
});

test('the filters are fetched once for a plain load and again on a refresh', async () => {
    const page = await bootWithFacets();
    assert.equal(callsTo(page, FACETS_PATH).length, 1);

    await page.run('loadActiveLibraryView()');
    await page.settle();
    assert.equal(callsTo(page, FACETS_PATH).length, 1);

    await page.run('loadActiveLibraryView({ refresh: true })');
    await page.settle();
    assert.equal(callsTo(page, FACETS_PATH).length, 2);
});

test('when the filters cannot be loaded the bar stays hidden and the library still works', async () => {
    const page = await bootLibrary({
        series: makeSeriesList(2),
        routes: { [`GET ${FACETS_PATH}`]: respond(500, 'boom') }
    });

    assert.equal(page.$('#libraryFacets').hidden, true);
    assert.deepEqual(renderedTitles(page), ['Series 001', 'Series 002']);
    assert.equal(page.$$('#messageContainer .message.error').length, 0);
});

test('the bar is only shown on the library page', async () => {
    const page = await bootWithFacets();

    page.run("navigate('#/series/series-1')");
    await page.settle();
    assert.equal(page.$('#libraryFacets').hidden, true);

    page.run("navigate('#/library')");
    await page.settle();
    assert.equal(page.$('#libraryFacets').hidden, false);
});

test('an empty filtered list suggests changing the filter', async () => {
    for (const [id, value] of [['libraryGenreFilter', 'Action'], ['libraryStatusFilter', 'completed']]) {
        const page = await bootLibrary({
            series: [],
            routes: { [`GET ${FACETS_PATH}`]: { ...FACETS } }
        });

        await choose(page, id, value);

        assert.match(page.$('#fileList').textContent, /Try a different search term or filter/);
    }
});

async function seriesPage(overrides) {
    const page = await bootLibrary({ series: [makeSeries(1, overrides)], routes: { [`GET ${FACETS_PATH}`]: FACETS } });
    page.run("navigate('#/series/series-1')");
    await page.settle();
    return page;
}

test('a series page shows its status, genres and provider link', async () => {
    const page = await seriesPage({
        metadata_source: 'AniListManga',
        genres: ['Action', 'Drama'],
        publication_status: 'completed',
        site_url: 'https://anilist.co/manga/30002'
    });

    assert.deepEqual(page.$$('.series-detail-chip').map(chip => chip.textContent), ['Completed', 'Action', 'Drama']);
    const link = page.$('.series-detail-link');
    assert.equal(link.getAttribute('href'), 'https://anilist.co/manga/30002');
    assert.equal(link.getAttribute('rel'), 'noopener noreferrer');
    assert.equal(link.textContent, 'View on AniListManga');
});

test('provider text on a series page is escaped', async () => {
    const page = await seriesPage({
        metadata_source: '<b>Source</b>',
        genres: ['<img src=x onerror=alert(1)>'],
        site_url: 'https://example.org/"><script>'
    });

    assert.equal(page.$('.series-detail-details img'), null);
    assert.equal(page.$('.series-detail-details script'), null);
    assert.equal(page.$('.series-detail-link b'), null);
    assert.equal(page.$('.series-detail-chip').textContent, '<img src=x onerror=alert(1)>');
});

test('a link that is not a web address is not rendered', async () => {
    const page = await seriesPage({ metadata_source: 'AniListManga', genres: ['Action'], site_url: 'javascript:alert(1)' });

    assert.equal(page.$('.series-detail-link'), null);
    assert.deepEqual(page.$$('.series-detail-chip').map(chip => chip.textContent), ['Action']);
});

test('a link without a named source still reads sensibly', async () => {
    const page = await seriesPage({ site_url: 'http://example.org/x' });

    assert.equal(page.$('.series-detail-link').textContent, 'View on provider');
    assert.equal(page.$('.series-detail-chip--status'), null);
});

test('a matched series with no details yet says they are coming', async () => {
    const page = await seriesPage({ metadata_source: 'MangaDex' });

    assert.equal(page.$('.series-detail-details'), null);
    assert.match(page.$('.series-detail-summary-body').textContent, /Genres and status are not available yet/);
});

test('a series with no match shows no details block at all', async () => {
    const page = await seriesPage({});

    assert.equal(page.$('.series-detail-details'), null);
    assert.doesNotMatch(page.$('.series-detail-summary-body').textContent, /not available yet/);
});
